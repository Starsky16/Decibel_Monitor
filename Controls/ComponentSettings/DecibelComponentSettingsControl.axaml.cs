using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Controls;
using ClassIsland.Shared;
using Decibel_Monitor.Models.ComponentSettings;
using Decibel_Monitor.Services;

namespace Decibel_Monitor.Controls.ComponentSettings;

/// <summary>
/// 分贝组件设置控件（"小组件显示侧"）。
/// 仅包含校准相关设置；提醒设置与设备诊断分别位于通知提供方设置与插件设置页。
/// </summary>
public partial class DecibelComponentSettingsControl : ComponentBase<DecibelComponentSettings>, IDisposable
{
    private readonly AudioPeakMeter? _audioPeakMeter;
    private bool _disposed;

    public DecibelComponentSettingsControl()
    {
        InitializeComponent();
        // 从宿主 DI 容器获取共享的峰值采样服务
        _audioPeakMeter = IAppHost.Host?.Services.GetService(typeof(AudioPeakMeter)) as AudioPeakMeter;
    }

    /// <summary>
    /// 异步事件处理：采样并校准，避免阻塞 UI 线程。
    /// </summary>
    public async void On_Calibrate(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_audioPeakMeter is null)
            {
                _ = CommonTaskDialogs.ShowDialog("校准失败", "采样服务不可用。");
                return;
            }

            // 校准是用户的显式操作：主动短暂录音采样一次以获取真实峰值（平时实时监测不会占用麦克风通道）。
            // 内部仍会优先尝试实时计量，不可用时才短暂打开录音通道。
            float measuredLinear = await _audioPeakMeter.CaptureSamplePeakAsync(_audioPeakMeter.DefaultCaptureMs).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (measuredLinear <= 0f)
                {
                    try { _ = CommonTaskDialogs.ShowDialog("校准结果", "未检测到有效输入，无法完成校准。请确认麦克风已启用并有声源。"); } catch { }
                    return;
                }

                if (Settings is null)
                {
                    try
                    {
                        _ = CommonTaskDialogs.ShowDialog("校准完成（未保存）", "当前控件未绑定到组件实例，无法直接保存设置。");
                    }
                    catch { }
                    return;
                }

                // ReferenceDecibel 为 UI 上的 dB（0..150），换算为 dBFS 后计算放大倍数
                double targetDb = Settings.ReferenceDecibel;
                double magnification = DecibelCalculator.CalculateMagnification(targetDb, measuredLinear);

                double adjustedLinear = measuredLinear * magnification;
                double adjustedDb = adjustedLinear > 0 ? 20.0 * Math.Log10(adjustedLinear) : double.NegativeInfinity;
                double measuredDb = measuredLinear > 0 ? 20.0 * Math.Log10(measuredLinear) : double.NegativeInfinity;

                Settings.Magnification = magnification;
                try
                {
                    _ = CommonTaskDialogs.ShowDialog("校准结果",
                        $"测量线性值: {measuredLinear:F4}\n" +
                        $"测量 dBFS: {measuredDb:F1} dB\n" +
                        $"目标 dB (显示): {targetDb:F1} dB\n" +
                        $"目标 dBFS: {DecibelCalculator.DisplayDbToDbFs(targetDb):F1} dBFS\n" +
                        $"放大倍数: {magnification:F6}×\n" +
                        $"放大后 dBFS (验证): {adjustedDb:F1} dB");
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            try { _ = CommonTaskDialogs.ShowDialog("校准失败", $"发生异常：{ex.Message}"); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

