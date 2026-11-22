# Audio & Music Library System

Last updated: 2026-11-22

## Key Scripts & Native Plugin
- `Assets/SonicTopography/Scripts/SonicMusicLibraryManager.cs`: Runtime storage permission request, async time-sliced library scan (`4 ms/frame` budget), metadata/cover art extraction, album color palette extraction, and audio clip loading/caching.
- `Assets/SonicTopography/Scripts/SonicMusicPlayerController.cs`: Queue/shuffle/repeat (`RepeatAll` default in memory, persisted in `PlayerPrefs`), play/pause/seek, and `OnTrackChanged` / `OnPlaybackStateChanged` events.
- `Assets/SonicTopography/Scripts/SonicTopographyAudioEngine.cs`: Wraps Unity `AudioSource` (`spatialBlend = 0f`) plus external Web Audio spectrum input (`SetExternalAudioSpectrum`), samples `512`-bin FFT, computes 8 frequency bands (`SubBass`..`Air`) + timbral metrics + auto beat triggers (`Pulse`, `Snare`, `Meteor`).
- `Assets/SonicTopography/Plugins/Android/SonicAndroidAudioDecoder.java`: `com.sonictopography.audio.SonicAndroidAudioDecoder.decodeToStereoWav(inputPath, outputWavPath)` using Android `MediaExtractor` + `MediaCodec` off-thread.
- `Assets/SonicTopography/Plugins/Android/SonicGeckoBrowserBridge.java` + `Assets/StreamingAssets/sonic_browser_ext/{manifest.json, background.js, content.js}`: Built-in GeckoView WebExtension tapping `<video>`/`<audio>` via Web Audio `AnalyserNode(fftSize=1024)` + `mediaSession` metadata over GeckoView native messaging (`sonic_browser`) without `RECORD_AUDIO`.

## Permissions & VRC.Quest.Security.2 Compliance
- **Manifest** (`Assets/Plugins/Android/AndroidManifest.xml`): Declares `READ_MEDIA_AUDIO`, `READ_EXTERNAL_STORAGE` (for `minSdkVersion=32` compat), `requestLegacyExternalStorage="true"`, and `SkipPermissionsDialog="false"`. Eye-tracking permissions are stripped (`tools:node="remove"` + `SonicQuestBuildTools`).
- **Runtime Prompt**: `SonicMusicLibraryManager.RequestAndroidAudioPermissionsIfNeeded()` checks `android.os.Build$VERSION.SDK_INT` so API 33+ (Quest 3 / Quest 3S) only requests `android.permission.READ_MEDIA_AUDIO`.
- **Denied Permission Fallback**: If denied (`user-fixed` or skipped), `RefreshLibrary()` handles inaccessible directories gracefully (`_tracks` empty) and `SonicSpatialUIManager` shows `EmptyLibraryCard` (`"No Songs Found — Tap to browse folders"`), passing `VRC.Quest.Security.2` and `VRC.Quest.Functional.1` at 72 FPS.

## Audio Formats & Cache Pipeline
- Supported extensions: `.mp3`, `.wav`, `.ogg`, `.m4a`, `.flac`, `.aac` (plus `.mp4`/`.opus` fallback decoding).
- Default Android scan folder: `/storage/emulated/0/Music` (or saved `PlayerPrefs` `SonicTopography_ScanFolder`), up to depth `12`.
- On Android, `.m4a`, `.flac`, `.aac`, `.mp4`, `.opus` decode on a `Task.Run` worker thread via `SonicAndroidAudioDecoder` into `Application.temporaryCachePath/SonicAudioCache/quest_<hash>.wav`, then load via `UnityWebRequestMultimedia.GetAudioClip`. Other formats that fail FMOD retry via `quest_fb_<hash>.wav`.
- Cached decoded WAVs in `Application.temporaryCachePath/SonicAudioCache` use a `512 MB` (`MaxAudioCacheBytes`) LRU cap (`HasValidCachedWav` + `PruneAudioCacheIfNeeded`), evicting oldest unused WAVs down to `384 MB`.
- Up to `MaxCachedAudioClips = 2` decoded `AudioClip` instances stay in memory at once; older clips are destroyed.

## Watch-Outs
- **Synchronous PCM Load**: `LoadTrackClipCoroutine` sets `dh.streamAudio = false` (full synchronous PCM upload on main thread). Verify `AudioSource.time` seeking and `clip.length` on Quest before switching `streamAudio = true`.
