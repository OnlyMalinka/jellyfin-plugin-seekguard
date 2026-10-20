using System;
using Jellyfin.Plugin.SeekGuard.Tracking;
using Xunit;

namespace Jellyfin.Plugin.SeekGuard.Tests;

public class PlaybackStateTests
{
    private static readonly GuardOptions _options = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
    private static readonly DateTime _t0 = new(2026, 1, 1, 20, 0, 0, DateTimeKind.Utc);

    private static long Sec(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    private static DateTime At(double seconds) => _t0.AddSeconds(seconds);

    [Fact]
    public void ContinuousPlaybackIsNormal()
    {
        var state = new PlaybackState(Sec(600), At(0));

        Assert.Equal(Observation.Normal, state.Observe(Sec(610), At(10), _options));
        Assert.Equal(Observation.Normal, state.Observe(Sec(620), At(20), _options));
        Assert.False(state.IsGuarding);
    }

    [Fact]
    public void PausedPlaybackIsNormal()
    {
        var state = new PlaybackState(Sec(600), At(0));

        Assert.Equal(Observation.Normal, state.Observe(Sec(600), At(300), _options));
    }

    [Fact]
    public void SmallSkipIsNotASeek()
    {
        var state = new PlaybackState(Sec(600), At(0));

        Assert.Equal(Observation.Normal, state.Observe(Sec(630), At(10), _options));
        Assert.Equal(Observation.Normal, state.Observe(Sec(605), At(11), _options));
    }

    [Fact]
    public void DoubleSpeedPlaybackIsNotASeek()
    {
        var state = new PlaybackState(Sec(600), At(0));

        Assert.Equal(Observation.Normal, state.Observe(Sec(620), At(10), _options));
    }

    [Fact]
    public void ForwardJumpStartsGuardFromPreviousPosition()
    {
        var state = new PlaybackState(Sec(600), At(0));

        Assert.Equal(Observation.GuardStarted, state.Observe(Sec(1800), At(5), _options));
        Assert.Equal(Sec(600), state.GuardedFromTicks);
    }

    [Fact]
    public void BackwardJumpStartsGuard()
    {
        var state = new PlaybackState(Sec(1800), At(0));

        Assert.Equal(Observation.GuardStarted, state.Observe(Sec(60), At(5), _options));
        Assert.Equal(Sec(1800), state.GuardedFromTicks);
    }

    [Fact]
    public void WatchingAfterSeekConfirmsIt()
    {
        var state = new PlaybackState(Sec(600), At(0));
        state.Observe(Sec(1800), At(5), _options);

        Assert.Equal(Observation.GuardHeld, state.Observe(Sec(1805), At(10), _options));
        Assert.Equal(Observation.GuardConfirmed, state.Observe(Sec(1815), At(20), _options));
        Assert.False(state.IsGuarding);
        Assert.Equal(Observation.Normal, state.Observe(Sec(1825), At(30), _options));
    }

    [Fact]
    public void PausingAfterSeekDoesNotConfirmIt()
    {
        var state = new PlaybackState(Sec(600), At(0));
        state.Observe(Sec(1800), At(5), _options);

        Assert.Equal(Observation.GuardHeld, state.Observe(Sec(1800), At(600), _options));
        Assert.True(state.IsGuarding);
    }

    [Fact]
    public void ScrubbingKeepsTheOriginalPositionAndRestartsTheCount()
    {
        var state = new PlaybackState(Sec(600), At(0));
        state.Observe(Sec(1800), At(5), _options);
        state.Observe(Sec(1808), At(13), _options);

        Assert.Equal(Observation.GuardExtended, state.Observe(Sec(300), At(15), _options));
        Assert.Equal(Sec(600), state.GuardedFromTicks);
        Assert.Equal(0, state.WatchedSinceSeekTicks);
        Assert.Equal(Observation.GuardHeld, state.Observe(Sec(305), At(20), _options));
    }

    [Fact]
    public void ScrubbingBackToTheStartingPointStillNeedsConfirmation()
    {
        var state = new PlaybackState(Sec(600), At(0));
        state.Observe(Sec(1800), At(5), _options);

        Assert.Equal(Observation.GuardExtended, state.Observe(Sec(602), At(8), _options));
        Assert.True(state.IsGuarding);
    }

    [Fact]
    public void IntentionalSkipDoesNotStartGuard()
    {
        var state = new PlaybackState(Sec(60), At(0));

        Assert.Equal(Observation.Normal, state.Observe(Sec(150), At(2), _options, (_, _) => true));
        Assert.False(state.IsGuarding);
        Assert.Equal(Sec(150), state.LastPositionTicks);
    }

    [Fact]
    public void IntentionalSkipDuringGuardDoesNotCountAsWatching()
    {
        var state = new PlaybackState(Sec(600), At(0));
        state.Observe(Sec(1800), At(5), _options);

        Assert.Equal(Observation.GuardHeld, state.Observe(Sec(1900), At(6), _options, (_, _) => true));
        Assert.Equal(0, state.WatchedSinceSeekTicks);
    }

    [Fact]
    public void ClockGoingBackwardsIsTolerated()
    {
        var state = new PlaybackState(Sec(600), At(10));

        Assert.Equal(Observation.Normal, state.Observe(Sec(605), At(0), _options));
    }
}
