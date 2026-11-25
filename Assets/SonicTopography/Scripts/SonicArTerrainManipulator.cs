using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;

namespace SonicTopography
{
    /// <summary>
    /// Handles Quest 3 AR interaction for both controllers and bare hands:
    /// - 1-hand Grip / Pinch drag with soft magnetic surface snapping (Tables, Floors, Walls)
    /// - 2-hand Grip / Pinch uniform scaling (0.28m to 3.60m diameter)
    /// - Index Trigger tap / Hand index poke ripples that spill into the scanned room
    /// </summary>
    public class SonicArTerrainManipulator : MonoBehaviour
    {
        private struct HandInputState
        {
            public bool isRight;
            public bool isBareHand;
            public bool gripPressed;
            public bool triggerPressed;
            public bool gripDown;
            public bool triggerDown;
            public bool hasRay;
            public Ray ray;
            public bool hasGrabPoint;
            public Vector3 grabPointWS;
            public bool hasPokeTip;
            public Vector3 pokeTipWS;
        }

        private SonicMixedRealityManager _mr;
        private SonicTopographyVisualizer _visualizer;
        private SonicArKeychainController _keychain;
        private XROrigin _xrOrigin;

        private GameObject _colliderGo;
        private CapsuleCollider _terrainGrabCollider;

        private bool _prevLeftGrip;
        private bool _prevRightGrip;
        private bool _prevLeftTrigger;
        private bool _prevRightTrigger;
        private bool _wasLeftPinching;
        private bool _wasRightPinching;

        // Terrain grab state
        private bool _grabbingTerrainLeft;
        private bool _grabbingTerrainRight;
        private bool _grabbingKeychainLeft;
        private bool _grabbingKeychainRight;

        private Vector3 _singleGrabLocalOffsetWS;
        private float _singleGrabRayDist;
        private bool _singleGrabViaRay;

        private float _twoHandInitialDist;
        private float _twoHandInitialScale;
        private Vector3 _twoHandInitialMidToCenter;

        private Vector3 _smoothedTargetPos;
        private Quaternion _smoothedTargetRot;
        private float _smoothedTargetScale;
        private bool _hasSmoothedPose;

        private float _leftPokeCooldownUntil;
        private float _rightPokeCooldownUntil;

        private UnityEngine.XR.InputDevice _leftDevice;
        private UnityEngine.XR.InputDevice _rightDevice;
        private UnityEngine.InputSystem.XR.XRController _cachedLeftCtrl;
        private UnityEngine.InputSystem.XR.XRController _cachedRightCtrl;
        private ButtonControl _leftGripBtn;
        private AxisControl _leftGripAxis;
        private ButtonControl _leftTrigBtn;
        private AxisControl _leftTrigAxis;
        private ButtonControl _rightGripBtn;
        private AxisControl _rightGripAxis;
        private ButtonControl _rightTrigBtn;
        private AxisControl _rightTrigAxis;

        private XRHandSubsystem _handSubsystem;
        private static readonly List<XRHandSubsystem> s_handSubsystems = new List<XRHandSubsystem>();

        public bool IsGrabbingAny => _grabbingTerrainLeft || _grabbingTerrainRight || _grabbingKeychainLeft || _grabbingKeychainRight;
        public bool IsGrabbingTerrain => _grabbingTerrainLeft || _grabbingTerrainRight;

        public void Initialize(
            SonicMixedRealityManager mr,
            SonicTopographyVisualizer visualizer,
            SonicArKeychainController keychain)
        {
            _mr = mr;
            _visualizer = visualizer;
            _keychain = keychain;
            _xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
        }

        private void EnsureTerrainColliderAttached()
        {
            if (_visualizer == null || _visualizer.PlatterRoot == null) return;
            Transform platterRoot = _visualizer.PlatterRoot.transform;

            if (_colliderGo == null)
            {
                _colliderGo = new GameObject("SonicArTerrainGrabCollider");
                _colliderGo.transform.SetParent(platterRoot, false);
                _colliderGo.transform.localPosition = Vector3.zero;
                _colliderGo.transform.localRotation = Quaternion.identity;
                _colliderGo.transform.localScale = Vector3.one;
                _colliderGo.layer = 2; // Ignore Raycast for XRI/default physics while we test _terrainGrabCollider.Raycast directly

                _terrainGrabCollider = _colliderGo.AddComponent<CapsuleCollider>();
                _terrainGrabCollider.direction = 1; // Y axis
                _terrainGrabCollider.radius = SonicMixedRealityManager.PlatterLocalRadiusUnits;
                _terrainGrabCollider.height = 18f;
                _terrainGrabCollider.center = new Vector3(0f, 3f, 0f);
                _terrainGrabCollider.isTrigger = true;
            }

            bool activeInAr = SonicMixedRealityManager.IsArControlActive;
            if (_colliderGo.activeSelf != activeInAr)
            {
                _colliderGo.SetActive(activeInAr);
            }
        }

        private void Update()
        {
            if (_mr == null || _visualizer == null) return;
            EnsureTerrainColliderAttached();

            if (!SonicMixedRealityManager.IsArControlActive || _mr.IsTransitioning)
            {
                ClearGrabStates();
                return;
            }

            HandInputState left = PollHandState(isRight: false);
            HandInputState right = PollHandState(isRight: true);

            bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

            HandleKeychainGrab(left, right, pointerOverUi);
            HandleTerrainGrabAndScale(left, right, pointerOverUi);
            HandleTerrainTapAndPokeRipples(left, right, pointerOverUi);
        }

        private void ClearGrabStates()
        {
            _grabbingTerrainLeft = false;
            _grabbingTerrainRight = false;
            _grabbingKeychainLeft = false;
            _grabbingKeychainRight = false;
            _hasSmoothedPose = false;
            _keychain?.SetDragged(false);
        }

        private void HandleKeychainGrab(in HandInputState left, in HandInputState right, bool pointerOverUi)
        {
            if (_keychain == null || !_keychain.IsVisible) return;

            if (_grabbingKeychainLeft && !left.gripPressed) _grabbingKeychainLeft = false;
            if (_grabbingKeychainRight && !right.gripPressed) _grabbingKeychainRight = false;

            if (!pointerOverUi)
            {
                if (left.gripDown && !_grabbingTerrainLeft && !_grabbingKeychainRight && CanGrabKeychain(left))
                {
                    _grabbingKeychainLeft = true;
                    _keychain.BeginGrab(left.hasRay ? left.ray : new Ray(left.grabPointWS, Vector3.forward), left.grabPointWS, !left.hasGrabPoint);
                }
                else if (right.gripDown && !_grabbingTerrainRight && !_grabbingKeychainLeft && CanGrabKeychain(right))
                {
                    _grabbingKeychainRight = true;
                    _keychain.BeginGrab(right.hasRay ? right.ray : new Ray(right.grabPointWS, Vector3.forward), right.grabPointWS, !right.hasGrabPoint);
                }
            }

            if (_grabbingKeychainLeft)
            {
                _keychain.UpdateGrab(left.hasRay ? left.ray : new Ray(left.grabPointWS, Vector3.forward), left.grabPointWS);
            }
            else if (_grabbingKeychainRight)
            {
                _keychain.UpdateGrab(right.hasRay ? right.ray : new Ray(right.grabPointWS, Vector3.forward), right.grabPointWS);
            }
            else
            {
                _keychain.SetDragged(false);
            }
        }

        private bool CanGrabKeychain(in HandInputState hand)
        {
            Collider kcCol = _keychain != null ? _keychain.GrabCollider : null;
            if (kcCol == null || !kcCol.enabled) return false;
            if (hand.hasGrabPoint && Vector3.Distance(hand.grabPointWS, _keychain.transform.position) <= 0.22f)
            {
                return true;
            }
            return hand.hasRay && kcCol.Raycast(hand.ray, out _, 12f);
        }

        private void HandleTerrainGrabAndScale(in HandInputState left, in HandInputState right, bool pointerOverUi)
        {
            _mr.GetCurrentTerrainPose(out Vector3 curPos, out Quaternion curRot, out float curScale, out float curRadius);
            if (!_hasSmoothedPose)
            {
                _smoothedTargetPos = curPos;
                _smoothedTargetRot = curRot;
                _smoothedTargetScale = curScale;
                _hasSmoothedPose = true;
            }

            bool prevL = _grabbingTerrainLeft;
            bool prevR = _grabbingTerrainRight;

            if (_grabbingTerrainLeft && !left.gripPressed) _grabbingTerrainLeft = false;
            if (_grabbingTerrainRight && !right.gripPressed) _grabbingTerrainRight = false;

            if (!pointerOverUi)
            {
                if (!_grabbingTerrainLeft && !_grabbingKeychainLeft && left.gripDown && CanGrabTerrain(left, curPos, curRot, curRadius))
                {
                    _grabbingTerrainLeft = true;
                    if (_grabbingTerrainRight)
                    {
                        BeginTwoHandScale(left, right, curPos, curScale);
                    }
                    else
                    {
                        BeginSingleHandGrab(left, curPos);
                    }
                }
                if (!_grabbingTerrainRight && !_grabbingKeychainRight && right.gripDown && CanGrabTerrain(right, curPos, curRot, curRadius))
                {
                    _grabbingTerrainRight = true;
                    if (_grabbingTerrainLeft)
                    {
                        BeginTwoHandScale(left, right, curPos, curScale);
                    }
                    else
                    {
                        BeginSingleHandGrab(right, curPos);
                    }
                }
            }

            // Handle 2-hand -> 1-hand handoff cleanly
            if (prevL && prevR)
            {
                if (_grabbingTerrainLeft && !_grabbingTerrainRight) BeginSingleHandGrab(left, curPos);
                else if (_grabbingTerrainRight && !_grabbingTerrainLeft) BeginSingleHandGrab(right, curPos);
            }

            if (!_grabbingTerrainLeft && !_grabbingTerrainRight) return;

            bool snapped = _mr.IsSnappedToSurface;
            Vector3 surfNormal = _mr.CurrentSurfaceNormal;
            Vector3 desiredPos = curPos;
            Quaternion desiredRot = curRot;
            float desiredScale = curScale;

            if (_grabbingTerrainLeft && _grabbingTerrainRight)
            {
                Vector3 pL = GetEffectiveHandPoint(left, curPos);
                Vector3 pR = GetEffectiveHandPoint(right, curPos);
                float dist = Mathf.Max(0.05f, Vector3.Distance(pL, pR));
                float ratio = dist / Mathf.Max(0.05f, _twoHandInitialDist);

                float minScale = SonicMixedRealityManager.MinArDiameterMeters / (SonicMixedRealityManager.PlatterLocalRadiusUnits * 2f);
                float maxScale = SonicMixedRealityManager.MaxArDiameterMeters / (SonicMixedRealityManager.PlatterLocalRadiusUnits * 2f);
                desiredScale = Mathf.Clamp(_twoHandInitialScale * ratio, minScale, maxScale);

                Vector3 mid = (pL + pR) * 0.5f;
                desiredPos = mid + _twoHandInitialMidToCenter * (desiredScale / Mathf.Max(0.0001f, _twoHandInitialScale));
                if (snapped && _mr.SurfaceSnapEnabled)
                {
                    if (_mr.TryFindBestSurfaceSnap(desiredPos, SonicMixedRealityManager.PlatterLocalRadiusUnits * desiredScale, true, out Vector3 sPos, out Quaternion sRot, out Vector3 sNorm))
                    {
                        desiredPos = sPos;
                        desiredRot = sRot;
                        surfNormal = sNorm;
                    }
                }
            }
            else
            {
                HandInputState activeHand = _grabbingTerrainRight ? right : left;
                if (_singleGrabViaRay && activeHand.hasRay)
                {
                    // Allow depth sliding slightly via ray anchor point
                    desiredPos = activeHand.ray.origin + activeHand.ray.direction * _singleGrabRayDist + _singleGrabLocalOffsetWS;
                }
                else if (activeHand.hasGrabPoint)
                {
                    desiredPos = activeHand.grabPointWS + _singleGrabLocalOffsetWS;
                }

                float radius = SonicMixedRealityManager.PlatterLocalRadiusUnits * desiredScale;
                if (_mr.TryFindBestSurfaceSnap(desiredPos, radius, snapped, out Vector3 snapPos, out Quaternion snapRot, out Vector3 snapNorm))
                {
                    desiredPos = snapPos;
                    desiredRot = snapRot;
                    surfNormal = snapNorm;
                    snapped = true;
                }
                else
                {
                    snapped = false;
                    surfNormal = Vector3.up;
                    desiredRot = Quaternion.Euler(0f, curRot.eulerAngles.y, 0f);
                }
            }

            float posLerp = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            float rotLerp = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            float scaleLerp = 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime);

            _smoothedTargetPos = Vector3.Lerp(_smoothedTargetPos, desiredPos, posLerp);
            _smoothedTargetRot = Quaternion.Slerp(_smoothedTargetRot, desiredRot, rotLerp);
            _smoothedTargetScale = Mathf.Lerp(_smoothedTargetScale, desiredScale, scaleLerp);

            _mr.SetArTerrainPose(_smoothedTargetPos, _smoothedTargetRot, _smoothedTargetScale, snapped, surfNormal);
        }

        private void BeginSingleHandGrab(in HandInputState hand, Vector3 terrainCenterWS)
        {
            if (hand.hasRay && _terrainGrabCollider != null && _terrainGrabCollider.Raycast(hand.ray, out RaycastHit hit, 14f))
            {
                _singleGrabViaRay = true;
                _singleGrabRayDist = hit.distance;
                _singleGrabLocalOffsetWS = terrainCenterWS - hit.point;
            }
            else if (hand.hasGrabPoint)
            {
                _singleGrabViaRay = false;
                _singleGrabLocalOffsetWS = terrainCenterWS - hand.grabPointWS;
            }
            else if (hand.hasRay)
            {
                _singleGrabViaRay = true;
                _singleGrabRayDist = Mathf.Clamp(Vector3.Distance(hand.ray.origin, terrainCenterWS), 0.25f, 8f);
                Vector3 anchorPt = hand.ray.origin + hand.ray.direction * _singleGrabRayDist;
                _singleGrabLocalOffsetWS = terrainCenterWS - anchorPt;
            }
        }

        private void BeginTwoHandScale(in HandInputState left, in HandInputState right, Vector3 terrainCenterWS, float currentScale)
        {
            Vector3 pL = GetEffectiveHandPoint(left, terrainCenterWS);
            Vector3 pR = GetEffectiveHandPoint(right, terrainCenterWS);
            _twoHandInitialDist = Mathf.Max(0.06f, Vector3.Distance(pL, pR));
            _twoHandInitialScale = currentScale;
            _twoHandInitialMidToCenter = terrainCenterWS - (pL + pR) * 0.5f;
        }

        private static Vector3 GetEffectiveHandPoint(in HandInputState hand, Vector3 fallbackPlaneCenter)
        {
            if (hand.hasGrabPoint) return hand.grabPointWS;
            if (hand.hasRay)
            {
                float dist = Mathf.Clamp(Vector3.Distance(hand.ray.origin, fallbackPlaneCenter), 0.3f, 6f);
                return hand.ray.origin + hand.ray.direction * dist;
            }
            return fallbackPlaneCenter;
        }

        private bool CanGrabTerrain(in HandInputState hand, Vector3 terrainCenterWS, Quaternion terrainRot, float terrainRadius)
        {
            if (_terrainGrabCollider == null) return false;
            if (hand.hasGrabPoint)
            {
                Vector3 rel = Quaternion.Inverse(terrainRot) * (hand.grabPointWS - terrainCenterWS);
                float xzDist = new Vector2(rel.x, rel.z).magnitude;
                if (xzDist <= terrainRadius + 0.14f && Mathf.Abs(rel.y) <= 0.22f)
                {
                    return true;
                }
            }
            return hand.hasRay && _terrainGrabCollider.Raycast(hand.ray, out _, 14f);
        }

        private void HandleTerrainTapAndPokeRipples(in HandInputState left, in HandInputState right, bool pointerOverUi)
        {
            if (_visualizer == null || !_visualizer.enableInteractiveRipples || _terrainGrabCollider == null) return;
            if (pointerOverUi) return;

            Transform platterT = _visualizer.PlatterRoot != null ? _visualizer.PlatterRoot.transform : null;
            if (platterT == null) return;

            if (!_grabbingTerrainLeft && !_grabbingKeychainLeft && left.triggerDown && left.hasRay &&
                _terrainGrabCollider.Raycast(left.ray, out RaycastHit leftHit, 14f))
            {
                FireRippleFromWorldHit(platterT, leftHit.point, 1.35f, isRight: false, haptic: !left.isBareHand);
            }

            if (!_grabbingTerrainRight && !_grabbingKeychainRight && right.triggerDown && right.hasRay &&
                _terrainGrabCollider.Raycast(right.ray, out RaycastHit rightHit, 14f))
            {
                FireRippleFromWorldHit(platterT, rightHit.point, 1.35f, isRight: true, haptic: !right.isBareHand);
            }

            // Direct bare-hand index fingertip poke into the sculpture surface
            TryHandPokeRipple(platterT, left, ref _leftPokeCooldownUntil);
            TryHandPokeRipple(platterT, right, ref _rightPokeCooldownUntil);
        }

        private void TryHandPokeRipple(Transform platterT, in HandInputState hand, ref float cooldownUntil)
        {
            if (!hand.hasPokeTip || Time.unscaledTime < cooldownUntil) return;
            Vector3 local = platterT.InverseTransformPoint(hand.pokeTipWS);
            float radial = new Vector2(local.x, local.z).magnitude;
            if (radial <= SonicMixedRealityManager.PlatterLocalRadiusUnits)
            {
                float worldHeightOffset = Mathf.Abs(local.y) * platterT.lossyScale.y;
                if (worldHeightOffset <= 0.040f)
                {
                    cooldownUntil = Time.unscaledTime + 0.24f;
                    _visualizer.TriggerRippleAtLocalPos(new Vector2(local.x, local.z), 1.35f, false);
                }
            }
        }

        private void FireRippleFromWorldHit(Transform platterT, Vector3 hitWS, float strength, bool isRight, bool haptic)
        {
            Vector3 local = platterT.InverseTransformPoint(hitWS);
            if (new Vector2(local.x, local.z).magnitude <= SonicMixedRealityManager.PlatterLocalRadiusUnits + 1.0f)
            {
                _visualizer.TriggerRippleAtLocalPos(new Vector2(local.x, local.z), strength, false);
                if (haptic)
                {
                    UnityEngine.XR.InputDevice dev = isRight ? _rightDevice : _leftDevice;
                    if (dev.isValid)
                    {
                        dev.SendHapticImpulse(0, 0.22f, 0.045f);
                    }
                }
            }
        }

        private HandInputState PollHandState(bool isRight)
        {
            HandInputState st = new HandInputState { isRight = isRight };

            XRNode node = isRight ? XRNode.RightHand : XRNode.LeftHand;
            ref UnityEngine.XR.InputDevice dev = ref (isRight ? ref _rightDevice : ref _leftDevice);
            if (!dev.isValid)
            {
                dev = InputDevices.GetDeviceAtXRNode(node);
            }

            bool isHandTrackDevice = dev.isValid && ((dev.characteristics & InputDeviceCharacteristics.HandTracking) != 0);
            st.isBareHand = isHandTrackDevice;

            bool grip = false;
            bool trig = false;

            // 1. Bare hand tracking pinch + poke fingertip from XRHandSubsystem
            EnsureHandSubsystem();
            bool barePinch = false;
            if (_handSubsystem != null && _handSubsystem.running)
            {
                XRHand xrHand = isRight ? _handSubsystem.rightHand : _handSubsystem.leftHand;
                if (xrHand.isTracked)
                {
                    Transform originOffset = _xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null
                        ? _xrOrigin.CameraFloorOffsetObject.transform
                        : (_xrOrigin != null ? _xrOrigin.transform : null);

                    bool hasThumb = xrHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose);
                    bool hasIndex = xrHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexPose);
                    if (hasIndex)
                    {
                        Vector3 idxWS = originOffset != null ? originOffset.TransformPoint(indexPose.position) : indexPose.position;
                        st.hasPokeTip = true;
                        st.pokeTipWS = idxWS;
                        if (hasThumb)
                        {
                            Vector3 thumbWS = originOffset != null ? originOffset.TransformPoint(thumbPose.position) : thumbPose.position;
                            Vector3 pinchMidWS = (idxWS + thumbWS) * 0.5f;
                            st.hasGrabPoint = true;
                            st.grabPointWS = pinchMidWS;

                            float pinchDist = Vector3.Distance(idxWS, thumbWS);
                            ref bool wasPinch = ref (isRight ? ref _wasRightPinching : ref _wasLeftPinching);
                            barePinch = wasPinch ? (pinchDist < 0.042f) : (pinchDist < 0.026f);
                            wasPinch = barePinch;
                        }
                    }
                }
            }

            // 2. OVRInput physical controller (Touch Plus)
            OVRInput.Controller ovrCtrl = isRight ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;
            if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, ovrCtrl) ||
                OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, ovrCtrl) > 0.60f)
            {
                grip = true;
            }
            if (OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, ovrCtrl) ||
                OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ovrCtrl) > 0.55f)
            {
                trig = true;
            }

            // 3. OpenXR InputDevice fallback + pose
            if (dev.isValid)
            {
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool gBtn) && gBtn) grip = true;
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float gAxis) && gAxis > 0.60f) grip = true;
                if (!st.isBareHand)
                {
                    if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool tBtn) && tBtn) trig = true;
                    if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float tAxis) && tAxis > 0.55f) trig = true;
                }

                if (!st.hasGrabPoint && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 devLocalPos))
                {
                    Transform offset = _xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null
                        ? _xrOrigin.CameraFloorOffsetObject.transform
                        : (_xrOrigin != null ? _xrOrigin.transform : null);
                    st.hasGrabPoint = true;
                    st.grabPointWS = offset != null ? offset.TransformPoint(devLocalPos) : devLocalPos;
                }
            }

            // 4. Unity Input System XRController fallback
            var xrCtrl = isRight ? UnityEngine.InputSystem.XR.XRController.rightHand : UnityEngine.InputSystem.XR.XRController.leftHand;
            PollInputSystemControls(xrCtrl, isRight, ref grip, ref trig, st.isBareHand);

            if (barePinch)
            {
                st.isBareHand = true;
                grip = true;
            }

            // Pointer ray from SonicMetaQuestController or XRI hand ray
            Transform pointerT = SonicMetaQuestController.GetPointerTransform(node);
            if (pointerT != null)
            {
                st.hasRay = true;
                st.ray = new Ray(pointerT.position, pointerT.forward);
            }
            else if (st.hasGrabPoint && Camera.main != null)
            {
                Vector3 aimDir = (st.grabPointWS - Camera.main.transform.position).normalized;
                if (aimDir.sqrMagnitude > 0.01f)
                {
                    st.hasRay = true;
                    st.ray = new Ray(st.grabPointWS, aimDir);
                }
            }

#if UNITY_EDITOR
            if (isRight && Mouse.current != null && Camera.main != null && !st.hasRay)
            {
                st.hasRay = true;
                st.ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
                if (Mouse.current.rightButton.isPressed) grip = true;
                if (Mouse.current.leftButton.isPressed) trig = true;
            }
#endif

            ref bool prevGrip = ref (isRight ? ref _prevRightGrip : ref _prevLeftGrip);
            ref bool prevTrig = ref (isRight ? ref _prevRightTrigger : ref _prevLeftTrigger);

            st.gripPressed = grip;
            st.triggerPressed = trig;
            st.gripDown = grip && !prevGrip;
            st.triggerDown = trig && !prevTrig;

            prevGrip = grip;
            prevTrig = trig;

            return st;
        }

        private void PollInputSystemControls(
            UnityEngine.InputSystem.XR.XRController xrCtrl,
            bool isRight,
            ref bool grip,
            ref bool trig,
            bool isBareHand)
        {
            if (isRight)
            {
                if (xrCtrl != _cachedRightCtrl)
                {
                    _cachedRightCtrl = xrCtrl;
                    _rightGripBtn = xrCtrl?.TryGetChildControl<ButtonControl>("gripButton");
                    _rightGripAxis = xrCtrl?.TryGetChildControl<AxisControl>("grip");
                    _rightTrigBtn = xrCtrl?.TryGetChildControl<ButtonControl>("triggerPressed");
                    _rightTrigAxis = xrCtrl?.TryGetChildControl<AxisControl>("trigger");
                }
                if (xrCtrl != null && xrCtrl.added)
                {
                    if (_rightGripBtn != null && _rightGripBtn.isPressed) grip = true;
                    if (_rightGripAxis != null && _rightGripAxis.ReadValue() > 0.60f) grip = true;
                    if (!isBareHand)
                    {
                        if (_rightTrigBtn != null && _rightTrigBtn.isPressed) trig = true;
                        if (_rightTrigAxis != null && _rightTrigAxis.ReadValue() > 0.55f) trig = true;
                    }
                }
            }
            else
            {
                if (xrCtrl != _cachedLeftCtrl)
                {
                    _cachedLeftCtrl = xrCtrl;
                    _leftGripBtn = xrCtrl?.TryGetChildControl<ButtonControl>("gripButton");
                    _leftGripAxis = xrCtrl?.TryGetChildControl<AxisControl>("grip");
                    _leftTrigBtn = xrCtrl?.TryGetChildControl<ButtonControl>("triggerPressed");
                    _leftTrigAxis = xrCtrl?.TryGetChildControl<AxisControl>("trigger");
                }
                if (xrCtrl != null && xrCtrl.added)
                {
                    if (_leftGripBtn != null && _leftGripBtn.isPressed) grip = true;
                    if (_leftGripAxis != null && _leftGripAxis.ReadValue() > 0.60f) grip = true;
                    if (!isBareHand)
                    {
                        if (_leftTrigBtn != null && _leftTrigBtn.isPressed) trig = true;
                        if (_leftTrigAxis != null && _leftTrigAxis.ReadValue() > 0.55f) trig = true;
                    }
                }
            }
        }

        private void EnsureHandSubsystem()
        {
            if (_handSubsystem != null && _handSubsystem.running) return;
            SubsystemManager.GetSubsystems(s_handSubsystems);
            for (int i = 0; i < s_handSubsystems.Count; i++)
            {
                if (s_handSubsystems[i] != null && s_handSubsystems[i].running)
                {
                    _handSubsystem = s_handSubsystems[i];
                    return;
                }
            }
        }

        private void OnDestroy()
        {
            if (_colliderGo != null)
            {
                Destroy(_colliderGo);
            }
        }
    }
}
