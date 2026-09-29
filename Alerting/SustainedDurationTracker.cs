using System;

namespace Decibel_Monitor.Alerting;

/// <summary>
/// "条件连续成立时长"计时器：按真实时间累计某个布尔条件连续成立的时间，
/// 条件一旦不成立、或调用间隔出现断层，即重新起算。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>按<strong>真实时间</strong>而非采样次数计时（与 <see cref="ScheduleGate"/> 同口径），
/// 采样节拍变化不会改变"连续 N 秒"的含义。</description></item>
/// <item><description><see cref="MaxGap"/> 限制两次调用的间隔上限，超过即视为中断。它自动覆盖
/// 时段闸门抑制、提醒后的防自激暂停、捕获设备切换这些"根本没被调用"的时段，
/// 避免把没在观察的时间也算进"连续超阈"。</description></item>
/// <item><description>与滑动平均窗口<strong>正交</strong>：平均窗口负责平滑瞬时尖峰，
/// 本类负责要求"必须持续"，两者叠加使用。</description></item>
/// </list>
/// </remarks>
public sealed class SustainedDurationTracker
{
    /// <summary>两次调用间隔的允许上限，超过即视为中断。默认 1 秒（采样节拍为 200 ms）。</summary>
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromSeconds(1);

    private DateTime _conditionSinceUtc = DateTime.MinValue;
    private DateTime _lastUpdateUtc = DateTime.MinValue;

    /// <summary>条件开始连续成立的时刻（UTC）；未在计时时为 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime ConditionSinceUtc => _conditionSinceUtc;

    /// <summary>
    /// 推进一次计时，并返回条件是否已连续成立至少 <paramref name="duration"/>。
    /// </summary>
    /// <param name="condition">本次观察到的条件是否成立。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <param name="duration">要求的连续时长；为 0 或负值时不做持续性要求，直接返回 <paramref name="condition"/>。</param>
    /// <returns>是否满足"连续成立 <paramref name="duration"/>"。</returns>
    public bool Update(bool condition, DateTime nowUtc, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            // 未启用持续性要求：不保留任何计时状态，行为与不加本类时逐拍等价
            Reset();
            return condition;
        }

        if (!condition)
        {
            Reset();
            return false;
        }

        // 首次观察，或距上次调用已超过 MaxGap（期间没被调用，不能算连续）→ 从本次重新起算
        var gapBroken = _lastUpdateUtc != DateTime.MinValue && nowUtc - _lastUpdateUtc > MaxGap;
        if (_conditionSinceUtc == DateTime.MinValue || gapBroken) _conditionSinceUtc = nowUtc;

        _lastUpdateUtc = nowUtc;
        return nowUtc - _conditionSinceUtc >= duration;
    }

    /// <summary>清空计时，下次调用重新起算。</summary>
    public void Reset()
    {
        _conditionSinceUtc = DateTime.MinValue;
        _lastUpdateUtc = DateTime.MinValue;
    }
}