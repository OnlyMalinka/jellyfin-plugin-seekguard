using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SeekGuard;

/// <summary>
/// The parts of a user's play state that a seek can change.
/// </summary>
internal sealed record UserDataSnapshot(long PlaybackPositionTicks, bool Played, int PlayCount, DateTime? LastPlayedDate)
{
    public static UserDataSnapshot From(UserItemData data)
        => new(data.PlaybackPositionTicks, data.Played, data.PlayCount, data.LastPlayedDate);

    public bool Matches(UserItemData data)
        => data.PlaybackPositionTicks == PlaybackPositionTicks
            && data.Played == Played
            && data.PlayCount == PlayCount
            && data.LastPlayedDate == LastPlayedDate;

    public void ApplyTo(UserItemData data)
    {
        data.PlaybackPositionTicks = PlaybackPositionTicks;
        data.Played = Played;
        data.PlayCount = PlayCount;
        data.LastPlayedDate = LastPlayedDate;
    }
}
