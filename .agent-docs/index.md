# AudioFloor — Agent Documentation

> Auto-generated living knowledge base for AI agent comprehension.
> Last updated: 2026-11-22

## Quick Context
**AudioFloor** (`com.Dexter.AudioFloor`) is a standalone Meta Quest 3 / Quest 3S VR music visualizer built in Unity `6000.3.26f1` (URP + OpenXR + XRI + Meta XR Core SDK + Mozilla GeckoView `133`). Users stand on an illuminated glass stage above a reactive 3D audio-topography field and celestial star river while playing local audio files or streaming via the built-in web browser.

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
3. Once the splash finishes (`OnSplashFinished` or 6s fallback), `SonicSpatialUIManager` opens the frosted-glass spatial panel at comfortable VR eye distance.
4. `SonicMusicLibraryManager` requests runtime storage permission (once on boot) and runs an async time-sliced folder scan (`<= 4 ms/frame`).
5. `SonicTopographyAudioEngine` feeds FFT bands and beat fluxes (from either Unity `AudioSource` or `SonicWebBrowserManager` external Web Audio FFT spectra) to `SonicTopographyVisualizer` and `SonicStarRiverVisualizer` every frame.
