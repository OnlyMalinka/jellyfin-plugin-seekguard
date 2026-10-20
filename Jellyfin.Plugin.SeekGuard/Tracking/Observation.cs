namespace Jellyfin.Plugin.SeekGuard.Tracking;

/// <summary>
/// The effect of a position report on a <see cref="PlaybackState"/>.
/// </summary>
public enum Observation
{
    /// <summary>Continuous playback, nothing is guarded.</summary>
    Normal,

    /// <summary>A seek was detected; the pre-seek resume point must be kept.</summary>
    GuardStarted,

    /// <summary>Another seek happened while a previous one was still unconfirmed.</summary>
    GuardExtended,

    /// <summary>Playback continues after a seek, but not long enough to trust it yet.</summary>
    GuardHeld,

    /// <summary>Enough was watched after the seek; the new position may be saved.</summary>
    GuardConfirmed,
}
