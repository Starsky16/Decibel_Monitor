using System;
using System.ComponentModel;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Decibel_Monitor.Services;
using DecibelComponentSettings = Decibel_Monitor.Models.ComponentSettings.DecibelComponentSettings;

namespace Decibel_Monitor.Controls.Components;

[ComponentInfo(
    "3540a8df-963c-45e9-a42d-8a55824994d5",
    "分贝值",
    "\uEB88",
    "在主界面上显示麦克风分贝值。"
)]
public partial class DecibelComponent : ComponentBase<DecibelComponentSettings>, INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _updateTimer;
    private readonly AudioPeakMeter? _audioPeakMeter;
    private readonly DecibelNotificationProvider? _notificationProvider;
    private volatile bool _disposed;
    private string _currentDecibelValue = "N/A";
    private bool _isAlertActive;
    private DateTime _nextAlertTimeUtc = DateTime.MinValue;
    private volatile bool _isUpdating;
    private string? _lastAlertText;

    // 子类独立事件：Avalonia 绑定通过 INotifyPropertyChanged 接口订阅，这里显式实现接口事件并转发到本事件，
    // 使 CurrentDecibelValue / IsAlertActive 等 CLR 属性的变化通知能到达绑定系统。
    public new event PropertyChangedEventHandler? PropertyChanged;

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => PropertyChanged += value;
        remove => PropertyChanged -= value;
    }

    public string CurrentDecibelValue
    {
        get => _currentDecibelValue;
        private set
        {
            if (_currentDecibelValue == value) return;
            _currentDecibelValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDecibelValue)));
        }
    }

    /// <summary>
    /// 当前是否处于"超过阈值"提醒状态（用于显示提示文字）。
    /// </summary>
    public bool IsAlertActive
    {
        get => _isAlertActive;
        private set
        {
            if (_isAlertActive == value) return;
            _isAlertActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAlertActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertDisplayText)));
        }
    }

    /// <summary>
    /// 提醒提示文字（取设置中的自定义文字）。
    /// </summary>
    public string AlertDisplayText => Settings?.AlertText ?? "请保持安静";

    public DecibelComponent()
    {
        InitializeComponent();

        // 从宿主 DI 容器获取共享的峰值采样服务（单例，含并发保护与缓存）
        _audioPeakMeter = IAppHost.Host?.Services.GetService(typeof(AudioPeakMeter)) as AudioPeakMeter;

        // 提醒通知提供方由 AddNotificationProvider 注册为 IHostedService（仅 IHostedService -> 类型），
        // 无法通过 DI 直接解析；其构造函数会写入静态 Instance，故直接使用该实例。
        _notificationProvider = DecibelNotificationProvider.Instance;

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
    }

    // 异步 Tick，内部会异步采样（不会阻塞 UI）
    private async void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        // 节流：上一次采样尚未结束时跳过本次 Tick，避免耗时采样导致异步任务堆积
        if (_isUpdating) return;
        _isUpdating = true;
        try
        {
            if (_audioPeakMeter is null)
            {
                CurrentDecibelValue = "无采样服务";
                return;
            }

            // 优先走实时计量；不可用时自动使用短时录音回退采样（带缓存与并发保护）
            float linear = await _audioPeakMeter.GetDefaultDevicePeakLinearAsync().ConfigureAwait(false);

            double magnification = Settings?.Magnification ?? 1.0;
            double mapped = DecibelCalculator.LinearToDisplayDb(linear, magnification);

            bool alertEnabled = Settings?.IsAlertEnabled ?? false;
            bool overThreshold = mapped > (Settings?.AlertThreshold ?? 120.0);
            bool shouldNotify = false;

            if (alertEnabled && overThreshold)
            {
                // 冷却期内不重复触发，避免频繁弹窗
                if (DateTime.UtcNow >= _nextAlertTimeUtc)
                {
                    _nextAlertTimeUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, Settings?.AlertCooldownMinutes ?? 10));
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                _notificationProvider?.NotifyOverThreshold(Settings?.AlertText ?? "请保持安静");
            }

            // 绑定属性变更必须回到 UI 线程触发（Avalonia 绑定依赖 UI 线程上的 PropertyChanged）。
            // 组件已释放时丢弃更新，避免在 Dispose 之后仍操作控件。
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;

                IsAlertActive = alertEnabled && overThreshold;
                CurrentDecibelValue = $"{mapped:F1}";

                // 提示文字变化时同步通知，保证 AlertDisplayText 绑定始终反映最新设置
                string alertText = Settings?.AlertText ?? "请保持安静";
                if (!string.Equals(_lastAlertText, alertText, StringComparison.Ordinal))
                {
                    _lastAlertText = alertText;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlertDisplayText)));
                }
            });
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed) return;
                CurrentDecibelValue = "读取失败";
            });
        }
        finally
        {
            _isUpdating = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _updateTimer.Tick -= UpdateTimer_Tick;
        _updateTimer.Stop();
    }
}
