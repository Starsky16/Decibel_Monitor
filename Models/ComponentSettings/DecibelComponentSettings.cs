using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models.ComponentSettings;

/// <summary>
/// 分贝组件的设置。每个组件实例独立持久化，仅保留与"小组件显示侧"相关的显示偏好。
/// 测量/校准（参考 dB、放大倍数）为全局设置，位于插件设置页（DecibelMonitorGlobalSettings）；
/// 提醒相关设置在通知提供方设置（DecibelNotificationProviderSettings）。
/// </summary>
public partial class DecibelComponentSettings : ObservableObject
{
    /// <summary>
    /// 是否在组件上显示"超过阈值"的提示文字（如“请保持安静”），默认 true。
    /// 关闭后超阈值仍会发送系统通知，但不在组件上显示红色提示文字。
    /// </summary>
    [ObservableProperty] private bool _showAlertTextOnComponent = true;
}
