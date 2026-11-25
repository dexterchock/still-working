using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using XRInputDevice = UnityEngine.XR.InputDevice;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace SonicTopography
{
    /// <summary>
    /// Official Meta Quest Touch Plus controller driver for Quest 3 / Quest 3S.
    /// Tracks the official com.meta.xr.sdk.core controller model using Meta's official VRAPI / OVR anchor pose conversion
    /// from OpenXR Grip Pose, with locked unrotated physical 10.8cm scale calibration, music-reactive dark-room shading,
    /// and pre-render ray trimming so the pointer line never overlaps the controller body.
    /// </summary>
    [DisallowMultipleComponent]
    public class SonicMetaQuestController : MonoBehaviour
    {
        public enum ControllerHand
        {
            Left,
            Right
        }

        [Header("Hand Setup")]
        [SerializeField] private ControllerHand hand = ControllerHand.Right;

        // Hide 3D Touch Plus meshes in AR (user sees physical controllers in passthrough), while keeping pointer rays/dots.
        public static bool HideControllerModelsInAr = true;

        [Header("Official Model References")]
        [SerializeField] private Transform modelContainer;
        [SerializeField] private GameObject modelRoot;
        [SerializeField] private Animator controllerAnimator;
        [SerializeField] private SkinnedMeshRenderer meshRenderer;

        private XRNode _xrNode;
        private XRInputDevice _inputDevice;
        private bool _hasDevice;

        // Scale lock state (measured once at Quaternion.identity so wrist rotation never changes scale)
        private bool _scaleCalibrated = false;
        private int _trackedFramesAfterWake = 0;
        private Vector3 _lockedLocalScale = Vector3.one;
        private bool _materialApplied = false;

        // Cached LineRenderer on the XRI controller so we can push its start point in front of the nozzle before rendering
        private LineRenderer _cachedLineRenderer;
        private bool _lineRendererSearched = false;

        // Cached InputSystem child controls to avoid per-frame string lookups
        private UnityEngine.InputSystem.XR.XRController _cachedInputCtrl;
        private ButtonControl _ctrlIsTracked;
        private Vector3Control _ctrlDevicePos;
        private QuaternionControl _ctrlDeviceRot;
        private ButtonControl _ctrlPrimaryBtn;
        private ButtonControl _ctrlSecondaryBtn;
        private ButtonControl _ctrlMenuBtn;
        private AxisControl _ctrlTrigger;
        private AxisControl _ctrlGrip;
        private Vector2Control _ctrlThumbstick;

        // Cached Animator parameter hashes
        private static readonly int HashButton1 = Animator.StringToHash("Button 1");
        private static readonly int HashButton2 = Animator.StringToHash("Button 2");
        private static readonly int HashButton3 = Animator.StringToHash("Button 3");
        private static readonly int HashTrigger = Animator.StringToHash("Trigger");
        private static readonly int HashGrip = Animator.StringToHash("Grip");
        private static readonly int HashJoyX = Animator.StringToHash("Joy X");
        private static readonly int HashJoyY = Animator.StringToHash("Joy Y");

        private static SonicMetaQuestController _leftInstance;
        private static SonicMetaQuestController _rightInstance;

        // Cached pointer transforms & modality/hand tracking references (zero per-frame GameObject.Find)
        private static Transform _leftPointerTransform;
        private static Transform _rightPointerTransform;
        private static bool _leftPointerSearched;
        private static bool _rightPointerSearched;
        private static XRInputModalityManager _modalityManager;
        private static bool _modalityManagerSearched;
        private static XRHandSubsystem _handSubsystem;
        private static readonly List<XRHandSubsystem> _sHandSubsystems = new List<XRHandSubsystem>(2);
        private static float _nextHandSubsystemSearchTime;

        // Official conversion matrix between OpenXR Grip Pose and Touch Plus FBX / VRAPI model pose.
        private static readonly Matrix4x4 GripToVrapiOffsetInverse = Matrix4x4.TRS(
            new Vector3(0f, -0.03f, -0.04f),
            new Quaternion(-0.5f, 0.0f, 0.0f, 0.86603f),
            Vector3.one
        ).inverse;

        // Distance in meters along the ray to trim so the white pointer line starts cleanly outside the front tip of the controller
        private const float RayNozzleStartOffsetMeters = 0.078f;

        public ControllerHand Hand => hand;
        public Transform ModelContainer => modelContainer;
        public GameObject ModelRoot => modelRoot;
        public Animator ControllerAnimator => controllerAnimator;

        private void Awake()
        {
            _xrNode = hand == ControllerHand.Right ? XRNode.RightHand : XRNode.LeftHand;
            if (hand == ControllerHand.Right) _rightInstance = this;
            else _leftInstance = this;

            _scaleCalibrated = false;
            _trackedFramesAfterWake = 0;
            _materialApplied = false;

            EnsureModelReferences();
            OffsetXriRayOriginForward();

            if (modelRoot != null)
            {
                modelRoot.SetActive(false);
            }
        }

        private void OnEnable()
        {
            InputDevices.deviceConnected += OnDeviceConnected;
            InputDevices.deviceDisconnected += OnDeviceDisconnected;
            Application.onBeforeRender += OnBeforeRenderTrimPointerLine;
            RefreshDevice();
        }

        private void OnDisable()
        {
            InputDevices.deviceConnected -= OnDeviceConnected;
            InputDevices.deviceDisconnected -= OnDeviceDisconnected;
            Application.onBeforeRender -= OnBeforeRenderTrimPointerLine;
        }

        private void OnDeviceConnected(XRInputDevice dev) => RefreshDevice();
        private void OnDeviceDisconnected(XRInputDevice dev) => RefreshDevice();

        private void RefreshDevice()
        {
            _inputDevice = InputDevices.GetDeviceAtXRNode(_xrNode);
            _hasDevice = _inputDevice.isValid;
        }

        public void Configure(ControllerHand controllerHand, Transform container, GameObject model, Animator animator, SkinnedMeshRenderer smr)
        {
            hand = controllerHand;
            _xrNode = hand == ControllerHand.Right ? XRNode.RightHand : XRNode.LeftHand;
            modelContainer = container;
            modelRoot = model;
            controllerAnimator = animator;
            meshRenderer = (smr != null && smr.name.IndexOf("battery", StringComparison.OrdinalIgnoreCase) < 0)
                ? smr
                : FindMainControllerMeshRenderer(model);

            _scaleCalibrated = false;
            _trackedFramesAfterWake = 0;
            _materialApplied = false;
            _lineRendererSearched = false;

            if (hand == ControllerHand.Right) _rightInstance = this;
            else _leftInstance = this;

            transform.localScale = Vector3.one;

            if (modelContainer != null)
            {
                modelContainer.localPosition = Vector3.zero;
                modelContainer.localRotation = Quaternion.identity;
                modelContainer.localScale = Vector3.one;
            }

            if (modelRoot != null)
            {
                modelRoot.transform.localPosition = Vector3.zero;
                modelRoot.transform.localRotation = Quaternion.identity;
                modelRoot.transform.localScale = Vector3.one;
            }

            if (meshRenderer != null)
            {
                meshRenderer.updateWhenOffscreen = true;
                EnsureControllerMaterial(meshRenderer, hand);
                _materialApplied = true;
            }

            OffsetXriRayOriginForward();

            if (modelRoot != null)
            {
                modelRoot.SetActive(false);
            }
        }

        public static SkinnedMeshRenderer FindMainControllerMeshRenderer(GameObject root)
        {
            if (root == null) return null;

            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer mainSmr = null;
            int maxVerts = -1;

            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer r = renderers[i];
                if (r == null) continue;

                if (r.name.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (r.gameObject.activeSelf)
                    {
                        r.gameObject.SetActive(false);
                    }
                    continue;
                }

                if (r.name.IndexOf("oculus_controller", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mainSmr = r;
                }
                else
                {
                    int vCount = r.sharedMesh != null ? r.sharedMesh.vertexCount : 0;
                    if (mainSmr == null && vCount > maxVerts)
                    {
                        maxVerts = vCount;
                        mainSmr = r;
                    }
                }
            }

            return mainSmr;
        }

        private void EnsureModelReferences()
        {
            string containerName = hand == ControllerHand.Right ? "RightController_Container" : "LeftController_Container";
            if (modelContainer == null)
            {
                modelContainer = transform.Find(containerName);
                if (modelContainer == null && transform.childCount > 0)
                {
                    Transform firstChild = transform.GetChild(0);
                    if (firstChild.name.Contains("Container"))
                    {
                        modelContainer = firstChild;
                    }
                    else
                    {
                        GameObject cGo = new GameObject(containerName);
                        cGo.transform.SetParent(transform, false);
                        cGo.transform.localPosition = Vector3.zero;
                        cGo.transform.localRotation = Quaternion.identity;
                        cGo.transform.localScale = Vector3.one;
                        modelContainer = cGo.transform;
                        firstChild.SetParent(modelContainer, false);
                    }
                }
            }

            if (modelContainer != null)
            {
                modelContainer.localPosition = Vector3.zero;
                modelContainer.localRotation = Quaternion.identity;

                if (modelRoot == null && modelContainer.childCount > 0)
                {
                    modelRoot = modelContainer.GetChild(0).gameObject;
                }
            }
            else if (modelRoot == null && transform.childCount > 0)
            {
                modelRoot = transform.GetChild(0).gameObject;
            }

            if (modelRoot != null)
            {
                modelRoot.transform.localPosition = Vector3.zero;
                modelRoot.transform.localRotation = Quaternion.identity;

                if (controllerAnimator == null)
                {
                    controllerAnimator = modelRoot.GetComponent<Animator>();
                    if (controllerAnimator == null)
                    {
                        controllerAnimator = modelRoot.GetComponentInChildren<Animator>(true);
                    }
                }

                SkinnedMeshRenderer mainSmr = FindMainControllerMeshRenderer(modelRoot);
                if (mainSmr != null)
                {
                    meshRenderer = mainSmr;
                }
                else if (meshRenderer == null)
                {
                    meshRenderer = modelRoot.GetComponent<SkinnedMeshRenderer>();
                }

                if (meshRenderer != null)
                {
                    meshRenderer.updateWhenOffscreen = true;
                    EnsureControllerMaterial(meshRenderer, hand);
                    _materialApplied = true;
                }

                modelRoot.SetActive(false);
            }
        }

        private void OffsetXriRayOriginForward()
        {
            Transform xriCtrl = GetCachedXriControllerTransform(_xrNode);
            if (xriCtrl == null) return;

            if (_cachedLineRenderer == null)
            {
                _cachedLineRenderer = xriCtrl.GetComponentInChildren<LineRenderer>(true);
            }

            foreach (Transform child in xriCtrl.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == xriCtrl) continue;
                string cn = child.name;
                if (cn.Contains("Ray Origin") || cn.Contains("RayInteractor") || cn.Contains("Near-Far Interactor") || cn.Contains("LineVisual"))
                {
                    if (child.localPosition.z < RayNozzleStartOffsetMeters)
                    {
                        child.localPosition = new Vector3(child.localPosition.x, child.localPosition.y, RayNozzleStartOffsetMeters);
                    }
                }
            }

            foreach (Component comp in xriCtrl.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                Type t = comp.GetType();
                string[] originProps = { "rayOriginTransform", "lineOriginTransform" };
                for (int p = 0; p < originProps.Length; p++)
                {
                    var prop = t.GetProperty(originProps[p]);
                    if (prop != null && prop.PropertyType == typeof(Transform))
                    {
                        Transform originT = prop.GetValue(comp, null) as Transform;
                        if (originT != null && originT != xriCtrl)
                        {
                            if (originT.localPosition.z < RayNozzleStartOffsetMeters)
                            {
                                originT.localPosition = new Vector3(originT.localPosition.x, originT.localPosition.y, RayNozzleStartOffsetMeters);
                            }
                        }
                        else if (originT == null || originT == xriCtrl)
                        {
                            Transform nozzle = xriCtrl.Find("NozzleRayOrigin");
                            if (nozzle == null)
                            {
                                GameObject nGo = new GameObject("NozzleRayOrigin");
                                nGo.transform.SetParent(xriCtrl, false);
                                nGo.transform.localPosition = new Vector3(0f, 0f, RayNozzleStartOffsetMeters);
                                nGo.transform.localRotation = Quaternion.identity;
                                nozzle = nGo.transform;
                            }
                            else
                            {
                                nozzle.localPosition = new Vector3(0f, 0f, RayNozzleStartOffsetMeters);
                            }
                            if (prop.CanWrite)
                            {
                                prop.SetValue(comp, nozzle, null);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Runs right after XRI's LineVisual updates the LineRenderer in OnBeforeRender,
        /// pushing the start vertices of the white pointer ray 7.8 cm forward so it never clips or flashes on the controller body.
        /// </summary>
        [BeforeRenderOrder(500)]
        private void OnBeforeRenderTrimPointerLine()
        {
            bool hiddenOnlyForAr = HideControllerModelsInAr && SonicMixedRealityManager.IsArVisualActive && !IsHandTrackingActive();
            if (modelRoot == null || (!modelRoot.activeInHierarchy && !hiddenOnlyForAr)) return;

            if (_cachedLineRenderer == null && !_lineRendererSearched)
            {
                Transform xriCtrl = GetCachedXriControllerTransform(_xrNode);
                if (xriCtrl != null)
                {
                    _lineRendererSearched = true;
                    _cachedLineRenderer = xriCtrl.GetComponentInChildren<LineRenderer>(true);
                }
            }

            if (_cachedLineRenderer == null || !_cachedLineRenderer.enabled || !_cachedLineRenderer. useWorldSpace) return;

            int count = _cachedLineRenderer.positionCount;
            if (count < 2) return;

            Vector3 startPt = _cachedLineRenderer.GetPosition(0);
            Vector3 endPt = _cachedLineRenderer.GetPosition(count - 1);
            Vector3 fullDelta = endPt - startPt;
            float totalLen = fullDelta.magnitude;

            if (totalLen <= RayNozzleStartOffsetMeters + 0.04f) return;

            Vector3 dir = fullDelta / totalLen;
            Vector3 newStart = startPt + dir * RayNozzleStartOffsetMeters;

            if (count == 2)
            {
                _cachedLineRenderer.SetPosition(0, newStart);
            }
            else
            {
                for (int i = 0; i < count - 1; i++)
                {
                    Vector3 pt = _cachedLineRenderer.GetPosition(i);
                    float proj = Vector3.Dot(pt - startPt, dir);
                    if (proj < RayNozzleStartOffsetMeters)
                    {
                        _cachedLineRenderer.SetPosition(i, newStart);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void Update()
        {
            UpdateGripPose();
            UpdateAnimatorInputs();
        }

        private void LateUpdate()
        {
            CalibrateAndLockScaleOnce();
            OnBeforeRenderTrimPointerLine();
        }

        private void CalibrateAndLockScaleOnce()
        {
            if (modelRoot == null || !modelRoot.activeInHierarchy) return;

            if (!_materialApplied && meshRenderer != null)
            {
                EnsureControllerMaterial(meshRenderer, hand);
                _materialApplied = true;
            }

            if (_scaleCalibrated)
            {
                transform.localScale = _lockedLocalScale;
                return;
            }

            _trackedFramesAfterWake++;
            if (_trackedFramesAfterWake < 2) return;

            if (meshRenderer == null || meshRenderer.name.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                meshRenderer = FindMainControllerMeshRenderer(modelRoot);
                if (meshRenderer == null) return;
            }

            Quaternion savedWorldRot = transform.rotation;
            transform.rotation = Quaternion.identity;

            if (controllerAnimator != null)
            {
                controllerAnimator.Update(0f);
            }

            Vector3 unrotatedSize = meshRenderer.bounds.size;
            transform.rotation = savedWorldRot;

            float maxUnrotatedDim = Mathf.Max(unrotatedSize.x, unrotatedSize.y, unrotatedSize.z);
            if (maxUnrotatedDim <= 0.00001f) return;

            float factor = 0.108f / maxUnrotatedDim;
            _lockedLocalScale = transform.localScale * factor;
            transform.localScale = _lockedLocalScale;
            _scaleCalibrated = true;
        }

        private bool IsHandTrackingActive()
        {
            if (!_modalityManagerSearched)
            {
                _modalityManagerSearched = true;
                _modalityManager = UnityEngine.Object.FindFirstObjectByType<XRInputModalityManager>();
            }

            if (_modalityManager != null)
            {
                GameObject handObj = hand == ControllerHand.Right ? _modalityManager.rightHand : _modalityManager.leftHand;
                if (handObj != null && handObj.activeInHierarchy)
                {
                    return true;
                }

                GameObject ctrlObj = hand == ControllerHand.Right ? _modalityManager.rightController : _modalityManager.leftController;
                if (ctrlObj != null && !ctrlObj.activeInHierarchy)
                {
                    return true;
                }
            }

            Transform xriCtrl = GetCachedXriControllerTransform(hand == ControllerHand.Right ? XRNode.RightHand : XRNode.LeftHand);
            if (xriCtrl != null && !xriCtrl.gameObject.activeInHierarchy)
            {
                return true;
            }

            if ((_handSubsystem == null || !_handSubsystem.running) && Time.unscaledTime >= _nextHandSubsystemSearchTime)
            {
                _nextHandSubsystemSearchTime = Time.unscaledTime + 1.0f;
                SubsystemManager.GetSubsystems(_sHandSubsystems);
                for (int i = 0; i < _sHandSubsystems.Count; i++)
                {
                    if (_sHandSubsystems[i] != null && _sHandSubsystems[i].running)
                    {
                        _handSubsystem = _sHandSubsystems[i];
                        break;
                    }
                }
            }

            if (_handSubsystem != null && _handSubsystem.running)
            {
                XRHand xrHand = hand == ControllerHand.Right ? _handSubsystem.rightHand : _handSubsystem.leftHand;
                if (xrHand.isTracked)
                {
                    return true;
                }
            }

            OVRInput.Controller ovrHand = hand == ControllerHand.Right ? OVRInput.Controller.RHand : OVRInput.Controller.LHand;
            OVRInput.Controller ovrTouch = hand == ControllerHand.Right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;
            OVRInput.Controller activeCtrl = OVRInput.GetActiveController();
            if (activeCtrl == OVRInput.Controller.Hands || activeCtrl == ovrHand)
            {
                return true;
            }
            if (OVRInput.IsControllerConnected(ovrHand) && !OVRInput.IsControllerConnected(ovrTouch))
            {
                return true;
            }

            return false;
        }

        private void EnsureInputSystemControls(UnityEngine.InputSystem.XR.XRController inputCtrl)
        {
            if (_cachedInputCtrl == inputCtrl) return;
            _cachedInputCtrl = inputCtrl;

            if (inputCtrl != null)
            {
                _ctrlIsTracked = inputCtrl.TryGetChildControl<ButtonControl>("isTracked");
                _ctrlDevicePos = inputCtrl.TryGetChildControl<Vector3Control>("devicePosition");
                _ctrlDeviceRot = inputCtrl.TryGetChildControl<QuaternionControl>("deviceRotation");
                _ctrlPrimaryBtn = inputCtrl.TryGetChildControl<ButtonControl>("primaryButton");
                _ctrlSecondaryBtn = inputCtrl.TryGetChildControl<ButtonControl>("secondaryButton");
                _ctrlMenuBtn = inputCtrl.TryGetChildControl<ButtonControl>("menuButton");
                _ctrlTrigger = inputCtrl.TryGetChildControl<AxisControl>("trigger");
                _ctrlGrip = inputCtrl.TryGetChildControl<AxisControl>("grip");
                _ctrlThumbstick = inputCtrl.TryGetChildControl<Vector2Control>("thumbstick");
            }
            else
            {
                _ctrlIsTracked = null;
                _ctrlDevicePos = null;
                _ctrlDeviceRot = null;
                _ctrlPrimaryBtn = null;
                _ctrlSecondaryBtn = null;
                _ctrlMenuBtn = null;
                _ctrlTrigger = null;
                _ctrlGrip = null;
                _ctrlThumbstick = null;
            }
        }

        private void UpdateGripPose()
        {
            if (IsHandTrackingActive())
            {
                if (modelRoot != null && modelRoot.activeSelf)
                {
                    modelRoot.SetActive(false);
                }
                return;
            }

            bool isTracked = false;
            Vector3 targetPos = Vector3.zero;
            Quaternion targetRot = Quaternion.identity;

            OVRInput.Controller ovrCtrl = hand == ControllerHand.Right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;
            if (OVRInput.IsControllerConnected(ovrCtrl))
            {
                Vector3 p = OVRInput.GetLocalControllerPosition(ovrCtrl);
                Quaternion r = OVRInput.GetLocalControllerRotation(ovrCtrl);
                if (p.sqrMagnitude > 0.0025f)
                {
                    targetPos = p;
                    targetRot = r;
                    isTracked = true;
                }
            }

            if (!isTracked)
            {
                if (!_hasDevice || !_inputDevice.isValid)
                {
                    RefreshDevice();
                }

                if (_hasDevice)
                {
                    _inputDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool devTracked);
                    bool hasPos = _inputDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 gripPos);
                    bool hasRot = _inputDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion gripRot);

                    if (devTracked && hasPos && hasRot && gripPos.sqrMagnitude > 0.0025f)
                    {
                        Matrix4x4 openXrMatrix = Matrix4x4.TRS(gripPos, gripRot, Vector3.one);
                        Matrix4x4 vrApiMatrix = openXrMatrix * GripToVrapiOffsetInverse;
                        targetPos = vrApiMatrix.GetPosition();
                        targetRot = vrApiMatrix.rotation;
                        isTracked = true;
                    }
                }
            }

            if (!isTracked)
            {
                var inputCtrl = hand == ControllerHand.Right
                    ? UnityEngine.InputSystem.XR.XRController.rightHand
                    : UnityEngine.InputSystem.XR.XRController.leftHand;

                if (inputCtrl != null && inputCtrl.added)
                {
                    EnsureInputSystemControls(inputCtrl);

                    bool simTracked = _ctrlIsTracked == null || _ctrlIsTracked.isPressed;
                    if (simTracked && _ctrlDevicePos != null && _ctrlDeviceRot != null)
                    {
                        Vector3 ipPos = _ctrlDevicePos.ReadValue();
                        Quaternion ipRot = _ctrlDeviceRot.ReadValue();
                        float normSq = ipRot.x * ipRot.x + ipRot.y * ipRot.y + ipRot.z * ipRot.z + ipRot.w * ipRot.w;

                        if (ipPos.sqrMagnitude > 0.0025f && normSq > 0.5f && normSq < 1.5f)
                        {
                            Matrix4x4 openXrMatrix = Matrix4x4.TRS(ipPos, ipRot, Vector3.one);
                            Matrix4x4 vrApiMatrix = openXrMatrix * GripToVrapiOffsetInverse;
                            targetPos = vrApiMatrix.GetPosition();
                            targetRot = vrApiMatrix.rotation;
                            isTracked = true;
                        }
                    }
                }
            }

            if (isTracked)
            {
                transform.localPosition = targetPos;
                transform.localRotation = targetRot;
            }

            bool hideInAr = HideControllerModelsInAr && SonicMixedRealityManager.IsArVisualActive;
            bool shouldShow = !hideInAr && isTracked && transform.localPosition.sqrMagnitude > 0.0025f;
            if (modelRoot != null && modelRoot.activeSelf != shouldShow)
            {
                modelRoot.SetActive(shouldShow);
                if (shouldShow && meshRenderer != null)
                {
                    EnsureControllerMaterial(meshRenderer, hand);
                    _materialApplied = true;
                }
            }
        }

        private void UpdateAnimatorInputs()
        {
            if (controllerAnimator == null || !controllerAnimator.gameObject.activeInHierarchy) return;

            bool btn1 = false;
            bool btn2 = false;
            bool btn3 = false;
            float trigger = 0f;
            float grip = 0f;
            Vector2 stick = Vector2.zero;

            OVRInput.Controller ovrCtrl = hand == ControllerHand.Right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;
            if (OVRInput.IsControllerConnected(ovrCtrl))
            {
                btn1 = OVRInput.Get(OVRInput.Button.One, ovrCtrl);
                btn2 = OVRInput.Get(OVRInput.Button.Two, ovrCtrl);
                btn3 = hand == ControllerHand.Left && OVRInput.Get(OVRInput.Button.Start, ovrCtrl);
                trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ovrCtrl);
                grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, ovrCtrl);
                stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, ovrCtrl);
            }

            if (_hasDevice)
            {
                bool b1, b2, b3;
                float t, g;
                Vector2 s;
                if (_inputDevice.TryGetFeatureValue(CommonUsages.primaryButton, out b1)) btn1 |= b1;
                if (_inputDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out b2)) btn2 |= b2;
                if (_inputDevice.TryGetFeatureValue(CommonUsages.menuButton, out b3)) btn3 |= b3;
                if (_inputDevice.TryGetFeatureValue(CommonUsages.trigger, out t)) trigger = Mathf.Max(trigger, t);
                if (_inputDevice.TryGetFeatureValue(CommonUsages.grip, out g)) grip = Mathf.Max(grip, g);
                if (_inputDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out s) && s.sqrMagnitude > stick.sqrMagnitude) stick = s;
            }

            var inputCtrl = hand == ControllerHand.Right
                ? UnityEngine.InputSystem.XR.XRController.rightHand
                : UnityEngine.InputSystem.XR.XRController.leftHand;

            if (inputCtrl != null && inputCtrl.added)
            {
                EnsureInputSystemControls(inputCtrl);

                if (_ctrlPrimaryBtn != null && _ctrlPrimaryBtn.isPressed) btn1 = true;
                if (_ctrlSecondaryBtn != null && _ctrlSecondaryBtn.isPressed) btn2 = true;
                if (_ctrlMenuBtn != null && _ctrlMenuBtn.isPressed) btn3 = true;
                if (_ctrlTrigger != null) trigger = Mathf.Max(trigger, _ctrlTrigger.ReadValue());
                if (_ctrlGrip != null) grip = Mathf.Max(grip, _ctrlGrip.ReadValue());
                if (_ctrlThumbstick != null && stick.sqrMagnitude < 0.001f) stick = _ctrlThumbstick.ReadValue();
            }

            controllerAnimator.SetFloat(HashButton1, btn1 ? 1.0f : 0.0f);
            controllerAnimator.SetFloat(HashButton2, btn2 ? 1.0f : 0.0f);
            controllerAnimator.SetFloat(HashButton3, btn3 ? 1.0f : 0.0f);
            controllerAnimator.SetFloat(HashTrigger, trigger);
            controllerAnimator.SetFloat(HashGrip, grip);
            controllerAnimator.SetFloat(HashJoyX, stick.x);
            controllerAnimator.SetFloat(HashJoyY, stick.y);
        }

        private static Transform GetCachedXriControllerTransform(XRNode node)
        {
            if (node == XRNode.RightHand)
            {
                if (_rightPointerTransform == null && !_rightPointerSearched)
                {
                    _rightPointerSearched = true;
                    if (!_modalityManagerSearched)
                    {
                        _modalityManagerSearched = true;
                        _modalityManager = UnityEngine.Object.FindFirstObjectByType<XRInputModalityManager>();
                    }
                    if (_modalityManager != null && _modalityManager.rightController != null)
                    {
                        _rightPointerTransform = _modalityManager.rightController.transform;
                    }
                    else if (_rightInstance != null && _rightInstance.transform.parent != null)
                    {
                        _rightPointerTransform = _rightInstance.transform.parent.Find("Right Controller");
                    }
                    if (_rightPointerTransform == null)
                    {
                        GameObject go = GameObject.Find("Right Controller");
                        if (go != null) _rightPointerTransform = go.transform;
                    }
                }
                return _rightPointerTransform;
            }
            else
            {
                if (_leftPointerTransform == null && !_leftPointerSearched)
                {
                    _leftPointerSearched = true;
                    if (!_modalityManagerSearched)
                    {
                        _modalityManagerSearched = true;
                        _modalityManager = UnityEngine.Object.FindFirstObjectByType<XRInputModalityManager>();
                    }
                    if (_modalityManager != null && _modalityManager.leftController != null)
                    {
                        _leftPointerTransform = _modalityManager.leftController.transform;
                    }
                    else if (_leftInstance != null && _leftInstance.transform.parent != null)
                    {
                        _leftPointerTransform = _leftInstance.transform.parent.Find("Left Controller");
                    }
                    if (_leftPointerTransform == null)
                    {
                        GameObject go = GameObject.Find("Left Controller");
                        if (go != null) _leftPointerTransform = go.transform;
                    }
                }
                return _leftPointerTransform;
            }
        }

        public static Transform GetPointerTransform(XRNode node)
        {
            Transform cached = GetCachedXriControllerTransform(node);
            if (cached != null) return cached;

            SonicMetaQuestController inst = node == XRNode.RightHand ? _rightInstance : _leftInstance;
            if (inst != null) return inst.transform;

            return Camera.main != null ? Camera.main.transform : null;
        }

        public static Ray GetPointerRay(XRNode node)
        {
            Transform t = GetPointerTransform(node);
            if (t != null)
            {
                return new Ray(t.position, t.forward);
            }
            return Camera.main != null ? new Ray(Camera.main.transform.position, Camera.main.transform.forward) : default;
        }

        public static void HideGenericControllerRenderers()
        {
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer == null) continue;
                string n = renderer.gameObject.name;
                Transform p = renderer.transform.parent;
                string pn = p != null ? p.name : "";

                if (n.Contains("UniversalController") || pn.Contains("UniversalController") ||
                    n.Contains("Controller_Base") || n == "ThumbStick" || n == "ThumbStick_Base" ||
                    n == "TouchPad" || n == "Button_A" || n == "Button_B" || n == "Button_Home" ||
                    (n == "Trigger" && p != null && p.name.Contains("Controller")) ||
                    (n == "Bumper" && p != null && p.name.Contains("Controller")))
                {
                    renderer.enabled = false;
                }
            }
        }

        public static void EnsureControllerVisuals()
        {
            HideGenericControllerRenderers();

            Transform cameraOffset = null;
            var origin = UnityEngine.Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null)
            {
                cameraOffset = origin.CameraFloorOffsetObject != null
                    ? origin.CameraFloorOffsetObject.transform
                    : origin.transform;
            }

            if (cameraOffset == null)
            {
                var cam = Camera.main;
                if (cam != null && cam.transform.parent != null)
                {
                    cameraOffset = cam.transform.parent;
                }
            }

            if (cameraOffset == null) return;

            EnsureGripAnchor(cameraOffset, ControllerHand.Left, "Left Controller Grip Anchor");
            EnsureGripAnchor(cameraOffset, ControllerHand.Right, "Right Controller Grip Anchor");
        }

        private static void EnsureGripAnchor(Transform cameraOffset, ControllerHand hand, string anchorName)
        {
            Transform anchor = cameraOffset.Find(anchorName);
            if (anchor == null)
            {
                GameObject anchorGo = new GameObject(anchorName);
                anchorGo.transform.SetParent(cameraOffset, false);
                anchorGo.transform.localPosition = Vector3.zero;
                anchorGo.transform.localRotation = Quaternion.identity;
                anchorGo.transform.localScale = Vector3.one;
                anchor = anchorGo.transform;
            }

            SonicMetaQuestController ctrl = anchor.GetComponent<SonicMetaQuestController>();
            if (ctrl == null)
            {
                ctrl = anchor.gameObject.AddComponent<SonicMetaQuestController>();
            }

            string containerName = hand == ControllerHand.Right ? "RightController_Container" : "LeftController_Container";
            Transform container = anchor.Find(containerName);
            if (container == null)
            {
                GameObject containerGo = new GameObject(containerName);
                containerGo.transform.SetParent(anchor, false);
                containerGo.transform.localPosition = Vector3.zero;
                containerGo.transform.localRotation = Quaternion.identity;
                containerGo.transform.localScale = Vector3.one;
                container = containerGo.transform;
            }

            string modelName = hand == ControllerHand.Right ? "MetaQuestTouchPlus_Right" : "MetaQuestTouchPlus_Left";
            Transform model = container.Find(modelName);

            if (model == null)
            {
                Transform oldModel = anchor.Find(modelName);
                if (oldModel != null)
                {
                    oldModel.SetParent(container, false);
                    oldModel.localPosition = Vector3.zero;
                    oldModel.localRotation = Quaternion.identity;
                    oldModel.localScale = Vector3.one;
                    model = oldModel;
                }
            }

            if (model == null)
            {
                GameObject resPrefab = Resources.Load<GameObject>($"Controllers/{modelName}");
                if (resPrefab != null)
                {
                    GameObject modelGo = Instantiate(resPrefab, container);
                    modelGo.name = modelName;
                    modelGo.transform.localPosition = Vector3.zero;
                    modelGo.transform.localRotation = Quaternion.identity;
                    modelGo.transform.localScale = Vector3.one;
                    model = modelGo.transform;
                }
            }

#if UNITY_EDITOR
            if (model == null)
            {
                string fbxPath = hand == ControllerHand.Right
                    ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Right.fbx"
                    : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";

                GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                if (prefab != null)
                {
                    GameObject modelGo = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, container);
                    modelGo.name = modelName;
                    modelGo.transform.localPosition = Vector3.zero;
                    modelGo.transform.localRotation = Quaternion.identity;
                    modelGo.transform.localScale = Vector3.one;
                    model = modelGo.transform;

                    string animPath = hand == ControllerHand.Right
                        ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusRightAnimation.controller"
                        : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusLeftAnimation.controller";

                    var animController = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(animPath);
                    Animator anim = modelGo.GetComponent<Animator>();
                    if (anim != null && animController != null)
                    {
                        anim.runtimeAnimatorController = animController;
                    }

                    string matPath = hand == ControllerHand.Right
                        ? "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Right_URP.mat"
                        : "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Left_URP.mat";

                    Material mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    SkinnedMeshRenderer smr = FindMainControllerMeshRenderer(modelGo);
                    if (smr != null && mat != null)
                    {
                        smr.sharedMaterial = mat;
                    }
                }
            }
#endif

            if (model != null)
            {
                model.gameObject.SetActive(false);
                Animator anim = model.GetComponentInChildren<Animator>(true);
                SkinnedMeshRenderer smr = FindMainControllerMeshRenderer(model.gameObject);
                ctrl.Configure(hand, container, model.gameObject, anim, smr);
            }
        }

        public static void EnsureControllerMaterial(SkinnedMeshRenderer smr, ControllerHand hand)
        {
            if (smr == null) return;

            string urpMatName = hand == ControllerHand.Right ? "MetaQuestTouchPlus_Right_URP" : "MetaQuestTouchPlus_Left_URP";
            string instanceMatName = $"{urpMatName}_MusicReactiveInstance";
            if (smr.sharedMaterial != null && smr.sharedMaterial.name == instanceMatName)
            {
                smr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                smr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                return;
            }

            Material sourceMat = Resources.Load<Material>($"Controllers/{urpMatName}");
            if (sourceMat == null)
            {
                sourceMat = smr.sharedMaterial;
            }

            Texture albedoTex = null;
            Texture normalTex = null;
            if (sourceMat != null)
            {
                if (sourceMat.HasProperty("_BaseMap")) albedoTex = sourceMat.GetTexture("_BaseMap");
                if (albedoTex == null && sourceMat.HasProperty("_MainTex")) albedoTex = sourceMat.GetTexture("_MainTex");
                if (sourceMat.HasProperty("_BumpMap")) normalTex = sourceMat.GetTexture("_BumpMap");
            }

            Shader litShader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (litShader == null) litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader != null && sourceMat != null && sourceMat.shader != null && sourceMat.shader.name.Contains("Simple Lit"))
            {
                litShader = sourceMat.shader;
            }

            if (litShader != null)
            {
                Material instanceMat = new Material(litShader)
                {
                    name = instanceMatName
                };

                // Balanced neutral base color so the Trilight music colors and dark-room shading show up clearly
                Color baseTint = new Color(0.58f, 0.58f, 0.58f, 1.0f);
                if (instanceMat.HasProperty("_BaseColor")) instanceMat.SetColor("_BaseColor", baseTint);
                if (instanceMat.HasProperty("_Color")) instanceMat.SetColor("_Color", baseTint);

                if (albedoTex != null)
                {
                    if (instanceMat.HasProperty("_BaseMap")) instanceMat.SetTexture("_BaseMap", albedoTex);
                    if (instanceMat.HasProperty("_MainTex")) instanceMat.SetTexture("_MainTex", albedoTex);
                }

                if (normalTex != null && instanceMat.HasProperty("_BumpMap"))
                {
                    instanceMat.SetTexture("_BumpMap", normalTex);
                    instanceMat.EnableKeyword("_NORMALMAP");
                }

                if (instanceMat.HasProperty("_Smoothness")) instanceMat.SetFloat("_Smoothness", 0.12f);
                if (instanceMat.HasProperty("_Glossiness")) instanceMat.SetFloat("_Glossiness", 0.12f);
                if (instanceMat.HasProperty("_GlossMapScale")) instanceMat.SetFloat("_GlossMapScale", 0.0f);
                if (instanceMat.HasProperty("_Metallic")) instanceMat.SetFloat("_Metallic", 0.0f);
                if (instanceMat.HasProperty("_SpecularHighlights")) instanceMat.SetFloat("_SpecularHighlights", 0.0f);
                if (instanceMat.HasProperty("_EnvironmentReflections")) instanceMat.SetFloat("_EnvironmentReflections", 0.0f);
                if (instanceMat.HasProperty("_SpecColor")) instanceMat.SetColor("_SpecColor", Color.black);
                if (instanceMat.HasProperty("_EmissionColor")) instanceMat.SetColor("_EmissionColor", Color.black);
                instanceMat.DisableKeyword("_EMISSION");
                instanceMat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                instanceMat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

                smr.sharedMaterial = instanceMat;
                smr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                smr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
        }
    }
}