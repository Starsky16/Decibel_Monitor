using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models.ComponentSettings;

/// <summary>
/// 分贝组件的设置。每个组件实例独立持久化。
/// 仅保留与"小组件显示侧"相关的设置项；提醒相关设置已迁移到通知提供方设置
/// （<see cref="Decibel_Monitor.Models.NotificationProviderSettings.DecibelNotificationProviderSettings"/>）。
/// </summary>
public partial class DecibelComponentSettings : ObservableObject
{
    /// <summary>
    /// 放大倍数，校准后保存到此属性。
    /// </summary>
    [ObservableProperty] private double _magnification = 1.0;

    /// <summary>
    /// 校准参考（显示刻度 dB 0..150，换算为 dBFS 时为其减去 150），默认 70（对应 -80 dBFS）。
    /// 此前为设置控件中不持久化的字段，现并入组件设置以便持久化保存。
    /// </summary>
    [ObservableProperty] private double _referenceDecibel = 70.0;

    /// <summary>
    /// 是否在组件上显示"超过阈值"的提示文字（如“请保持安静”），默认 true。
    /// 关闭后超阈值仍会发送系统通知，但不在组件上显示红色提示文字。
    /// </summary>
    [ObservableProperty] private bool _showAlertTextOnComponent = true;
}
