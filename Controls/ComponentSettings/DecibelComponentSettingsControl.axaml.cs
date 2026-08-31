using System;
using System.ComponentModel;
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
/// 分贝组件设置控件，用于管理分贝监测组件的相关配置界面。
/// </summary>
public partial class DecibelComponentSettingsControl : ComponentBase<DecibelComponentSettings>, INotifyPropertyChanged, IDisposable
{
    private readonly AudioPeakMeter? _audioPeakMeter;
    private bool _disposed;

    // 当前 ReferenceDecibel 表示 UI 上的 dB（0..150），默认 70（对应 -80 dBFS）
    private double _referenceDecibel = 70.0;

    // 子类独立事件：Avalonia 绑定通过 INotifyPropertyChanged 接口订阅，这里显式实现接口事件并转发到本事件。
    public new event PropertyChangedEventHandler? PropertyChanged;

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => PropertyChanged += value;
        remove => PropertyChanged -= value;
    }

    public double ReferenceDecibel
    {
        get => _referenceDecibel;
        set
        {
            if (Math.Abs(_referenceDecibel - value) < 0.0001) return;
            _referenceDecibel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReferenceDecibel)));
        }
    }

    public DecibelComponentSettingsControl()
    {
        InitializeComponent();
        // 从宿主 DI 容器获取共享的峰值采样服务
        _audioPeakMeter = IAppHost.Host?.Services.GetService(typeof(AudioPeakMeter)) as AudioPeakMeter;
    }

    /// <summary>
    /// 列出系统捕获设备用于诊断。
    /// </summary>
    public void ShowAvailableCaptureDevices()
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
            _ = CommonTaskDialogs.ShowDialog("捕获设备列表", string.IsNullOrWhiteSpace(text) ? "（未发现可用捕获设备）" : text);
        }
        catch
        {
            /* 忽略诊断失败 */
        }
    }

    /// <summary>
    /// "捕获设备列表"按钮点击事件。
    /// </summary>
    public void On_ShowDevices(object? sender, RoutedEventArgs e)
    {
        ShowAvailableCaptureDevices();
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

            float measuredLinear = await _audioPeakMeter.GetDefaultDevicePeakLinearAsync().ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (measuredLinear <= 0f)
                {
                    try { _ = CommonTaskDialogs.ShowDialog("校准结果", "未检测到有效输入，无法完成校准。请确认麦克风已启用并有声源。"); } catch { }
                    return;
                }

                // ReferenceDecibel 为 UI 上的 dB（0..150），换算为 dBFS 后计算放大倍数
                double targetDb = ReferenceDecibel;
                double magnification = DecibelCalculator.CalculateMagnification(targetDb, measuredLinear);

                double adjustedLinear = measuredLinear * magnification;
                double adjustedDb = adjustedLinear > 0 ? 20.0 * Math.Log10(adjustedLinear) : double.NegativeInfinity;
                double measuredDb = measuredLinear > 0 ? 20.0 * Math.Log10(measuredLinear) : double.NegativeInfinity;

                if (Settings is not null)
                {
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
                }
                else
                {
                    try
                    {
                        _ = CommonTaskDialogs.ShowDialog("校准完成（未保存）",
                            $"测量线性值: {measuredLinear:F4}\n" +
                            $"测量 dBFS: {measuredDb:F1} dB\n" +
                            $"目标 dB (显示): {targetDb:F1} dB\n" +
                            $"目标 dBFS: {DecibelCalculator.DisplayDbToDbFs(targetDb):F1} dBFS\n" +
                            $"建议放大倍数: {magnification:F6}×\n" +
                            $"放大后 dBFS (验证): {adjustedDb:F1} dB\n\n" +
                            "当前控件未绑定到组件实例，无法直接保存设置。");
                    }
                    catch { }
                }
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
