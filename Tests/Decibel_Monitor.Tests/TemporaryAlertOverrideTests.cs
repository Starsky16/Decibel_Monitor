using System;
using Decibel_Monitor.Alerting;

namespace Decibel_Monitor.Tests;

public class TemporaryAlertOverrideTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsSilenced_Should_BeFalse_WhenNeverSet()
    {
        var sut = new TemporaryAlertOverride();

        Assert.False(sut.IsSilenced(Now));
        Assert.Equal(DateTime.MinValue, sut.SilenceUntilUtc);
    }

    [Fact]
    public void SilenceUntil_Should_Expire_WhenDeadlineReached()
    {
        var sut = new TemporaryAlertOverride();
        var until = Now.AddMinutes(45);

        sut.SilenceUntil(Now, until);

        Assert.True(sut.IsSilenced(Now));
        Assert.True(sut.IsSilenced(until.AddMilliseconds(-1)));
        // 到点自动失效：不需要任何定时器
        Assert.False(sut.IsSilenced(until));
        Assert.False(sut.IsSilenced(until.AddHours(1)));
    }

    [Fact]
    public void SilenceRemaining_Should_CountDown_AndBeZeroAfterDeadline()
    {
        var sut = new TemporaryAlertOverride();
        sut.SilenceUntil(Now, Now.AddMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(45), sut.SilenceRemaining(Now));
        Assert.Equal(TimeSpan.FromMinutes(15), sut.SilenceRemaining(Now.AddMinutes(30)));
        Assert.Equal(TimeSpan.Zero, sut.SilenceRemaining(Now.AddMinutes(45)));
    }

    [Fact]
    public void SilenceAndForceEnable_Should_BeMutuallyExclusive()
    {
        var sut = new TemporaryAlertOverride();

        sut.SilenceUntil(Now, Now.AddMinutes(30));
        sut.ForceEnableUntil(Now, Now.AddMinutes(30));

        // 后设置的"临时开启"清掉了静默
        Assert.False(sut.IsSilenced(Now));
        Assert.True(sut.IsForceEnabled(Now));
        Assert.Equal(DateTime.MinValue, sut.SilenceUntilUtc);

        sut.SilenceUntil(Now, Now.AddMinutes(30));

        // 反过来同样成立
        Assert.True(sut.IsSilenced(Now));
        Assert.False(sut.IsForceEnabled(Now));
        Assert.Equal(DateTime.MinValue, sut.ForceEnableUntilUtc);
    }

    [Fact]
    public void SilenceUntil_Should_ClampToMaxDuration()
    {
        var sut = new TemporaryAlertOverride();

        // 传入远超上限的截止时间（如算错的日终）按 24 小时夹取
        sut.SilenceUntil(Now, Now.AddDays(30));

        Assert.Equal(Now + TemporaryAlertOverride.MaxDuration, sut.SilenceUntilUtc);
        Assert.True(sut.IsSilenced(Now.AddHours(23)));
        Assert.False(sut.IsSilenced(Now.AddHours(24)));
    }

    [Fact]
    public void SilenceUntil_Should_EndImmediately_WhenDeadlineInPast()
    {
        var sut = new TemporaryAlertOverride();

        sut.SilenceUntil(Now, Now.AddMinutes(-5));

        Assert.False(sut.IsSilenced(Now));
    }

    [Fact]
    public void Clear_Should_ResetBothKinds()
    {
        var sut = new TemporaryAlertOverride();
        sut.SilenceUntil(Now, Now.AddHours(1));

        sut.Clear();

        Assert.False(sut.IsSilenced(Now));
        Assert.False(sut.IsForceEnabled(Now));
        Assert.Equal(DateTime.MinValue, sut.SilenceUntilUtc);
    }

    [Fact]
    public void ForceEnableUntil_Should_Expire_WhenDeadlineReached()
    {
        var sut = new TemporaryAlertOverride();
        var until = Now.AddMinutes(60);

        sut.ForceEnableUntil(Now, until);

        Assert.True(sut.IsForceEnabled(Now));
        Assert.False(sut.IsForceEnabled(until));
    }

    [Fact]
    public void EndOfLocalDay_Should_ReturnNextMidnight()
    {
        var local = new DateTime(2026, 1, 1, 14, 30, 0, DateTimeKind.Local);

        var end = TemporaryAlertOverride.EndOfLocalDay(local);

        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0), end);
        // .Date 保留输入的 Kind，便于调用方直接 ToUniversalTime
        Assert.Equal(DateTimeKind.Local, end.Kind);
    }
}