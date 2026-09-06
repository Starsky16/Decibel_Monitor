using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models;

/// <summary>
/// Decibel_Monitor 插件的全局设置（存放于插件配置目录，与组件/通知设置相互独立）。
/// </summary>
public partial class DecibelMonitorGlobalSettings : ObservableObject
{
    /// <summary>
    /// 回退采样单次录音时长（毫秒）。较长时间可捕获更低频率/更弱信号，但会提高触发开销。
    /// </summary>
    [ObservableProperty] private int _fallbackCaptureMs = 400;

    /// <summary>
    /// 线性峰值信号检测阈值：超过该值视为"检测到有效信号"（可在采样异常时调低）。
    /// </summary>
    [ObservableProperty] private double _signalThreshold = 0.0001;

    /// <summary>
    /// 是否允许"回退录音采样"：为 false（默认）时实时监测仅使用系统实时计量
    /// （AudioMeterInformation，不打开麦克风录音通道，避免触发 Windows 麦克风占用提示）。
    /// 开启后会在实时计量读不到有效信号时周期性短暂录音以获取峰值（会占用麦克风并可能导致占用图标闪烁），
    /// 仅供系统不提供实时计量的设备使用。
    /// </summary>
    [ObservableProperty] private bool _enableFallbackSampling = false;
}
