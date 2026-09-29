using System;
using System.Threading.Tasks;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using Decibel_Monitor.Alarm;

namespace Decibel_Monitor.Alerting.Actions;

/// <summary>
/// 行动：恢复分贝提醒常态（同时清除"临时静默"与"临时开启"，无参数）。
/// </summary>
[ActionInfo("decibel-monitor.alert.resume", "恢复分贝提醒", defaultGroupToMenu: "分贝监测")]
public class ResumeAlertAction : ActionBase
{
    private readonly AlertOverrideService _overrideService;

    /// <param name="overrideService">临时静默 / 临时开启服务（由宿主根容器注入插件单例）。</param>
    public ResumeAlertAction(AlertOverrideService overrideService)
    {
        _overrideService = overrideService ?? throw new ArgumentNullException(nameof(overrideService));
    }

    /// <inheritdoc />
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        _overrideService.Clear();
    }
}