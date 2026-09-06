using System;
using System.Linq;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using Decibel_Monitor.Models;
using Decibel_Monitor.Services;

namespace Decibel_Monitor.Views.SettingsPages;

/// <summary>
/// Decibel_Monitor 插件设置页（与其他设置项同层级）。
/// 承载插件全局配置与设备诊断。
/// </summary>
[SettingsPageInfo(
    "Decibel_Monitor.PluginSettings",
    "Decibel_Monitor",
    "\uEB88",
    "\uEB88",
    category: SettingsPageCategory.External)]
public partial class DecibelMonitorSettingsPage : SettingsPageBase
{
    private readonly AudioPeakMeter? _audioPeakMeter;

    /// <summary>
    /// 插件全局设置（用于绑定采样偏好）。
    /// </summary>
    public DecibelMonitorGlobalSettings GlobalSettings { get; }

    /// <summary>
    /// 供 XAML 资源加载器 / 设计器使用的默认构造。
    /// </summary>
    public DecibelMonitorSettingsPage() : this(new DecibelMonitorSettingsService(null), null)
    {
    }

    public DecibelMonitorSettingsPage(DecibelMonitorSettingsService settingsService, AudioPeakMeter? audioPeakMeter = null)
    {
        InitializeComponent();
        GlobalSettings = settingsService.Settings;
        _audioPeakMeter = audioPeakMeter;
        DataContext = this;
        RefreshDefaultDeviceText();
    }

    /// <summary>
    /// 刷新默认捕获设备描述文本。
    /// </summary>
    private void RefreshDefaultDeviceText()
    {
        try
        {
            var desc = _audioPeakMeter?.GetDefaultCaptureDeviceDescription();
            TextDefaultDevice.Text = string.IsNullOrWhiteSpace(desc)
                ? "（未发现可用的默认捕获设备）"
                : $"当前默认捕获设备：{desc}";
        }
        catch
        {
            TextDefaultDevice.Text = "（读取默认捕获设备失败）";
        }
    }

    /// <summary>
    /// "刷新设备"按钮点击事件。
    /// </summary>
    public void On_RefreshDevices(object? sender, RoutedEventArgs e)
    {
        RefreshDefaultDeviceText();
    }

    /// <summary>
    /// "捕获设备列表"按钮点击事件。
    /// </summary>
    public void On_ShowDevices(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_audioPeakMeter is null)
            {
                _ = CommonTaskDialogs.ShowDialog("捕获设备列表", "采样服务不可用。");
                return;
            }

            var devices = _audioPeakMeter.GetCaptureDeviceDescriptions();
            var text = string.Join("\n", devices);
            _ = CommonTaskDialogs.ShowDialog("捕获设备列表",
                string.IsNullOrWhiteSpace(text) ? "（未发现可用捕获设备）" : text);
        }
        catch
        {
            /* 忽略诊断失败 */
        }
    }
}

