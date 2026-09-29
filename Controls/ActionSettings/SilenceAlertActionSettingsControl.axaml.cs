using ClassIsland.Core.Abstractions.Controls;
using Decibel_Monitor.Models.ActionSettings;

namespace Decibel_Monitor.Controls.ActionSettings;

/// <summary>
/// 「静默分贝提醒」行动的设置控件（绑定走宿主行动项的 <c>Settings</c>，见同名 axaml）。
/// </summary>
public partial class SilenceAlertActionSettingsControl : ActionSettingsControlBase<SilenceAlertActionSettings>
{
    public SilenceAlertActionSettingsControl() => InitializeComponent();
}