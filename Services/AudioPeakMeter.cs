using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Decibel_Monitor.Services;

/// <summary>
/// 麦克风峰值采样服务。
/// 统一管理多重回退采样（AudioMeterInformation → WasapiCapture(指定设备) → WasapiCapture(默认) → WaveInEvent），
/// 通过信号量保证任意时刻只有一个采样会话，避免多个调用方同时抢占音频设备。
/// 设备枚举结果与慢速回退采样结果均带缓存，避免高频 UI 刷新产生不必要的开销。
/// </summary>
public sealed class AudioPeakMeter : IDisposable
{
    /// <summary>设备列表缓存刷新间隔（毫秒）。</summary>
    private const int DeviceRefreshIntervalMs = 5000;

    /// <summary>慢速回退采样结果缓存时长（毫秒），避免高频 UI 刷新反复触发耗时采样。</summary>
    private const int FallbackResultCacheMs = 3000;

    /// <summary>回退采样单次录音时长（毫秒）。</summary>
    private const int FallbackCaptureMs = 400;

    /// <summary>线性峰值超过该阈值视为"检测到有效信号"。</summary>
    private const float SignalThreshold = 0.0001f;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SemaphoreSlim _samplingGate = new(1, 1);

    private MMDevice[]? _cachedCaptureDevices;
    private MMDevice? _cachedDefaultDevice;
    private DateTime _deviceCacheTimeUtc = DateTime.MinValue;

    private float _cachedFallbackLinear;
    private DateTime _fallbackCacheTimeUtc = DateTime.MinValue;

    /// <summary>实时计量（AudioMeterInformation）已验证可用及对应的默认设备 ID。</summary>
    private volatile bool _fastPathVerified;
    private volatile string? _fastPathVerifiedDeviceId;

    private volatile bool _disposed;

    /// <summary>当前服务是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 快速读取默认捕获设备的实时线性峰值（0..1）。
    /// 仅读取 AudioMeterInformation，不做耗时采样，适合高频 UI 刷新。
    /// 若设备不支持实时计量或读取失败，返回 0。
    /// </summary>
    public float GetDefaultDevicePeakLinearFast()
    {
        if (_disposed) return 0f;
        try
        {
            var device = GetDefaultCaptureDevice();
            return device?.AudioMeterInformation?.MasterPeakValue ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>
    /// 获取默认捕获设备的线性峰值（0..1）。
    /// 优先使用实时计量；不可用时进行短时录音回退采样（结果缓存一段时间，避免反复触发耗时采样）。
    /// 回退采样受信号量保护，同一时间只会有一个采样会话。
    /// </summary>
    public async Task<float> GetDefaultDevicePeakLinearAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return 0f;

        var fast = GetDefaultDevicePeakLinearFast();
        if (fast > SignalThreshold)
        {
            // 快速路径读到有效信号：说明该设备支持实时计量，标记验证通过。
            // 此后静音（读 0）即为真实静音，无需再触发昂贵的回退录音采样。
            _fastPathVerified = true;
            _fastPathVerifiedDeviceId = _cachedDefaultDevice?.ID;
            return fast;
        }

        // 实时计量已验证可用且默认设备未变化：0 就是真实静音，直接返回，避免反复打开音频设备。
        if (_fastPathVerified && _fastPathVerifiedDeviceId == _cachedDefaultDevice?.ID)
        {
            return 0f;
        }

        if (IsFallbackCacheFresh())
        {
            return _cachedFallbackLinear;
        }

        await _samplingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 双重检查：等待锁期间可能有其他调用已完成采样并更新缓存
            if (IsFallbackCacheFresh())
            {
                return _cachedFallbackLinear;
            }

            var device = GetDefaultCaptureDevice();
            var result = await GetPeakLinearAsync(device, FallbackCaptureMs, cancellationToken).ConfigureAwait(false);

            _cachedFallbackLinear = result;
            _fallbackCacheTimeUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            _samplingGate.Release();
        }
    }

    /// <summary>
    /// 获取指定设备（可空表示默认设备）的线性峰值（0..1），按 AudioMeterInformation → WasapiCapture → WaveInEvent 顺序回退。
    /// </summary>
    private async Task<float> GetPeakLinearAsync(MMDevice? device, int captureMs, CancellationToken cancellationToken)
    {
        // 1) AudioMeterInformation（低成本实时计量）
        var meterValue = device?.AudioMeterInformation?.MasterPeakValue ?? 0f;
        if (meterValue > SignalThreshold) return meterValue;

        // 2) WasapiCapture（指定设备）
        try
        {
            var v = await GetPeakByWasapiCaptureAsync(device, captureMs, cancellationToken).ConfigureAwait(false);
            if (v > SignalThreshold) return v;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略，继续回退 */ }

        // 3) WasapiCapture（默认设备）：仅在指定设备并非默认设备时才需要再次尝试，
        //    避免对同一设备重复录音造成不必要的开销。
        if (!IsDefaultDevice(device))
        {
            try
            {
                var v = await GetPeakByWasapiCaptureAsync(null, captureMs, cancellationToken).ConfigureAwait(false);
                if (v > SignalThreshold) return v;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 忽略 */ }
        }

        // 4) WaveInEvent（旧 API，兼容性更好）
        try
        {
            var v = await GetPeakByWaveInAsync(captureMs, cancellationToken).ConfigureAwait(false);
            if (v > SignalThreshold) return v;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 忽略 */ }

        return 0f;
    }

    /// <summary>
    /// 判断指定设备是否为默认捕获设备（null 表示默认设备）。
    /// </summary>
    private bool IsDefaultDevice(MMDevice? device)
    {
        if (device is null) return true;
        var def = _cachedDefaultDevice;
        return def is null || def.ID == device.ID;
    }

    /// <summary>
    /// 使用 WasapiCapture 短时录音并计算最大样本绝对值（0..1）。
    /// 检测到首个有效信号即提前返回，避免等满整个录音时长。
    /// </summary>
    private async Task<float> GetPeakByWasapiCaptureAsync(MMDevice? device, int captureMs, CancellationToken cancellationToken)
    {
        float maxSample = 0f;
        var tcs = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var capture = device is null ? new WasapiCapture() : new WasapiCapture(device);
        capture.DataAvailable += (s, e) =>
        {
            try
            {
                var wf = capture.WaveFormat;
                float peak = 0f;
                if (wf.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    for (int n = 0; n + 4 <= e.BytesRecorded; n += 4)
                    {
                        float sample = Math.Abs(BitConverter.ToSingle(e.Buffer, n));
                        if (sample > peak) peak = sample;
                    }
                }
                else if (wf.BitsPerSample == 16)
                {
                    for (int n = 0; n + 2 <= e.BytesRecorded; n += 2)
                    {
                        short s16 = BitConverter.ToInt16(e.Buffer, n);
                        float sample = Math.Abs(s16 / 32768f);
                        if (sample > peak) peak = sample;
                    }
                }
                else if (wf.BitsPerSample == 32)
                {
                    for (int n = 0; n + 4 <= e.BytesRecorded; n += 4)
                    {
                        int i32 = BitConverter.ToInt32(e.Buffer, n);
                        float sample = Math.Abs(i32 / (float)int.MaxValue);
                        if (sample > peak) peak = sample;
                    }
                }

                if (peak > maxSample) maxSample = peak;
                // 检测到有效信号即可提前完成采样，避免等满整个录音时长
                if (maxSample > SignalThreshold) tcs.TrySetResult(maxSample);
            }
            catch { /* 忽略单个数据块解析错误 */ }
        };
        capture.RecordingStopped += (s, e) => tcs.TrySetResult(maxSample);

        capture.StartRecording();
        try
        {
            // 首个有效信号或录音时长到点即返回
            await Task.WhenAny(tcs.Task, Task.Delay(captureMs, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            capture.StopRecording();
        }

        // 采样结果经 TCS 传递，保证回调线程写入对当前线程可见
        var peak = tcs.Task.IsCompleted ? await tcs.Task.ConfigureAwait(false) : maxSample;
        return Math.Clamp(peak, 0f, 1f);
    }

    /// <summary>
    /// 使用 WaveInEvent 短时录音并计算最大样本绝对值（0..1）。
    /// 检测到首个有效信号即提前返回，避免等满整个录音时长。
    /// </summary>
    private async Task<float> GetPeakByWaveInAsync(int captureMs, CancellationToken cancellationToken)
    {
        float maxSample = 0f;
        var tcs = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var waveIn = new WaveInEvent();
        waveIn.WaveFormat = new WaveFormat(16000, 16, 1); // 单声道 16k 16bit
        waveIn.DataAvailable += (s, e) =>
        {
            try
            {
                for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
                {
                    short s16 = BitConverter.ToInt16(e.Buffer, i);
                    float sample = Math.Abs(s16 / 32768f);
                    if (sample > maxSample) maxSample = sample;
                }
                if (maxSample > SignalThreshold) tcs.TrySetResult(maxSample);
            }
            catch { }
        };
        waveIn.RecordingStopped += (s, e) => tcs.TrySetResult(maxSample);

        waveIn.StartRecording();
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(captureMs, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            waveIn.StopRecording();
        }

        var peak = tcs.Task.IsCompleted ? await tcs.Task.ConfigureAwait(false) : maxSample;
        return Math.Clamp(peak, 0f, 1f);
    }

    /// <summary>
    /// 获取当前可用捕获设备的描述列表（用于诊断显示）。
    /// </summary>
    public string[] GetCaptureDeviceDescriptions()
    {
        if (_disposed) return Array.Empty<string>();
        try
        {
            EnsureDeviceCache();
            return _cachedCaptureDevices?.Select(d => $"{d.FriendlyName} ({d.ID})").ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 获取默认捕获设备（设备列表带 5 秒缓存）。
    /// </summary>
    private MMDevice? GetDefaultCaptureDevice()
    {
        try
        {
            EnsureDeviceCache();
            if (_cachedDefaultDevice is null) return null;
            if (_cachedCaptureDevices is null) return _cachedDefaultDevice;
            return _cachedCaptureDevices.FirstOrDefault(c => c.ID == _cachedDefaultDevice.ID) ?? _cachedDefaultDevice;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 刷新设备缓存（超过缓存时间或尚未初始化时重新枚举）。
    /// </summary>
    private void EnsureDeviceCache()
    {
        var now = DateTime.UtcNow;
        if (_cachedCaptureDevices is null || (now - _deviceCacheTimeUtc).TotalMilliseconds >= DeviceRefreshIntervalMs)
        {
            _cachedCaptureDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
            _cachedDefaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            _deviceCacheTimeUtc = now;
        }
    }

    private bool IsFallbackCacheFresh()
    {
        return (DateTime.UtcNow - _fallbackCacheTimeUtc).TotalMilliseconds < FallbackResultCacheMs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.Dispose();
        _samplingGate.Dispose();
    }
}
