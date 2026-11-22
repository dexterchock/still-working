# AudioFloor (`com.Dexter.AudioFloor`) — Source & Architecture Context

Standalone **Meta Quest 3 & Quest 3S** VR music visualizer and spatial media environment built with **Unity 6 (`6000.3.26f1`)**, **URP (`17.3.0`)**, **OpenXR (`1.17.1`)**, **XR Interaction Toolkit (`3.5.1`)**, **Meta XR Core SDK (`207.0.1`)**, and **Mozilla GeckoView (`133.0`)**.

> **Purpose of this Repository**: This repository contains the complete custom source code, HLSL shaders, Android JNI Java bridges, GeckoView WebExtension, Android manifest, package manifest, and architectural documentation for **AudioFloor**, curated specifically for [Repomix](https://repomix.com/) and large-context LLM analysis (e.g., Google AI Studio) without heavy binary Unity assets or SDK caches.

---

## Start Here (For AI Assistants)

1. **[`AGENTS.md`](AGENTS.md)** — **Mandatory reading.** Documents the production baseline (`v1.1`, `versionCode=3`), all non-negotiable Quest 3 hardware bug fixes and architectural invariants (`UnityPlayerGameActivity` theme, native `EditText` virtual keyboard bridge, Touch Plus controller orientation/scale lock/ray trim, hand-tracking vs. controller isolation, `1.5x` supersampling budget, off-thread `MediaCodec` audio decoding, and GeckoView + `OVROverlay` underlay + WebExtension 512-bin FFT pipeline).
2. **[`.agent-docs/index.md`](.agent-docs/index.md)** — Living knowledge base covering scene bootstrap flow, XR input/controllers, audio/library pipeline, spatial glass UI, and custom shaders.

---

## Repository Structure

```text
├── AGENTS.md                                      # Project rules & non-negotiable Quest invariants
├── repomix.config.json                            # Repomix configuration for single-file AI packing
├── .agent-docs/                                   # Structured subsystem & scene documentation
│   ├── index.md
│   ├── project-overview.md
│   ├── prefabs/controllers.md
│   ├── scenes/
│   │   ├── _flow.md
│   │   └── sample-scene.md
│   └── systems/
│       ├── audio-and-library.md
│       ├── visuals-and-ui.md
│       └── xr-input-and-controllers.md
├── Assets/
│   ├── Plugins/Android/
│   │   └── AndroidManifest.xml                    # Quest 3/3S GameActivity manifest & permission stripping
│   ├── StreamingAssets/sonic_browser_ext/         # Built-in GeckoView WebExtension (Web Audio FFT & input sync)
│   │   ├── manifest.json
│   │   ├── background.js
│   │   └── content.js
│   └── SonicTopography/
│       ├── Editor/
│       │   ├── SonicQuestBuildTools.cs            # Gradle/Manifest post-processor & Quest 3 APK build pipeline
│       │   └── SonicTopographyEditorSetup.cs      # Scene & asset setup utilities
│       ├── Plugins/Android/
│       │   ├── SonicAndroidAudioDecoder.java      # MediaExtractor + MediaCodec off-thread audio decoder
│       │   ├── SonicGeckoBrowserBridge.java       # GeckoView 133 session, OVROverlay Surface & native messaging
│       │   └── SonicQuestSoftInputBridge.java     # Native Android EditText bridge for Horizon OS overlay keyboard
│       ├── Resources/Shaders/                     # Custom Quest-optimized URP HLSL shaders
│       │   ├── SonicAmbientStarfield.shader
│       │   ├── SonicBrowserUnderlayPunchout.shader
│       │   ├── SonicFloatingBlocks.shader
│       │   ├── SonicGlassStage.shader
│       │   ├── SonicScreenFadeOverlay.shader
│       │   ├── SonicSimpleInstanced.shader
│       │   ├── SonicSolidSkybox.shader
│       │   ├── SonicSplashCube.shader
│       │   ├── SonicSplashVoid.shader
│       │   ├── SonicStarRiver.shader
│       │   ├── SonicTerrainPillars.shader
│       │   └── SonicVisionGlassUI.shader
│       └── Scripts/                               # Core runtime C# scripts
│           ├── AudioFloorControllerActions.cs     # Locomotion, jump physics, ground lock & button routing
│           ├── SonicMetaQuestController.cs        # Touch Plus controller pose, scale lock, ray trim & anims
│           ├── SonicMusicLibraryManager.cs        # Permission flow, time-sliced folder scan & PCM/cache loader
│           ├── SonicMusicPlayerController.cs      # Playback state machine, shuffle/repeat & mutual browser pause
│           ├── SonicQuestNativeKeyboard.cs        # C# wrapper for SonicQuestSoftInputBridge
│           ├── SonicSpatialUIManager.cs           # Procedural frosted-glass spatial UI, tabs & virtual keyboard opener
│           ├── SonicStarRiverVisualizer.cs        # Celestial star river & floating blocks visualizer
│           ├── SonicTopographyAudioEngine.cs      # FFT spectrum analysis, beat flux & external browser FFT ingestion
│           ├── SonicTopographySplashScreen.cs     # Boot splash sequence
│           ├── SonicTopographyThemes.cs           # Visualizer color palettes & album art palette extraction
│           ├── SonicTopographyVisualizer.cs       # 16-bit chunked reactive 3D terrain pillars & glass stage
│           ├── SonicTopographyVRBootstrap.cs      # RuntimeInitializeOnLoad bootstrap, 72Hz/1.5x lock & XRI setup
│           ├── SonicUIIconGenerator.cs            # Procedural SDF/vector UI icons
│           └── SonicWebBrowserManager.cs          # Lazy GeckoView lifecycle, OVROverlay quad & FFT/input polling
├── Packages/
│   └── manifest.json                              # Unity package dependencies
└── ProjectSettings/
    └── ProjectVersion.txt                         # Unity Editor version (6000.3.26f1)
```
