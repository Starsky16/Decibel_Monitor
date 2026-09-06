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
}
