# Project Overview

Last updated: 2026-11-22

## Identity & Store Baseline
- **App Name**: `AudioFloor` (`Dexter Chock`)
- **Package ID**: `com.Dexter.AudioFloor`
- **Meta App ID**: `1822201432331484`
- **Unity Version**: `6000.3.26f1` (`de8167e7440c`)
- **Released Store Build**: `v1.0` (`versionCode=2`, Build `1826148581936769`, `Builds/AudioFloor.apk`)
- **Verified Test Device**: Meta Quest 3 (`2G0YC28F6Z003Z`, Android OS 14 / API 34)

## Target Platform & Build Settings
- **Devices**: `quest3|quest3s` (`com.oculus.supportedDevices`), `com.meta.store.defaultDeviceTargets = quest3+`
- **Android SDK**: `minSdkVersion = 32`, `targetSdkVersion = 34`, Horizon OS SDK `min=60 / target=207`
- **Backend & Entry**: IL2CPP, `arm64-v8a` only (32-bit `armeabi-v7a` assets stripped in `launcher/build.gradle`), Gradle, `AndroidApplicationEntry.GameActivity` (`com.unity3d.player.UnityPlayerGameActivity` with `@style/BaseUnityGameActivityTheme`)
- **Signing Keystore**: `Keystore/audiofloor_release.keystore` (`CN=Dexter Chock, OU=AudioFloor, O=Dexter Chock, C=US`, APK Signature Scheme v2)
- **Build & Setup Tooling**:
  - `Assets/SonicTopography/Editor/SonicQuestBuildTools.cs`: Configures PlayerSettings/URP `1.5f`/keystore/icons, provides menu item `AudioFloor/Build Quest 3 APK`, watches `Temp/TriggerQuestBuild.txt` for automated builds, and patches Gradle + `AndroidManifest.xml` in `OnPostGenerateGradleAndroidProject`.
  - `Assets/SonicTopography/Editor/SonicTopographyEditorSetup.cs`: Auto-enables required OpenXR features and cleans leftover template scene objects on Editor load.

## Key Packages (`Packages/manifest.json`)
- **Rendering**: `com.unity.render-pipelines.universal` `17.3.0` (`Performance` & `Quality` URP configs locked to `renderScale=1.5`, no HDR/shadows/opaque/depth textures).
- **Meta XR**: `com.meta.xr.sdk.core` `207.0.1` (embedded in `Packages/com.meta.xr.sdk.core/`) for OVR performance hints, Touch Plus FBX/anim assets, and manifest/project validation.
- **Unity XR**: `com.unity.xr.openxr` `1.17.1`, `com.unity.xr.meta-openxr` `2.5.1`, `com.unity.xr.interaction.toolkit` `3.5.1`, `com.unity.xr.hands` `1.8.1`, `com.unity.inputsystem` `1.20.0`.
- **Tooling**: `com.coplaydev.unity-mcp` (Unity Editor MCP bridge).

## Non-Runtime Workspace Folders
- `StoreAssets/`: Meta Horizon Store icons, covers (`Hero_Cover_3000x900.png`, `Universal_Cover_2560x1440.png`), and 8K `AudioFloor_360_Preview_8192x4096.jpg`.
- `Builds/`: Release `AudioFloor.apk` and older dev artifacts (`build_n_run/`, logs).
- `Documentation/`, `Icons_Preview/`, `Mineradio_Ref/`: Reference/design assets; not packaged into Android builds.
