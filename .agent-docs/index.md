# AudioFloor — Agent Documentation

> Auto-generated living knowledge base for AI agent comprehension.
> Last updated: 2026-11-26

## Quick Context
**AudioFloor** (`com.Dexter.AudioFloor`) is a standalone Meta Quest 3 / Quest 3S VR & Passthrough Mixed Reality music visualizer built in Unity `6000.3.26f1` (URP + OpenXR + XRI + Meta XR Core SDK + MRUK + Meta XR Audio SDK + Mozilla GeckoView `133`). Users can switch seamlessly between VR void stage mode and Passthrough AR room mode with depth occlusion, scanned room light-wash + spilling ripples, detachable Keychain controls, and spatialized music while playing local audio files or streaming via the built-in web browser.

## Document Map
- [Project Overview](project-overview.md)
- Scenes
  - [Scene Flow](scenes/_flow.md)
  - [SampleScene](scenes/sample-scene.md)
- Systems
  - [XR Input & Controllers](systems/xr-input-and-controllers.md)
  - [Audio & Music Library](systems/audio-and-library.md)
  - [Visuals, Shaders & Spatial UI](systems/visuals-and-ui.md)
- Prefabs
  - [Touch Plus Controllers](prefabs/controllers.md)

## Runtime Flow
1. `Assets/Scenes/SampleScene.unity` loads and triggers `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]` on `SonicTopographyVRBootstrap` and `SonicStarRiverVisualizer`.
2. `SonicTopographyVRBootstrap` locks 72 Hz / `1.5x` render scale, hides template environment roots, builds `GlassStageCube`, configures XRI/hands/teleport layers, spawns Touch Plus controller anchors, starts `SonicTopographySplashScreen`, and initializes player/library/UI managers.
3. Once the splash finishes (`OnSplashFinished` or 6s fallback), `SonicSpatialUIManager` opens the frosted-glass spatial panel (including the right-side `VR`/`AR` mode switch pill).
4. `SonicMusicLibraryManager` requests runtime storage permission (once on boot) and runs an async time-sliced folder scan (`<= 4 ms/frame`).
5. `SonicTopographyAudioEngine` feeds unattenuated FFT bands and beat fluxes (from either local PCM/`AudioSource` or `SonicWebBrowserManager` external Web Audio FFT spectra) to `SonicTopographyVisualizer` and `SonicStarRiverVisualizer` every frame while `SonicMixedRealityManager` coordinates VR <-> AR ripple dissolve, Depth API occlusion, MRUK room light wash, `SonicArKeychainController`, `SonicArTerrainManipulator`, and spatial audio routing.
