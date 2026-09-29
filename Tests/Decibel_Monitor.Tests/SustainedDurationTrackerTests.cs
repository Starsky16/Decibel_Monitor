using System;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

public class SustainedDurationTrackerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    /// <summary>按 250 ms 节拍推进计时（模拟真实采样，避免触发 MaxGap 中断），返回最后一拍的结果。</summary>
    private static bool Advance(SustainedDurationTracker tracker, bool condition, DateTime startUtc, TimeSpan span)
    {
        var step = TimeSpan.FromMilliseconds(250);
        var result = false;
        for (var t = TimeSpan.Zero; t <= span; t += step)
        {
            result = tracker.Update(condition, startUtc + t, Duration);
        }

        return result;
    }

    [Fact]
    public void Update_Should_BehaveAsPlainCondition_WhenDurationIsZero()
    {
        var tracker = new SustainedDurationTracker();

        Assert.True(tracker.Update(true, Now, TimeSpan.Zero));
        Assert.False(tracker.Update(false, Now.AddSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void Update_Should_NotSatisfy_WhenConditionJustStarted()
    {
        var tracker = new SustainedDurationTracker();

        Assert.False(tracker.Update(true, Now, Duration));
        Assert.Equal(Now, tracker.ConditionSinceUtc);
    }

    [Fact]
    public void Update_Should_Satisfy_AfterDurationElapsed()
    {
        var tracker = new SustainedDurationTracker();

        Assert.False(Advance(tracker, true, Now, TimeSpan.FromSeconds(9.5)));
        Assert.True(Advance(tracker, true, Now.AddSeconds(9.5), TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void Update_Should_Restart_WhenConditionBreaks()
    {
        var tracker = new SustainedDurationTracker();

        // 连续成立 8 秒后条件中断
        Assert.False(Advance(tracker, true, Now, TimeSpan.FromSeconds(8)));
        Assert.False(tracker.Update(false, Now.AddMilliseconds(8250), Duration));

        // 中断即重新起算：即使此前已累计 8 秒，也要再连续满 10 秒才满足
        Assert.False(Advance(tracker, true, Now.AddSeconds(8.5), TimeSpan.FromSeconds(9.75)));
        Assert.True(tracker.Update(true, Now.AddSeconds(18.5), Duration));
    }

    [Fact]
    public void Update_Should_Restart_WhenGapExceeded()
    {
        var tracker = new SustainedDurationTracker { MaxGap = TimeSpan.FromSeconds(1) };

        tracker.Update(true, Now, Duration);
        // 距上次调用 5 秒（超过 MaxGap）⇒ 期间没在观察，不能算作连续超阈
        Assert.False(tracker.Update(true, Now.AddSeconds(5), Duration));
        Assert.Equal(Now.AddSeconds(5), tracker.ConditionSinceUtc);
    }

    [Fact]
    public void Update_Should_KeepTiming_WhenGapWithinLimit()
    {
        var tracker = new SustainedDurationTracker { MaxGap = TimeSpan.FromSeconds(1) };

        tracker.Update(true, Now, Duration);
        Assert.False(tracker.Update(true, Now.AddMilliseconds(200), Duration));

        Assert.Equal(Now, tracker.ConditionSinceUtc);
    }

    [Fact]
    public void Reset_Should_ClearConditionSince()
    {
        var tracker = new SustainedDurationTracker();
        tracker.Update(true, Now, Duration);

        tracker.Reset();

        Assert.Equal(DateTime.MinValue, tracker.ConditionSinceUtc);
    }

    [Fact]
    public void Update_Should_ClearState_WhenDurationTurnsZero()
    {
        var tracker = new SustainedDurationTracker();
        tracker.Update(true, Now, Duration);

        Assert.True(tracker.Update(true, Now.AddSeconds(5), TimeSpan.Zero));
        Assert.Equal(DateTime.MinValue, tracker.ConditionSinceUtc);
    }

    // ---- 非改动的探测（P1 两段式） ----

    [Fact]
    public void IsSustained_Should_BehaveAsPlainCondition_WhenDurationIsZero()
    {
        var tracker = new SustainedDurationTracker();

        Assert.True(tracker.IsSustained(true, Now, TimeSpan.Zero));
        Assert.False(tracker.IsSustained(false, Now, TimeSpan.Zero));
    }

    [Fact]
    public void IsSustained_Should_NotStartTiming()
    {
        // 反复探测不得推进计时：起点始终不建立，否则"连按探测"就能把连续时长凑满
        var tracker = new SustainedDurationTracker();

        for (var t = TimeSpan.Zero; t <= TimeSpan.FromSeconds(20); t += TimeSpan.FromSeconds(5))
        {
            Assert.False(tracker.IsSustained(true, Now + t, Duration));
        }

        Assert.Equal(DateTime.MinValue, tracker.ConditionSinceUtc);
    }

    [Fact]
    public void IsSustained_Should_MatchUpdate_WithoutAdvancingTiming()
    {
        var tracker = new SustainedDurationTracker();
        Advance(tracker, true, Now, TimeSpan.FromSeconds(10));

        // 与"此刻调用 Update"的判定一致，但不推进 _lastUpdateUtc（因此可无限次重复探测）
        Assert.True(tracker.IsSustained(true, Now.AddSeconds(10), Duration));
        Assert.True(tracker.IsSustained(true, Now.AddSeconds(10), Duration));
        Assert.False(tracker.IsSustained(false, Now.AddSeconds(10), Duration));
    }

    [Fact]
    public void IsSustained_Should_RespectMaxGap()
    {
        var tracker = new SustainedDurationTracker { MaxGap = TimeSpan.FromSeconds(1) };
        tracker.Update(true, Now, Duration);

        // 距上次推进 5 秒（超过 MaxGap）⇒ 与 Update 同样视为中断
        Assert.False(tracker.IsSustained(true, Now.AddSeconds(5), Duration));
    }
}