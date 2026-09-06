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

    private readonly object _deviceCacheLock = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SemaphoreSlim _samplingGate = new(1, 1);

    /// <summary>回退采样单次录音时长（毫秒），来自插件全局设置，默认 400。</summary>
    private readonly int _fallbackCaptureMs = 400;

    /// <summary>线性峰值超过该阈值视为"检测到有效信号"，来自插件全局设置，默认 0.0001。</summary>
    private readonly float _signalThreshold = 0.0001f;

    /// <summary>
    /// 是否允许回退录音采样。默认 false：实时监测只用系统实时计量（不打开录音通道），
    /// 避免周期性占用麦克风导致的 Windows 占用图标闪烁。
    /// </summary>
    private readonly bool _enableFallbackSampling;

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
    /// 当前默认的录音采样时长（毫秒，来自插件全局设置；未注入时 400）。
    /// </summary>
    public int DefaultCaptureMs => _fallbackCaptureMs;

    /// <summary>
    /// 初始化采样服务。
    /// </summary>
    /// <param name="settingsService">插件全局设置服务（可空；缺省时使用内置默认采样参数）。</param>
    public AudioPeakMeter(DecibelMonitorSettingsService? settingsService = null)
    {
        if (settingsService is not null)
        {
            _fallbackCaptureMs = Math.Clamp(settingsService.Settings.FallbackCaptureMs, 100, 5000);
            _signalThreshold = (float)Math.Clamp(settingsService.Settings.SignalThreshold, 0.000001, 1.0);
            _enableFallbackSampling = settingsService.Settings.EnableFallbackSampling;
        }
    }

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
    /// 获取默认捕获设备的线性峰值（0..1），供组件周期刷新使用。
    /// 默认仅使用系统实时计量（AudioMeterInformation），不会打开麦克风录音通道，
    /// 从而避免 Windows 麦克风占用提示与"读取失败/正常值"交替闪烁。
    /// 仅当全局设置中显式开启"回退录音采样"时，才在实时计量读不到有效信号时
    /// 周期性短时录音（受信号量保护并缓存结果）。
    /// </summary>
    public async Task<float> GetDefaultDevicePeakLinearAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return 0f;

        var fast = GetDefaultDevicePeakLinearFast();
        if (fast > _signalThreshold)
        {
            // 实时计量读到有效信号：说明该设备支持实时计量，标记验证通过。
            // 此后静音（读 0）即为真实静音，无需再触发回退录音采样。
            _fastPathVerified = true;
            _fastPathVerifiedDeviceId = _cachedDefaultDevice?.ID;
            return fast;
        }

        // 默认关闭回退录音：实时计量读 0 时视为静音，直接返回，绝不周期性打开录音通道。
        if (!_enableFallbackSampling)
        {
            return 0f;
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

        return await CaptureSamplePeakAsync(_fallbackCaptureMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 显式采样一次默认捕获设备的线性峰值（0..1）——用户主动触发（如校准、诊断）时使用。
    /// 会按 AudioMeterInformation → WasapiCapture → WaveInEvent 顺序尝试，
    /// 期间会短暂打开录音通道（受信号量保护，同一时间仅一个采样会话）。
    /// </summary>
    /// <param name="captureMs">录音采样时长（毫秒），限制在 100..5000 之间。</param>
    public async Task<float> CaptureSamplePeakAsync(int captureMs, CancellationToken cancellationToken = default)
    {
        if (_disposed) return 0f;

        await _samplingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var device = GetDefaultCaptureDevice();
            var result = await GetPeakLinearAsync(device, Math.Clamp(captureMs, 100, 5000), cancellationToken).ConfigureAwait(false);

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
        if (meterValue > _signalThreshold) return meterValue;

        // 2) WasapiCapture（指定设备）
        try
        {
            var v = await GetPeakByWasapiCaptureAsync(device, captureMs, cancellationToken).ConfigureAwait(false);
            if (v > _signalThreshold) return v;
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
                if (v > _signalThreshold) return v;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 忽略 */ }
        }

        // 4) WaveInEvent（旧 API，兼容性更好）
        try
        {
            var v = await GetPeakByWaveInAsync(captureMs, cancellationToken).ConfigureAwait(false);
            if (v > _signalThreshold) return v;
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
                float peak = PeakSample.ComputePeak(
                    e.Buffer, e.BytesRecorded,
                    wf.Encoding == WaveFormatEncoding.IeeeFloat, wf.BitsPerSample);

                if (peak > maxSample) maxSample = peak;
                // 检测到有效信号即可提前完成采样，避免等满整个录音时长
                if (maxSample > _signalThreshold) tcs.TrySetResult(maxSample);
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
                float peak = PeakSample.ComputePeak(e.Buffer, e.BytesRecorded, false, 16);
                if (peak > maxSample) maxSample = peak;
                if (maxSample > _signalThreshold) tcs.TrySetResult(maxSample);
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
            lock (_deviceCacheLock)
            {
                return _cachedCaptureDevices?.Select(d => $"{d.FriendlyName} ({d.ID})").ToArray() ?? Array.Empty<string>();
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 获取默认捕获设备的描述（用于诊断显示）。
    /// </summary>
    public string? GetDefaultCaptureDeviceDescription()
    {
        if (_disposed) return null;
        try
        {
            var d = GetDefaultCaptureDevice();
            return d is null ? null : $"{d.FriendlyName} ({d.ID})";
        }
        catch
        {
            return null;
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
            lock (_deviceCacheLock)
            {
                if (_cachedDefaultDevice is null) return null;
                if (_cachedCaptureDevices is null) return _cachedDefaultDevice;
                return _cachedCaptureDevices.FirstOrDefault(c => c.ID == _cachedDefaultDevice.ID) ?? _cachedDefaultDevice;
            }
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
        lock (_deviceCacheLock)
        {
            var now = DateTime.UtcNow;
            if (_cachedCaptureDevices is null || (now - _deviceCacheTimeUtc).TotalMilliseconds >= DeviceRefreshIntervalMs)
            {
                _cachedCaptureDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToArray();
                _cachedDefaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                _deviceCacheTimeUtc = now;
            }
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
