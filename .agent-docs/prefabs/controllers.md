# Touch Plus Controller Prefabs

Last updated: 2026-11-22

## Assets (`Assets/SonicTopography/Resources/Controllers/`)
- `MetaQuestTouchPlus_Left.prefab` & `MetaQuestTouchPlus_Left_URP.mat`
- `MetaQuestTouchPlus_Right.prefab` & `MetaQuestTouchPlus_Right_URP.mat`
- Editor fallback source: `Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_{Left,Right}.fbx` and animations `MetaQuestTouchPlus{Left,Right}Animation.controller`.

## Runtime Hierarchy (`SonicMetaQuestController.EnsureControllerVisuals()`)
Attached under `XROrigin.CameraFloorOffsetObject` at runtime:
- `{Left|Right} Controller Grip Anchor` (has `SonicMetaQuestController`, scale locked to ~`0.108m` unrotated controller length)
  - `{Left|Right}Controller_Container` (identity local transform)
    - `MetaQuestTouchPlus_{Left|Right}` (`Animator` + `SkinnedMeshRenderer` body + hidden `"battery"` child mesh)

## Key Inspector & Shader Configuration
- `EnsureControllerMaterial()` builds a runtime `Universal Render Pipeline/Simple Lit` material instance (`*_MusicReactiveInstance`) preserving albedo/normal maps, disabling specular highlights (`_SPECULARHIGHLIGHTS_OFF`) and reflections (`_ENVIRONMENTREFLECTIONS_OFF`), disabling light/reflection probes (`LightProbeUsage.Off`), and setting `updateWhenOffscreen = true` on `SkinnedMeshRenderer`.
- `SonicTopographyEditorSetup` cleans up any Edit-Mode controller artifacts from `SampleScene.unity` so controllers are spawned cleanly at runtime without baked stale scale/rotation overrides.
- Used in: `Assets/Scenes/SampleScene.unity` via [XR Input & Controllers](../systems/xr-input-and-controllers.md).
