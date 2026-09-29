using System;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using Decibel_Monitor.Alerting;
using Decibel_Monitor.Services;

namespace Decibel_Monitor.Alarm;

/// <summary>
/// 临时静默 / 临时开启的插件级服务（单例）：把设置页、宿主托盘菜单与自动化动作的请求
/// 翻译成 <see cref="TemporaryAlertOverride"/> 上的绝对截止时间。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>语义对齐时段闸门：<strong>抑制期间不推进判定源、不写冷却、不发通知，状态点照常显示</strong>。</description></item>
/// <item><description>不持久化、不需要新定时器：到点由读取方（采样拍）比较截止时间自动失效。</description></item>
/// <item><description>课表服务惰性解析并缓存（沿用运行时服务的 <c>??=</c> 写法）；解析不到时「静默本节课」回退到设置里的兜底分钟数。</description></item>
/// </list>
/// </remarks>
public sealed class AlertOverrideService
{
    /// <summary>各入口时长的夹取范围（分钟）。</summary>
    private const int MinMinutes = 1;
    private const int MaxMinutes = 24 * 60;

    private readonly TemporaryAlertOverride _override = new();
    private readonly DecibelMonitorSettingsService _settingsService;
    private ILessonsService? _lessonsService;

    /// <param name="settingsService">插件全局设置服务（提供兜底时长与「一个午」时长）。</param>
    public AlertOverrideService(DecibelMonitorSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>静默截止时间（UTC）；未静默时为 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime SilenceUntilUtc => _override.SilenceUntilUtc;

    /// <summary>临时开启截止时间（UTC）；未开启时为 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime ForceEnableUntilUtc => _override.ForceEnableUntilUtc;

    /// <summary>当前是否处于临时静默期。</summary>
    public bool IsSilenced(DateTime nowUtc) => _override.IsSilenced(nowUtc);

    /// <summary>当前是否处于临时开启期。</summary>
    public bool IsForceEnabled(DateTime nowUtc) => _override.IsForceEnabled(nowUtc);

    /// <summary>静默剩余时间；未静默时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan SilenceRemaining(DateTime nowUtc) => _override.SilenceRemaining(nowUtc);

    /// <summary>
    /// 静默本节课：上课中且能取到本节课剩余时间时用它，否则回退到设置里的
    /// <see cref="Models.DecibelMonitorGlobalSettings.SilenceFallbackMinutes"/>。
    /// </summary>
    public void SilenceForThisClass()
    {
        var nowUtc = DateTime.UtcNow;
        var remaining = TryGetRemainingClassTime();
        var duration = remaining ?? TimeSpan.FromMinutes(ClampMinutes(_settingsService.Settings.SilenceFallbackMinutes));
        _override.SilenceUntil(nowUtc, nowUtc + duration);
    }

    /// <summary>静默一个午：固定 <see cref="Models.DecibelMonitorGlobalSettings.NoonSilenceMinutes"/> 分钟。</summary>
    public void SilenceForNoon()
    {
        var nowUtc = DateTime.UtcNow;
        var minutes = ClampMinutes(_settingsService.Settings.NoonSilenceMinutes);
        _override.SilenceUntil(nowUtc, nowUtc + TimeSpan.FromMinutes(minutes));
    }

    /// <summary>静默到今日结束（本地零点）。</summary>
    public void SilenceForToday()
    {
        var nowUtc = DateTime.UtcNow;
        _override.SilenceUntil(nowUtc, TemporaryAlertOverride.EndOfLocalDay(DateTime.Now).ToUniversalTime());
    }

    /// <summary>临时开启指定分钟数（判定源照常工作，即"上课默认禁用 + 需要时临时开启"）。</summary>
    /// <param name="minutes">临时开启的时长（分钟），按 1..1440 夹取。</param>
    public void ForceEnableForMinutes(int minutes)
    {
        var nowUtc = DateTime.UtcNow;
        _override.ForceEnableUntil(nowUtc, nowUtc + TimeSpan.FromMinutes(ClampMinutes(minutes)));
    }

    /// <summary>恢复常态（同时清除静默与临时开启）。</summary>
    public void Clear() => _override.Clear();

    /// <summary>
    /// 本节课的剩余时间；非上课状态、课表未加载或找不到当前课程点时返回 null。
    /// </summary>
    private TimeSpan? TryGetRemainingClassTime()
    {
        var lessons = _lessonsService ??= IAppHost.TryGetService<ILessonsService>();
        if (lessons is null || !lessons.IsClassPlanLoaded || lessons.CurrentState != TimeState.OnClass) return null;

        var plan = lessons.CurrentClassPlan;
        if (plan is null) return null;

        var nowOfDay = DateTime.Now.TimeOfDay;
        foreach (var item in plan.ValidTimeLayoutItems)
        {
            // TimeType：0 - 上课，1 - 课间，2 - 分割线，3 - 行动；只认"上课"点
            if (item.TimeType != 0) continue;
            if (nowOfDay >= item.StartTime && nowOfDay < item.EndTime) return item.EndTime - nowOfDay;
        }

        return null;
    }

    private static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);
}