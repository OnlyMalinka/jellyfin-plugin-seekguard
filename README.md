# Seek Guard for Jellyfin

Jellyfin saves your playback position the moment it changes. If you tap the timeline by accident, for
example while swiping up to close the app on a phone, the episode reopens at the wrong place and you
have to find where you were.

Seek Guard makes Jellyfin behave like YouTube here: **after a seek, the saved resume point stays where it
was until you have actually watched the new position for a few seconds.** Seek and keep watching, and the new
position is saved as usual. Seek and leave right away, and you come back to where you were before the seek.

It works on the server, so every client (Android, iOS, web, TV, desktop) is covered without changing any app.

## Installation

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (or **Catalog → ⚙**) and add a repository:
   - Name: `Seek Guard`
   - URL: `https://raw.githubusercontent.com/OnlyMalinka/jellyfin-plugin-seekguard/main/manifest.json`
2. Install **Seek Guard** from the catalog.
3. Restart Jellyfin.

Manual install: download the zip from [Releases](https://github.com/OnlyMalinka/jellyfin-plugin-seekguard/releases),
extract `Jellyfin.Plugin.SeekGuard.dll` into `<config>/data/plugins/Seek Guard/` and restart Jellyfin.

Requires Jellyfin 12.0 or newer.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Enabled | on | Turns the guard on or off without uninstalling. |
| Minimum seek | 30 s | Smaller position changes, like the 10-second skip buttons, are saved immediately. |
| Watch after a seek | 10 s | How long playback must continue after a seek before the new position is saved. |
| Save segment skips right away | on | Skipping an intro, recap or credits through media segments (for example from [Intro Skipper](https://github.com/intro-skipper/intro-skipper)) counts as deliberate. |

## How it works

Jellyfin stores the reported position and then raises a playback event to plugins. Seek Guard listens to
these events for each playback session:

- A position change that playback alone cannot explain (backwards, or forward faster than 2× speed) and is
  larger than *Minimum seek* is treated as a seek.
- While a seek is unconfirmed, the plugin writes the pre-seek play state (position, watched flag, play count)
  back after every report, so the database always holds the safe value, even if the app is killed and never
  reports a stop.
- Once *Watch after a seek* seconds have been played after the seek, the plugin stops interfering and the
  real position is saved by Jellyfin as usual.
- Several seeks in a row (scrubbing) keep the position from before the first one.
- Starting playback away from the saved resume point counts as a seek too. So does a device that wakes up
  with an old playback after another device has saved newer progress (for example, a tablet left paused
  overnight on an episode you have since finished on your phone).

Pausing after a seek does not confirm it. If you seek on purpose, pause and leave, you will resume from the
position before the seek.

## Known limitations

- An accidental seek past the "played" threshold briefly marks the item as watched. Seek Guard reverts that
  on the item being played, but other versions of the same movie that Jellyfin marked at the same time stay
  marked.
- Clients that report progress rarely confirm a seek later than the configured time.
- Tracking lives in memory. If Jellyfin restarts in the middle of an episode, a seek in that episode is
  not guarded until playback is started again.

## Building

```bash
dotnet test
dotnet publish Jellyfin.Plugin.SeekGuard -c Release -o artifacts
```

Releases are built by GitHub Actions: push a tag such as `v1.0.0` on `main` and the workflow attaches the
zip to a release and adds the version to `manifest.json`. Release notes come from the matching
section of `CHANGELOG.md`, so add it before tagging.

## License

GPL-3.0, like Jellyfin itself.
