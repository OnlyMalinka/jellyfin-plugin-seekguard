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
    private static readonly long _resumeEarlyTolerance = TimeSpan.FromSeconds(5).Ticks;

    private long? _awaitingResumeTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackState"/> class.
    /// </summary>
    /// <param name="positionTicks">The first known playback position.</param>
    /// <param name="timestampUtc">When that position was reported.</param>
    /// <param name="resumeTicks">The resume point saved before playback started, if any.</param>
    public PlaybackState(long positionTicks, DateTime timestampUtc, long? resumeTicks = null)
    {
        LastPositionTicks = positionTicks;
        LastSeenUtc = timestampUtc;

        // Starting away from the saved resume point is treated like a seek. Some clients start at 0 and seek to
        // the resume point only after the player is ready; others start from a position they cached long ago.
        if (resumeTicks is long resume && Math.Abs(positionTicks - resume) > _resumeEarlyTolerance)
        {
            _awaitingResumeTicks = resumeTicks;
        }
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

    /// <summary>Gets a value indicating whether the client has not reached the saved resume point yet.</summary>
    public bool IsAwaitingResume => _awaitingResumeTicks.HasValue;

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

        if (_awaitingResumeTicks is long resume)
        {
            if (positionTicks >= resume - _resumeEarlyTolerance && positionTicks <= resume + options.MinJump.Ticks)
            {
                _awaitingResumeTicks = null;
                WatchedSinceSeekTicks = 0;
                return Observation.Resumed;
            }

            if (isJump)
            {
                // Seeking elsewhere before the resume point was reached: guard the resume point itself.
                _awaitingResumeTicks = null;
                from = resume;
            }
            else
            {
                // Watching from where the client started instead of the resume point: trust it once watched.
                WatchedSinceSeekTicks += Math.Clamp(delta, 0, maxPlayed);
                if (WatchedSinceSeekTicks < options.ConfirmAfter.Ticks)
                {
                    return Observation.AwaitingResume;
                }

                _awaitingResumeTicks = null;
                WatchedSinceSeekTicks = 0;
                return Observation.GuardConfirmed;
            }
        }

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
