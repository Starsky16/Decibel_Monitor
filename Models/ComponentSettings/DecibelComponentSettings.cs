using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models.ComponentSettings;

public partial class DecibelComponentSettings : ObservableObject
{
    /// <summary>
    /// 放大倍数，校准后保存到此属性。
    /// </summary>
    [ObservableProperty] private double _magnification = 1.0;

    /// <summary>
    /// 提醒功能开关。
    /// </summary>
    [ObservableProperty] private bool _isAlertEnabled = false;

    /// <summary>
    /// 提醒阈值（显示刻度 0..150），超过该值触发提醒。
    /// </summary>
    [ObservableProperty] private double _alertThreshold = 120.0;

    /// <summary>
    /// 超出阈值时显示的提示文字（组件内提示与系统通知正文）。
    /// </summary>
    [ObservableProperty] private string _alertText = "请保持安静";

    /// <summary>
    /// 触发提醒后的冷却时间（分钟），避免频繁弹窗，默认 10 分钟。
    /// </summary>
    [ObservableProperty] private int _alertCooldownMinutes = 10;
}
