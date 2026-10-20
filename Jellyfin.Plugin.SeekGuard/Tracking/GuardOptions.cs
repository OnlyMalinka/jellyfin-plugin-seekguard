using System;

namespace Jellyfin.Plugin.SeekGuard.Tracking;

/// <summary>
/// Thresholds used to tell a seek from normal playback.
/// </summary>
/// <param name="MinJump">The smallest position change that counts as a seek.</param>
/// <param name="ConfirmAfter">How much must be played after a seek before the new position is trusted.</param>
/// <param name="MaxPlaybackRate">The fastest playback speed to tolerate without calling it a seek.</param>
public sealed record GuardOptions(TimeSpan MinJump, TimeSpan ConfirmAfter, double MaxPlaybackRate = 2.0);
