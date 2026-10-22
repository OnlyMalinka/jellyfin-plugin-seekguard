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
    private static readonly TimeSpan _staleAfter = TimeSpan.FromDays(3);
    private static readonly TimeSpan _startSnapshotLifetime = TimeSpan.FromMinutes(2);

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILogger<SeekGuardService> _logger;

    // Keyed by user, device and item rather than by session: sessions are replaced when a client reconnects,
    // but a device that wakes up still reports the playback it had before.
    private readonly ConcurrentDictionary<string, TrackedPlayback> _playbacks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), (UserDataSnapshot Snapshot, DateTime SavedUtc)> _startSnapshots = new();

    // The play state this plugin last accepted for each user and item, and which device produced it.
    private readonly ConcurrentDictionary<(Guid UserId, Guid ItemId), AcceptedState> _accepted = new();

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
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.SessionEnded -= OnSessionEnded;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _playbacks.Clear();
        _startSnapshots.Clear();
        _accepted.Clear();
        return Task.CompletedTask;
    }

    private static GuardOptions CreateOptions(PluginConfiguration config)
        => new(TimeSpan.FromSeconds(Math.Max(1, config.MinJumpSeconds)), TimeSpan.FromSeconds(Math.Max(1, config.ConfirmSeconds)));

    private static string GetDeviceKey(SessionInfo session)
        => session.UserId.ToString("N") + ":" + (string.IsNullOrEmpty(session.DeviceId) ? session.Id : session.DeviceId);

    private static string GetKey(string deviceKey, BaseItem item) => deviceKey + ":" + item.Id.ToString("N");

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

    // Jellyfin saves the play state when playback starts, before any progress report. Its position is still the
    // resume point from the previous playback, whereas the first progress reports of clients that start at 0 save 0.
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason == UserDataSaveReason.PlaybackStart && e.Item is not null && e.UserData is not null)
        {
            _startSnapshots[(e.UserId, e.Item.Id)] = (UserDataSnapshot.From(e.UserData), DateTime.UtcNow);
        }
    }

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
            var deviceKey = GetDeviceKey(e.Session);
            var position = e.PlaybackPositionTicks ?? 0;

            // A start begins a new playback, unless progress for this very playback overtook the queued start event.
            var playback = _playbacks.AddOrUpdate(
                GetKey(deviceKey, item),
                _ => CreatePlayback(item, e.Users, e.Session, deviceKey, position),
                (_, existing) => existing.WaitingForStart ? existing : CreatePlayback(item, e.Users, e.Session, deviceKey, position));
            playback.WaitingForStart = false;

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
            var deviceKey = GetDeviceKey(e.Session);
            var key = GetKey(deviceKey, item);
            _playbacks.TryGetValue(key, out var existing);

            if (FromOtherDevice(existing, e.Users, item, deviceKey) is { } accepted)
            {
                // This device reports a playback it had before another device moved on: compare against that.
                var replacement = CreateFromAccepted(item, e.Users, e.Session, deviceKey, accepted);
                _playbacks[key] = replacement;
                lock (replacement)
                {
                    HandleProgress(replacement, position, config);
                }

                return;
            }

            if (existing is null)
            {
                // Progress arrived before the queued start event: start tracking from here.
                var playback = _playbacks.GetOrAdd(key, _ => CreatePlayback(item, e.Users, e.Session, deviceKey, position, waitingForStart: true));
                lock (playback)
                {
                    if (playback.State.IsAwaitingResume)
                    {
                        // This report has just saved a position away from the resume point; put the resume point back.
                        Restore(playback);
                    }
                }

                return;
            }

            lock (existing)
            {
                HandleProgress(existing, position, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle playback progress");
        }
    }

    private void HandleProgress(TrackedPlayback playback, long position, PluginConfiguration config)
    {
        // Progress after a stop without a new start: the server stopped an idle (long paused) session
        // and the client carried on. Keep comparing against where it was.
        playback.IsStopped = false;

        var observation = playback.State.Observe(position, DateTime.UtcNow, CreateOptions(config), playback.IsSegmentSkip);
        switch (observation)
        {
            case Observation.Normal:
            case Observation.Resumed:
                Accept(playback);
                break;
            case Observation.GuardConfirmed:
                _logger.LogInformation(
                    "Seek Guard: new position in '{Item}' confirmed by watching, saving {Position}",
                    playback.Item.Name,
                    TimeSpan.FromTicks(position));
                Accept(playback);
                break;
            case Observation.GuardStarted:
                _logger.LogInformation(
                    "Seek Guard: seek in '{Item}' from {From} to {To}, keeping the old resume point until it is watched",
                    playback.Item.Name,
                    TimeSpan.FromTicks(playback.State.GuardedFromTicks ?? 0),
                    TimeSpan.FromTicks(position));
                Restore(playback);
                break;
            default:
                Restore(playback);
                break;
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
            var deviceKey = GetDeviceKey(e.Session);
            var key = GetKey(deviceKey, item);
            _playbacks.TryGetValue(key, out var playback);

            if (FromOtherDevice(playback, e.Users, item, deviceKey) is { } accepted)
            {
                // A device woke up and reported a playback another device has moved past since.
                playback = CreateFromAccepted(item, e.Users, e.Session, deviceKey, accepted);
                _playbacks[key] = playback;
            }

            // The state is kept after a stop: when the server stops a session that was paused for too long,
            // the client resumes without reporting a new start, and its next stop must still be checked.
            if (playback is null)
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

                if (playback.State.IsGuarding || playback.State.IsAwaitingResume)
                {
                    LogRestore(playback, "playback stopped");
                    Restore(playback);
                }
                else
                {
                    Accept(playback);
                }

                playback.IsStopped = true;
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
            // The state is kept: a client closing its connection (a browser tab, for example) may still send its
            // stop report after the session has ended.
            var sessionId = e.SessionInfo.Id;
            foreach (var playback in _playbacks.Values.Where(p => p.SessionId == sessionId))
            {
                lock (playback)
                {
                    if (!playback.IsStopped && (playback.State.IsGuarding || playback.State.IsAwaitingResume))
                    {
                        LogRestore(playback, "session ended");
                        Restore(playback);
                    }

                    playback.IsStopped = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Seek Guard failed to handle session end");
        }
    }

    // Returns the state another device saved after this device's last report, if there is one.
    private AcceptedState? FromOtherDevice(TrackedPlayback? playback, IReadOnlyList<User> users, BaseItem item, string deviceKey)
    {
        if (!_accepted.TryGetValue((users[0].Id, item.Id), out var accepted) || accepted.DeviceKey == deviceKey)
        {
            return null;
        }

        return playback is null || playback.State.LastSeenUtc < accepted.SavedUtc ? accepted : null;
    }

    private TrackedPlayback CreatePlayback(BaseItem item, IReadOnlyList<User> users, SessionInfo session, string deviceKey, long positionTicks, bool waitingForStart = false)
    {
        var now = DateTime.UtcNow;
        var snapshots = new Dictionary<Guid, UserDataSnapshot>();
        foreach (var user in users)
        {
            if (_startSnapshots.TryRemove((user.Id, item.Id), out var saved) && now - saved.SavedUtc < _startSnapshotLifetime)
            {
                snapshots[user.Id] = saved.Snapshot;
            }
            else if (_userDataManager.GetUserData(user, item) is { } data)
            {
                snapshots[user.Id] = UserDataSnapshot.From(data);
            }
        }

        var resumeTicks = snapshots.Values.FirstOrDefault()?.PlaybackPositionTicks;
        return new TrackedPlayback(item, users.ToArray(), session.Id, deviceKey, new PlaybackState(positionTicks, now, resumeTicks), snapshots)
        {
            WaitingForStart = waitingForStart
        };
    }

    // Starts tracking as if this device were at the position another device last saved, so that its report
    // counts as a seek away from it.
    private TrackedPlayback CreateFromAccepted(BaseItem item, IReadOnlyList<User> users, SessionInfo session, string deviceKey, AcceptedState accepted)
    {
        var snapshots = new Dictionary<Guid, UserDataSnapshot>();
        foreach (var user in users)
        {
            if (_accepted.TryGetValue((user.Id, item.Id), out var userState))
            {
                snapshots[user.Id] = userState.Snapshot;
            }
        }

        var state = new PlaybackState(accepted.Snapshot.PlaybackPositionTicks, DateTime.UtcNow);
        return new TrackedPlayback(item, users.ToArray(), session.Id, deviceKey, state, snapshots);
    }

    // The current play state is legitimate: remember it as the state to fall back to.
    private void Accept(TrackedPlayback playback)
    {
        var now = DateTime.UtcNow;
        foreach (var user in playback.Users)
        {
            var data = _userDataManager.GetUserData(user, playback.Item);
            if (data is null)
            {
                continue;
            }

            var snapshot = UserDataSnapshot.From(data);
            playback.Snapshots[user.Id] = snapshot;
            _accepted[(user.Id, playback.Item.Id)] = new AcceptedState(snapshot, playback.DeviceKey, now);
        }
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
        var snapshot = playback.Snapshots.Values.FirstOrDefault();
        _logger.LogInformation(
            "Seek Guard: {Reason} before the new position in '{Item}' was watched, keeping {Position}{Played}",
            reason,
            playback.Item.Name,
            TimeSpan.FromTicks(snapshot?.PlaybackPositionTicks ?? 0),
            snapshot?.Played == true ? " (played)" : string.Empty);
    }

    private void RemoveStale()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _playbacks.Where(p => now - p.Value.State.LastSeenUtc > _staleAfter))
        {
            _playbacks.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _accepted.Where(p => now - p.Value.SavedUtc > _staleAfter))
        {
            _accepted.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _startSnapshots.Where(p => now - p.Value.SavedUtc > _startSnapshotLifetime))
        {
            _startSnapshots.TryRemove(pair.Key, out _);
        }
    }

    private sealed record AcceptedState(UserDataSnapshot Snapshot, string DeviceKey, DateTime SavedUtc);

    private sealed class TrackedPlayback
    {
        public TrackedPlayback(BaseItem item, IReadOnlyList<User> users, string sessionId, string deviceKey, PlaybackState state, Dictionary<Guid, UserDataSnapshot> snapshots)
        {
            Item = item;
            Users = users;
            SessionId = sessionId;
            DeviceKey = deviceKey;
            State = state;
            Snapshots = snapshots;
        }

        public BaseItem Item { get; }

        public IReadOnlyList<User> Users { get; }

        public string SessionId { get; }

        public string DeviceKey { get; }

        public PlaybackState State { get; }

        public Dictionary<Guid, UserDataSnapshot> Snapshots { get; }

        public IReadOnlyList<MediaSegmentDto> Segments { get; set; } = [];

        public bool IsStopped { get; set; }

        public bool WaitingForStart { get; set; }

        // A forward jump that starts inside an intro/recap/credits segment and lands past its end is a skip button.
        public bool IsSegmentSkip(long fromTicks, long toTicks)
            => toTicks > fromTicks
                && Segments.Any(s => fromTicks >= s.StartTicks - _segmentStartSlack.Ticks
                    && fromTicks <= s.EndTicks
                    && toTicks >= s.EndTicks - _segmentEndSlack.Ticks);
    }
}
