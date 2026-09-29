using System;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using Decibel_Monitor.Alarm;
using Decibel_Monitor.Models.ActionSettings;

namespace Decibel_Monitor.Alerting.Actions;

/// <summary>
/// 行动：临时静默分贝提醒（本节课 / 指定分钟数 / 到今日结束）。
/// </summary>
/// <remarks>
/// 与托盘菜单共用 <see cref="AlertOverrideService"/>，因此两者语义天然一致。
/// 静默期间不推进判定源、不写冷却、不发通知，状态点仍照常显示当前是否超阈值。
/// </remarks>
[ActionInfo("decibel-monitor.alert.silence", "静默分贝提醒", defaultGroupToMenu: "分贝监测")]
public class SilenceAlertAction : ActionBase<SilenceAlertActionSettings>
{
    private readonly AlertOverrideService _overrideService;

    /// <param name="overrideService">临时静默 / 临时开启服务（由宿主根容器注入插件单例）。</param>
    public SilenceAlertAction(AlertOverrideService overrideService)
    {
        _overrideService = overrideService ?? throw new ArgumentNullException(nameof(overrideService));
    }

    /// <inheritdoc />
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        switch (Settings.Kind)
        {
            case SilenceAlertKind.Minutes:
                _overrideService.SilenceForMinutes((int)Settings.Minutes);
                break;
            case SilenceAlertKind.Today:
                _overrideService.SilenceForToday();
                break;
            default:
                _overrideService.SilenceForThisClass();
                break;
        }
    }
}