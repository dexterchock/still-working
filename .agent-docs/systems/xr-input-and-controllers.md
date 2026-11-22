# XR Input & Controllers System

Last updated: 2026-11-22

## Key Scripts & Prefabs
- `Assets/SonicTopography/Scripts/SonicTopographyVRBootstrap.cs`: Bootstraps 72 Hz / `1.5x` URP+XR scaling, OVR/OpenXR performance levels (`SustainedHigh` CPU/GPU), XRI interaction layer hygiene, focus/keyboard guards, and splash-to-UI reveal.
- `Assets/SonicTopography/Scripts/SonicMetaQuestController.cs`: Drives left/right Meta Quest Touch Plus visuals (`Assets/SonicTopography/Resources/Controllers/`).
- `Assets/SonicTopography/Scripts/AudioFloorControllerActions.cs`: Maps Touch controller actions (`A` jump, `B` play/pause, `X` next, `Y` previous, `Grip` cycle theme), enforces floor height locking (`LockVerticalOriginToGround`), and suppresses accidental jump on focus/keyboard transitions.
- See [Touch Plus Controllers Prefabs](../prefabs/controllers.md) for prefab/material details and [Visuals & Spatial UI](visuals-and-ui.md) for `SonicQuestVirtualKeyboardOpener`.

## Verified Invariants & Past Bug Fixes

### Touch Plus Pose, Scale & Ray Trimming (`SonicMetaQuestController`)
- **Grip Pose Orientation**: Checks `OVRInput.GetLocalControllerPosition/Rotation` first, then OpenXR `InputDevice`, then Input System `XRController`. OpenXR Grip Pose must be converted with `GripToVrapiOffsetInverse` (`Matrix4x4.TRS((0, -0.03, -0.04), Quaternion(-0.5, 0, 0, 0.86603), Vector3.one).inverse`) or controllers point at the wrong pitch/offset.
- **Unrotated Scale Lock**: `CalibrateAndLockScaleOnce()` waits 2 tracked frames, temporarily resets `transform.rotation = Quaternion.identity`, measures `meshRenderer.bounds.size`, scales max dimension to `0.108f` meters, restores rotation, and caches `_lockedLocalScale`. Never measure rotated AABB bounds.
- **Battery Mesh & Generic Controllers**: `FindMainControllerMeshRenderer()` hides `"battery"` sub-renderers; `HideGenericControllerRenderers()` disables XRI's fallback `UniversalController` renderers.
- **Nozzle Ray Trim**: `OffsetXriRayOriginForward()` and `[BeforeRenderOrder(500)] OnBeforeRenderTrimPointerLine()` offset line start points by `RayNozzleStartOffsetMeters` (`0.078f`) so white pointer rays never intersect the controller nose.
- **Hand Modality**: `IsHandTrackingActive()` hides `modelRoot` during bare hand tracking (`XRInputModalityManager`, `XRHandSubsystem`, or `OVRInput`).

### Interaction & Virtual Keyboard Protection (`SonicTopographyVRBootstrap`, `AudioFloorControllerActions`)
- **Teleport vs. Hand Pinch**: `GlassStageCube` `TeleportationArea` uses `"Teleport"` (`bit 31`) with `OnSelectExited`. `ConfigureHandAndControllerInteractions()` strips `bit 31` from all `NearFarInteractor`, `XRPokeInteractor`, and `CurveInteractionCaster` masks, enables `XRUIInputModule` (`enableXRInput=true`, `enableMouseInput=true`, `enableTouchInput=false`), and disables `ClimbProvider` + `ClimbTeleportInteractor`.
- **Keyboard Focus Shield**: When Horizon OS opens the keyboard (`OnApplicationFocus(false)`), `SonicTopographyVRBootstrap` checks `SonicQuestVirtualKeyboardOpener.IsKeyboardActiveOrRecentlyUsed(1.0f)` and only runs `ResetPinchAndSelectionStatesOnly()`. Full `ResetAllInteractionAndUIStates()` (deselecting `EventSystem` or cycling `XRUIInputModule`) kills the OS keyboard.
- **Phantom Jump Guard**: `AudioFloorControllerActions` ignores `aDown` (Jump) and `xDown` (Next) for bare hands and invokes `CancelAnyActiveJumpImmediate(...)` with `_suppressJumpUntilTime` (~`0.85–1.0s`) on focus/pause/keyboard events.
- **Ground & Gravity Lock**: `GravityProvider.useGravity = false`; `LockVerticalOriginToGround()` pins `XROrigin` to `0f` on Glass Stage (`|x|,|z| <= 1.8`, `y >= -2`), `-11.0f` over Terrain Platter (`r <= 79m`), or respawns to `(0,0,0)` below `-22f`, forcing `CameraYOffset = 0` on Android.

## Notes
- `SonicMetaQuestController.UpdateAnimatorInputs()` scopes `OVRInput.Get(...)` calls with `ovrCtrl` (`RTouch`/`LTouch`) for accurate per-hand triggers, grips, buttons, and thumbsticks.
- `AudioFloorControllerActions` gates `Keyboard.current` WASD locomotion and keyboard shortcuts behind `#if UNITY_EDITOR || UNITY_STANDALONE`.
