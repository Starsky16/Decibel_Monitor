using System;
using System.Collections.Generic;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

/// <summary>
/// §24.3 窗口最后一个采样拍的边界（确定性单测，不依赖真实时钟、音频设备或 UI 线程）。
/// 采样节拍按运行时固定的 200 ms 建模；窗口 10 秒、冷却 1 分钟。
/// </summary>
public class HotkeyWindowBoundaryTests
{
    private const double Threshold = 100.0;
    private const string KeyName = "F5";
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private static AlertContext Context(double averageDb) => new(averageDb, averageDb);

    private static HotkeyConfirmDecisionSource CreateSource() =>
        new(Threshold, new HotkeyDefinition(KeyName, HotkeyModifiers.None), Window, Cooldown) { IsEnabled = true };

    private static void OpenWindow(HotkeyConfirmDecisionSource source)
    {
        var decision = source.Decide(Context(120.0), Now);

        Assert.False(decision.ShouldAlert);
        Assert.True(source.IsWindowOpen);
        Assert.Equal(Now + Window, source.WindowOpenUntilUtc);
    }

    // ---- 恰好落在窗口边界 ----

    [Fact]
    public void Decide_Should_KeepWindowOpen_ExactlyAtDeadline_WithoutConfirmation()
    {
        var source = CreateSource();
        OpenWindow(source);

        // 超时判定用严格大于：正好落在截止时间的那一拍仍属于窗口内
        var atDeadline = source.Decide(Context(120.0), Now + Window);

        Assert.False(atDeadline.ShouldAlert);
        Assert.True(source.IsWindowOpen);
        Assert.Equal(Now + Window, source.WindowOpenUntilUtc);
    }

    [Fact]
    public void HandleKeyPress_Should_AcceptKey_ExactlyAtDeadline()
    {
        var source = CreateSource();
        OpenWindow(source);

        var hit = source.HandleKeyPress(KeyName, HotkeyModifiers.None, Now + Window);

        Assert.True(hit);
        Assert.True(source.WouldAlert(Context(120.0), Now + Window));
    }

    [Fact]
    public void Decide_Should_Alert_WhenKeyConfirmedExactlyAtDeadline_IsConsumedInSameTick()
    {
        var source = CreateSource();
        OpenWindow(source);
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, Now + Window));

        var decision = source.Decide(Context(120.0), Now + Window);

        Assert.True(decision.ShouldAlert);
        Assert.False(source.IsWindowOpen);
        Assert.Equal(Now + Window + Cooldown, source.NextAlertTimeUtc);
    }

    // ---- 收尾拍确认跨过截止时间 ----

    [Fact]
    public void Decide_Should_Alert_WhenKeyConfirmedExactlyAtDeadline_IsConsumedOneTickLater()
    {
        var source = CreateSource();
        OpenWindow(source);
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, Now + Window));

        // 当拍按键落在边界内，但消费该确认的下一拍已经越过截止时间：必须兑现，不能当成超时丢掉
        var consumeAt = Now + Window + SampleInterval;
        var decision = source.Decide(Context(120.0), consumeAt);

        Assert.True(decision.ShouldAlert);
        Assert.False(source.IsWindowOpen);
        Assert.Equal(consumeAt + Cooldown, source.NextAlertTimeUtc);
        Assert.True(source.IsCoolingDown(consumeAt + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Decide_Should_Alert_WhenKeyPressedLessThanOneSampleIntervalBeforeDeadline()
    {
        var source = CreateSource();
        OpenWindow(source);

        // 剩余不足一拍（100 ms < 200 ms）时按下，消费拍仍可能在截止时间之后
        var pressAt = Now + Window - TimeSpan.FromMilliseconds(100);
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, pressAt));

        var consumeAt = Now + Window + SampleInterval;
        var decision = source.Decide(Context(120.0), consumeAt);

        Assert.True(decision.ShouldAlert);
        Assert.Equal(consumeAt + Cooldown, source.NextAlertTimeUtc);
    }

    // ---- 多一拍 ----

    [Fact]
    public void HandleKeyPress_Should_RejectKey_OneSampleIntervalAfterDeadline()
    {
        var source = CreateSource();
        OpenWindow(source);

        var hit = source.HandleKeyPress(KeyName, HotkeyModifiers.None, Now + Window + SampleInterval);

        Assert.False(hit);
        Assert.True(source.IsWindowOpen); // 按键被拒本身不推进窗口状态，超时由 Decide 判定
    }

    [Fact]
    public void Decide_Should_TimeoutIntoCooldown_OneSampleIntervalAfterDeadline_WhenNoConfirmation()
    {
        var source = CreateSource();
        OpenWindow(source);

        var timeoutAt = Now + Window + SampleInterval;
        var decision = source.Decide(Context(120.0), timeoutAt);

        Assert.False(decision.ShouldAlert);
        Assert.False(source.IsWindowOpen);
        Assert.Equal(timeoutAt + Cooldown, source.NextAlertTimeUtc);
        Assert.True(source.IsCoolingDown(timeoutAt + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Coordinator_Should_Notify_WhenLastTickConfirmation_IsConsumedOneTickAfterDeadline()
    {
        var source = CreateSource();
        var coordinator = new AlertDecisionCoordinator(new[] { source });
        var notified = new List<string>();
        coordinator.NotifyRequested += notified.Add;

        // 第一拍无确认：仲裁让源 Observe 推进并开窗，不产生通知
        Assert.False(coordinator.Decide(Context(120.0), Now).ShouldAlert);

        // 按键精确落在窗口边界
        Assert.True(source.HandleKeyPress(KeyName, HotkeyModifiers.None, Now + Window));

        // 下一拍越过截止时间：仍应被仲裁选中并经由 NotifyRequested 发出提醒
        var decision = coordinator.Decide(Context(120.0), Now + Window + SampleInterval);

        Assert.True(decision.ShouldAlert);
        Assert.Equal(HotkeyConfirmDecisionSource.SourceId, decision.TriggerSourceId);
        Assert.Equal(new[] { HotkeyConfirmDecisionSource.SourceId }, notified);
    }
}