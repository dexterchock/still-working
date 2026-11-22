using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace SonicTopography
{
    /// <summary>
    /// AudioFloorControllerActions: Maps Meta Quest controller buttons and hand grip triggers
    /// to core AudioFloor actions while strictly preserving controller models, scales, and transforms.
    ///
    /// Controls Mapping:
    /// - Button A (Right Controller Primary): Jump (Physical controller only; never triggered by hand pinch or keyboard overlay)
    /// - Right Joystick: Locomotion &amp; Teleportation on the glass stage
    /// - Button B (Right Controller Secondary): Play / Pause toggle
    /// - Button X (Left Controller Primary): Next Track
    /// - Button Y (Left Controller Secondary): Previous Track
    /// - Left Hand Grip (Trigger): Previous Theme (syncs visualizer and UI theme page)
    /// - Right Hand Grip (Trigger): Next Theme (syncs visualizer and UI theme page)
    /// - Index Triggers &amp; Left Menu Button: Preserved untouched.
    /// </summary>
    [AddComponentMenu("SonicTopography/AudioFloor Controller Actions")]
    public class AudioFloorControllerActions : MonoBehaviour
    {
        private static AudioFloorControllerActions _instance;
        private static readonly SonicThemePreset[] AllThemePresets = (SonicThemePreset[])Enum.GetValues(typeof(SonicThemePreset));

        public static bool IsActivelyJumping => _instance != null && _instance._isJumping;

        [Header("Core Systems")]
        [SerializeField] private SonicMusicPlayerController _player;
        [SerializeField] private SonicTopographyVisualizer _visualizer;
        [SerializeField] private SonicSpatialUIManager _spatialUI;
        [SerializeField] private SonicWebBrowserManager _browserManager;

        [Header("Locomotion & Jump References")]
        [SerializeField] private GravityProvider _gravityProvider;
        [SerializeField] private CharacterController _characterController;
        [SerializeField] private XROrigin _xrOrigin;
        [SerializeField] private Transform _originTransform;

        [Header("Jump Tuning")]
        [SerializeField] private float _jumpVelocity = 4.0f;
        [SerializeField] private float _gravity = 9.81f;

        // Button press edge-detection states
        private bool _wasRightPrimary = false;   // Button A
        private bool _wasRightSecondary = false; // Button B
        private bool _wasLeftPrimary = false;    // Button X
        private bool _wasLeftSecondary = false;  // Button Y
        private bool _wasLeftGrip = false;       // Left Hand Grip
        private bool _wasRightGrip = false;      // Right Hand Grip

        // Jump physics state
        private bool _isJumping = false;
        private float _verticalVelocity = 0f;
        private float _jumpCooldown = 0f;
        private float _lastJumpStartTime = -100f;
#if UNITY_EDITOR || UNITY_STANDALONE
        private float _walkImpulseTimer = 0f;
        private Vector3 _walkImpulseDir = Vector3.zero;
#endif

        // Virtual Keyboard / Focus transition guards
        private float _suppressJumpUntilTime = -100f;

        private UnityEngine.XR.Interaction.Toolkit.Locomotion.XRBodyTransformer _bodyTransformer;

#if UNITY_EDITOR
        private UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets.DynamicMoveProvider _editorMoveProvider;
        private UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.SnapTurnProvider _editorTurnProvider;
        private bool _wasRightStickTurn = false;
#endif

        // Cached XR InputDevice & InputSystem controls to avoid per-frame lookups and string marshaling
        private UnityEngine.XR.InputDevice _rightXrDevice;
        private bool _isRightDeviceHandTracking;
        private UnityEngine.XR.InputDevice _leftXrDevice;
        private bool _isLeftDeviceHandTracking;

        private UnityEngine.InputSystem.XR.XRController _cachedXrRight;
        private ButtonControl _rightPrimaryCtrl;
        private ButtonControl _rightSecondaryCtrl;
        private ButtonControl _rightGripBtnCtrl;
        private AxisControl _rightGripAxisCtrl;

        private UnityEngine.InputSystem.XR.XRController _cachedXrLeft;
        private ButtonControl _leftPrimaryCtrl;
        private ButtonControl _leftSecondaryCtrl;
        private ButtonControl _leftGripBtnCtrl;
        private AxisControl _leftGripAxisCtrl;

        private void Awake()
        {
            _instance = this;
            ResolveDependencies();
        }

        private void OnEnable()
        {
            _instance = this;
            Application.onBeforeRender += OnBeforeRenderValidateVerticalPose;
            BindBodyTransformer();
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderValidateVerticalPose;
            UnbindBodyTransformer();
        }

        private void Start()
        {
            _instance = this;
            ResolveDependencies();
            BindBodyTransformer();
            EnsureGlassStageTeleportationReady();
            LockVerticalOriginToGround();
        }

        private void BindBodyTransformer()
        {
            if (_bodyTransformer == null)
            {
                _bodyTransformer = FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.XRBodyTransformer>();
            }
            if (_bodyTransformer != null)
            {
                _bodyTransformer.afterApplyTransformations -= OnAfterBodyTransformations;
                _bodyTransformer.afterApplyTransformations += OnAfterBodyTransformations;
            }
        }

        private void UnbindBodyTransformer()
        {
            if (_bodyTransformer != null)
            {
                _bodyTransformer.afterApplyTransformations -= OnAfterBodyTransformations;
            }
        }

        private void OnAfterBodyTransformations(UnityEngine.XR.Interaction.Toolkit.Locomotion.ApplyBodyTransformationsEventArgs args)
        {
            if (!_isJumping && _originTransform != null)
            {
                LockVerticalOriginToGround();
            }
        }

        private void OnBeforeRenderValidateVerticalPose()
        {
            if (!_isJumping && _originTransform != null)
            {
                LockVerticalOriginToGround();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            _suppressJumpUntilTime = Mathf.Max(_suppressJumpUntilTime, Time.unscaledTime + 1.0f);
            _wasRightPrimary = true;

            // If a jump was triggered right as focus was lost (e.g. clicking search to open system keyboard), cancel it immediately
            if (_isJumping && (Time.unscaledTime - _lastJumpStartTime) < 0.5f)
            {
                CancelAnyActiveJumpImmediate($"OnApplicationFocus({hasFocus})");
            }
            else if (!_isJumping)
            {
                LockVerticalOriginToGround();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            _suppressJumpUntilTime = Mathf.Max(_suppressJumpUntilTime, Time.unscaledTime + 1.0f);
            _wasRightPrimary = true;
            if (_isJumping)
            {
                CancelAnyActiveJumpImmediate($"OnApplicationPause({pauseStatus})");
            }
            else
            {
                LockVerticalOriginToGround();
            }
        }

        public static void CancelAnyActiveJumpImmediate(string reason)
        {
            if (_instance == null) return;
            _instance._suppressJumpUntilTime = Mathf.Max(_instance._suppressJumpUntilTime, Time.unscaledTime + 1.0f);
            _instance._wasRightPrimary = true;

            _instance._isJumping = false;
            _instance._verticalVelocity = 0f;
            _instance.LockVerticalOriginToGround();
        }

        public void ResolveDependencies()
        {
            if (_player == null) _player = FindFirstObjectByType<SonicMusicPlayerController>();
            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
            if (_spatialUI == null) _spatialUI = FindFirstObjectByType<SonicSpatialUIManager>();
            if (_browserManager == null) _browserManager = FindFirstObjectByType<SonicWebBrowserManager>();
            if (_gravityProvider == null) _gravityProvider = FindFirstObjectByType<GravityProvider>();
            if (_characterController == null) _characterController = FindFirstObjectByType<CharacterController>();
            if (_xrOrigin == null) _xrOrigin = FindFirstObjectByType<XROrigin>();

#if UNITY_EDITOR
            if (_xrOrigin != null)
            {
                if (_editorMoveProvider == null)
                    _editorMoveProvider = _xrOrigin.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets.DynamicMoveProvider>();
                if (_editorTurnProvider == null)
                    _editorTurnProvider = _xrOrigin.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.SnapTurnProvider>();
            }
#endif

            if (_originTransform == null)
            {
                if (_xrOrigin != null)
                {
                    _originTransform = _xrOrigin.Origin != null ? _xrOrigin.Origin.transform : _xrOrigin.transform;
                }
                else if (_characterController != null)
                {
                    _originTransform = _characterController.transform;
                }
                else
                {
                    GameObject originGo = GameObject.Find("XR Origin Hands (XR Rig)");
                    if (originGo == null) originGo = GameObject.Find("XR Origin (XR Rig)");
                    if (originGo != null) _originTransform = originGo.transform;
                }
            }

            // Disable GravityProvider's redundant falling velocity so it never fights CharacterController or causes vertical jitter
            if (_gravityProvider != null)
            {
                _gravityProvider.useGravity = false;
                _gravityProvider.ResetFallForce();
            }

            // Disable unused ClimbProvider and ClimbTeleportInteractor so hand pinches never trigger climb locomotion
            foreach (var climb in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing.ClimbProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (climb != null) climb.enabled = false;
            }
            foreach (var climbTele in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing.ClimbTeleportInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (climbTele != null)
                {
                    climbTele.enabled = false;
                    climbTele.gameObject.SetActive(false);
                }
            }
        }

        private void EnsureGlassStageTeleportationReady()
        {
            GameObject glassCube = GameObject.Find("GlassStageCube");
            if (glassCube != null)
            {
                BoxCollider col = glassCube.GetComponent<BoxCollider>();
                if (col == null)
                {
                    col = glassCube.AddComponent<BoxCollider>();
                }
                col.center = Vector3.zero;
                col.size = Vector3.one;
                col.isTrigger = false;
                col.enabled = true;

                TeleportationArea teleportArea = glassCube.GetComponent<TeleportationArea>();
                if (teleportArea == null)
                {
                    teleportArea = glassCube.AddComponent<TeleportationArea>();
                }
                teleportArea.enabled = true;
                int teleportLayer = UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask.GetMask("Teleport");
                if (teleportLayer == 0) teleportLayer = 1 << 31;
                teleportArea.interactionLayers = teleportLayer; // Strictly Teleport layer (bit 31), preventing NearFarInteractor from hovering the floor
                teleportArea.matchOrientation = MatchOrientation.WorldSpaceUp;
                teleportArea.teleportTrigger = BaseTeleportationInteractable.TeleportTrigger.OnSelectExited;
                if (!teleportArea.colliders.Contains(col))
                {
                    teleportArea.colliders.Add(col);
                }
            }
        }

        private void Update()
        {
            if (_jumpCooldown > 0f)
            {
                _jumpCooldown -= Time.deltaTime;
            }


            // Track virtual keyboard & focus suppression window
            bool keyboardOrFocusBusy =
                !Application.isFocused ||
                TouchScreenKeyboard.visible ||
                SonicQuestVirtualKeyboardOpener.IsKeyboardActiveOrRecentlyUsed(1.0f) ||
                (_spatialUI != null && _spatialUI.IsSearchFocused);

            if (keyboardOrFocusBusy)
            {
                _suppressJumpUntilTime = Mathf.Max(_suppressJumpUntilTime, Time.unscaledTime + 0.85f);
                _wasRightPrimary = true;

                if (_isJumping)
                {
                    CancelAnyActiveJumpImmediate("KeyboardOrFocusBusy");
                }
            }

            // 1. Read controller button / grip trigger edge inputs
            ReadInputs(
                out bool aDown,
                out bool bDown,
                out bool xDown,
                out bool yDown,
                out bool leftGripDown,
                out bool rightGripDown);

            // 2. Button A: Jump (Strictly Physical Controller only, and never during/after Virtual Keyboard or Focus handoff)
            if (aDown && !keyboardOrFocusBusy && Time.unscaledTime >= _suppressJumpUntilTime)
            {
                PerformJump();
            }

            if ((bDown || xDown || yDown) && _browserManager == null)
            {
                _browserManager = FindFirstObjectByType<SonicWebBrowserManager>();
            }
            bool routeToBrowser = _browserManager != null
                && _browserManager.IsBrowserAudioActive
                && (_player == null || !_player.IsPlaying);

            // 3. Button B: Play / Pause toggle
            if (bDown && !keyboardOrFocusBusy)
            {
                if (routeToBrowser)
                {
                    _browserManager.TogglePlayPause();
                }
                else if (_player != null)
                {
                    _player.TogglePlayPause();
                }
            }

            // 4. Button X: Next Track
            if (xDown && !keyboardOrFocusBusy)
            {
                if (routeToBrowser)
                {
                    _browserManager.NextTrack();
                }
                else if (_player != null)
                {
                    _player.NextTrack();
                }
            }

            // 5. Button Y: Previous Track
            if (yDown && !keyboardOrFocusBusy)
            {
                if (routeToBrowser)
                {
                    _browserManager.PrevTrack();
                }
                else if (_player != null)
                {
                    _player.PreviousTrack();
                }
            }

            // 6. Left Hand Grip Trigger: Previous Theme
            if (leftGripDown && !keyboardOrFocusBusy)
            {
                CycleTheme(-1);
            }

            // 7. Right Hand Grip Trigger: Next Theme
            if (rightGripDown && !keyboardOrFocusBusy)
            {
                CycleTheme(1);
            }

            // 8. Artificial Locomotion (Smooth continuous walking & Snap turning)
            UpdateArtificialLocomotion();

            // 9. Update jump physics or lock vertical origin to ground (stage or terrain)
            UpdateJumpPhysicsAndLockGroundHeight();
        }

        private void LateUpdate()
        {
            if (!_isJumping && _originTransform != null)
            {
                LockVerticalOriginToGround();
            }
        }

        public const float TERRAIN_GROUND_Y = -11.0f;

        public static float GetGroundHeightAt(Vector3 worldPos)
        {
            // 1. Is user on the Glass Stage?
            // Glass stage is at (0, -0.06, 0), bounds [-1.8, 1.8] on X and Z.
            bool onStage = Mathf.Abs(worldPos.x) <= 1.8f && Mathf.Abs(worldPos.z) <= 1.8f;
            if (onStage && worldPos.y >= -2.0f)
            {
                return 0f; // Stage surface
            }

            // 2. Is user over the Terrain Platter?
            // Platter center is (0, -26, 64), radius 78.5m. Top surface is -11.0m (local Y = 15.0m).
            float dx = worldPos.x - 0f;
            float dz = worldPos.z - 64f;
            float distSqr = dx * dx + dz * dz;
            if (distSqr <= 79.0f * 79.0f)
            {
                return TERRAIN_GROUND_Y; // Terrain platter surface (-11.0m)
            }

            // 3. Raycast check against colliders in case of custom terrain elevation
            if (Physics.Raycast(new Vector3(worldPos.x, worldPos.y + 2.0f, worldPos.z), Vector3.down, out RaycastHit hit, 60f))
            {
                return hit.point.y;
            }

            return -100f; // Abyss
        }

        private Transform GetHeadCameraTransform()
        {
            if (_xrOrigin != null && _xrOrigin.Camera != null)
            {
                return _xrOrigin.Camera.transform;
            }
            Camera mainCam = Camera.main;
            return mainCam != null ? mainCam.transform : null;
        }

        private Vector3 GetPlayerFeetWorldPos(Vector3 originPos)
        {
            // Check stage/terrain bounds at HMD XZ rather than _originTransform XZ so roomscale walking works cleanly.
            Transform camTr = GetHeadCameraTransform();
            if (camTr != null)
            {
                Vector3 camPos = camTr.position;
                return new Vector3(camPos.x, originPos.y, camPos.z);
            }
            return originPos;
        }

        private void LockVerticalOriginToGround()
        {
            if (_originTransform != null)
            {
                Vector3 pos = _originTransform.position;
                float groundY = GetGroundHeightAt(GetPlayerFeetWorldPos(pos));

                // If not actively jumping or falling, lock firmly to ground
                if (!_isJumping && pos.y <= groundY + 0.05f)
                {
                    if (Mathf.Abs(pos.y - groundY) > 0.0005f)
                    {
                        pos.y = groundY;
                        _originTransform.position = pos;
                    }
                }
            }

            if (_xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (Mathf.Abs(_xrOrigin.CameraYOffset) > 0.0001f)
                {
                    _xrOrigin.CameraYOffset = 0f;
                }
                Transform offsetTr = _xrOrigin.CameraFloorOffsetObject.transform;
                if (Mathf.Abs(offsetTr.localPosition.y) > 0.0001f)
                {
                    Vector3 lp = offsetTr.localPosition;
                    lp.y = 0f;
                    offsetTr.localPosition = lp;
                }
#else
                Transform offsetTr = _xrOrigin.CameraFloorOffsetObject.transform;
                if (_xrOrigin.CurrentTrackingOriginMode == TrackingOriginModeFlags.Floor && Mathf.Abs(offsetTr.localPosition.y) > 0.0001f)
                {
                    Vector3 lp = offsetTr.localPosition;
                    lp.y = 0f;
                    offsetTr.localPosition = lp;
                }
#endif
            }

            if (_gravityProvider != null)
            {
                if (_gravityProvider.useGravity) _gravityProvider.useGravity = false;
                _gravityProvider.ResetFallForce();
            }
        }

        private void PerformJump()
        {
            if (_jumpCooldown > 0f || _isJumping) return;

            _isJumping = true;
            _verticalVelocity = _jumpVelocity;
            _jumpCooldown = 0.35f;
            _lastJumpStartTime = Time.unscaledTime;
            Debug.Log("[AudioFloor] PerformJump triggered!");
        }

        private void UpdateJumpPhysicsAndLockGroundHeight()
        {
            if (_originTransform == null) return;

            Vector3 currentPos = _originTransform.position;
            float groundY = GetGroundHeightAt(GetPlayerFeetWorldPos(currentPos));

            // Safety respawn if fallen off into the deep abyss below the terrain platter
            if (currentPos.y < -22f || (groundY < -90f && currentPos.y < -5f))
            {
                Debug.Log("[AudioFloor] User fell off terrain platter! Respawning on glass stage.");
                Transform camTr = GetHeadCameraTransform();
                if (camTr != null)
                {
                    Vector3 camPos = camTr.position;
                    currentPos = new Vector3(currentPos.x - camPos.x, 0f, currentPos.z - camPos.z);
                }
                else
                {
                    currentPos = new Vector3(0f, 0f, 0f);
                }
                _verticalVelocity = 0f;
                _isJumping = false;
                _originTransform.position = currentPos;
                return;
            }

            float dt = Mathf.Min(Time.deltaTime, 0.05f);

            // If jumping OR airborne (e.g. walked or jumped off the glass stage at y=0 down to the terrain at y=-11.0)
            if (_isJumping || currentPos.y > groundY + 0.001f)
            {
                _verticalVelocity -= _gravity * dt;
                currentPos.y += _verticalVelocity * dt;

                if (currentPos.y <= groundY)
                {
                    // Landed on ground!
                    currentPos.y = groundY;
                    _verticalVelocity = 0f;
                    _isJumping = false;
                }
                _originTransform.position = currentPos;
            }
            else
            {
                // Solidly on the ground
                _verticalVelocity = 0f;
                _isJumping = false;
                if (Mathf.Abs(currentPos.y - groundY) > 0.0005f)
                {
                    currentPos.y = groundY;
                    _originTransform.position = currentPos;
                }
            }
        }

        private void UpdateArtificialLocomotion()
        {
#if UNITY_EDITOR || UNITY_STANDALONE
            bool isTyping = _spatialUI != null && _spatialUI.IsSearchFocused;
            if (!isTyping && Keyboard.current != null)
            {
                Vector2 kbMove = Vector2.zero;
                if (Keyboard.current.wKey.isPressed || Keyboard.current.wKey.wasPressedThisFrame) kbMove.y += 1f;
                if (Keyboard.current.sKey.isPressed || Keyboard.current.sKey.wasPressedThisFrame) kbMove.y -= 1f;
                if (Keyboard.current.dKey.isPressed || Keyboard.current.dKey.wasPressedThisFrame) kbMove.x += 1f;
                if (Keyboard.current.aKey.isPressed || Keyboard.current.aKey.wasPressedThisFrame) kbMove.x -= 1f;

                if (kbMove.sqrMagnitude > 0.01f)
                {
                    Transform camTr = (_xrOrigin != null && _xrOrigin.Camera != null) ? _xrOrigin.Camera.transform : (Camera.main != null ? Camera.main.transform : transform);
                    Vector3 fwd = Vector3.ProjectOnPlane(camTr.forward, Vector3.up).normalized;
                    Vector3 right = Vector3.ProjectOnPlane(camTr.right, Vector3.up).normalized;
                    _walkImpulseDir = (fwd * kbMove.y + right * kbMove.x).normalized;
                    _walkImpulseTimer = 0.55f;
                    Debug.Log($"[AudioFloor] Walking on stage: dir={_walkImpulseDir}, pos={(_originTransform != null ? _originTransform.position : Vector3.zero)}");
                }
            }

            if (_walkImpulseTimer > 0f)
            {
                _walkImpulseTimer -= Time.deltaTime;
                Vector3 moveDelta = _walkImpulseDir * (1.8f * Time.deltaTime);
                if (_originTransform != null)
                {
                    Vector3 nextPos = _originTransform.position + moveDelta;
                    nextPos.x = Mathf.Clamp(nextPos.x, -1.4f, 1.4f);
                    nextPos.z = Mathf.Clamp(nextPos.z, -1.4f, 1.4f);
                    _originTransform.position = nextPos;
                }
            }
#endif

#if UNITY_EDITOR
            // DynamicMoveProvider on XR Origin already moves at native speed (2.5 m/s) on Quest.
            // SnapTurnProvider on XR Origin already performs 45-degree snap turning on Quest.
            // We only provide locomotion here as a fallback in Editor if XRI providers are inactive.
            if (Gamepad.current == null) return;

            bool hasXRMove = _editorMoveProvider != null && _editorMoveProvider.isActiveAndEnabled;
            bool hasXRTurn = _editorTurnProvider != null && _editorTurnProvider.isActiveAndEnabled;

            if (!hasXRMove)
            {
                Vector2 moveInput = Gamepad.current.leftStick.ReadValue();
                if (moveInput.sqrMagnitude > 0.01f)
                {
                    Transform camTr = (_xrOrigin != null && _xrOrigin.Camera != null) ? _xrOrigin.Camera.transform : (Camera.main != null ? Camera.main.transform : transform);
                    Vector3 fwd = Vector3.ProjectOnPlane(camTr.forward, Vector3.up).normalized;
                    Vector3 right = Vector3.ProjectOnPlane(camTr.right, Vector3.up).normalized;
                    Vector3 moveDir = (fwd * moveInput.y + right * moveInput.x);
                    if (moveDir.sqrMagnitude > 1.0f) moveDir.Normalize();

                    Vector3 moveDelta = moveDir * (2.5f * Time.deltaTime);
                    if (_characterController != null && _characterController.enabled)
                    {
                        _characterController.Move(moveDelta);
                    }
                    else if (_originTransform != null)
                    {
                        _originTransform.position += moveDelta;
                    }
                }
            }

            if (!hasXRTurn)
            {
                Vector2 turnInput = Gamepad.current.rightStick.ReadValue();
                if (turnInput.x > 0.65f && !_wasRightStickTurn)
                {
                    _wasRightStickTurn = true;
                    PerformSnapTurn(45f);
                }
                else if (turnInput.x < -0.65f && !_wasRightStickTurn)
                {
                    _wasRightStickTurn = true;
                    PerformSnapTurn(-45f);
                }
                else if (Mathf.Abs(turnInput.x) < 0.25f)
                {
                    _wasRightStickTurn = false;
                }
            }
#endif
        }

#if UNITY_EDITOR
        private void PerformSnapTurn(float angle)
        {
            if (_originTransform == null) return;
            Transform camTr = (_xrOrigin != null && _xrOrigin.Camera != null) ? _xrOrigin.Camera.transform : (Camera.main != null ? Camera.main.transform : transform);
            Vector3 pivot = camTr.position;
            _originTransform.RotateAround(pivot, Vector3.up, angle);
        }
#endif

        public void CycleTheme(int direction)
        {
            if (_visualizer == null)
            {
                _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
                if (_visualizer == null) return;
            }

            if (AllThemePresets == null || AllThemePresets.Length == 0) return;

            int currentIndex = 0;
            for (int i = 0; i < AllThemePresets.Length; i++)
            {
                if (AllThemePresets[i] == _visualizer.activeTheme)
                {
                    currentIndex = i;
                    break;
                }
            }

            int nextIndex = (currentIndex + direction) % AllThemePresets.Length;
            if (nextIndex < 0) nextIndex += AllThemePresets.Length;

            SonicThemePreset nextTheme = AllThemePresets[nextIndex];
            _visualizer.activeTheme = nextTheme;
            _visualizer.colorMode = SonicColorMode.PresetThemes;

            PlayerPrefs.SetInt("SonicTopography_ActiveTheme", (int)nextTheme);
            PlayerPrefs.SetInt("SonicTopography_ColorMode", (int)SonicColorMode.PresetThemes);
            PlayerPrefs.Save();

            if (_spatialUI == null)
            {
                _spatialUI = FindFirstObjectByType<SonicSpatialUIManager>();
            }
            if (_spatialUI != null)
            {
                _spatialUI.RefreshSettingsUI();
            }
        }

        private void ReadInputs(
            out bool aDown,
            out bool bDown,
            out bool xDown,
            out bool yDown,
            out bool leftGripDown,
            out bool rightGripDown)
        {
            // Determine if bare hands are actively being used (without controllers)
            bool isBareHandActive = false;
            var activeCtrl = OVRInput.GetActiveController();
            if ((activeCtrl & (OVRInput.Controller.Hands | OVRInput.Controller.LHand | OVRInput.Controller.RHand)) != 0 &&
                (activeCtrl & (OVRInput.Controller.Touch | OVRInput.Controller.LTouch | OVRInput.Controller.RTouch)) == 0)
            {
                isBareHandActive = true;
            }
            if (UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager.currentInputMode.Value == UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager.InputMode.TrackedHand)
            {
                isBareHandActive = true;
            }

            // ------------------------------------------------------------------
            // 1. Right Hand Device (Button A = Jump, Button B = Play/Pause, Right Grip = Next Theme)
            // ------------------------------------------------------------------
            bool rightA = false;
            bool rightB = false;
            bool rightGrip = false;

            // Direct OVRInput for physical Right Touch controller
            if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch)) rightA = true;
            if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch)) rightB = true;
            if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch) ||
                (OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch) > 0.60f))
            {
                rightGrip = true;
            }

            // OpenXR InputDevices query (cached device handle & characteristics)
            if (!_rightXrDevice.isValid)
            {
                _rightXrDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                if (_rightXrDevice.isValid)
                {
                    string devName = _rightXrDevice.name;
                    _isRightDeviceHandTracking = (_rightXrDevice.characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                                                 (devName != null && devName.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            if (_rightXrDevice.isValid)
            {
                // Button A: Jump (Physical controller only, strictly ignore if bare hand tracking)
                if (!isBareHandActive && !_isRightDeviceHandTracking)
                {
                    if (_rightXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool aVal) && aVal) rightA = true;
                }

                // Button B: Play/Pause (Controllers only emit secondaryButton; hands do not)
                if (_rightXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool bVal) && bVal) rightB = true;

                // Right Grip: Next Theme
                if (_rightXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool gBtnVal) && gBtnVal) rightGrip = true;
                if (_rightXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float gVal) && gVal > 0.60f) rightGrip = true;
            }

            // New Input System XRController fallback (cached child controls)
            var xrRight = UnityEngine.InputSystem.XR.XRController.rightHand;
            if (xrRight != _cachedXrRight)
            {
                _cachedXrRight = xrRight;
                if (xrRight != null)
                {
                    _rightPrimaryCtrl = xrRight.TryGetChildControl<ButtonControl>("primaryButton") ?? xrRight.TryGetChildControl<ButtonControl>("buttonSouth");
                    _rightSecondaryCtrl = xrRight.TryGetChildControl<ButtonControl>("secondaryButton") ?? xrRight.TryGetChildControl<ButtonControl>("buttonNorth");
                    _rightGripBtnCtrl = xrRight.TryGetChildControl<ButtonControl>("gripButton");
                    _rightGripAxisCtrl = xrRight.TryGetChildControl<AxisControl>("grip");
                }
                else
                {
                    _rightPrimaryCtrl = null;
                    _rightSecondaryCtrl = null;
                    _rightGripBtnCtrl = null;
                    _rightGripAxisCtrl = null;
                }
            }
            if (xrRight != null && xrRight.added)
            {
                if (!isBareHandActive && _rightPrimaryCtrl != null && _rightPrimaryCtrl.isPressed) rightA = true;
                if (_rightSecondaryCtrl != null && _rightSecondaryCtrl.isPressed) rightB = true;
                if (_rightGripBtnCtrl != null && _rightGripBtnCtrl.isPressed) rightGrip = true;
                if (_rightGripAxisCtrl != null && _rightGripAxisCtrl.ReadValue() > 0.60f) rightGrip = true;
            }

            // ------------------------------------------------------------------
            // 2. Left Hand Device (Button X = Next Track, Button Y = Prev Track, Left Grip = Prev Theme)
            // ------------------------------------------------------------------
            bool leftX = false;
            bool leftY = false;
            bool leftGrip = false;

            // Direct OVRInput for physical Left Touch controller
            if (OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch)) leftX = true;
            if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch)) leftY = true;
            if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) ||
                (OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch) > 0.60f))
            {
                leftGrip = true;
            }

            // OpenXR InputDevices query (cached device handle & characteristics)
            if (!_leftXrDevice.isValid)
            {
                _leftXrDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                if (_leftXrDevice.isValid)
                {
                    string devName = _leftXrDevice.name;
                    _isLeftDeviceHandTracking = (_leftXrDevice.characteristics & InputDeviceCharacteristics.HandTracking) != 0 ||
                                                (devName != null && devName.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            if (_leftXrDevice.isValid)
            {
                // Button X: Next Track (Ignore on bare hand pinch)
                if (!isBareHandActive && !_isLeftDeviceHandTracking)
                {
                    if (_leftXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool xVal) && xVal) leftX = true;
                }

                // Button Y: Previous Track
                if (_leftXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool yVal) && yVal) leftY = true;

                // Left Grip: Previous Theme
                if (_leftXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool gBtnVal) && gBtnVal) leftGrip = true;
                if (_leftXrDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float gVal) && gVal > 0.60f) leftGrip = true;
            }

            // New Input System XRController fallback (cached child controls)
            var xrLeft = UnityEngine.InputSystem.XR.XRController.leftHand;
            if (xrLeft != _cachedXrLeft)
            {
                _cachedXrLeft = xrLeft;
                if (xrLeft != null)
                {
                    _leftPrimaryCtrl = xrLeft.TryGetChildControl<ButtonControl>("primaryButton") ?? xrLeft.TryGetChildControl<ButtonControl>("buttonSouth");
                    _leftSecondaryCtrl = xrLeft.TryGetChildControl<ButtonControl>("secondaryButton") ?? xrLeft.TryGetChildControl<ButtonControl>("buttonNorth");
                    _leftGripBtnCtrl = xrLeft.TryGetChildControl<ButtonControl>("gripButton");
                    _leftGripAxisCtrl = xrLeft.TryGetChildControl<AxisControl>("grip");
                }
                else
                {
                    _leftPrimaryCtrl = null;
                    _leftSecondaryCtrl = null;
                    _leftGripBtnCtrl = null;
                    _leftGripAxisCtrl = null;
                }
            }
            if (xrLeft != null && xrLeft.added)
            {
                if (!isBareHandActive && _leftPrimaryCtrl != null && _leftPrimaryCtrl.isPressed) leftX = true;
                if (_leftSecondaryCtrl != null && _leftSecondaryCtrl.isPressed) leftY = true;
                if (_leftGripBtnCtrl != null && _leftGripBtnCtrl.isPressed) leftGrip = true;
                if (_leftGripAxisCtrl != null && _leftGripAxisCtrl.ReadValue() > 0.60f) leftGrip = true;
            }

            // ------------------------------------------------------------------
            // 3. Edge-Detection (Down frame only)
            // ------------------------------------------------------------------
            aDown = rightA && !_wasRightPrimary;
            _wasRightPrimary = rightA;

            bDown = rightB && !_wasRightSecondary;
            _wasRightSecondary = rightB;

            xDown = leftX && !_wasLeftPrimary;
            _wasLeftPrimary = leftX;

            yDown = leftY && !_wasLeftSecondary;
            _wasLeftSecondary = leftY;

            leftGripDown = leftGrip && !_wasLeftGrip;
            _wasLeftGrip = leftGrip;

            rightGripDown = rightGrip && !_wasRightGrip;
            _wasRightGrip = rightGrip;

#if UNITY_EDITOR || UNITY_STANDALONE
            // ------------------------------------------------------------------
            // 4. Desktop / Editor Keyboard Fallback (Ignored when typing in search)
            // ------------------------------------------------------------------
            bool isTyping = _spatialUI != null && _spatialUI.IsSearchFocused;
            if (!isTyping && Keyboard.current != null)
            {
                if (Keyboard.current.spaceKey.wasPressedThisFrame) aDown = true;
                if (Keyboard.current.pKey.wasPressedThisFrame) bDown = true;
                if (Keyboard.current.nKey.wasPressedThisFrame || Keyboard.current.rightArrowKey.wasPressedThisFrame) xDown = true;
                if (Keyboard.current.bKey.wasPressedThisFrame || Keyboard.current.leftArrowKey.wasPressedThisFrame) yDown = true;
                if (Keyboard.current.leftBracketKey.wasPressedThisFrame) leftGripDown = true;
                if (Keyboard.current.rightBracketKey.wasPressedThisFrame) rightGripDown = true;
            }
#endif
        }
    }
}
