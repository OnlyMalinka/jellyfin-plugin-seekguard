# Changelog

## 1.0.2

- Fix: a device that wakes up with an old playback (a tablet left paused overnight, for example) could overwrite progress made on another device, turning a finished episode back into "in progress". Its report now counts as a seek away from the state the other device saved.
- Starting playback away from the saved resume point (a stale position cached by the client, or playing from the beginning) is confirmed by watching, like a seek.
- Playback is tracked per user, device and item, so it survives the client reconnecting with a new session.

## 1.0.1

- Fix: an accidental seek was saved after a long pause. Jellyfin stops a session that has been paused for more than 5 minutes, and clients resume without reporting a new start; Seek Guard now keeps tracking that playback.
- Fix: closing a Jellyfin Web tab right after a seek saved the new position and could mark the episode watched, because the browser's stop report arrives after the session has ended.

## 1.0.0

- First release: after a seek, the resume point stays where it was until the new position has been watched.
- Settings for the minimum seek, the watch time needed to confirm a seek, and trusting media segment skips.
