using System;

namespace Decibel_Monitor.Alerting;

/// <summary>
/// 临时提醒覆盖：用"绝对截止时间"表达"临时静默"与"临时开启"，两者<strong>互斥</strong>。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>纯逻辑、可单测：不引用宿主类型、不自持定时器；读取方（采样拍）每拍比较截止时间即可，
/// 到点自动失效，因此"打断/恢复"就是改写这个时间戳，宿主无需重启。</description></item>
/// <item><description><strong>不持久化</strong>：宿主重启即恢复常态（计划 §25.2 的既有取舍）。</description></item>
/// <item><description>时间基准沿用插件既有的 <c>DateTime.UtcNow</c> 墙钟口径；所有方法都要求调用方传入当前时间，
/// 以保证行为可测（不读系统时钟）。</description></item>
/// </list>
/// </remarks>
public sealed class TemporaryAlertOverride
{
    /// <summary>单次覆盖的时长上限（24 小时）：夹取非法或异常大的截止时间（如传入的日终时间算错）。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    private DateTime _silenceUntilUtc = DateTime.MinValue;
    private DateTime _forceEnableUntilUtc = DateTime.MinValue;

    /// <summary>静默截止时间（UTC）；<see cref="DateTime.MinValue"/> 表示从未设置。</summary>
    public DateTime SilenceUntilUtc => _silenceUntilUtc;

    /// <summary>临时开启截止时间（UTC）；<see cref="DateTime.MinValue"/> 表示从未设置。</summary>
    public DateTime ForceEnableUntilUtc => _forceEnableUntilUtc;

    /// <summary>当前是否处于临时静默期。</summary>
    public bool IsSilenced(DateTime nowUtc) => nowUtc < _silenceUntilUtc;

    /// <summary>当前是否处于临时开启期。</summary>
    public bool IsForceEnabled(DateTime nowUtc) => nowUtc < _forceEnableUntilUtc;

    /// <summary>静默剩余时间；未静默时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan SilenceRemaining(DateTime nowUtc) =>
        IsSilenced(nowUtc) ? _silenceUntilUtc - nowUtc : TimeSpan.Zero;

    /// <summary>
    /// 静默到 <paramref name="untilUtc"/>，并清除"临时开启"（互斥）。
    /// 截止时间超过 <see cref="MaxDuration"/> 时按上限夹取；早于当前时刻时视为立即结束。
    /// </summary>
    public void SilenceUntil(DateTime nowUtc, DateTime untilUtc)
    {
        _silenceUntilUtc = Clamp(nowUtc, untilUtc);
        _forceEnableUntilUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 临时开启到 <paramref name="untilUtc"/>，并清除"临时静默"（互斥）。夹取规则同 <see cref="SilenceUntil"/>。
    /// </summary>
    public void ForceEnableUntil(DateTime nowUtc, DateTime untilUtc)
    {
        _forceEnableUntilUtc = Clamp(nowUtc, untilUtc);
        _silenceUntilUtc = DateTime.MinValue;
    }

    /// <summary>恢复常态（同时清除静默与临时开启）。</summary>
    public void Clear()
    {
        _silenceUntilUtc = DateTime.MinValue;
        _forceEnableUntilUtc = DateTime.MinValue;
    }

    /// <summary>本地时间的"今日结束"（次日零点），供"静默到今日结束"换算截止时间。</summary>
    /// <param name="localNow">当前本地时间。</param>
    /// <remarks>返回值保留输入的 <see cref="DateTime.Kind"/>（<c>DateTime.Now</c> 传入即 <see cref="DateTimeKind.Local"/>），
    /// 调用方可直接 <c>ToUniversalTime()</c>。</remarks>
    public static DateTime EndOfLocalDay(DateTime localNow) => localNow.Date.AddDays(1);

    private static DateTime Clamp(DateTime nowUtc, DateTime untilUtc)
    {
        var max = nowUtc + MaxDuration;
        if (untilUtc > max) return max;
        return untilUtc < nowUtc ? nowUtc : untilUtc;
    }
}