# Changelog

## 1.0.1

- Fix: an accidental seek was saved after a long pause. Jellyfin stops a session that has been paused for more than 5 minutes, and clients resume without reporting a new start; Seek Guard now keeps tracking that playback.
- Fix: closing a Jellyfin Web tab right after a seek saved the new position and could mark the episode watched, because the browser's stop report arrives after the session has ended.

## 1.0.0

- First release: after a seek, the resume point stays where it was until the new position has been watched.
- Settings for the minimum seek, the watch time needed to confirm a seek, and trusting media segment skips.
