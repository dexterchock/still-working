using System;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Meta.XR.MRUtilityKit;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;

namespace SonicTopography
{
    public enum SonicExperienceMode
    {
        VR = 0,
        AR = 1
    }

    /// <summary>
    /// Coordinates Passthrough AR mode, VR &lt;-&gt; AR Sonic Ripple Dissolve transitions,
    /// Meta Environment Depth occlusion, MRUK room scanning / EffectMesh light wash,
    /// contextual boundary suppression, and Meta XR Audio room acoustics.
    /// Preserves XROrigin and all AGENTS.md Quest invariants.
    /// </summary>
    public class SonicMixedRealityManager : MonoBehaviour
    {
        public const float PlatterLocalRadiusUnits = 77.5f;
        public const float DefaultArDiameterMeters = 0.62f;
        public const float MinArDiameterMeters = 0.28f;
        public const float MaxArDiameterMeters = 3.60f;
        public const float DefaultRoomGlow = 1.0f;

        private const string ScenePermission = "com.oculus.permission.USE_SCENE";
        private const string PrefRoomGlow = "SonicTopography_ArRoomGlow";
        private const string PrefRoomLight = "SonicTopography_ArRoomLight";
        private const string PrefRealOcclusion = "SonicTopography_ArRealOcclusion";
        private const string PrefSurfaceSnap = "SonicTopography_ArSurfaceSnap";
        private const float TransitionDurationSeconds = 1.10f;

        public static SonicMixedRealityManager Instance { get; private set; }

        public static bool IsArVisualActive =>
            Instance != null && (Instance._arBlend > 0.01f || Instance._targetMode == SonicExperienceMode.AR);

        public static bool IsArControlActive =>
            Instance != null && (Instance._targetMode == SonicExperienceMode.AR || Instance._arBlend > 0.05f);

        public SonicExperienceMode CurrentMode => _currentMode;
        public SonicExperienceMode TargetMode => _targetMode;
        public bool IsArMode => _targetMode == SonicExperienceMode.AR;
        public bool IsTransitioning => _isTransitioning;
        public float ArBlend => _arBlend;
        public bool HasScenePermission =>
#if UNITY_ANDROID && !UNITY_EDITOR
            Permission.HasUserAuthorizedPermission(ScenePermission);
#else
            true;
#endif
        public bool HasLoadedRoom => _mruk != null && _mruk.GetCurrentRoom() != null;
        public bool IsSnappedToSurface => _isArSnappedToSurface;
        public Vector3 CurrentSurfaceNormal => _arSurfaceNormal;

        public float RoomGlow
        {
            get => _roomGlow;
            set
            {
                float clamped = Mathf.Clamp(value, 0f, 2f);
                if (!Mathf.Approximately(_roomGlow, clamped))
                {
                    _roomGlow = clamped;
                    PlayerPrefs.SetFloat(PrefRoomGlow, _roomGlow);
                    PlayerPrefs.Save();
                }
            }
        }

        public bool RoomLightEnabled
        {
            get => _roomLightEnabled;
            set
            {
                if (_roomLightEnabled != value)
                {
                    _roomLightEnabled = value;
                    PlayerPrefs.SetInt(PrefRoomLight, value ? 1 : 0);
                    PlayerPrefs.Save();
                    SyncRoomEffectVisibility();
                }
            }
        }

        public bool RealOcclusionEnabled
        {
            get => _realOcclusionEnabled;
            set
            {
                if (_realOcclusionEnabled != value)
                {
                    _realOcclusionEnabled = value;
                    PlayerPrefs.SetInt(PrefRealOcclusion, value ? 1 : 0);
                    PlayerPrefs.Save();
                    SyncDepthManagerState();
                }
            }
        }

        public bool SurfaceSnapEnabled
        {
            get => _surfaceSnapEnabled;
            set
            {
                if (_surfaceSnapEnabled != value)
                {
                    _surfaceSnapEnabled = value;
                    PlayerPrefs.SetInt(PrefSurfaceSnap, value ? 1 : 0);
                    PlayerPrefs.Save();
                    if (!value)
                    {
                        _isArSnappedToSurface = false;
                        _arSurfaceNormal = Vector3.up;
                    }
                }
            }
        }

        public event Action<SonicExperienceMode> OnModeChanged;
        public event Action OnArSettingsChanged;

        private SonicTopographyVisualizer _visualizer;
        private SonicTopographyAudioEngine _audioEngine;
        private SonicStarRiverVisualizer _starRiver;
        private AudioFloorControllerActions _controllerActions;
        private SonicWebBrowserManager _browserManager;
        private XROrigin _xrOrigin;

        private OVRPassthroughLayer _passthroughLayer;
        private EnvironmentDepthManager _depthManager;
        private EnvironmentRaycastManager _raycastManager;
        private MRUK _mruk;
        private EffectMesh _effectMesh;
        private MetaXRAudioRoomAcousticProperties _roomAcoustics;

        private GameObject _mrInfraRoot;
        private GameObject _mrukRoot;
        private GameObject _dissolveDomeGo;
        private Mesh _dissolveDomeMesh;
        private Material _dissolveDomeMat;
        private Material _roomLightWashMat;

        private SonicExperienceMode _currentMode = SonicExperienceMode.VR;
        private SonicExperienceMode _targetMode = SonicExperienceMode.VR;
        private bool _isTransitioning = false;
        private float _transitionT = 0f;
        private float _arBlend = 0f;
        private bool _vrRenderersDisabledForAr = false;

        // AR terrain state
        private bool _hasCustomArPose = false;
        private Vector3 _arTargetPos = new Vector3(0f, 1.0f, 0.72f);
        private Quaternion _arTargetRot = Quaternion.identity;
        private float _arTargetScale = DefaultArDiameterMeters / (PlatterLocalRadiusUnits * 2f);
        private bool _isArSnappedToSurface = false;
        private Vector3 _arSurfaceNormal = Vector3.up;

        // Transition snapshot poses
        private Vector3 _transStartPos;
        private Quaternion _transStartRot;
        private float _transStartScale;
        private Vector3 _transEndPos;
        private Quaternion _transEndRot;
        private float _transEndScale;
        private Vector3 _dissolveOriginWS;
        private Vector3 _dissolveForwardWS = Vector3.forward;

        // Persistent AR settings
        private float _roomGlow = DefaultRoomGlow;
        private bool _roomLightEnabled = true;
        private bool _realOcclusionEnabled = true;
        private bool _surfaceSnapEnabled = true;

        // Scene permission & loading state
        private bool _scenePermissionRequested = false;
        private bool _sceneLoadInitiated = false;
        private int _mrukRoomLayer = 28;
        private float _nextAudioRoomSyncTime = 0f;
        private float _nextPermissionPollTime = 0f;

        // Cached shader IDs
        private static readonly int IdSonicArModeGlobal = Shader.PropertyToID("_SonicArModeGlobal");
        private static readonly int IdDissolveOriginWS = Shader.PropertyToID("_SonicArDissolveOriginWS");
        private static readonly int IdDissolveParams = Shader.PropertyToID("_SonicArDissolveParams");
        private static readonly int IdDissolveRingColor = Shader.PropertyToID("_SonicArDissolveRingColor");
        private static readonly int IdDomeParams = Shader.PropertyToID("_SonicArDomeParams");
        private static readonly int IdDomeForwardWS = Shader.PropertyToID("_SonicArDomeForwardWS");

        private static readonly int IdRoomPlatterCenter = Shader.PropertyToID("_uSonicRoomPlatterCenterWS");
        private static readonly int IdRoomPlatterNormal = Shader.PropertyToID("_uSonicRoomPlatterNormalWS");
        private static readonly int IdRoomPlatterRight = Shader.PropertyToID("_uSonicRoomPlatterRightWS");
        private static readonly int IdRoomPlatterForward = Shader.PropertyToID("_uSonicRoomPlatterForwardWS");
        private static readonly int IdRoomParams = Shader.PropertyToID("_uSonicRoomParams");
        private static readonly int IdRoomFx = Shader.PropertyToID("_uSonicRoomFx");
        private static readonly int IdRoomOcc = Shader.PropertyToID("_uSonicRoomOcc");
        private static readonly int IdRoomCoolCore = Shader.PropertyToID("_uSonicRoomCoolCore");
        private static readonly int IdRoomWarmCore = Shader.PropertyToID("_uSonicRoomWarmCore");
        private static readonly int IdRoomRippleColor = Shader.PropertyToID("_uSonicRoomRippleColor");
        private static readonly int IdRoomRipplePts = Shader.PropertyToID("_uSonicRoomRipplePosTimeStrength");
        private static readonly int IdRoomRippleState = Shader.PropertyToID("_uSonicRoomRippleState");

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            LoadArPreferences();
            Shader.SetGlobalFloat(IdSonicArModeGlobal, 0f);
            Shader.SetGlobalVector(IdDissolveParams, Vector4.zero);
            Shader.SetGlobalVector(IdDomeParams, Vector4.zero);
        }

        public void Initialize(
            SonicTopographyVisualizer visualizer,
            SonicTopographyAudioEngine audioEngine,
            SonicStarRiverVisualizer starRiver,
            AudioFloorControllerActions controllerActions,
            SonicWebBrowserManager browserManager)
        {
            _visualizer = visualizer;
            _audioEngine = audioEngine;
            _starRiver = starRiver;
            _controllerActions = controllerActions;
            _browserManager = browserManager;
            _xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);

            int namedLayer = LayerMask.NameToLayer("MRUKRoom");
            _mrukRoomLayer = namedLayer >= 0 ? namedLayer : 28;

            EnsureSharedOVRManager();
            EnsureMrInfrastructure();
            SyncDepthManagerState();
            SyncRoomEffectVisibility();
            SyncContextualBoundarySuppression();
            ApplySpatialAudioForCurrentState();
        }

        public static OVRManager EnsureSharedOVRManager()
        {
            if (OVRManager.instance != null)
            {
                return OVRManager.instance;
            }

            OVRManager mgr = FindFirstObjectByType<OVRManager>(FindObjectsInactive.Include);
            if (mgr == null)
            {
                GameObject owner = Instance != null ? Instance.gameObject : new GameObject("SonicOVRManager");
                mgr = owner.AddComponent<OVRManager>();
            }

            if (mgr != null)
            {
                // Leave trackingOriginType untouched (AGENTS.md §2.7)
                mgr.enableDynamicResolution = false;
                mgr.resetTrackerOnLoad = false;
                mgr.launchSimultaneousHandsControllersOnStartup = false;
                mgr.isInsightPassthroughEnabled = false;
            }

            SonicTopographyVRBootstrap.ConfigureXRPerformanceAndResolution();
            return mgr;
        }

        private void LoadArPreferences()
        {
            _roomGlow = PlayerPrefs.GetFloat(PrefRoomGlow, DefaultRoomGlow);
            _roomLightEnabled = PlayerPrefs.GetInt(PrefRoomLight, 1) == 1;
            _realOcclusionEnabled = PlayerPrefs.GetInt(PrefRealOcclusion, 1) == 1;
            _surfaceSnapEnabled = PlayerPrefs.GetInt(PrefSurfaceSnap, 1) == 1;
        }

        public void ResetArGlowToDefault()
        {
            RoomGlow = DefaultRoomGlow;
            OnArSettingsChanged?.Invoke();
        }

        public void ToggleMode()
        {
            SetMode(_targetMode == SonicExperienceMode.VR ? SonicExperienceMode.AR : SonicExperienceMode.VR);
        }

        public void SetMode(SonicExperienceMode mode, bool immediate = false)
        {
            if (_targetMode == mode && !_isTransitioning &&
                ((mode == SonicExperienceMode.AR && _arBlend >= 0.999f) ||
                 (mode == SonicExperienceMode.VR && _arBlend <= 0.001f)))
            {
                return;
            }

            _targetMode = mode;

            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
            if (_starRiver == null) _starRiver = FindFirstObjectByType<SonicStarRiverVisualizer>();
            if (_audioEngine == null) _audioEngine = FindFirstObjectByType<SonicTopographyAudioEngine>();
            if (_controllerActions == null) _controllerActions = FindFirstObjectByType<AudioFloorControllerActions>();

            Camera cam = Camera.main;
            Transform camT = cam != null ? cam.transform : null;

            if (mode == SonicExperienceMode.AR)
            {
                EnsureMrInfrastructure();
                EnablePassthroughUnderlay(true);
                EnsureScenePermissionAndLoadRoom();
                SyncDepthManagerState();
                SyncRoomEffectVisibility();

                if (!_hasCustomArPose)
                {
                    ComputeDefaultArTabletopPose(camT, out _arTargetPos, out _arTargetRot);
                    _arTargetScale = DefaultArDiameterMeters / (PlatterLocalRadiusUnits * 2f);
                    _isArSnappedToSurface = false;
                    _arSurfaceNormal = Vector3.up;
                }

                // Transition snapshot
                if (_visualizer != null)
                {
                    _visualizer.GetCurrentEffectivePlatterPose(out _transStartPos, out _transStartRot, out _transStartScale);
                }
                else
                {
                    _transStartPos = new Vector3(0f, -26f, 64f);
                    _transStartRot = Quaternion.identity;
                    _transStartScale = 1f;
                }
                _transEndPos = _arTargetPos;
                _transEndRot = _arTargetRot;
                _transEndScale = _arTargetScale;

                // Ensure VR environments stay visible so the expanding ripple can dissolve them out
                SetVrEnvironmentRenderersEnabled(true);
            }
            else
            {
                // Returning to VR: restore VR renderers immediately so the reverse ripple materializes them back in
                SetVrEnvironmentRenderersEnabled(true);

                if (_visualizer != null)
                {
                    _visualizer.GetCurrentEffectivePlatterPose(out _transStartPos, out _transStartRot, out _transStartScale);
                    _transEndPos = _visualizer.platterWorldPosition;
                    _transEndRot = Quaternion.Euler(0f, _visualizer.CurrentPlatterRotationDegrees, 0f);
                    _transEndScale = 1f;
                }
                else
                {
                    _transStartPos = _arTargetPos;
                    _transStartRot = _arTargetRot;
                    _transStartScale = _arTargetScale;
                    _transEndPos = new Vector3(0f, -26f, 64f);
                    _transEndRot = Quaternion.identity;
                    _transEndScale = 1f;
                }

                if (_controllerActions != null)
                {
                    _controllerActions.RecenterUserOntoGlassStage();
                }
            }

            if (camT != null)
            {
                _dissolveOriginWS = camT.position;
                Vector3 fwd = camT.forward;
                _dissolveForwardWS = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            }
            else
            {
                _dissolveOriginWS = Vector3.zero;
                _dissolveForwardWS = Vector3.forward;
            }

            SyncContextualBoundarySuppression();
            ApplySpatialAudioForCurrentState();

            if (immediate)
            {
                _isTransitioning = false;
                _transitionT = mode == SonicExperienceMode.AR ? 1f : 0f;
                _arBlend = _transitionT;
                _currentMode = mode;
                FinalizeTransitionState();
            }
            else
            {
                _transitionT = _arBlend;
                _isTransitioning = true;
                SetDissolveDomeActive(true);
            }

            OnModeChanged?.Invoke(_targetMode);
        }

        public void SetArTerrainPose(Vector3 worldPos, Quaternion worldRot, float uniformScale, bool snapped, Vector3 surfaceNormal)
        {
            _hasCustomArPose = true;
            _arTargetPos = worldPos;
            _arTargetRot = worldRot;
            _arTargetScale = Mathf.Clamp(uniformScale,
                MinArDiameterMeters / (PlatterLocalRadiusUnits * 2f),
                MaxArDiameterMeters / (PlatterLocalRadiusUnits * 2f));
            _isArSnappedToSurface = snapped;
            _arSurfaceNormal = surfaceNormal.sqrMagnitude > 0.001f ? surfaceNormal.normalized : Vector3.up;

            if (_isTransitioning && _targetMode == SonicExperienceMode.AR)
            {
                _transEndPos = _arTargetPos;
                _transEndRot = _arTargetRot;
                _transEndScale = _arTargetScale;
            }
        }

        public void GetCurrentTerrainPose(out Vector3 pos, out Quaternion rot, out float scale, out float worldRadius)
        {
            if (_isTransitioning)
            {
                float s = SmoothCubic(_arBlend);
                if (_targetMode == SonicExperienceMode.AR)
                {
                    pos = Vector3.Lerp(_transStartPos, _transEndPos, s);
                    rot = Quaternion.Slerp(_transStartRot, _transEndRot, s);
                    scale = Mathf.Lerp(_transStartScale, _transEndScale, s);
                }
                else
                {
                    float invS = SmoothCubic(1f - _arBlend);
                    pos = Vector3.Lerp(_transStartPos, _transEndPos, invS);
                    rot = Quaternion.Slerp(_transStartRot, _transEndRot, invS);
                    scale = Mathf.Lerp(_transStartScale, _transEndScale, invS);
                }
            }
            else if (_targetMode == SonicExperienceMode.AR)
            {
                pos = _arTargetPos;
                rot = _arTargetRot;
                scale = _arTargetScale;
            }
            else
            {
                pos = _visualizer != null ? _visualizer.platterWorldPosition : new Vector3(0f, -26f, 64f);
                rot = _visualizer != null ? Quaternion.Euler(0f, _visualizer.CurrentPlatterRotationDegrees, 0f) : Quaternion.identity;
                scale = 1f;
            }

            worldRadius = PlatterLocalRadiusUnits * Mathf.Max(0.0005f, scale);
        }

        private void ComputeDefaultArTabletopPose(Transform camT, out Vector3 pos, out Quaternion rot)
        {
            if (camT != null)
            {
                Vector3 flatFwd = new Vector3(camT.forward.x, 0f, camT.forward.z);
                if (flatFwd.sqrMagnitude < 0.001f) flatFwd = Vector3.forward;
                flatFwd.Normalize();

                pos = camT.position + flatFwd * 0.68f + Vector3.down * 0.34f;
                rot = Quaternion.LookRotation(flatFwd, Vector3.up);
            }
            else
            {
                pos = new Vector3(0f, 1.0f, 0.72f);
                rot = Quaternion.identity;
            }
        }

        private void EnsureMrInfrastructure()
        {
            if (_mrInfraRoot == null)
            {
                _mrInfraRoot = new GameObject("SonicMixedRealityInfra");
                _mrInfraRoot.transform.SetParent(transform, false);
            }

            if (_passthroughLayer == null)
            {
                _passthroughLayer = _mrInfraRoot.GetComponent<OVRPassthroughLayer>();
                if (_passthroughLayer == null)
                {
                    _passthroughLayer = _mrInfraRoot.AddComponent<OVRPassthroughLayer>();
                }
                _passthroughLayer.hidden = true;
                _passthroughLayer.textureOpacity = 1f;
                _passthroughLayer.edgeRenderingEnabled = false;
            }

            if (_depthManager == null)
            {
                _depthManager = _mrInfraRoot.GetComponent<EnvironmentDepthManager>();
                if (_depthManager == null)
                {
                    _depthManager = _mrInfraRoot.AddComponent<EnvironmentDepthManager>();
                }
                if (_xrOrigin == null) _xrOrigin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
                if (_xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null)
                {
                    _depthManager.CustomTrackingSpace = _xrOrigin.CameraFloorOffsetObject.transform;
                }
                _depthManager.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
                _depthManager.RemoveHands = false;
                _depthManager.enabled = false;
            }

            EnsureRoomLightWashMaterial();

            if (_mrukRoot == null)
            {
                _mrukRoot = new GameObject("SonicMRUKRoomRig");
                _mrukRoot.SetActive(false); // Configure SceneSettings before MRUK.Awake runs
                _mrukRoot.transform.SetParent(_mrInfraRoot.transform, false);

                _mruk = _mrukRoot.AddComponent<MRUK>();
                _mruk.EnableWorldLock = false;
                _mruk.SceneSettings = new MRUK.MRUKSettings
                {
                    DataSource = MRUK.SceneDataSource.Device,
                    LoadSceneOnStartup = false,
                    SeatWidth = 0.6f
                };

                _effectMesh = _mrukRoot.AddComponent<EffectMesh>();
                _effectMesh.SpawnOnStart = MRUK.RoomFilter.CurrentRoomOnly;
                _effectMesh.MeshMaterial = _roomLightWashMat;
                _effectMesh.Colliders = true;
                _effectMesh.CastShadow = false;
                _effectMesh.HideMesh = true;
                _effectMesh.Layer = _mrukRoomLayer;
                _effectMesh.CutHoles = MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME;
                _effectMesh.Labels = MRUKAnchor.SceneLabels.FLOOR |
                                     MRUKAnchor.SceneLabels.CEILING |
                                     MRUKAnchor.SceneLabels.WALL_FACE |
                                     MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE |
                                     MRUKAnchor.SceneLabels.INNER_WALL_FACE |
                                     MRUKAnchor.SceneLabels.TABLE |
                                     MRUKAnchor.SceneLabels.COUCH |
                                     MRUKAnchor.SceneLabels.OTHER |
                                     MRUKAnchor.SceneLabels.STORAGE |
                                     MRUKAnchor.SceneLabels.BED |
                                     MRUKAnchor.SceneLabels.SCREEN |
                                     MRUKAnchor.SceneLabels.GLOBAL_MESH;

                _raycastManager = _mrukRoot.AddComponent<EnvironmentRaycastManager>();
                if (_xrOrigin != null && _xrOrigin.CameraFloorOffsetObject != null)
                {
                    _raycastManager.CustomTrackingSpace = _xrOrigin.CameraFloorOffsetObject.transform;
                }
                _raycastManager.enabled = false;

                _mrukRoot.SetActive(true);
                if (_mruk != null)
                {
                    _mruk.RegisterSceneLoadedCallback(OnMrukSceneLoaded);
                }
            }

            if (_roomAcoustics == null)
            {
                _roomAcoustics = FindFirstObjectByType<MetaXRAudioRoomAcousticProperties>(FindObjectsInactive.Include);
                if (_roomAcoustics == null)
                {
                    GameObject acousticsGo = new GameObject("SonicAudioRoomAcoustics");
                    acousticsGo.transform.SetParent(_mrInfraRoot.transform, false);
                    _roomAcoustics = acousticsGo.AddComponent<MetaXRAudioRoomAcousticProperties>();
                }
                _roomAcoustics.lockPositionToListener = false;
                _roomAcoustics.width = 4.6f;
                _roomAcoustics.height = 2.75f;
                _roomAcoustics.depth = 5.4f;
                _roomAcoustics.clutterFactor = 0.55f;
                _roomAcoustics.leftMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.GypsumBoard;
                _roomAcoustics.rightMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.GypsumBoard;
                _roomAcoustics.frontMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.GypsumBoard;
                _roomAcoustics.backMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.GypsumBoard;
                _roomAcoustics.ceilingMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.AcousticTile;
                _roomAcoustics.floorMaterial = MetaXRAudioRoomAcousticProperties.MaterialPreset.WoodFloor;
            }

            EnsureVoidDissolveDome();
        }

        private void EnsureRoomLightWashMaterial()
        {
            if (_roomLightWashMat != null) return;
            Shader s = Resources.Load<Shader>("Shaders/SonicRoomLightWash");
            if (s == null) s = Shader.Find("SonicTopography/RoomLightWash");
            if (s != null)
            {
                _roomLightWashMat = new Material(s)
                {
                    name = "SonicRoomLightWashRuntime",
                    enableInstancing = true
                };
            }
        }

        private void EnsureVoidDissolveDome()
        {
            if (_dissolveDomeGo != null) return;
            Shader domeShader = Resources.Load<Shader>("Shaders/SonicArVoidDissolveDome");
            if (domeShader == null) domeShader = Shader.Find("SonicTopography/ArVoidDissolveDome");
            if (domeShader == null) return;

            _dissolveDomeMat = new Material(domeShader)
            {
                name = "SonicArVoidDissolveDomeMat"
            };
            _dissolveDomeMesh = CreateInvertedSphereMesh(110f, 24, 16);

            _dissolveDomeGo = new GameObject("SonicArVoidDissolveDome");
            _dissolveDomeGo.transform.SetParent(_mrInfraRoot.transform, false);
            MeshFilter mf = _dissolveDomeGo.AddComponent<MeshFilter>();
            mf.sharedMesh = _dissolveDomeMesh;
            MeshRenderer mr = _dissolveDomeGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _dissolveDomeMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _dissolveDomeGo.SetActive(false);
        }

        private void SetDissolveDomeActive(bool active)
        {
            if (_dissolveDomeGo != null && _dissolveDomeGo.activeSelf != active)
            {
                _dissolveDomeGo.SetActive(active);
            }
        }

        private void EnablePassthroughUnderlay(bool enable)
        {
            OVRManager mgr = EnsureSharedOVRManager();
            if (mgr != null && mgr.isInsightPassthroughEnabled != enable)
            {
                mgr.isInsightPassthroughEnabled = enable;
            }
            if (_passthroughLayer != null)
            {
                _passthroughLayer.hidden = !enable;
            }
        }

        private void SyncDepthManagerState()
        {
            if (_depthManager == null) return;
            bool shouldRun = IsArVisualActive && _realOcclusionEnabled && HasScenePermission;
            try
            {
                if (EnvironmentDepthManager.IsSupported)
                {
                    _depthManager.OcclusionShadersMode = shouldRun
                        ? OcclusionShadersMode.SoftOcclusion
                        : OcclusionShadersMode.None;
                    _depthManager.enabled = shouldRun;
                }
                else
                {
                    _depthManager.enabled = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicMR] EnvironmentDepthManager toggle ignored: {ex.Message}");
            }

            if (_raycastManager != null)
            {
                try
                {
                    bool raySupported = EnvironmentRaycastManager.IsSupported;
                    _raycastManager.enabled = raySupported && IsArVisualActive && HasScenePermission;
                }
                catch
                {
                    _raycastManager.enabled = false;
                }
            }
        }

        private void SyncRoomEffectVisibility()
        {
            if (_effectMesh == null) return;
            bool visible = IsArVisualActive && _roomLightEnabled && HasLoadedRoom;
            _effectMesh.HideMesh = !visible;
        }

        private void SyncContextualBoundarySuppression()
        {
            if (OVRManager.instance == null) return;
            try
            {
                OVRManager.instance.shouldBoundaryVisibilityBeSuppressed =
                    _targetMode == SonicExperienceMode.AR || _arBlend > 0.05f;
            }
            catch
            {
                // Runtime without boundary visibility extension
            }
        }

        private void EnsureScenePermissionAndLoadRoom()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                if (!_scenePermissionRequested)
                {
                    _scenePermissionRequested = true;
                    PermissionCallbacks callbacks = new PermissionCallbacks();
                    callbacks.PermissionGranted += _ =>
                    {
                        SyncDepthManagerState();
                        TryLoadMrukRoomAsync();
                    };
                    callbacks.PermissionDenied += _ =>
                    {
                        Debug.Log("[SonicMR] USE_SCENE denied; running gracefully in free-floating AR mode.");
                        SyncDepthManagerState();
                    };
                    Permission.RequestUserPermission(ScenePermission, callbacks);
                }
                return;
            }
#endif
            TryLoadMrukRoomAsync();
        }

        private async void TryLoadMrukRoomAsync()
        {
            if (_sceneLoadInitiated || _mruk == null) return;
            _sceneLoadInitiated = true;

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!Permission.HasUserAuthorizedPermission(ScenePermission))
                {
                    _sceneLoadInitiated = false;
                    return;
                }
                var res = await _mruk.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false, removeMissingRooms: true);
                Debug.Log($"[SonicMR] MRUK LoadSceneFromDevice result: {res}");
                if (res != MRUK.LoadDeviceResult.Success)
                {
                    // Allow retry later if user completes Space Setup externally
                    _sceneLoadInitiated = false;
                }
#endif
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicMR] MRUK LoadSceneFromDevice exception: {ex.Message}");
                _sceneLoadInitiated = false;
            }
        }

        private void OnMrukSceneLoaded()
        {
            SyncRoomEffectVisibility();
            SyncRoomAcousticsToMrukRoom();
        }

        private void SyncRoomAcousticsToMrukRoom()
        {
            if (_roomAcoustics == null || _mruk == null) return;
            MRUKRoom room = _mruk.GetCurrentRoom();
            if (room == null) return;

            try
            {
                Bounds roomBounds = new Bounds(room.transform.position, new Vector3(4f, 2.6f, 4f));
                bool hasPoint = false;
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor == null) continue;
                    if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.WALL_FACE))
                    {
                        Vector3 c = anchor.GetAnchorCenter();
                        Vector3 sz = anchor.VolumeBounds.HasValue
                            ? anchor.VolumeBounds.Value.size
                            : (anchor.PlaneRect.HasValue
                                ? new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.05f)
                                : new Vector3(1f, 1f, 0.1f));
                        if (!hasPoint)
                        {
                            roomBounds = new Bounds(c, Vector3.Max(sz, Vector3.one * 0.1f));
                            hasPoint = true;
                        }
                        else
                        {
                            roomBounds.Encapsulate(c + sz * 0.5f);
                            roomBounds.Encapsulate(c - sz * 0.5f);
                        }
                    }
                }

                if (hasPoint)
                {
                    _roomAcoustics.transform.position = roomBounds.center;
                    _roomAcoustics.width = Mathf.Clamp(roomBounds.size.x, 2.0f, 18.0f);
                    _roomAcoustics.height = Mathf.Clamp(roomBounds.size.y, 2.0f, 8.0f);
                    _roomAcoustics.depth = Mathf.Clamp(roomBounds.size.z, 2.0f, 18.0f);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicMR] Could not sync RoomAcoustics bounds: {ex.Message}");
            }
        }

        private void Update()
        {
            if (_isTransitioning)
            {
                float step = Time.unscaledDeltaTime / TransitionDurationSeconds;
                if (_targetMode == SonicExperienceMode.AR)
                {
                    _transitionT = Mathf.Min(1f, _transitionT + step);
                }
                else
                {
                    _transitionT = Mathf.Max(0f, _transitionT - step);
                }

                _arBlend = SmoothCubic(_transitionT);
                UpdateTransitionShaders();

                bool reached = (_targetMode == SonicExperienceMode.AR && _transitionT >= 1f) ||
                               (_targetMode == SonicExperienceMode.VR && _transitionT <= 0f);
                if (reached)
                {
                    _isTransitioning = false;
                    _currentMode = _targetMode;
                    FinalizeTransitionState();
                }
            }

            // Retry scene permission / room load silently if user grants outside callback
            if (_targetMode == SonicExperienceMode.AR && !_sceneLoadInitiated && Time.unscaledTime >= _nextPermissionPollTime)
            {
                _nextPermissionPollTime = Time.unscaledTime + 1.0f;
                if (HasScenePermission)
                {
                    SyncDepthManagerState();
                    TryLoadMrukRoomAsync();
                }
            }

            if (_dissolveDomeGo != null && _dissolveDomeGo.activeSelf && Camera.main != null)
            {
                _dissolveDomeGo.transform.position = Camera.main.transform.position;
            }

            PushVisualAndRoomUniforms();

            if (Time.unscaledTime >= _nextAudioRoomSyncTime)
            {
                _nextAudioRoomSyncTime = Time.unscaledTime + 0.1f;
                ApplySpatialAudioForCurrentState();
            }
        }

        private void FinalizeTransitionState()
        {
            if (_currentMode == SonicExperienceMode.AR)
            {
                SetVrEnvironmentRenderersEnabled(false);
                SetDissolveDomeActive(false);
                Shader.SetGlobalVector(IdDissolveParams, Vector4.zero);
                Shader.SetGlobalVector(IdDomeParams, Vector4.zero);
            }
            else
            {
                SetVrEnvironmentRenderersEnabled(true);
                SetDissolveDomeActive(false);
                EnablePassthroughUnderlay(false);
                SyncDepthManagerState();
                SyncRoomEffectVisibility();
                Shader.SetGlobalVector(IdDissolveParams, Vector4.zero);
                Shader.SetGlobalVector(IdDomeParams, Vector4.zero);
            }
            SyncContextualBoundarySuppression();
            ApplySpatialAudioForCurrentState();
        }

        private void UpdateTransitionShaders()
        {
            Color ringColor = _visualizer != null
                ? Color.Lerp(_visualizer.CurrentColors.uCoolCore, _visualizer.CurrentColors.uRippleColor, 0.5f)
                : new Color(0.35f, 0.78f, 1.0f, 1f);
            Shader.SetGlobalColor(IdDissolveRingColor, new Color(ringColor.r, ringColor.g, ringColor.b, 1f));

            // Dome angular sweep: _transitionT 0 -> 1 maps -0.15 -> +1.15
            float domeSweep = Mathf.Lerp(-0.15f, 1.15f, _transitionT);
            Shader.SetGlobalVector(IdDomeForwardWS, new Vector4(_dissolveForwardWS.x, _dissolveForwardWS.y, _dissolveForwardWS.z, 0f));
            Shader.SetGlobalVector(IdDomeParams, new Vector4(domeSweep, 1f, 0.14f, 0f));

            // World-space sweep for VR geometry (Stage, StarRiver, Blocks, Meteors)
            float worldSweepR = Mathf.Lerp(-1.5f, 115f, _transitionT);
            Shader.SetGlobalVector(IdDissolveOriginWS, new Vector4(_dissolveOriginWS.x, _dissolveOriginWS.y, _dissolveOriginWS.z, 1f));
            Shader.SetGlobalVector(IdDissolveParams, new Vector4(worldSweepR, 1f, Mathf.Lerp(1.6f, 9f, _transitionT), 0f));
        }

        private void PushVisualAndRoomUniforms()
        {
            Shader.SetGlobalFloat(IdSonicArModeGlobal, _arBlend);

            GetCurrentTerrainPose(out Vector3 platterPos, out Quaternion platterRot, out float platterScale, out float platterWorldRadius);

            if (_visualizer != null)
            {
                bool occlusionActive = IsArVisualActive && _realOcclusionEnabled &&
                                       _depthManager != null && _depthManager.enabled && _depthManager.IsDepthAvailable;
                _visualizer.SetArModeState(_arBlend, platterPos, platterRot, platterScale, occlusionActive);
                _visualizer.GetCurrentEffectivePlatterPose(out platterPos, out platterRot, out platterScale);
                platterWorldRadius = PlatterLocalRadiusUnits * Mathf.Max(0.0005f, platterScale);

                // Push live platter + theme + ripple state to SonicRoomLightWash globals
                if (_arBlend > 0.001f && _roomLightEnabled)
                {
                    Vector3 upN = platterRot * Vector3.up;
                    Vector3 rightV = platterRot * Vector3.right;
                    Vector3 fwdV = platterRot * Vector3.forward;
                    SonicThemeColors tc = _visualizer.CurrentColors;

                    Shader.SetGlobalVector(IdRoomPlatterCenter, new Vector4(platterPos.x, platterPos.y, platterPos.z, 1f));
                    Shader.SetGlobalVector(IdRoomPlatterNormal, new Vector4(upN.x, upN.y, upN.z, 0f));
                    Shader.SetGlobalVector(IdRoomPlatterRight, new Vector4(rightV.x, rightV.y, rightV.z, 0f));
                    Shader.SetGlobalVector(IdRoomPlatterForward, new Vector4(fwdV.x, fwdV.y, fwdV.z, 0f));
                    Shader.SetGlobalVector(IdRoomParams, new Vector4(
                        platterWorldRadius,
                        platterScale,
                        _visualizer.LastAudioEnergy,
                        _visualizer.LastBassEnergy));
                    Shader.SetGlobalVector(IdRoomFx, new Vector4(
                        _visualizer.LastWarmth,
                        _roomGlow,
                        _arBlend,
                        Time.time));
                    Shader.SetGlobalVector(IdRoomOcc, new Vector4(
                        occlusionActive ? 1f : 0f,
                        1f,
                        0.5f + 0.5f * Mathf.Sin(Time.time * 1.4f),
                        0f));
                    Shader.SetGlobalVector(IdRoomCoolCore, new Vector4(tc.uCoolCore.r, tc.uCoolCore.g, tc.uCoolCore.b, 1f));
                    Shader.SetGlobalVector(IdRoomWarmCore, new Vector4(tc.uWarmCore.r, tc.uWarmCore.g, tc.uWarmCore.b, 1f));
                    Shader.SetGlobalVector(IdRoomRippleColor, new Vector4(tc.uRippleColor.r, tc.uRippleColor.g, tc.uRippleColor.b, 1f));
                    Shader.SetGlobalVectorArray(IdRoomRipplePts, _visualizer.RipplePosTimeStrengthBuffer);
                    Shader.SetGlobalVectorArray(IdRoomRippleState, _visualizer.RippleStateBuffer);
                }
                else
                {
                    Shader.SetGlobalVector(IdRoomFx, new Vector4(0f, 0f, 0f, Time.time));
                }
            }
        }

        private void ApplySpatialAudioForCurrentState()
        {
            if (_audioEngine == null) return;
            GetCurrentTerrainPose(out Vector3 pos, out _, out _, out float radius);
            bool spatialActive = IsArVisualActive;
            _audioEngine.SetSpatialAudioMode(spatialActive, pos, radius);
        }

        private void SetVrEnvironmentRenderersEnabled(bool enabled)
        {
            if (_vrRenderersDisabledForAr == !enabled) return;
            _vrRenderersDisabledForAr = !enabled;

            if (_starRiver != null)
            {
                _starRiver.SetArHidden(!enabled);
            }
            if (_visualizer != null)
            {
                _visualizer.SetVrOnlyEffectsHidden(!enabled);
            }

            GameObject glassCube = GameObject.Find("GlassStageCube");
            if (glassCube != null)
            {
                MeshRenderer mr = glassCube.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = enabled;
                var teleportArea = glassCube.GetComponent<UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea>();
                if (teleportArea != null) teleportArea.enabled = enabled;
            }
        }

        /// <summary>
        /// Soft magnetic snapping onto scanned MRUK surfaces (Tables, Floors, Walls) with depth-raycast fallback.
        /// </summary>
        public bool TryFindBestSurfaceSnap(
            Vector3 desiredCenterWS,
            float terrainWorldRadius,
            bool currentlySnapped,
            out Vector3 snappedCenterWS,
            out Quaternion snappedRotationWS,
            out Vector3 surfaceNormalWS)
        {
            snappedCenterWS = desiredCenterWS;
            snappedRotationWS = Quaternion.identity;
            surfaceNormalWS = Vector3.up;

            if (!_surfaceSnapEnabled) return false;

            float snapInRange = currentlySnapped ? 0.16f : 0.11f;
            float bestDist = float.MaxValue;
            Vector3 bestPoint = desiredCenterWS;
            Vector3 bestNormal = Vector3.up;
            bool found = false;

            // 1. Primary: Query MRUK room anchors (Tables, Couches, Floors, Walls)
            MRUKRoom room = _mruk != null ? _mruk.GetCurrentRoom() : null;
            if (room != null)
            {
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor == null) continue;
                    bool isHorizontalVol = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED);
                    bool isFloor = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR);
                    bool isWall = anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE);
                    if (!isHorizontalVol && !isFloor && !isWall) continue;

                    if (isHorizontalVol && anchor.VolumeBounds.HasValue)
                    {
                        Bounds b = anchor.VolumeBounds.Value;
                        Vector3 localPt = anchor.transform.InverseTransformPoint(desiredCenterWS);
                        // Top face in anchor space
                        float topLocalY = b.max.y;
                        if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH))
                        {
                            // In Scene API volumes, +Z can be top or +Y depending on anchor; test both top face candidates
                            Vector3 worldUpInAnchor = anchor.transform.InverseTransformDirection(Vector3.up);
                            if (Mathf.Abs(worldUpInAnchor.z) > Mathf.Abs(worldUpInAnchor.y))
                            {
                                float zFace = worldUpInAnchor.z > 0f ? b.max.z : b.min.z;
                                Vector3 clampedLocal = new Vector3(
                                    Mathf.Clamp(localPt.x, b.min.x, b.max.x),
                                    Mathf.Clamp(localPt.y, b.min.y, b.max.y),
                                    zFace);
                                Vector3 candidateWS = anchor.transform.TransformPoint(clampedLocal);
                                float d = Vector3.Distance(desiredCenterWS, candidateWS);
                                if (d < snapInRange && d < bestDist)
                                {
                                    bestDist = d;
                                    bestPoint = candidateWS;
                                    bestNormal = Vector3.up;
                                    found = true;
                                }
                                continue;
                            }
                        }

                        Vector3 clamped = new Vector3(
                            Mathf.Clamp(localPt.x, b.min.x, b.max.x),
                            topLocalY,
                            Mathf.Clamp(localPt.z, b.min.z, b.max.z));
                        Vector3 topWS = anchor.transform.TransformPoint(clamped);
                        float dist = Vector3.Distance(desiredCenterWS, topWS);
                        if (dist < snapInRange && dist < bestDist)
                        {
                            bestDist = dist;
                            bestPoint = topWS;
                            bestNormal = Vector3.up;
                            found = true;
                        }
                    }
                    else if (anchor.PlaneRect.HasValue)
                    {
                        Rect rect = anchor.PlaneRect.Value;
                        Vector3 localPt = anchor.transform.InverseTransformPoint(desiredCenterWS);
                        Vector3 clampedLocal = new Vector3(
                            Mathf.Clamp(localPt.x, rect.xMin, rect.xMax),
                            Mathf.Clamp(localPt.y, rect.yMin, rect.yMax),
                            0f);
                        Vector3 candidateWS = anchor.transform.TransformPoint(clampedLocal);
                        float dist = Vector3.Distance(desiredCenterWS, candidateWS);
                        if (dist < snapInRange && dist < bestDist)
                        {
                            Vector3 planeNormal = anchor.transform.forward;
                            if (Camera.main != null && Vector3.Dot(planeNormal, Camera.main.transform.position - candidateWS) < 0f)
                            {
                                planeNormal = -planeNormal;
                            }
                            if (isFloor && planeNormal.y < 0.7f) planeNormal = Vector3.up;
                            bestDist = dist;
                            bestPoint = candidateWS;
                            bestNormal = planeNormal.normalized;
                            found = true;
                        }
                    }
                }
            }

            // 2. Fallback: Raycast against MRUK EffectMesh colliders
            if (!found && HasLoadedRoom)
            {
                int mask = 1 << _mrukRoomLayer;
                Vector3 camFwd = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
                Vector3[] dirs = { Vector3.down, Vector3.up, new Vector3(camFwd.x, 0f, camFwd.z).normalized };
                foreach (Vector3 dir in dirs)
                {
                    if (dir.sqrMagnitude < 0.001f) continue;
                    if (Physics.Raycast(desiredCenterWS - dir * 0.04f, dir, out RaycastHit hit, snapInRange + 0.05f, mask))
                    {
                        float d = Mathf.Abs(Vector3.Distance(desiredCenterWS, hit.point));
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestPoint = hit.point;
                            bestNormal = hit.normal.normalized;
                            found = true;
                        }
                    }
                }
            }

            // 3. Fallback 2: Depth API EnvironmentRaycastManager when no Scene Scan exists
            if (!found && _raycastManager != null && _raycastManager.enabled && EnvironmentRaycastManager.IsSupported)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    Vector3 toTarget = desiredCenterWS - cam.transform.position;
                    float distToTarget = toTarget.magnitude;
                    if (distToTarget > 0.15f &&
                        _raycastManager.Raycast(new Ray(cam.transform.position, toTarget / distToTarget), out EnvironmentRaycastHit envHit, distToTarget + snapInRange))
                    {
                        float d = Vector3.Distance(desiredCenterWS, envHit.point);
                        if (d <= snapInRange)
                        {
                            bestDist = d;
                            bestPoint = envHit.point;
                            bestNormal = envHit.normalConfidence > 0.25f && envHit.normal.sqrMagnitude > 0.01f
                                ? envHit.normal.normalized
                                : Vector3.up;
                            found = true;
                        }
                    }
                }
            }

            if (!found) return false;

            snappedCenterWS = bestPoint;
            surfaceNormalWS = bestNormal;

            if (bestNormal.y > 0.72f)
            {
                surfaceNormalWS = Vector3.up;
                snappedRotationWS = Quaternion.Euler(0f, _arTargetRot.eulerAngles.y, 0f);
            }
            else if (Mathf.Abs(bestNormal.y) < 0.40f)
            {
                // Auto-tilt 90 degrees onto wall so pillars extrude outward into room while staying upright in local XZ
                Vector3 flatNormal = new Vector3(bestNormal.x, 0f, bestNormal.z).normalized;
                surfaceNormalWS = flatNormal;
                snappedRotationWS = Quaternion.LookRotation(Vector3.down, flatNormal);
            }
            else
            {
                snappedRotationWS = Quaternion.FromToRotation(Vector3.up, bestNormal);
            }

            return true;
        }

        private static float SmoothCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static Mesh CreateInvertedSphereMesh(float radius, int lonSegments, int latSegments)
        {
            Mesh mesh = new Mesh { name = "SonicArVoidDissolveDomeMesh" };
            int vertCount = (lonSegments + 1) * (latSegments + 1);
            Vector3[] verts = new Vector3[vertCount];
            int[] tris = new int[lonSegments * latSegments * 6];

            int v = 0;
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float a1 = Mathf.PI * lat / latSegments;
                float sin1 = Mathf.Sin(a1);
                float cos1 = Mathf.Cos(a1);
                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float a2 = Mathf.PI * 2f * lon / lonSegments;
                    verts[v++] = new Vector3(sin1 * Mathf.Cos(a2), cos1, sin1 * Mathf.Sin(a2)) * radius;
                }
            }

            int t = 0;
            int stride = lonSegments + 1;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int cur = lat * stride + lon;
                    int next = cur + stride;
                    tris[t++] = cur;
                    tris[t++] = cur + 1;
                    tris[t++] = next + 1;

                    tris[t++] = cur;
                    tris[t++] = next + 1;
                    tris[t++] = next;
                }
            }

            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (radius * 2f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            Shader.SetGlobalFloat(IdSonicArModeGlobal, 0f);
            Shader.SetGlobalVector(IdDissolveParams, Vector4.zero);
            Shader.SetGlobalVector(IdDomeParams, Vector4.zero);
            if (_roomLightWashMat != null) Destroy(_roomLightWashMat);
            if (_dissolveDomeMat != null) Destroy(_dissolveDomeMat);
            if (_dissolveDomeMesh != null) Destroy(_dissolveDomeMesh);
        }
    }
}
