using System;

namespace Decibel_Monitor.Alerting;

/// <summary>
/// 判定源①：到阈值自动提醒。
/// 当前（窗口平均）分贝值超过阈值且自身冷却已过时，直接判定为需要提醒。
/// </summary>
public sealed class AutoThresholdDecisionSource : IAlertDecisionSource
{
    /// <summary>判定源标识（用于仲裁模块的优先级排序与配置映射）。</summary>
    public const string SourceId = "auto-threshold";

    private DateTime _nextAlertTimeUtc = DateTime.MinValue;
    private readonly SustainedDurationTracker _sustainTracker = new();

    /// <param name="threshold">提醒阈值（显示刻度 0..150）。</param>
    /// <param name="cooldown">提醒冷却时间；为 null 时表示无冷却。</param>
    public AutoThresholdDecisionSource(double threshold = 120.0, TimeSpan? cooldown = null)
    {
        Threshold = threshold;
        Cooldown = cooldown ?? TimeSpan.Zero;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "自动提醒";

    /// <inheritdoc />
    public string Description => "平均分贝超过阈值时立即提醒。";

    /// <inheritdoc />
    public bool IsEnabled { get; set; }

    /// <summary>提醒阈值（显示刻度 0..150）。</summary>
    public double Threshold { get; set; }

    /// <inheritdoc />
    public TimeSpan Cooldown { get; set; }

    /// <summary>
    /// 连续超阈时长要求：平均分贝需连续超过阈值至少该时长才提醒。
    /// <see cref="TimeSpan.Zero"/>（默认）表示不做持续性要求，只看平均值。
    /// </summary>
    public TimeSpan SustainThreshold { get; set; }

    /// <summary>下一次允许提醒的时间（UTC）；<see cref="DateTime.MinValue"/> 表示从未提醒过。</summary>
    public DateTime NextAlertTimeUtc => _nextAlertTimeUtc;

    /// <summary>当前是否处于"超过阈值"状态（供组件显示提醒状态）。</summary>
    public bool IsTriggerActive { get; private set; }

    /// <inheritdoc />
    public bool IsCoolingDown(DateTime nowUtc) =>
        IsEnabled && _nextAlertTimeUtc != DateTime.MinValue && nowUtc < _nextAlertTimeUtc;

    /// <inheritdoc />
    public bool WouldAlert(AlertContext context, DateTime nowUtc)
    {
        if (!IsEnabled) return false;

        // 阈值比较为严格大于：等于阈值不算超阈值（与 Decide 同口径）
        if (!(context.AverageDecibel > Threshold)) return false;
        if (!_sustainTracker.IsSustained(true, nowUtc, SustainThreshold)) return false;

        return nowUtc >= _nextAlertTimeUtc;
    }

    /// <inheritdoc />
    public AlertDecision Observe(AlertContext context, DateTime nowUtc)
    {
        // 推进本拍状态（含条件满足时照常写入冷却），但不发出提醒：
        // 保证同一拍最多只有被仲裁选中的目标源产生一条通知（计划 §21.1）。
        var decision = Decide(context, nowUtc);
        return decision with { ShouldAlert = false };
    }

    /// <inheritdoc />
    public AlertDecision Decide(AlertContext context, DateTime nowUtc)
    {
        if (!IsEnabled)
        {
            // 未启用时清空状态：再次启用后不会沿用旧冷却
            IsTriggerActive = false;
            _nextAlertTimeUtc = DateTime.MinValue;
            _sustainTracker.Reset();
            return default;
        }

        // 阈值比较为严格大于：等于阈值不算超阈值
        var isActive = context.AverageDecibel > Threshold;
        IsTriggerActive = isActive;

        // 连续超阈要求（与平均窗口正交）只影响"是否提醒"；是否超阈值的显示仍用原始判断，
        // 否则抑制期状态点会自相矛盾地显示"不吵"，而画面上的数字明明已经超过阈值。
        if (!_sustainTracker.Update(isActive, nowUtc, SustainThreshold))
        {
            return new AlertDecision(false, isActive);
        }

        if (nowUtc < _nextAlertTimeUtc) return new AlertDecision(false, true);

        _nextAlertTimeUtc = nowUtc + EffectiveCooldown();
        return new AlertDecision(true, true);
    }

    /// <summary>生效冷却时间：负值按无冷却处理。</summary>
    private TimeSpan EffectiveCooldown() => Cooldown > TimeSpan.Zero ? Cooldown : TimeSpan.Zero;
}
