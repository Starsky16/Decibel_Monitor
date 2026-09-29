using System;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using Decibel_Monitor.Alarm;
using Decibel_Monitor.Models.ActionSettings;

namespace Decibel_Monitor.Alerting.Actions;

/// <summary>
/// 行动：临时开启分贝提醒指定分钟数。
/// </summary>
/// <remarks>
/// 典型用法是"上课默认禁用、需要时临时开启"：设置页把两个判定源都关掉，
/// 再用本行动在需要的那段时间里让它们照常工作。
/// </remarks>
[ActionInfo("decibel-monitor.alert.forceEnable", "临时开启分贝提醒", defaultGroupToMenu: "分贝监测")]
public class ForceEnableAlertAction : ActionBase<ForceEnableAlertActionSettings>
{
    private readonly AlertOverrideService _overrideService;

    /// <param name="overrideService">临时静默 / 临时开启服务（由宿主根容器注入插件单例）。</param>
    public ForceEnableAlertAction(AlertOverrideService overrideService)
    {
        _overrideService = overrideService ?? throw new ArgumentNullException(nameof(overrideService));
    }

    /// <inheritdoc />
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        _overrideService.ForceEnableForMinutes((int)Settings.Minutes);
    }
}