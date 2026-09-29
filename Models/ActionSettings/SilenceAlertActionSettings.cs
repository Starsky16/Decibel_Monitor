using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Decibel_Monitor.Models.ActionSettings;

/// <summary>「静默分贝提醒」行动的静默时长选项。</summary>
public enum SilenceAlertKind
{
    /// <summary>静默本节课（取不到本节课剩余时间时回退设置页的兜底时长）。</summary>
    ThisClass = 0,

    /// <summary>静默指定分钟数。</summary>
    Minutes = 1,

    /// <summary>静默到今日结束（本地零点）。</summary>
    Today = 2,
}

/// <summary>
/// 「静默分贝提醒」行动的设置。
/// </summary>
public class SilenceAlertActionSettings : ObservableObject
{
    private SilenceAlertKind _kind = SilenceAlertKind.ThisClass;
    private double _minutes = 45;

    /// <summary>静默到什么时候（默认本节课）。</summary>
    public SilenceAlertKind Kind
    {
        get => _kind;
        set
        {
            if (value == _kind) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KindIndex));
            OnPropertyChanged(nameof(IsMinutesKind));
        }
    }

    /// <summary>供 <c>ComboBox.SelectedIndex</c> 绑定的序号（顺序与 <see cref="SilenceAlertKind"/> 一致）。</summary>
    [JsonIgnore]
    public int KindIndex
    {
        get => (int)_kind;
        set => Kind = (SilenceAlertKind)Math.Clamp(value, 0, 2);
    }

    /// <summary>是否选择了"指定分钟数"（控制分钟输入框的可见性）。</summary>
    [JsonIgnore]
    public bool IsMinutesKind => _kind == SilenceAlertKind.Minutes;

    /// <summary>静默时长（分钟），仅在 <see cref="SilenceAlertKind.Minutes"/> 时使用；消费端按 1..1440 夹取。</summary>
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