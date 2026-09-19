using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Decibel_Monitor.Alerting;
using Decibel_Monitor.Measurement;
using Decibel_Monitor.Services;
using Microsoft.Extensions.Hosting;

namespace Decibel_Monitor.Alarm;

/// <summary>
/// 提醒运行时编排服务：周期采样 → 构造判定上下文 → 交由仲裁模块裁定 →
/// 裁定提醒时接入通知提供方并进入 3 秒防自激（暂停分贝采样判定）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>判定源与仲裁模块由 DI 以单例注入；本服务只做「采样 → 判定 → 执行 → 防自激」编排，
/// 不接触具体判定逻辑。</description></item>
/// <item><description>通知的唯一出口是仲裁模块的 <see cref="AlertDecisionCoordinator.NotifyRequested"/> 事件；
/// 本服务把它接到现有 <see cref="DecibelNotificationProvider"/>，由宿主通知系统负责强调通知与语音，
/// <strong>不新增通知/语音开关</strong>。</description></item>
/// <item><description>无任何启用判定源时不采样，避免无效的音频访问开销。</description></item>
/// </list>
/// </remarks>
public sealed class AlertRuntimeService : IHostedService, IDisposable
{
    /// <summary>采样与判定周期。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>防自激时长：执行提醒后暂停分贝采样判定。</summary>
    private static readonly TimeSpan AntiSelfKickDelay = TimeSpan.FromSeconds(3);

    /// <summary>平均分贝滑动窗口的样本容量（1 秒一个样本）。</summary>
    private const int AverageWindowCount = 20;

    private readonly AudioPeakMeter _audioPeakMeter;
    private readonly DecibelMonitorSettingsService? _settingsService;
    private readonly AlertDecisionCoordinator _coordinator;
    private readonly List<float> _window = new();
    private readonly DispatcherTimer _timer;
    private DateTime _pauseUntilUtc = DateTime.MinValue;
    private double _lastAverage;
    private bool _disposed;

    /// <summary>
    /// 是否处于防自激暂停期（执行提醒后 3 秒内）。暂停期内不采样、不判定。
    /// </summary>
    public bool IsSamplingPaused => DateTime.UtcNow < _pauseUntilUtc;

    /// <param name="audioPeakMeter">共享的麦克风峰值采样服务。</param>
    /// <param name="settingsService">插件全局设置服务（可空；用于读取放大倍数）。</param>
    /// <param name="coordinator">仲裁模块（自持判定源集合与优先级配置）。</param>
    public AlertRuntimeService(
        AudioPeakMeter audioPeakMeter,
        DecibelMonitorSettingsService? settingsService,
        AlertDecisionCoordinator coordinator)
    {
        _audioPeakMeter = audioPeakMeter ?? throw new ArgumentNullException(nameof(audioPeakMeter));
        _settingsService = settingsService;
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        _coordinator.NotifyRequested += OnNotifyRequested;

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += Timer_Tick;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 仲裁模块裁定需要提醒时的唯一出口：接入现有通知提供方发出强调通知。
    /// </summary>
    private void OnNotifyRequested(string sourceId)
    {
        try
        {
            // 复用现有通知提供方：强调通知与语音均由宿主通知系统负责
            DecibelNotificationProvider.Instance?.Evaluate(_lastAverage);
        }
        catch
        {
            // 通知失败不应中断编排
        }
    }

    // 异步 Tick：全程保持在 DispatcherTimer 绑定的 UI 线程上下文（不就地切线程），
    // 与热键确认封送回 UI 线程的处理保持一致，避免判定源状态被并发修改。
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        try
        {
            // 防自激：执行提醒后 3 秒内暂停采样判定
            if (IsSamplingPaused) return;

            // 无启用判定源时跳过采样，避免无效的音频访问
            if (!_coordinator.Sources.Any(s => s.IsEnabled)) return;

            var linear = await _audioPeakMeter.GetDefaultDevicePeakLinearAsync();
            var magnification = _settingsService?.Settings.Magnification ?? 1.0;
            var mapped = DecibelCalculator.LinearToDisplayDb(linear, magnification);

            // 维护平均分贝滑动窗口（容量固定，超出即丢弃最旧样本）
            _window.Add((float)mapped);
            if (_window.Count > AverageWindowCount) _window.RemoveAt(0);
            var average = AverageVolumeAlertEvaluator.CalculateAverage(_window);
            _lastAverage = average;

            var decision = _coordinator.Decide(new AlertContext(mapped, average), DateTime.UtcNow);
            if (!decision.ShouldAlert) return;

            // 提醒已执行（Decide 内部经 NotifyRequested 发出）→ 进入防自激，
            // 并清空样本窗口，避免残留高值在恢复后立即再次触发。
            _pauseUntilUtc = DateTime.UtcNow.Add(AntiSelfKickDelay);
            _window.Clear();
        }
        catch
        {
            // 采样/判定任一步骤失败都不应抛出到定时器循环
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _coordinator.NotifyRequested -= OnNotifyRequested;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
    }
}