using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models.ActionSettings;

/// <summary>
/// 「临时开启分贝提醒」行动的设置。
/// </summary>
public class ForceEnableAlertActionSettings : ObservableObject
{
    private double _minutes = 60;

    /// <summary>临时开启的时长（分钟），默认 60；消费端按 1..1440 夹取。</summary>
    public double Minutes
    {
        get => _minutes;
        set
        {
            if (value.Equals(_minutes)) return;
            _minutes = value;
            OnPropertyChanged();
        }
    }
}