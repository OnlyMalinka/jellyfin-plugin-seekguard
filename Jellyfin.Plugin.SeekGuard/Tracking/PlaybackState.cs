using System;

namespace Jellyfin.Plugin.SeekGuard.Tracking;

/// <summary>
/// Tracks the reported position of one item in one playback session and decides whether
/// the saved resume point must be protected from a recent seek.
/// </summary>
/// <remarks>
/// The class is pure logic with no Jellyfin dependencies and is not thread-safe; callers serialize access.
/// </remarks>
public sealed class PlaybackState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackState"/> class.
    /// </summary>
    /// <param name="positionTicks">The first known playback position.</param>
    /// <param name="timestampUtc">When that position was reported.</param>
    public PlaybackState(long positionTicks, DateTime timestampUtc)
    {
        LastPositionTicks = positionTicks;
        LastSeenUtc = timestampUtc;
    }

    /// <summary>Gets the last reported position.</summary>
    public long LastPositionTicks { get; private set; }

    /// <summary>Gets the time of the last report.</summary>
    public DateTime LastSeenUtc { get; private set; }

    /// <summary>Gets the position playback was at before the unconfirmed seek, if one is pending.</summary>
    public long? GuardedFromTicks { get; private set; }

    /// <summary>Gets how much content has been played since the last unconfirmed seek.</summary>
    public long WatchedSinceSeekTicks { get; private set; }

    /// <summary>Gets a value indicating whether a seek is pending confirmation.</summary>
    public bool IsGuarding => GuardedFromTicks.HasValue;

    /// <summary>
    /// Feeds a new position report into the state.
    /// </summary>
    /// <param name="positionTicks">The reported position.</param>
    /// <param name="nowUtc">The time of the report.</param>
    /// <param name="options">Detection thresholds.</param>
    /// <param name="isIntentionalSkip">Optional predicate (from, to) that marks a jump as a deliberate skip.</param>
    /// <returns>What the report changed.</returns>
    public Observation Observe(long positionTicks, DateTime nowUtc, GuardOptions options, Func<long, long, bool>? isIntentionalSkip = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var elapsed = nowUtc - LastSeenUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var from = LastPositionTicks;
        var delta = positionTicks - from;
        var maxPlayed = (long)(elapsed.Ticks * options.MaxPlaybackRate);
        var isJump = delta <= -options.MinJump.Ticks || delta >= maxPlayed + options.MinJump.Ticks;

        LastPositionTicks = positionTicks;
        LastSeenUtc = nowUtc;

        if (isJump && isIntentionalSkip?.Invoke(from, positionTicks) == true)
        {
            // A segment skip is deliberate: it neither starts a guard nor counts as watching.
            return IsGuarding ? Observation.GuardHeld : Observation.Normal;
        }

        if (isJump)
        {
            WatchedSinceSeekTicks = 0;
            if (IsGuarding)
            {
                return Observation.GuardExtended;
            }

            GuardedFromTicks = from;
            return Observation.GuardStarted;
        }

        if (!IsGuarding)
        {
            return Observation.Normal;
        }

        WatchedSinceSeekTicks += Math.Clamp(delta, 0, maxPlayed);
        if (WatchedSinceSeekTicks >= options.ConfirmAfter.Ticks)
        {
            GuardedFromTicks = null;
            WatchedSinceSeekTicks = 0;
            return Observation.GuardConfirmed;
        }

        return Observation.GuardHeld;
    }
}
