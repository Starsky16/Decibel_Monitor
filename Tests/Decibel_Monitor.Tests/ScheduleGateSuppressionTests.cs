using System;
using System.Collections.Generic;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

/// <summary>
/// §24.4 闸门抑制分支（确定性单测）。
/// AlertRuntimeService 依赖 Avalonia / 宿主服务，未编入测试项目；因此这里用运行时同款的纯逻辑组件
/// （ScheduleGate + HotkeyConfirmDecisionSource + AlertDecisionCoordinator）覆盖抑制分支的两个关键契约：
/// 抑制期作废待兑现确认且不写冷却；闸门解除后重开完整窗口并恢复正常提醒。
/// </summary>
public class ScheduleGateSuppressionTests
{
    private const double Threshold = 100.0;
    private const string KeyName = "F5";
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);
    private static readonly AlertContext Context = new(120.0, 120.0);

    private static HotkeyConfirmDecisionSource CreateSource() =>
        new(Threshold, new HotkeyDefinition(KeyName, HotkeyModifiers.None), Window, Cooldown) { IsEnabled = true };

    private static bool BreakSuppressed(ScheduleGate gate, DateTime nowUtc) =>
        gate.Update(
            SchedulePhase.Breaking,
            isClassPlanEnabled: true,
            isClassPlanLoaded: true,
            suppressDuringBreak: true,
            classStartProtectionMinutes: 3,
            nowUtc);

    private static bool Normal(ScheduleGate gate, DateTime nowUtc) =>
        gate.Update(
            SchedulePhase.Other,
            isClassPlanEnabled: true,
            isClassPlanLoaded: true,
            suppressDuringBreak: true,
            classStartProtectionMinutes: 3,
            nowUtc);

    [Fact]
    public void Suppression_Should_CancelPendingConfirmation_WithoutWritingCooldown()
    {
        var source = CreateSource();
        var coordinator = new AlertDecisionCoordinator(new[] { source });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;
        var gate = new ScheduleGate();

        var t0 = Now;
        Assert.False(coordinator.Decide(Context, t0).ShouldAlert); // 开窗
        Assert.True(source.IsWindowOpen);

        var t1 = t0 + TimeSpan.FromSeconds(1);
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, t1));
        Assert.True(source.WouldAlert(Context, t1));

        // 下一拍进入课间：运行时跳过 Decide，并调用 CancelWindow（见 AlertRuntimeService.cs:224-238）
        var t2 = t1 + TimeSpan.FromMilliseconds(200);
        Assert.True(BreakSuppressed(gate, t2));
        source.CancelWindow();

        Assert.False(source.IsWindowOpen);
        Assert.False(source.WouldAlert(Context, t2));
        Assert.Equal(DateTime.MinValue, source.WindowOpenUntilUtc);
        Assert.Equal(DateTime.MinValue, source.NextAlertTimeUtc);
        Assert.False(source.IsCoolingDown(t2));
        Assert.Empty(notified);
    }

    [Fact]
    public void Suppression_Should_ReopenFreshWindow_And_Alert_AfterGateLifted()
    {
        var source = CreateSource();
        var coordinator = new AlertDecisionCoordinator(new[] { source });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;
        var gate = new ScheduleGate();

        var t0 = Now;
        coordinator.Decide(Context, t0); // 开窗
        source.HandleKeyPress(KeyName, HotkeyModifiers.None, t0 + TimeSpan.FromSeconds(1));

        // 抑制期：作废窗口与已命中确认；本拍不推进判定源，也不发通知
        var breakAt = t0 + TimeSpan.FromSeconds(2);
        Assert.True(BreakSuppressed(gate, breakAt));
        source.CancelWindow();
        Assert.Empty(notified);

        // 闸门持续抑制：源保持非开窗、非冷却
        var stillBreak = breakAt + TimeSpan.FromMinutes(5);
        Assert.True(BreakSuppressed(gate, stillBreak));
        Assert.False(source.IsWindowOpen);
        Assert.False(source.IsCoolingDown(stillBreak));

        // 闸门解除：下一拍恢复 Decide，先重开一个完整时长的窗口（仍需按键确认，不会补发旧确认）
        var liftedAt = stillBreak + TimeSpan.FromMilliseconds(200);
        Assert.False(Normal(gate, liftedAt));
        var afterLift = coordinator.Decide(Context, liftedAt);

        Assert.False(afterLift.ShouldAlert);
        Assert.True(source.IsWindowOpen);
        Assert.Equal(liftedAt + Window, source.WindowOpenUntilUtc);
        Assert.Empty(notified);

        // 新窗口内确认 → 恢复正常提醒
        var confirmedAt = liftedAt + TimeSpan.FromSeconds(1);
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, confirmedAt));
        var recovered = coordinator.Decide(Context, confirmedAt);

        Assert.True(recovered.ShouldAlert);
        Assert.Equal(HotkeyConfirmDecisionSource.SourceId, recovered.TriggerSourceId);
        Assert.Equal(new[] { HotkeyConfirmDecisionSource.SourceId }, notified);
        Assert.True(source.IsCoolingDown(confirmedAt + TimeSpan.FromTicks(1)));
    }
}