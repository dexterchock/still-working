# Visuals, Shaders & Spatial UI System

Last updated: 2026-11-22

## Key Scripts & Shaders
- `Assets/SonicTopography/Scripts/SonicTopographyVisualizer.cs`: Builds chunked terrain pillar meshes (`PILLARS_PER_CHUNK = 3000` for 16-bit Adreno GPU indices), floating crystal blocks, meteors, particles, skybox (`SolidSkybox`), and ground collider; interpolates themes and updates shader uniforms (`_uTime`, 8 EQ bands, 10 ripple buffers).
- `Assets/SonicTopography/Scripts/SonicStarRiverVisualizer.cs`: Renders GPU point-cloud aurora wave + ambient starfield (`SonicStarRiver.shader`, `SonicAmbientStarfield.shader`) and syncs `GlassStageCube` LED rim uniforms (`_LedColor`, `_AudioPulse`, `_AudioBeat`).
- `Assets/SonicTopography/Scripts/SonicTopographyThemes.cs`: Defines `13` presets (`SonicThemePreset`, default `Nocturnal` / "Cosmos") and `GroundEqSettings` (default `terrainDensity = 46f` -> `155x155` pillars, `floatingBlockCount = 96`).
- `Assets/SonicTopography/Scripts/SonicTopographySplashScreen.cs`: Camera-pinned 33-cube animated logo inside a `5.0m` black void sphere; transitions out (`~3.4s`) and invokes `OnSplashFinished`.
- `Assets/SonicTopography/Scripts/SonicSpatialUIManager.cs`: Procedurally generates the frosted-glass XR Canvas (`Music`, `Browse`, `Browser`, `Settings`, docked `PlayerBar`, and `SonicQuestVirtualKeyboardOpener` inputs).
- `Assets/SonicTopography/Scripts/SonicWebBrowserManager.cs` & `SonicBrowserViewportInput`: Lazily initializes GeckoView (`SonicGeckoBrowserBridge.java`) on first Browser tab open, maps `OVROverlay` (`Underlay`, `1280x680` external `Surface`) to `BrowserViewport`, forwards XR pointer touch/drag/scroll, bridges Quest virtual keyboard for web inputs, and feeds Web Audio FFT spectra to `SonicTopographyAudioEngine`.
- `Assets/SonicTopography/Scripts/SonicUIIconGenerator.cs`: Procedural vector-crisp UI icons (including `Globe` and `Keyboard`) + `SonicGlassSurface` UV metadata injector for analytical SDF rounded panels/covers.
- **Shaders** (`Assets/SonicTopography/Resources/Shaders/`):
  - `SonicTerrainPillars.shader`, `SonicFloatingBlocks.shader`, `SonicSimpleInstanced.shader`, `SonicSolidSkybox.shader`
  - `SonicGlassStage.shader`, `SonicStarRiver.shader`, `SonicAmbientStarfield.shader`
  - `SonicVisionGlassUI.shader`, `SonicFrostedGlassUI.shader`, `SonicBrowserUnderlayPunchout.shader` (UI SDF glass, blur & Underlay alpha cutout)
  - `SonicSplashCube.shader`, `SonicSplashVoid.shader`, `SonicScreenFadeOverlay.shader`

## UI & Virtual Keyboard Rules
- Keep search using `UnityEngine.UI.InputField` + `SonicQuestVirtualKeyboardOpener` (legacy `InputField` triggers Horizon OS `TouchScreenKeyboard` reliably when combined with the `OnApplicationFocus` guard in [XR Input & Controllers](xr-input-and-controllers.md)).
- `SonicSpatialUIManager.spawnDistance = 1.22f`, `verticalEyeOffset = -0.05f`, `canvasWorldScale = 0.00095f`.
- `HandleLibraryUpdated()` and `HandleTrackChanged()` update the Player Bar and refresh only the currently visible `_activeSection` via `RefreshActiveSection()`.

## GPU & Headroom Watch-Outs
- Quest 3 runs `1.5x` eye texture scale (`SF=1.50` in `VrApi`) with GPU utilization around `0.87–0.96` at 72–90 Hz.
- Avoid enabling URP shadows, HDR, DepthTexture, OpaqueTexture, or extra full-screen transparent layers. Future 2D web/video overlays should use Meta/OpenXR compositor layers rather than heavy world-space Unity RT quads.
