using System;
using System.Collections.Generic;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

public class AlertDecisionCoordinatorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly AlertContext Context = new(120.0, 120.0);

    private sealed class FakeSource : IAlertDecisionSource
    {
        public FakeSource(string id, bool shouldAlert, bool enabled = true)
        {
            Id = id;
            ShouldAlert = shouldAlert;
            IsEnabled = enabled;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public string Description => Id;

        public bool IsEnabled { get; set; }

        public bool ShouldAlert { get; set; }

        public bool IsCoolingDownValue { get; set; }

        public TimeSpan Cooldown => TimeSpan.Zero;

        public int DecideCount { get; private set; }

        public int WouldAlertCount { get; private set; }

        public int ObserveCount { get; private set; }

        /// <summary>本对象收到的调用序列（"probe" / "observe" / "decide"），用于验证两段式的调用顺序。</summary>
        public List<string> Calls { get; } = new();

        public bool IsCoolingDown(DateTime nowUtc) => IsCoolingDownValue;

        public bool WouldAlert(AlertContext context, DateTime nowUtc)
        {
            WouldAlertCount++;
            Calls.Add("probe");
            return IsEnabled && ShouldAlert;
        }

        public AlertDecision Observe(AlertContext context, DateTime nowUtc)
        {
            ObserveCount++;
            Calls.Add("observe");
            return IsEnabled ? new AlertDecision(false, ShouldAlert) : default;
        }

        public AlertDecision Decide(AlertContext context, DateTime nowUtc)
        {
            DecideCount++;
            Calls.Add("decide");
            return IsEnabled ? new AlertDecision(ShouldAlert, ShouldAlert) : default;
        }
    }

    [Fact]
    public void Constructor_Should_Throw_WhenNoSourceProvided()
    {
        Assert.Throws<InvalidOperationException>(() => new AlertDecisionCoordinator(Array.Empty<IAlertDecisionSource>()));
    }

    [Fact]
    public void Decide_Should_Alert_ByPriorityOrder()
    {
        var low = new FakeSource("low", shouldAlert: true);
        var high = new FakeSource("high", shouldAlert: true);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });

        var decision = coordinator.Decide(Context, Now);

        Assert.True(decision.ShouldAlert);
        Assert.Equal("high", decision.TriggerSourceId);
    }

    [Fact]
    public void Decide_Should_FallBack_ToLowerPriority_WhenHigherDoesNotAlert()
    {
        var low = new FakeSource("low", shouldAlert: true);
        var high = new FakeSource("high", shouldAlert: false);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });

        var decision = coordinator.Decide(Context, Now);

        Assert.True(decision.ShouldAlert);
        Assert.Equal("low", decision.TriggerSourceId);
    }

    [Fact]
    public void Decide_Should_AdvanceAllSources_EvenAfterOneAlerts()
    {
        // 非目标判定源也必须在每个周期推进一次（改走 Observe），否则其内部窗口/冷却状态机无法推进
        var low = new FakeSource("low", shouldAlert: false);
        var high = new FakeSource("high", shouldAlert: true);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });

        coordinator.Decide(Context, Now);

        Assert.Equal(1, high.DecideCount);
        Assert.Equal(0, low.DecideCount);
        Assert.Equal(1, low.ObserveCount);
    }

    [Fact]
    public void Decide_Should_ProbeAllSources_BeforeAnyStateIsAdvanced()
    {
        // 两段式：先在一次完整的探测里选出目标源，目标源随后才 Decide，其余源 Observe。
        // 若边决定边探测，前面源的状态落地会改变后面源的条件，优先级配置会失去意义。
        var low = new FakeSource("low", shouldAlert: false);
        var high = new FakeSource("high", shouldAlert: true);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });

        coordinator.Decide(Context, Now);

        Assert.Equal(new[] { "probe", "decide" }, high.Calls);
        Assert.Equal(new[] { "observe" }, low.Calls);
        // 命中目标后不再探测低优先级源
        Assert.Equal(0, low.WouldAlertCount);
    }

    [Fact]
    public void Decide_Should_RaiseNotifyRequested_OnlyOnce_WhenBothSourcesWouldAlert()
    {
        // "强制只发一条"：未被选中的源走 Observe——推进本拍状态但不提醒，
        // 也不会在下一拍补发第二条（其本次条件按"已给过一次机会"落地）
        var low = new FakeSource("low", shouldAlert: true);
        var high = new FakeSource("high", shouldAlert: true);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;

        var decision = coordinator.Decide(Context, Now);

        Assert.Equal(new[] { "high" }, notified);
        Assert.Equal("high", decision.TriggerSourceId);
        Assert.Equal(0, low.DecideCount);
        Assert.Equal(1, low.ObserveCount);
    }

    [Fact]
    public void Decide_Should_ObserveDisabledSource_WithoutAlerting()
    {
        var disabled = new FakeSource("a", shouldAlert: true, enabled: false);
        var coordinator = new AlertDecisionCoordinator(new[] { disabled });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;

        var decision = coordinator.Decide(Context, Now);

        Assert.False(decision.ShouldAlert);
        Assert.Empty(notified);
        Assert.Equal(0, disabled.DecideCount);
        Assert.Equal(1, disabled.ObserveCount);
    }

    [Fact]
    public void Decide_Should_NotAlert_WhenNoSourceTriggers()
    {
        var coordinator = new AlertDecisionCoordinator(new[] { new FakeSource("a", shouldAlert: false) });

        var decision = coordinator.Decide(Context, Now);

        Assert.False(decision.ShouldAlert);
        Assert.Null(decision.TriggerSourceId);
    }

    [Fact]
    public void Decide_Should_IgnoreDisabledSource()
    {
        var coordinator = new AlertDecisionCoordinator(new[] { new FakeSource("a", shouldAlert: true, enabled: false) });

        var decision = coordinator.Decide(Context, Now);

        Assert.False(decision.ShouldAlert);
    }

    [Fact]
    public void Decide_Should_RaiseNotifyRequested_OnlyWhenAlerting()
    {
        var coordinator = new AlertDecisionCoordinator(new[] { new FakeSource("a", shouldAlert: true) });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;

        coordinator.Decide(Context, Now);
        Assert.Equal(new[] { "a" }, notified);
    }

    [Fact]
    public void Decide_Should_NotRaiseNotifyRequested_WhenNotAlerting()
    {
        var coordinator = new AlertDecisionCoordinator(new[] { new FakeSource("a", shouldAlert: false) });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;

        coordinator.Decide(Context, Now);

        Assert.Empty(notified);
    }

    [Fact]
    public void Sources_Should_AppendUnlistedSources_AfterPriorityOrder()
    {
        var a = new FakeSource("a", shouldAlert: false);
        var b = new FakeSource("b", shouldAlert: false);
        var c = new FakeSource("c", shouldAlert: false);
        var coordinator = new AlertDecisionCoordinator(new[] { a, b, c }, new[] { "c", "a" });

        Assert.Equal(new[] { "c", "a", "b" }, new[] { coordinator.Sources[0].Id, coordinator.Sources[1].Id, coordinator.Sources[2].Id });
    }

    [Fact]
    public void Sources_Should_KeepGivenOrder_WhenNoPriorityConfigured()
    {
        var a = new FakeSource("a", shouldAlert: false);
        var b = new FakeSource("b", shouldAlert: false);
        var coordinator = new AlertDecisionCoordinator(new[] { a, b });

        Assert.Equal(new[] { "a", "b" }, new[] { coordinator.Sources[0].Id, coordinator.Sources[1].Id });
    }

    [Fact]
    public void ApplyPriorityOrder_Should_ReorderSources_AndTakeEffectOnNextDecide()
    {
        var low = new FakeSource("low", shouldAlert: true);
        var high = new FakeSource("high", shouldAlert: true);
        var coordinator = new AlertDecisionCoordinator(new[] { low, high }, new[] { "high", "low" });

        coordinator.ApplyPriorityOrder(new[] { "low", "high" });

        Assert.Equal(new[] { "low", "high" }, new[] { coordinator.Sources[0].Id, coordinator.Sources[1].Id });
        Assert.Equal("low", coordinator.Decide(Context, Now).TriggerSourceId);
    }

    [Fact]
    public void ApplyPriorityOrder_Should_IgnoreEmptyOrder()
    {
        var a = new FakeSource("a", shouldAlert: false);
        var b = new FakeSource("b", shouldAlert: false);
        var coordinator = new AlertDecisionCoordinator(new[] { a, b }, new[] { "b", "a" });

        coordinator.ApplyPriorityOrder(Array.Empty<string>());
        coordinator.ApplyPriorityOrder(null);

        Assert.Equal(new[] { "b", "a" }, new[] { coordinator.Sources[0].Id, coordinator.Sources[1].Id });
    }

    [Fact]
    public void ApplyPriorityOrder_Should_AppendUnlistedSources()
    {
        var a = new FakeSource("a", shouldAlert: false);
        var b = new FakeSource("b", shouldAlert: false);
        var c = new FakeSource("c", shouldAlert: false);
        var coordinator = new AlertDecisionCoordinator(new[] { a, b, c });

        coordinator.ApplyPriorityOrder(new[] { "b" });

        Assert.Equal(new[] { "b", "a", "c" }, new[] { coordinator.Sources[0].Id, coordinator.Sources[1].Id, coordinator.Sources[2].Id });
    }
}
