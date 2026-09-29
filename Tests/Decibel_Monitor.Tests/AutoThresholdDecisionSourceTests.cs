using System;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

public class AutoThresholdDecisionSourceTests
{
    private const double Threshold = 100.0;

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AlertContext Context(double averageDb, double currentDb = 0.0) => new(currentDb, averageDb);

    private static AutoThresholdDecisionSource CreateSource(TimeSpan? cooldown = null, bool enabled = true)
        => new(Threshold, cooldown) { IsEnabled = enabled };

    [Fact]
    public void Decide_Should_NotAlert_WhenDisabled()
    {
        var source = CreateSource(enabled: false);

        var decision = source.Decide(Context(150.0), Now);

        Assert.False(decision.ShouldAlert);
        Assert.False(decision.IsTriggerActive);
        Assert.False(source.IsTriggerActive);
    }

    [Fact]
    public void Decide_Should_Alert_OnFirstOverThreshold()
    {
        var source = CreateSource();

        var decision = source.Decide(Context(120.0), Now);

        Assert.True(decision.ShouldAlert);
        Assert.True(decision.IsTriggerActive);
    }

    [Fact]
    public void Decide_Should_NotAlert_WhenEqualToThreshold()
    {
        var source = CreateSource();

        var decision = source.Decide(Context(Threshold), Now);

        Assert.False(decision.ShouldAlert);
        Assert.False(decision.IsTriggerActive);
    }

    [Fact]
    public void Decide_Should_CompareAverage_NotCurrentValue()
    {
        // 瞬时值超阈值但窗口平均未超：不提醒（判定以平均值为准）
        var source = CreateSource();

        var decision = source.Decide(Context(90.0, 150.0), Now);

        Assert.False(decision.ShouldAlert);
    }

    [Fact]
    public void Decide_Should_NotAlert_WhileCoolingDown_ButStayActive()
    {
        var source = CreateSource(TimeSpan.FromMinutes(5));
        var first = source.Decide(Context(120.0), Now);
        Assert.True(first.ShouldAlert);

        var cooling = source.Decide(Context(120.0), Now.AddMinutes(1));

        Assert.False(cooling.ShouldAlert);
        Assert.True(cooling.IsTriggerActive);
        Assert.Equal(Now.AddMinutes(5), source.NextAlertTimeUtc);
    }

    [Fact]
    public void Decide_Should_AlertAgain_WhenCooldownElapsed()
    {
        var source = CreateSource(TimeSpan.FromMinutes(5));
        source.Decide(Context(120.0), Now);

        var again = source.Decide(Context(120.0), Now.AddMinutes(5));

        Assert.True(again.ShouldAlert);
        Assert.Equal(Now.AddMinutes(10), source.NextAlertTimeUtc);
    }

    [Fact]
    public void Decide_Should_AlertEveryTick_WhenCooldownIsZero()
    {
        var source = CreateSource(TimeSpan.Zero);

        Assert.True(source.Decide(Context(120.0), Now).ShouldAlert);
        Assert.True(source.Decide(Context(120.0), Now).ShouldAlert);
    }

    [Fact]
    public void Decide_Should_ClearCooldown_WhenDisabledThenReEnabled()
    {
        var source = CreateSource(TimeSpan.FromMinutes(10));
        source.Decide(Context(120.0), Now);

        source.IsEnabled = false;
        source.Decide(Context(120.0), Now);
        source.IsEnabled = true;

        var decision = source.Decide(Context(120.0), Now.AddMinutes(1));

        Assert.True(decision.ShouldAlert);
    }

    [Fact]
    public void Decide_Should_NotAlert_WhenBelowThreshold()
    {
        var source = CreateSource();

        var decision = source.Decide(Context(50.0), Now);

        Assert.False(decision.ShouldAlert);
        Assert.False(decision.IsTriggerActive);
    }

    [Fact]
    public void IsCoolingDown_Should_BeTrue_AfterAlert_UntilCooldownElapsed()
    {
        var source = CreateSource(TimeSpan.FromMinutes(5));
        Assert.False(source.IsCoolingDown(Now));

        source.Decide(Context(120.0), Now);

        Assert.True(source.IsCoolingDown(Now));
        Assert.True(source.IsCoolingDown(Now.AddMinutes(4)));
        Assert.False(source.IsCoolingDown(Now.AddMinutes(5)));
    }

    [Fact]
    public void IsCoolingDown_Should_BeFalse_WhenDisabled()
    {
        var source = CreateSource(TimeSpan.FromMinutes(5));
        source.Decide(Context(120.0), Now);

        source.IsEnabled = false;
        source.Decide(Context(120.0), Now);

        Assert.False(source.IsCoolingDown(Now.AddMinutes(1)));
    }

    [Fact]
    public void IsCoolingDown_Should_BeFalse_WhenCooldownIsZero()
    {
        var source = CreateSource(TimeSpan.Zero);

        source.Decide(Context(120.0), Now);

        Assert.False(source.IsCoolingDown(Now));
    }

    // ---- 连续超阈时长 ----

    private const double OverThreshold = 120.0;

    /// <summary>按 250 ms 节拍推进判定（模拟真实采样，避免触发 SustainedDurationTracker 的 MaxGap 中断）。</summary>
    private static void Advance(AutoThresholdDecisionSource source, double averageDb, DateTime startUtc, TimeSpan span)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var t = TimeSpan.Zero; t <= span; t += step)
        {
            source.Decide(Context(averageDb), startUtc + t);
        }
    }

    /// <summary>按 250 ms 节拍推进判定，返回首次提醒的时间偏移；始终未提醒则为 null。</summary>
    private static TimeSpan? FirstAlertOffset(AutoThresholdDecisionSource source, double averageDb, DateTime startUtc, TimeSpan span)
    {
        var step = TimeSpan.FromMilliseconds(250);
        for (var t = TimeSpan.Zero; t <= span; t += step)
        {
            if (source.Decide(Context(averageDb), startUtc + t).ShouldAlert) return t;
        }

        return null;
    }

    [Fact]
    public void Decide_Should_AlertImmediately_WhenSustainThresholdIsZero()
    {
        var source = CreateSource();
        source.SustainThreshold = TimeSpan.Zero;

        Assert.True(source.Decide(Context(OverThreshold), Now).ShouldAlert);
    }

    [Fact]
    public void Decide_Should_NotAlert_BeforeSustainDurationElapsed()
    {
        var source = CreateSource();
        source.SustainThreshold = TimeSpan.FromSeconds(5);

        Assert.False(source.Decide(Context(OverThreshold), Now).ShouldAlert);
        // 尚未满足持续性要求时，仍应如实显示"超阈值"，否则状态点与读数自相矛盾
        Assert.True(source.IsTriggerActive);

        Assert.Equal(TimeSpan.FromSeconds(5), FirstAlertOffset(source, OverThreshold, Now, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Decide_Should_RestartSustain_WhenDropsBelowThreshold()
    {
        var source = CreateSource();
        source.SustainThreshold = TimeSpan.FromSeconds(5);

        // 连续超阈 2 秒后安静下来，累计作废
        Advance(source, OverThreshold, Now, TimeSpan.FromSeconds(2));
        source.Decide(Context(50.0), Now.AddMilliseconds(2250));

        // 重新超阈：需重新连续满 5 秒，故接下来 4 秒内不应提醒
        Assert.Null(FirstAlertOffset(source, OverThreshold, Now.AddSeconds(2.5), TimeSpan.FromSeconds(4)));
    }
}
