using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeekGuard.Configuration;
using Jellyfin.Plugin.SeekGuard.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeekGuard;

/// <summary>
/// Watches playback reports and keeps the pre-seek resume point until the new position has been watched.
/// </summary>
/// <remarks>
/// Jellyfin saves the reported position before it raises <see cref="ISessionManager.PlaybackProgress"/>
/// (synchronously) and <see cref="ISessionManager.PlaybackStopped"/> (queued). Writing the pre-seek state back
/// from those handlers keeps the database on the safe position at all times, so it survives clients that are
/// killed without reporting a stop.
/// </remarks>
public sealed class SeekGuardService : IHostedService
{
    private static readonly TimeSpan _segmentStartSlack = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _segmentEndSlack = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _staleAfter = TimeSpan.FromHours(12);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILogger<SeekGuardService> _logger;
    private readonly ConcurrentDictionary<string, TrackedPlayback> _playbacks = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SeekGuardService"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSegmentManager">The media segment manager.</param>
    /// <param name="logger">The logger.</param>
    public SeekGuardService(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        ILogger<SeekGuardService> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.SessionEnded += OnSessionEnded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
        _playbacks.Clear();
        return Task.CompletedTask;
    }

    private static GuardOptions CreateOptions(PluginConfiguration config)
        => new(TimeSpan.FromSeconds(Math.Max(1, config.MinJumpSeconds)), TimeSpan.FromSeconds(Math.Max(1, config.ConfirmSeconds)));

    private static string GetKey(SessionInfo session, BaseItem item) => session.Id + ":" + item.Id.ToString("N");

    // Mirrors SessionManager.GetProgressItem: progress of an alternate version is stored on that version.
    private static BaseItem GetProgressItem(BaseItem libraryItem, string? mediaSourceId)
    {
        if (libraryItem is Video video
            && Guid.TryParse(mediaSourceId, out var mediaSourceItemId)
            && !mediaSourceItemId.Equals(video.Id))
        {
            return video.GetAlternateVersion(mediaSourceItemId) ?? libraryItem;
        }

        return libraryItem;
    }

    private static bool IsTracked(PlaybackProgressEventArgs e, PluginConfiguration config)
        => config.Enabled
            && e.Session is not null
            && e.Item is not null
            && e.Item.SupportsPositionTicksResume
            && e.Users is { Count: > 0 };

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (!IsTracked(e, Config))
            {
                return;
            }

            RemoveStale();

            var item = GetProgressItem(e.Item, e.MediaSourceId);
            var playback = _playbacks.GetOrAdd(
                GetKey(e.Session, item),
                _ => CreatePlayback(item, e.Users, e.PlaybackPositionTicks ?? 0));

            if (Config.TrustSegmentSkips)
            {
                var segments = await _mediaSegmentManager
                    .GetSegmentsAsync(e.Item, null, _libraryManager.GetLibraryOptions(e.Item), false)
                    .ConfigureAwait(false);
                playback.Segments = segments.ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle playback start");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Config;
            if (e.IsAutomated || !IsTracked(e, config) || e.PlaybackPositionTicks is not long position)
            {
                return;
            }

            var item = GetProgressItem(e.Item, e.MediaSourceId);
            var key = GetKey(e.Session, item);
            if (!_playbacks.TryGetValue(key, out var playback))
            {
                // Progress arrived before the queued start event: start tracking from here.
                _playbacks.TryAdd(key, CreatePlayback(item, e.Users, position));
                return;
            }

            lock (playback)
            {
                var observation = playback.State.Observe(position, DateTime.UtcNow, CreateOptions(config), playback.IsSegmentSkip);
                switch (observation)
                {
                    case Observation.Normal:
                        playback.TakeSnapshots(_userDataManager);
                        break;
                    case Observation.GuardConfirmed:
                        _logger.LogInformation(
                            "Seek Guard: seek in '{Item}' confirmed by watching, saving {Position}",
                            item.Name,
                            TimeSpan.FromTicks(position));
                        playback.TakeSnapshots(_userDataManager);
                        break;
                    case Observation.GuardStarted:
                        _logger.LogInformation(
                            "Seek Guard: seek in '{Item}' from {From} to {To}, keeping the old resume point until it is watched",
                            item.Name,
                            TimeSpan.FromTicks(playback.State.GuardedFromTicks ?? 0),
                            TimeSpan.FromTicks(position));
                        Restore(playback);
                        break;
                    default:
                        Restore(playback);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle playback progress");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var config = Config;
            if (!IsTracked(e, config))
            {
                return;
            }

            var item = GetProgressItem(e.Item, e.MediaSourceId);
            if (!_playbacks.TryRemove(GetKey(e.Session, item), out var playback))
            {
                return;
            }

            lock (playback)
            {
                // The stop report itself may carry a seek that no progress report has shown yet.
                if (e.PlaybackPositionTicks is long position)
                {
                    playback.State.Observe(position, DateTime.UtcNow, CreateOptions(config), playback.IsSegmentSkip);
                }

                if (playback.State.IsGuarding)
                {
                    LogRestore(playback, "playback stopped");
                    Restore(playback);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle playback stop");
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        try
        {
            var prefix = e.SessionInfo.Id + ":";
            foreach (var key in _playbacks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (_playbacks.TryRemove(key, out var playback))
                {
                    lock (playback)
                    {
                        if (playback.State.IsGuarding)
                        {
                            LogRestore(playback, "session ended");
                            Restore(playback);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle session end");
        }
    }

    private TrackedPlayback CreatePlayback(BaseItem item, IReadOnlyList<User> users, long positionTicks)
    {
        var playback = new TrackedPlayback(item, users.ToArray(), new PlaybackState(positionTicks, DateTime.UtcNow));
        playback.TakeSnapshots(_userDataManager);
        return playback;
    }

    private void Restore(TrackedPlayback playback)
    {
        foreach (var user in playback.Users)
        {
            if (!playback.Snapshots.TryGetValue(user.Id, out var snapshot))
            {
                continue;
            }

            var data = _userDataManager.GetUserData(user, playback.Item);
            if (data is null || snapshot.Matches(data))
            {
                continue;
            }

            snapshot.ApplyTo(data);
            _userDataManager.SaveUserData(user, playback.Item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
    }

    private void LogRestore(TrackedPlayback playback, string reason)
    {
        var position = playback.Snapshots.Values.FirstOrDefault()?.PlaybackPositionTicks ?? 0;
        _logger.LogInformation(
            "Seek Guard: {Reason} right after a seek in '{Item}', resume point kept at {Position}",
            reason,
            playback.Item.Name,
            TimeSpan.FromTicks(position));
    }

    private void RemoveStale()
    {
        var threshold = DateTime.UtcNow - _staleAfter;
        foreach (var pair in _playbacks.Where(p => p.Value.State.LastSeenUtc < threshold))
        {
            _playbacks.TryRemove(pair.Key, out _);
        }
    }

    private sealed class TrackedPlayback
    {
        public TrackedPlayback(BaseItem item, IReadOnlyList<User> users, PlaybackState state)
        {
            Item = item;
            Users = users;
            State = state;
        }

        public BaseItem Item { get; }

        public IReadOnlyList<User> Users { get; }

        public PlaybackState State { get; }

        public Dictionary<Guid, UserDataSnapshot> Snapshots { get; } = new();

        public IReadOnlyList<MediaSegmentDto> Segments { get; set; } = [];

        public void TakeSnapshots(IUserDataManager userDataManager)
        {
            foreach (var user in Users)
            {
                var data = userDataManager.GetUserData(user, Item);
                if (data is not null)
                {
                    Snapshots[user.Id] = UserDataSnapshot.From(data);
                }
            }
        }

        // A forward jump that starts inside an intro/recap/credits segment and lands past its end is a skip button.
        public bool IsSegmentSkip(long fromTicks, long toTicks)
            => toTicks > fromTicks
                && Segments.Any(s => fromTicks >= s.StartTicks - _segmentStartSlack.Ticks
                    && fromTicks <= s.EndTicks
                    && toTicks >= s.EndTicks - _segmentEndSlack.Ticks);
    }
}
