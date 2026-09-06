using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Decibel_Monitor.Services;

/// <summary>
/// 麦克风峰值采样服务。
/// 通过一条"常驻静默捕获流"（WASAPI 共享模式，仅计算峰值、不保存音频）持续读取默认麦克风峰值，
/// 供组件高频刷新直接读取——数值稳定、无周期性打开/关闭录音导致的占用图标闪烁。
/// 常驻流不可用时回退到系统实时计量（AudioMeterInformation）；
/// 显式操作（校准等）可调用 <see cref="CaptureSamplePeakAsync"/> 临时采样一次。
/// 设备枚举结果带缓存，采样会话（临时采样）受信号量保护。
/// </summary>
public sealed class AudioPeakMeter : IDisposable
{
    /// <summary>设备列表缓存刷新间隔（毫秒）。</summary>
    private const int DeviceRefreshIntervalMs = 5000;

    /// <summary>常驻捕获流启动失败后的重试退避间隔（毫秒）。</summary>
    private const int CaptureRestartBackoffMs = 5000;

    /// <summary>常驻捕获流的峰值窗口（毫秒）：超过该窗口后重新开始累计最大值。</summary>
    private static readonly TimeSpan ContinuousPeakWindow = TimeSpan.FromMilliseconds(400);

    private readonly object _deviceCacheLock = new();
    private readonly object _captureLock = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SemaphoreSlim _samplingGate = new(1, 1);

    /// <summary>临时（显式）采样单次录音时长（毫秒），来自插件全局设置，默认 400。</summary>
    private readonly int _fallbackCaptureMs = 400;

    /// <summary>线性峰值超过该阈值视为"检测到有效信号"，来自插件全局设置，默认 0.0001。</summary>
    private readonly float _signalThreshold = 0.0001f;

    /// <summary>
    /// 是否启用"常驻静默捕获流"。默认 true：保持一条低开销捕获流以持续获取峰值
    /// （Windows 会显示麦克风正在使用且图标常亮，但不会闪烁）。
    /// 关闭后仅使用系统实时计量（多数设备在无活跃录音会话时该值为 0）。
    /// </summary>
    private readonly bool _enableContinuousMonitoring;

    private MMDevice[]? _cachedCaptureDevices;
    private MMDevice? _cachedDefaultDevice;
    private DateTime _deviceCacheTimeUtc = DateTime.MinValue;

    // ---- 常驻捕获流 ----
    private WasapiCapture? _continuousCapture;
    private string? _continuousCaptureDeviceId;
    private DateTime _continuousWindowStartUtc;
    private DateTime _nextCaptureStartAttemptUtc = DateTime.MinValue;
    private volatile bool _captureRunning;
    private volatile float _continuousPeak;

    private volatile bool _disposed;

    /// <summary>当前服务是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 当前默认的临时采样时长（毫秒，来自插件全局设置；未注入时 400）。
    /// </summary>
    public int DefaultCaptureMs => _fallbackCaptureMs;

    /// <summary>
    /// 是否启用"常驻静默捕获流"。
    /// </summary>
    public bool EnableContinuousMonitoring => _enableContinuousMonitoring;

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
            _enableContinuousMonitoring = settingsService.Settings.EnableContinuousMonitoring;
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
    /// 启用"常驻静默捕获流"时，返回常驻流在当前峰值窗口内的最大值（稳定、无周期性开/关录音）；
    /// 同时优先返回实时计量（AudioMeterInformation）读到的更大瞬时值以提高响应灵敏度。
    /// 未启用常驻流时仅使用系统实时计量（多数设备在无活跃录音会话时该值为 0）。
    /// </summary>
    public Task<float> GetDefaultDevicePeakLinearAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return Task.FromResult(0f);

        var fast = GetDefaultDevicePeakLinearFast();
        if (fast > _signalThreshold)
        {
            return Task.FromResult(fast);
        }

        if (_enableContinuousMonitoring)
        {
            EnsureContinuousCapture();
            return Task.FromResult(_continuousPeak);
        }

        return Task.FromResult(0f);
    }

    /// <summary>
    /// 确保"常驻静默捕获流"已启动。捕获流仅计算峰值、不保存音频；
    /// 若默认设备变化或捕获意外停止则自动重启（带退避）。
    /// </summary>
    private void EnsureContinuousCapture()
    {
        if (_disposed || !_enableContinuousMonitoring) return;

        // 捕获运行中：仅检查默认设备是否变化，变化则重启
        if (_captureRunning)
        {
            if (_continuousCaptureDeviceId != GetDefaultCaptureDevice()?.ID)
            {
                RestartContinuousCapture();
            }
            return;
        }

        if (DateTime.UtcNow < _nextCaptureStartAttemptUtc) return; // 失败退避中

        lock (_captureLock)
        {
            if (_disposed || _captureRunning) return;
            if (DateTime.UtcNow < _nextCaptureStartAttemptUtc) return;

            try
            {
                var device = GetDefaultCaptureDevice();
                if (device is null)
                {
                    _nextCaptureStartAttemptUtc = DateTime.UtcNow.AddMilliseconds(CaptureRestartBackoffMs);
                    return;
                }

                StopContinuousCaptureLocked(); // 清理可能残留的对象

                var capture = new WasapiCapture(device);
                capture.DataAvailable += OnContinuousData;
                capture.RecordingStopped += OnContinuousStopped;
                capture.StartRecording();

                _continuousCapture = capture;
                _continuousCaptureDeviceId = device.ID;
                _continuousWindowStartUtc = DateTime.MinValue;
                _continuousPeak = 0f;
                _captureRunning = true;
            }
            catch
            {
                // 启动失败：清理并退避，避免高频重试
                StopContinuousCaptureLocked();
                _nextCaptureStartAttemptUtc = DateTime.UtcNow.AddMilliseconds(CaptureRestartBackoffMs);
            }
        }
    }

    /// <summary>
    /// 重启常驻捕获流（默认设备变化等场景）。调用方应确保在 <see cref="_captureLock"/> 外或已持有该锁。
    /// </summary>
    private void RestartContinuousCapture()
    {
        lock (_captureLock)
        {
            StopContinuousCaptureLocked();
            _nextCaptureStartAttemptUtc = DateTime.MinValue;
        }
        // 让 EnsureContinuousCapture 的加锁路径重新启动
        EnsureContinuousCapture();
    }

    /// <summary>
    /// 停止并释放当前常驻捕获流。须在持有 <see cref="_captureLock"/> 时调用。
    /// </summary>
    private void StopContinuousCaptureLocked()
    {
        var capture = _continuousCapture;
        _continuousCapture = null;
        _continuousCaptureDeviceId = null;
        _captureRunning = false;
        _continuousPeak = 0f;

        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnContinuousData;
            capture.RecordingStopped -= OnContinuousStopped;
            capture.StopRecording();
        }
        catch { /* 忽略停止时的异常 */ }
        capture.Dispose();
    }

    /// <summary>
    /// 常驻捕获流数据回调：在每个数据块中取峰值，并在 <see cref="ContinuousPeakWindow"/>
    /// 窗口内保留最大值（供读取线程经 <see cref="_continuousPeak"/> 获取）。
    /// </summary>
    private void OnContinuousData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (sender is not WasapiCapture capture) return;

            var wf = capture.WaveFormat;
            float blockPeak = PeakSample.ComputePeak(
                e.Buffer, e.BytesRecorded,
                wf.Encoding == WaveFormatEncoding.IeeeFloat, wf.BitsPerSample);

            var now = DateTime.UtcNow;
            if (_continuousWindowStartUtc == DateTime.MinValue ||
                now - _continuousWindowStartUtc > ContinuousPeakWindow)
            {
                _continuousWindowStartUtc = now;
                _continuousPeak = blockPeak;
            }
            else if (blockPeak > _continuousPeak)
            {
                _continuousPeak = blockPeak;
            }
        }
        catch { /* 忽略单个数据块解析错误 */ }
    }

    /// <summary>
    /// 常驻捕获流停止回调（设备被拔出、被独占或异常等）。
    /// </summary>
    private void OnContinuousStopped(object? sender, StoppedEventArgs e)
    {
        // 仅处理当前流的事件，忽略重启过程中旧流延迟到达的回调
        if (!ReferenceEquals(sender, _continuousCapture)) return;

        _captureRunning = false;
        _continuousPeak = 0f;
        if (e.Exception is not null)
        {
            _nextCaptureStartAttemptUtc = DateTime.UtcNow.AddMilliseconds(CaptureRestartBackoffMs);
        }
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
            return await GetPeakLinearAsync(device, Math.Clamp(captureMs, 100, 5000), cancellationToken).ConfigureAwait(false);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_captureLock)
        {
            StopContinuousCaptureLocked();
        }

        _enumerator.Dispose();
        _samplingGate.Dispose();
    }
}
