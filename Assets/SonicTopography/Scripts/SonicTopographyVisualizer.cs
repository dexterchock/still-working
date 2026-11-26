using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace SonicTopography
{
    public enum SonicColorMode
    {
        AutoAlbumArt = 0,
        PresetThemes = 1,
        ManualColors = 2
    }

    public enum SonicColorFallbackMode
    {
        DefaultPresetTheme = 0,
        UserManualColors = 1
    }

    [RequireComponent(typeof(SonicTopographyAudioEngine))]
    public class SonicTopographyVisualizer : MonoBehaviour
    {
        private const int MAX_RIPPLES = 10;
        private const int MAX_METEORS = 20;
        private const int MAX_PARTICLES = 200;
        private const int BATCH_MAX = 1023;

        public const float DefaultRotationSpeed = 0f;
        public const float DefaultAmplitude = 50f;
        public const float DefaultMotionSpeed = 50f;

        [Header("Scene & Spatial Placement (VR)")]
        [Tooltip("World position of the Sonic Topography platter center beneath the VR Glass Stage")]
        public Vector3 platterWorldPosition = new Vector3(0f, -26f, 64f);

        [Tooltip("Platter Y rotation speed in radians/sec (0 = stationary default)")]
        public float rotationSpeed = DefaultRotationSpeed;

        [Header("Theme, Colors & Ground EQ")]
        public SonicColorMode colorMode = SonicColorMode.AutoAlbumArt;
        public SonicColorFallbackMode noCoverFallbackMode = SonicColorFallbackMode.DefaultPresetTheme;
        public SonicThemePreset activeTheme = SonicThemePreset.Nocturnal;
        public SonicThemeColors manualThemeColors;
        public GroundEqSettings groundEqSettings = new GroundEqSettings();

        [Header("Effect Toggles (Performance & Visuals)")]
        public bool enableTerrainPillars = true;
        public bool enableAmbientParticles = true;

        [Header("VR & Desktop Interaction")]
        public bool enableInteractiveRipples = true;
        public bool enableThemeCycleShortcut = true;
        [NonSerialized] public bool suppressWorldPointerInput = false;

        private bool _manualColorsInitialized = false;
        private bool _userHasSetManualColors = false;
        private bool _hasActiveAlbumPalette = false;
        private SonicThemeColors _activeAlbumPalette;

        public SonicTopographyAudioEngine AudioEngine => _audioEngine;
        public SonicThemeColors CurrentColors => _currentColors;
        public bool HasActiveAlbumPalette => _hasActiveAlbumPalette;
        public SonicThemeColors ActiveAlbumPalette => _activeAlbumPalette;
        public bool UserHasSetManualColors => _userHasSetManualColors;

        private SonicTopographyAudioEngine _audioEngine;

        // Platter Root & Procedural MeshRenderers (16-bit index chunks for Quest 3 Adreno GPU compatibility)
        private const int PILLARS_PER_CHUNK = 3000;
        private GameObject _platterRoot;
        private GameObject _terrainColliderGo;
        private Mesh _terrainDiscColliderMesh;
        private Mesh[] _terrainChunkMeshes;
        private MeshRenderer[] _terrainChunkRenderers;
        private Mesh _floatingBlocksCombinedMesh;
        private MeshRenderer _floatingBlocksRenderer;
        private Mesh _meteorsCombinedMesh;
        private MeshRenderer _meteorsRenderer;
        private Mesh _particlesCombinedMesh;
        private MeshRenderer _particlesRenderer;

        // Materials
        private Material _terrainMaterial;
        private Material _floatingBlockMaterial;
        private Material _meteorMaterial;
        private Material _particleMaterial;
        private Material _skyboxMaterial;

        // Cached terrain grid state
        private int _builtGridSize = -1;
        private float _builtBoxWidth = -1f;

        // Smoothed Ground EQ band state
        private float _smSubBass;
        private float _smBass;
        private float _smLowMid;
        private float _smMid;
        private float _smHighMid;
        private float _smPresence;
        private float _smBrilliance;
        private float _smAir;

        // Theme color lerp state
        private SonicThemeColors _currentColors;
        private bool _colorsInitialized = false;

        // Platter rotation state
        private float _platterRotationRad = 0f;

        // Mixed Reality (AR) adaptation state
        private float _arBlend = 0f;
        private Vector3 _arPlatterPos = new Vector3(0f, -26f, 64f);
        private Quaternion _arPlatterRot = Quaternion.identity;
        private float _arPlatterScale = 1f;
        private bool _arOcclusionEnabled = false;
        private bool _arIsSnapped = false;
        private bool _vrEffectsHidden = false;

        public float CurrentPlatterRotationDegrees => _platterRotationRad * Mathf.Rad2Deg;
        public float LastAudioEnergy { get; private set; }
        public float LastBassEnergy { get; private set; }
        public float LastWarmth { get; private set; }
        public Vector4[] RipplePosTimeStrengthBuffer => _ripplePosTimeStrength;
        public Vector4[] RippleStateBuffer => _rippleState;

        public void TriggerRippleAtLocalPos(Vector2 localXZ, float strength, bool isWhite = false)
        {
            AddRipple(localXZ.x, localXZ.y, strength, isWhite);
        }

        public void GetCurrentEffectivePlatterPose(out Vector3 pos, out Quaternion rot, out float scale)
        {
            if (_platterRoot != null)
            {
                pos = _platterRoot.transform.position;
                rot = _platterRoot.transform.rotation;
                scale = _platterRoot.transform.localScale.x;
                return;
            }
            if (_arBlend > 0.0001f)
            {
                pos = _arPlatterPos;
                rot = _arPlatterRot * Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f);
                scale = Mathf.Max(0.0005f, _arPlatterScale);
            }
            else
            {
                pos = platterWorldPosition;
                rot = Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f);
                scale = 1f;
            }
        }

        public void SetArModeState(float arBlend, Vector3 arPos, Quaternion arRot, float arScale, bool occlusionEnabled, bool isSnapped = false)
        {
            _arBlend = Mathf.Clamp01(arBlend);
            _arPlatterPos = arPos;
            _arPlatterRot = arRot;
            _arPlatterScale = Mathf.Max(0.0005f, arScale);
            // Real Occlusion disabled for now so AR crystal pillars always render crisply opaque without depth-map artifacts
            _arOcclusionEnabled = false;
            _arIsSnapped = isSnapped;

            Vector3 effectivePos = _arBlend > 0.0001f ? _arPlatterPos : platterWorldPosition;
            Quaternion effectiveRot = _arBlend > 0.0001f
                ? _arPlatterRot * Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f)
                : Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f);
            float effectiveScale = _arBlend > 0.0001f ? _arPlatterScale : 1f;

            if (_platterRoot != null)
            {
                _platterRoot.transform.SetPositionAndRotation(effectivePos, effectiveRot);
                _platterRoot.transform.localScale = Vector3.one * effectiveScale;
            }
            if (_terrainColliderGo != null)
            {
                bool shouldEnableCollider = _arBlend <= 0.05f;
                if (_terrainColliderGo.activeSelf != shouldEnableCollider)
                {
                    _terrainColliderGo.SetActive(shouldEnableCollider);
                }
            }
            if (_terrainMaterial != null)
            {
                _terrainMaterial.SetMatrix(PropPlatterMatrix, Matrix4x4.TRS(effectivePos, effectiveRot, Vector3.one * effectiveScale));
                _terrainMaterial.SetFloat(PropUArMode, _arBlend);
                _terrainMaterial.SetFloat(PropUArOcclusionEnabled, 0f);
                _terrainMaterial.SetFloat(PropEnvironmentDepthBias, _arIsSnapped ? 0.032f : 0.012f);
            }
        }

        public void SetVrOnlyEffectsHidden(bool hidden)
        {
            _vrEffectsHidden = hidden;
            if (hidden)
            {
                if (_floatingBlocksRenderer != null) _floatingBlocksRenderer.enabled = false;
                if (_meteorsRenderer != null) _meteorsRenderer.enabled = false;
                if (_particlesRenderer != null) _particlesRenderer.enabled = false;
            }
        }

        // Ripples ring buffer
        private readonly Vector4[] _ripplePosTimeStrength = new Vector4[MAX_RIPPLES];
        private readonly Vector4[] _rippleState = new Vector4[MAX_RIPPLES];
        private int _rippleIndex = 0;

        // Floating blocks state
        private struct FloatingBlockData
        {
            public float x;
            public float y;
            public float z;
            public float baseScale;
            public float phase;
            public float rotationSpeed;
        }
        private FloatingBlockData[] _floatingBlocks;
        private float _floatingBlockPulse = 0f;

        // Meteors state
        private struct MeteorState
        {
            public bool active;
            public float x;
            public float y;
            public float z;
            public float speed;
            public float strength;
        }
        private readonly MeteorState[] _meteors = new MeteorState[MAX_METEORS];
        private readonly Vector4[] _meteorPosActive = new Vector4[MAX_PARTICLES];
        private readonly Vector4[] _meteorScale = new Vector4[MAX_PARTICLES];
        private int _meteorIndex = 0;
        private float _lastMeteorSpawnTime = -999f;

        // Particles state
        private struct ParticleState
        {
            public bool active;
            public float x, y, z;
            public float vx, vy, vz;
            public float life, maxLife, scale;
        }
        private readonly ParticleState[] _particles = new ParticleState[MAX_PARTICLES];
        private readonly Vector4[] _particlePosActive = new Vector4[MAX_PARTICLES];
        private readonly Vector4[] _particleScale = new Vector4[MAX_PARTICLES];
        private int _particleIndex = 0;

        // Pointer / Controller interaction state
        private bool _wasRightTriggerPressed;
        private float _rightPressStartTime;
        private bool _wasLeftTriggerPressed;
        private float _leftPressStartTime;
        private bool _wasThemeButtonPressed;
        private float _mousePressStartTime;

        private static readonly int ThemePresetCount = Enum.GetValues(typeof(SonicThemePreset)).Length;
        private Camera[] _cameraBuffer = new Camera[8];

        private UnityEngine.XR.InputDevice _rightXrDevice;
        private UnityEngine.XR.InputDevice _leftXrDevice;
        private UnityEngine.InputSystem.XR.XRController _cachedXrRight;
        private UnityEngine.InputSystem.Controls.AxisControl _rightTriggerAxis;
        private UnityEngine.InputSystem.Controls.ButtonControl _rightTriggerBtn;
        private UnityEngine.InputSystem.XR.XRController _cachedXrLeft;
        private UnityEngine.InputSystem.Controls.AxisControl _leftTriggerAxis;
        private UnityEngine.InputSystem.Controls.ButtonControl _leftTriggerBtn;

        // Shader property IDs
        private static readonly int PropUTime = Shader.PropertyToID("_uTime");
        private static readonly int PropUSubBass = Shader.PropertyToID("_uSubBass");
        private static readonly int PropUBass = Shader.PropertyToID("_uBass");
        private static readonly int PropULowMid = Shader.PropertyToID("_uLowMid");
        private static readonly int PropUMid = Shader.PropertyToID("_uMid");
        private static readonly int PropUHighMid = Shader.PropertyToID("_uHighMid");
        private static readonly int PropUPresence = Shader.PropertyToID("_uPresence");
        private static readonly int PropUBrilliance = Shader.PropertyToID("_uBrilliance");
        private static readonly int PropUAir = Shader.PropertyToID("_uAir");
        private static readonly int PropUWarmth = Shader.PropertyToID("_uWarmth");
        private static readonly int PropUBrightness = Shader.PropertyToID("_uBrightness");
        private static readonly int PropUSharpness = Shader.PropertyToID("_uSharpness");
        private static readonly int PropUSmoothness = Shader.PropertyToID("_uSmoothness");
        private static readonly int PropUDensity = Shader.PropertyToID("_uDensity");
        private static readonly int PropUSpectralCentroid = Shader.PropertyToID("_uSpectralCentroid");
        private static readonly int PropUEnergy = Shader.PropertyToID("_uEnergy");
        private static readonly int PropUAmplitude = Shader.PropertyToID("_uAmplitude");
        private static readonly int PropUPulse = Shader.PropertyToID("_uPulse");

        private static readonly int PropUBaseColor1 = Shader.PropertyToID("_uBaseColor1");
        private static readonly int PropUBaseColor2 = Shader.PropertyToID("_uBaseColor2");
        private static readonly int PropUFogColor = Shader.PropertyToID("_uFogColor");
        private static readonly int PropUCoolCore = Shader.PropertyToID("_uCoolCore");
        private static readonly int PropUCoolEdge = Shader.PropertyToID("_uCoolEdge");
        private static readonly int PropUWarmCore = Shader.PropertyToID("_uWarmCore");
        private static readonly int PropUWarmEdge = Shader.PropertyToID("_uWarmEdge");
        private static readonly int PropURippleColor = Shader.PropertyToID("_uRippleColor");
        private static readonly int PropUGlowIntensity = Shader.PropertyToID("_uGlowIntensity");
        private static readonly int PropUBackdropLinear = Shader.PropertyToID("_uBackdropLinear");

        private static readonly int PropPlatterMatrix = Shader.PropertyToID("_PlatterMatrix");
        private static readonly int PropBoxWidth = Shader.PropertyToID("_BoxWidth");
        private static readonly int PropRipplePosTimeStrength = Shader.PropertyToID("_uRipplePosTimeStrength");
        private static readonly int PropRippleState = Shader.PropertyToID("_uRippleState");
        private static readonly int PropBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int PropUPulseScale = Shader.PropertyToID("_uPulseScale");
        private static readonly int PropULiftOffset = Shader.PropertyToID("_uLiftOffset");
        private static readonly int PropUItemPosActive = Shader.PropertyToID("_uItemPosActive");
        private static readonly int PropUItemScale = Shader.PropertyToID("_uItemScale");
        private static readonly int PropUArMode = Shader.PropertyToID("_uArMode");
        private static readonly int PropUArOcclusionEnabled = Shader.PropertyToID("_uArOcclusionEnabled");
        private static readonly int PropEnvironmentDepthBias = Shader.PropertyToID("_EnvironmentDepthBias");

        private void Awake()
        {
            _audioEngine = GetComponent<SonicTopographyAudioEngine>();
            InitializeMeshesAndMaterials();
            InitializeRipples();
            RebuildTerrainGridIfNeeded();
            RebuildFloatingBlocksIfNeeded();

            LoadPreferences();

            _currentColors = SonicTopographyConfig.GetTheme(activeTheme);
            if (!_manualColorsInitialized)
            {
                manualThemeColors = _currentColors;
                _manualColorsInitialized = true;
            }
            _colorsInitialized = true;
            ApplyCameraAndEnvironmentBackdrop(_currentColors);
        }

        public void LoadPreferences()
        {
            if (PlayerPrefs.HasKey("SonicTopography_MotionRotation"))
            {
                rotationSpeed = PlayerPrefs.GetFloat("SonicTopography_MotionRotation", DefaultRotationSpeed);
            }
            if (groundEqSettings != null)
            {
                if (PlayerPrefs.HasKey("SonicTopography_MotionElevation"))
                {
                    groundEqSettings.amplitude = PlayerPrefs.GetFloat("SonicTopography_MotionElevation", DefaultAmplitude);
                }
                if (PlayerPrefs.HasKey("SonicTopography_MotionSpeed"))
                {
                    groundEqSettings.motionSpeed = PlayerPrefs.GetFloat("SonicTopography_MotionSpeed", DefaultMotionSpeed);
                }
                if (PlayerPrefs.HasKey("SonicTopography_FxFloatingBlocks"))
                {
                    groundEqSettings.floatingBlocksEnabled = PlayerPrefs.GetInt("SonicTopography_FxFloatingBlocks", 1) == 1;
                }
            }

            if (PlayerPrefs.HasKey("SonicTopography_FxAmbientDust"))
            {
                enableAmbientParticles = PlayerPrefs.GetInt("SonicTopography_FxAmbientDust", 1) == 1;
            }
            if (PlayerPrefs.HasKey("SonicTopography_FxRayRipples"))
            {
                enableInteractiveRipples = PlayerPrefs.GetInt("SonicTopography_FxRayRipples", 1) == 1;
            }

            if (_audioEngine != null)
            {
                if (PlayerPrefs.HasKey("SonicTopography_FxKickPulse"))
                {
                    _audioEngine.pulseTrigger.enabled = PlayerPrefs.GetInt("SonicTopography_FxKickPulse", 1) == 1;
                }
                if (PlayerPrefs.HasKey("SonicTopography_FxSnareRipples"))
                {
                    _audioEngine.snareTrigger.enabled = PlayerPrefs.GetInt("SonicTopography_FxSnareRipples", 1) == 1;
                }
                if (PlayerPrefs.HasKey("SonicTopography_FxMeteors"))
                {
                    _audioEngine.meteorTrigger.enabled = PlayerPrefs.GetInt("SonicTopography_FxMeteors", 1) == 1;
                }
            }

            if (PlayerPrefs.HasKey("SonicTopography_ColorMode"))
            {
                colorMode = (SonicColorMode)PlayerPrefs.GetInt("SonicTopography_ColorMode", (int)SonicColorMode.AutoAlbumArt);
            }
            if (PlayerPrefs.HasKey("SonicTopography_ActiveTheme"))
            {
                activeTheme = (SonicThemePreset)PlayerPrefs.GetInt("SonicTopography_ActiveTheme", (int)SonicThemePreset.Nocturnal);
            }
        }

        public void ResetMotionToDefaults()
        {
            rotationSpeed = DefaultRotationSpeed;
            _platterRotationRad = 0f;
            if (groundEqSettings != null)
            {
                groundEqSettings.amplitude = DefaultAmplitude;
                groundEqSettings.motionSpeed = DefaultMotionSpeed;
            }
            PlayerPrefs.SetFloat("SonicTopography_MotionRotation", DefaultRotationSpeed);
            PlayerPrefs.SetFloat("SonicTopography_MotionElevation", DefaultAmplitude);
            PlayerPrefs.SetFloat("SonicTopography_MotionSpeed", DefaultMotionSpeed);
            PlayerPrefs.Save();
        }

        public void SetManualPalette(Color baseColor, Color coolTone, Color warmTone, Color rippleAccent, float glowIntensity = 1.25f)
        {
            Color deepVoid = Color.Lerp(Color.black, baseColor, 0.05f);
            deepVoid.a = 1f;
            Color troughColor = Color.Lerp(Color.black, Color.Lerp(baseColor, coolTone, 0.35f), 0.14f);
            troughColor.a = 1f;
            manualThemeColors = new SonicThemeColors
            {
                uBaseColor1 = deepVoid,
                uBaseColor2 = troughColor,
                uFogColor = deepVoid,
                uCoolCore = coolTone,
                uCoolEdge = Color.Lerp(coolTone, Color.white, 0.45f),
                uWarmCore = warmTone,
                uWarmEdge = Color.Lerp(warmTone, Color.white, 0.55f),
                uRippleColor = rippleAccent,
                uGlowIntensity = glowIntensity
            };
            _manualColorsInitialized = true;
            _userHasSetManualColors = true;
            noCoverFallbackMode = SonicColorFallbackMode.UserManualColors;
        }

        public void SetAlbumArtPalette(bool hasAlbumCover, SonicThemeColors extractedPalette)
        {
            _hasActiveAlbumPalette = hasAlbumCover;
            if (hasAlbumCover)
            {
                _activeAlbumPalette = extractedPalette;
            }
        }

        public void CycleToNextPresetTheme()
        {
            int next = ((int)activeTheme + 1) % ThemePresetCount;
            activeTheme = (SonicThemePreset)next;
        }

        private void OnEnable()
        {
            if (_audioEngine == null) _audioEngine = GetComponent<SonicTopographyAudioEngine>();
            if (_audioEngine != null)
            {
                _audioEngine.OnFreqTrigger += HandleFreqTrigger;
            }
        }

        private void OnDisable()
        {
            if (_audioEngine != null)
            {
                _audioEngine.OnFreqTrigger -= HandleFreqTrigger;
            }
        }

        private void OnDestroy()
        {
            if (_terrainChunkMeshes != null)
            {
                for (int i = 0; i < _terrainChunkMeshes.Length; i++)
                {
                    if (_terrainChunkMeshes[i] != null) Destroy(_terrainChunkMeshes[i]);
                }
            }
            if (_floatingBlocksCombinedMesh != null) Destroy(_floatingBlocksCombinedMesh);
            if (_meteorsCombinedMesh != null) Destroy(_meteorsCombinedMesh);
            if (_particlesCombinedMesh != null) Destroy(_particlesCombinedMesh);
            if (_terrainColliderGo != null) Destroy(_terrainColliderGo);
            if (_terrainDiscColliderMesh != null) Destroy(_terrainDiscColliderMesh);
            if (_platterRoot != null) Destroy(_platterRoot);

            if (_terrainMaterial != null) Destroy(_terrainMaterial);
            if (_floatingBlockMaterial != null) Destroy(_floatingBlockMaterial);
            if (_meteorMaterial != null) Destroy(_meteorMaterial);
            if (_particleMaterial != null) Destroy(_particleMaterial);
            if (_skyboxMaterial != null) Destroy(_skyboxMaterial);
        }

        private static Shader FindOrLoadShader(string resourceName, string shaderName)
        {
            Shader s = Resources.Load<Shader>(resourceName);
            if (s == null) s = Shader.Find(shaderName);
            return s;
        }

        private void EnsurePlatterRoot()
        {
            if (_platterRoot == null)
            {
                _platterRoot = new GameObject("SonicPlatterVisualRoot");
                _platterRoot.transform.position = platterWorldPosition;
                _platterRoot.transform.rotation = Quaternion.identity;
                _platterRoot.transform.localScale = Vector3.one;
            }
            EnsureTerrainGroundCollider();
        }

        private void EnsureTerrainGroundCollider()
        {
            if (_platterRoot == null) return;
            if (_terrainColliderGo != null) return;

            _terrainColliderGo = new GameObject("SonicTerrainPlatterGroundCollider");
            _terrainColliderGo.transform.SetParent(_platterRoot.transform, false);
            _terrainColliderGo.transform.localPosition = Vector3.zero;
            _terrainColliderGo.transform.localRotation = Quaternion.identity;
            _terrainColliderGo.transform.localScale = Vector3.one;

            if (_terrainDiscColliderMesh == null)
            {
                // Top surface at local Y = 15.0m (-11.0m world), bottom at local Y = 13.0m, radius 78.5m, 36 segments
                _terrainDiscColliderMesh = CreateTerrainPlatterDiscMesh(78.5f, 15.0f, 13.0f, 36);
            }

            MeshCollider mc = _terrainColliderGo.AddComponent<MeshCollider>();
            mc.sharedMesh = _terrainDiscColliderMesh;
            mc.convex = false;
            mc.isTrigger = false;

            // Enable XR Teleportation on the terrain platter
            var teleArea = _terrainColliderGo.AddComponent<UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea>();
            teleArea.enabled = true;
            int teleportLayer = UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask.GetMask("Teleport");
            if (teleportLayer == 0) teleportLayer = 1 << 31;
            teleArea.interactionLayers = teleportLayer;
            teleArea.matchOrientation = UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.MatchOrientation.WorldSpaceUp;
            teleArea.teleportTrigger = UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.BaseTeleportationInteractable.TeleportTrigger.OnSelectExited;
            if (!teleArea.colliders.Contains(mc))
            {
                teleArea.colliders.Add(mc);
            }
        }

        private static Mesh CreateTerrainPlatterDiscMesh(float radius, float topY, float bottomY, int segments)
        {
            Mesh mesh = new Mesh { name = "SonicTerrainPlatterDiscMesh" };
            int vertCount = (segments + 1) * 2 + segments * 4;
            Vector3[] verts = new Vector3[vertCount];
            int[] tris = new int[(segments * 2 + segments * 2) * 3];

            int v = 0;
            int t = 0;

            // 1. Center top vertex
            int topCenterIdx = v++;
            verts[topCenterIdx] = new Vector3(0f, topY, 0f);

            int topRingStart = v;
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                verts[v++] = new Vector3(Mathf.Cos(angle) * radius, topY, Mathf.Sin(angle) * radius);
            }

            // Top cap triangles (seen from above)
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                tris[t++] = topCenterIdx;
                tris[t++] = topRingStart + next;
                tris[t++] = topRingStart + i;
            }

            // 2. Center bottom vertex
            int botCenterIdx = v++;
            verts[botCenterIdx] = new Vector3(0f, bottomY, 0f);

            int botRingStart = v;
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                verts[v++] = new Vector3(Mathf.Cos(angle) * radius, bottomY, Mathf.Sin(angle) * radius);
            }

            // Bottom cap triangles (seen from below)
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                tris[t++] = botCenterIdx;
                tris[t++] = botRingStart + i;
                tris[t++] = botRingStart + next;
            }

            // 3. Side quad triangles
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int t0 = v++;
                int t1 = v++;
                int b0 = v++;
                int b1 = v++;

                float a0 = (float)i / segments * Mathf.PI * 2f;
                float a1 = (float)next / segments * Mathf.PI * 2f;

                verts[t0] = new Vector3(Mathf.Cos(a0) * radius, topY, Mathf.Sin(a0) * radius);
                verts[t1] = new Vector3(Mathf.Cos(a1) * radius, topY, Mathf.Sin(a1) * radius);
                verts[b0] = new Vector3(Mathf.Cos(a0) * radius, bottomY, Mathf.Sin(a0) * radius);
                verts[b1] = new Vector3(Mathf.Cos(a1) * radius, bottomY, Mathf.Sin(a1) * radius);

                tris[t++] = t0;
                tris[t++] = t1;
                tris[t++] = b0;

                tris[t++] = b0;
                tris[t++] = t1;
                tris[t++] = b1;
            }

            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material CreateRuntimeMaterial(string matResourceName, string shaderResourceName, string shaderName)
        {
            Material templateMat = Resources.Load<Material>(matResourceName);
            if (templateMat != null && templateMat.shader != null)
            {
                return new Material(templateMat);
            }
            Shader s = FindOrLoadShader(shaderResourceName, shaderName);
            if (s == null)
            {
                Debug.LogError($"[SonicVisualizer] Missing shader: {shaderName} ({shaderResourceName})");
                return null;
            }
            return new Material(s);
        }

        private void InitializeMeshesAndMaterials()
        {
            EnsurePlatterRoot();

            _terrainMaterial = CreateRuntimeMaterial("Materials/SonicTerrainPillars", "Shaders/SonicTerrainPillars", "SonicTopography/TerrainPillars");
            _floatingBlockMaterial = CreateRuntimeMaterial("Materials/SonicFloatingBlocks", "Shaders/SonicFloatingBlocks", "SonicTopography/FloatingBlocks");
            _meteorMaterial = CreateRuntimeMaterial("Materials/SonicSimpleInstanced", "Shaders/SonicSimpleInstanced", "SonicTopography/SimpleInstanced");
            _particleMaterial = CreateRuntimeMaterial("Materials/SonicSimpleInstanced", "Shaders/SonicSimpleInstanced", "SonicTopography/SimpleInstanced");
            _skyboxMaterial = CreateRuntimeMaterial("Materials/SonicSolidSkybox", "Shaders/SonicSolidSkybox", "SonicTopography/SolidSkybox");

            // Build combined 16-bit meshes for Meteors (20 cubes) and Particles (200 cubes)
            _meteorsCombinedMesh = CreateIndexedCubesMesh(MAX_METEORS, "SonicMeteorsCombinedMesh");
            _meteorsRenderer = CreateChildMeshRenderer("SonicMeteorsRenderer", _meteorsCombinedMesh, _meteorMaterial);
            _meteorsRenderer.enabled = false;

            _particlesCombinedMesh = CreateIndexedCubesMesh(MAX_PARTICLES, "SonicParticlesCombinedMesh");
            _particlesRenderer = CreateChildMeshRenderer("SonicParticlesRenderer", _particlesCombinedMesh, _particleMaterial);
            _particlesRenderer.enabled = false;

            Debug.Log($"[SonicVisualizer] Initialized shaders: terrain={(_terrainMaterial != null && _terrainMaterial.shader != null && _terrainMaterial.shader.isSupported)}, floating={(_floatingBlockMaterial != null && _floatingBlockMaterial.shader != null && _floatingBlockMaterial.shader.isSupported)}");
        }

        private MeshRenderer CreateChildMeshRenderer(string objName, Mesh mesh, Material mat)
        {
            EnsurePlatterRoot();
            GameObject go = new GameObject(objName);
            go.transform.SetParent(_platterRoot.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        private void InitializeRipples()
        {
            for (int i = 0; i < MAX_RIPPLES; i++)
            {
                _ripplePosTimeStrength[i] = new Vector4(0f, 0f, -100f, 0f);
                _rippleState[i] = Vector4.zero;
            }
        }

        public GameObject PlatterRoot => _platterRoot;
        public float CurrentPlatterRotationDeg => _platterRotationRad * Mathf.Rad2Deg;

        public void AddRipple(float platterX, float platterZ, float strength, bool isWhite = false)
        {
            int idx = _rippleIndex;
            _ripplePosTimeStrength[idx] = new Vector4(platterX, platterZ, Time.time, strength);
            _rippleState[idx] = new Vector4(1f, isWhite ? 1f : 0f, 0f, 0f);
            _rippleIndex = (idx + 1) % MAX_RIPPLES;
        }

        public void AddMeteor(float strength)
        {
            float now = Time.time;
            float cooldownSeconds = _audioEngine != null ? (_audioEngine.meteorTrigger.cooldown / 60f) : 4f;
            if (now - _lastMeteorSpawnTime < cooldownSeconds) return;
            _lastMeteorSpawnTime = now;

            int idx = _meteorIndex;
            float angle = UnityEngine.Random.value * Mathf.PI * 2f;
            float dist = UnityEngine.Random.value * 25f;

            _meteors[idx] = new MeteorState
            {
                active = true,
                x = Mathf.Cos(angle) * dist,
                z = Mathf.Sin(angle) * dist,
                y = 30f + UnityEngine.Random.value * 10f,
                speed = 1.0f + UnityEngine.Random.value * 0.5f + (strength * 1.5f),
                strength = strength
            };
            _meteorIndex = (idx + 1) % MAX_METEORS;
        }

        private void SpawnParticle(float x, float y, float z, float speedMultiplier)
        {
            int idx = _particleIndex;
            _particles[idx] = new ParticleState
            {
                active = true,
                x = x + (UnityEngine.Random.value - 0.5f) * 1.5f,
                y = y + (UnityEngine.Random.value - 0.5f) * 1.5f,
                z = z + (UnityEngine.Random.value - 0.5f) * 1.5f,
                vx = (UnityEngine.Random.value - 0.5f) * 2.0f,
                vy = UnityEngine.Random.value * 2.0f + speedMultiplier * 10.0f,
                vz = (UnityEngine.Random.value - 0.5f) * 2.0f,
                life = 0f,
                maxLife = 0.5f + UnityEngine.Random.value * 0.5f,
                scale = UnityEngine.Random.value * 0.6f + 0.2f
            };
            _particleIndex = (idx + 1) % MAX_PARTICLES;
        }

        private void HandleFreqTrigger(float strength, TriggerFireMode mode, TriggerActionType action)
        {
            if (action == TriggerActionType.Meteor)
            {
                if (!_vrEffectsHidden) AddMeteor(strength);
            }
            else if (action == TriggerActionType.Snare)
            {
                float angle = UnityEngine.Random.value * Mathf.PI * 2f;
                float dist = 10f + UnityEngine.Random.value * 35f;
                AddRipple(Mathf.Cos(angle) * dist, Mathf.Sin(angle) * dist, Mathf.Min(strength * 3.0f, 3.0f), true);
            }
            else
            {
                float angle = UnityEngine.Random.value * Mathf.PI * 2f;
                if (mode == TriggerFireMode.Kick)
                {
                    float dist = UnityEngine.Random.value * 20f;
                    AddRipple(Mathf.Cos(angle) * dist, Mathf.Sin(angle) * dist, Mathf.Min(strength * 2.0f, 3.0f), false);
                }
                else
                {
                    float dist = 10f + UnityEngine.Random.value * 25f;
                    AddRipple(Mathf.Cos(angle) * dist, Mathf.Sin(angle) * dist, Mathf.Min(strength * 3.0f, 3.0f), false);
                }
            }
        }

        private void RebuildTerrainGridIfNeeded()
        {
            TerrainGridSettings grid = SonicTopographyConfig.DeriveTerrainGridSettings(groundEqSettings.terrainDensity);
            if (_builtGridSize == grid.gridSize && Mathf.Approximately(_builtBoxWidth, grid.boxWidth) && _terrainChunkRenderers != null)
            {
                return;
            }

            _builtGridSize = grid.gridSize;
            _builtBoxWidth = grid.boxWidth;

            if (_terrainChunkRenderers != null)
            {
                for (int i = 0; i < _terrainChunkRenderers.Length; i++)
                {
                    if (_terrainChunkRenderers[i] != null) Destroy(_terrainChunkRenderers[i].gameObject);
                }
            }
            if (_terrainChunkMeshes != null)
            {
                for (int i = 0; i < _terrainChunkMeshes.Length; i++)
                {
                    if (_terrainChunkMeshes[i] != null) Destroy(_terrainChunkMeshes[i]);
                }
            }

            int maxTotal = grid.instanceCount;
            Vector2[] allPillarXZ = new Vector2[maxTotal];
            float offset = (grid.gridSize * grid.spacing) * 0.5f;
            const float maxPillarDistSq = 77.5f * 77.5f;
            int total = 0;

            for (int x = 0; x < grid.gridSize; x++)
            {
                float px = x * grid.spacing - offset;
                float pxSq = px * px;
                for (int z = 0; z < grid.gridSize; z++)
                {
                    float pz = z * grid.spacing - offset;
                    if (pxSq + pz * pz <= maxPillarDistSq)
                    {
                        allPillarXZ[total++] = new Vector2(px, pz);
                    }
                }
            }

            int chunkCount = Mathf.CeilToInt((float)total / PILLARS_PER_CHUNK);
            _terrainChunkMeshes = new Mesh[chunkCount];
            _terrainChunkRenderers = new MeshRenderer[chunkCount];

            for (int c = 0; c < chunkCount; c++)
            {
                int startIdx = c * PILLARS_PER_CHUNK;
                int count = Mathf.Min(PILLARS_PER_CHUNK, total - startIdx);
                Mesh chunkMesh = CreateTerrainPillarChunkMesh(allPillarXZ, startIdx, count, $"SonicTerrainChunk_{c}");
                _terrainChunkMeshes[c] = chunkMesh;
                _terrainChunkRenderers[c] = CreateChildMeshRenderer($"SonicTerrainChunk_{c}", chunkMesh, _terrainMaterial);
            }
        }

        private void RebuildFloatingBlocksIfNeeded()
        {
            int count = Mathf.Clamp(groundEqSettings.floatingBlockCount, 1, BATCH_MAX);
            if (_floatingBlocks != null && _floatingBlocks.Length == count && _floatingBlocksRenderer != null)
            {
                return;
            }

            _floatingBlocks = new FloatingBlockData[count];

            for (int index = 0; index < count; index++)
            {
                float ring = (float)index / count;
                float angle = ring * Mathf.PI * 2f * 5.0f + Mathf.Sin(index * 12.9898f) * 0.7f;
                float radius = 14f + ((index * 37) % 62);
                float height = 6f + ((index * 17) % 19);

                _floatingBlocks[index] = new FloatingBlockData
                {
                    x = Mathf.Cos(angle) * radius,
                    z = Mathf.Sin(angle) * radius,
                    y = height,
                    baseScale = 0.75f + ((index * 11) % 9) * 0.05f,
                    phase = index * 0.73f,
                    rotationSpeed = 0.18f + ((index * 7) % 10) * 0.035f
                };
            }

            if (_floatingBlocksRenderer != null) Destroy(_floatingBlocksRenderer.gameObject);
            if (_floatingBlocksCombinedMesh != null) Destroy(_floatingBlocksCombinedMesh);

            _floatingBlocksCombinedMesh = CreateFloatingBlocksCombinedMesh(_floatingBlocks);
            _floatingBlocksRenderer = CreateChildMeshRenderer("SonicFloatingBlocksRenderer", _floatingBlocksCombinedMesh, _floatingBlockMaterial);
        }

        private void Update()
        {
            float delta = Time.deltaTime;
            float elapsed = Time.time;

            RebuildTerrainGridIfNeeded();
            RebuildFloatingBlocksIfNeeded();

            // Rotate visual platter
            _platterRotationRad += rotationSpeed * delta;
            Vector3 effectivePos = _arBlend > 0.0001f ? _arPlatterPos : platterWorldPosition;
            Quaternion effectiveRot = _arBlend > 0.0001f
                ? _arPlatterRot * Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f)
                : Quaternion.Euler(0f, _platterRotationRad * Mathf.Rad2Deg, 0f);
            float effectiveScale = _arBlend > 0.0001f ? _arPlatterScale : 1f;
            Matrix4x4 platterMatrix = Matrix4x4.TRS(effectivePos, effectiveRot, Vector3.one * effectiveScale);

            EnsurePlatterRoot();
            _platterRoot.transform.position = effectivePos;
            _platterRoot.transform.rotation = effectiveRot;
            _platterRoot.transform.localScale = Vector3.one * effectiveScale;
            if (_terrainColliderGo != null)
            {
                bool shouldEnableCollider = _arBlend <= 0.05f;
                if (_terrainColliderGo.activeSelf != shouldEnableCollider)
                {
                    _terrainColliderGo.SetActive(shouldEnableCollider);
                }
            }

            // Handle VR / Desktop interaction (ripples & theme cycling)
            HandleUserInteractions(platterMatrix);

            // Fetch audio metrics
            SonicAudioData data = _audioEngine != null ? _audioEngine.GetAudioData() : default;

            // Ground EQ & motion speed smoothing (MapScene.tsx lines 375-415)
            float motionSpeed = Mathf.Clamp(groundEqSettings.motionSpeed, 0f, 100f);
            float amplitudeSetting = Mathf.Clamp(groundEqSettings.amplitude, 0f, 100f);
            float responseRate = Mathf.Lerp(2.2f, 60f, motionSpeed / 100f);
            float responseBlend = SonicTopographyConfig.ClampAnimationBlend(1f - Mathf.Exp(-responseRate * delta));

            float[] eqBands = groundEqSettings.bands;
            bool[] enabledBands = groundEqSettings.enabledBands;

            SonicTopographyConfig.DeriveKickFollowLowBands(
                data.kickEnvelope,
                data.subBass,
                data.bass,
                eqBands,
                enabledBands,
                out float targetEqSubBass,
                out float targetEqBass
            );

            float targetEqLowMid = IsBandEnabled(enabledBands, 2) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.lowMid, eqBands, 2) : 0f;
            float targetEqMid = IsBandEnabled(enabledBands, 3) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.mid, eqBands, 3) : 0f;
            float targetEqHighMid = IsBandEnabled(enabledBands, 4) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.highMid, eqBands, 4) : 0f;
            float targetEqPresence = IsBandEnabled(enabledBands, 5) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.presence, eqBands, 5) : 0f;
            float targetEqBrilliance = IsBandEnabled(enabledBands, 6) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.brilliance, eqBands, 6) : 0f;
            float targetEqAir = IsBandEnabled(enabledBands, 7) ? SonicTopographyConfig.ApplyGroundEqBandValue(data.air, eqBands, 7) : 0f;

            _smSubBass = Mathf.Lerp(_smSubBass, targetEqSubBass, responseBlend);
            _smBass = Mathf.Lerp(_smBass, targetEqBass, responseBlend);
            _smLowMid = Mathf.Lerp(_smLowMid, targetEqLowMid, responseBlend);
            _smMid = Mathf.Lerp(_smMid, targetEqMid, responseBlend);
            _smHighMid = Mathf.Lerp(_smHighMid, targetEqHighMid, responseBlend);
            _smPresence = Mathf.Lerp(_smPresence, targetEqPresence, responseBlend);
            _smBrilliance = Mathf.Lerp(_smBrilliance, targetEqBrilliance, responseBlend);
            _smAir = Mathf.Lerp(_smAir, targetEqAir, responseBlend);

            float eqAverage = 50f;
            if (eqBands != null && eqBands.Length > 0)
            {
                float sum = 0f;
                for (int i = 0; i < eqBands.Length; i++) sum += eqBands[i];
                eqAverage = sum / eqBands.Length;
            }
            float eqEnergy = Mathf.Clamp01(data.energy * (0.25f + (eqAverage / 50f) * 0.75f));

            // Smoothly transition theme colors:
            // - AutoAlbumArt: matches the active track's album cover colors; if no album cover exists, falls back to user's manual colors or default preset theme
            // - PresetThemes: uses selected preset theme
            // - ManualColors: uses user's custom colors
            if (!_manualColorsInitialized)
            {
                manualThemeColors = SonicTopographyConfig.GetTheme(activeTheme);
                _manualColorsInitialized = true;
            }

            SonicThemeColors targetTheme;
            if (colorMode == SonicColorMode.AutoAlbumArt)
            {
                if (_hasActiveAlbumPalette)
                {
                    targetTheme = _activeAlbumPalette;
                }
                else if (noCoverFallbackMode == SonicColorFallbackMode.UserManualColors && _userHasSetManualColors)
                {
                    targetTheme = manualThemeColors;
                }
                else
                {
                    targetTheme = SonicTopographyConfig.GetTheme(activeTheme);
                }
            }
            else if (colorMode == SonicColorMode.ManualColors)
            {
                targetTheme = manualThemeColors;
            }
            else
            {
                targetTheme = SonicTopographyConfig.GetTheme(activeTheme);
            }

            float colorLerpSpeed = SonicTopographyConfig.ClampAnimationBlend(3.0f * delta);
            if (!_colorsInitialized)
            {
                _currentColors = targetTheme;
                _colorsInitialized = true;
            }
            else
            {
                _currentColors = SonicThemeColors.Lerp(_currentColors, targetTheme, colorLerpSpeed);
            }

            ApplyCameraAndEnvironmentBackdrop(_currentColors);

            float amplitudeMultiplier;
            if (amplitudeSetting <= 50f)
            {
                amplitudeMultiplier = amplitudeSetting / 50.0f;
            }
            else
            {
                float t = (amplitudeSetting - 50f) / 50.0f;
                amplitudeMultiplier = 1.0f + (t * t) * 14.0f;
            }

            float lowMidSum = _smSubBass + _smBass + _smLowMid + _smMid;
            float highSum = _smPresence + _smBrilliance + _smAir;
            float totalBandSum = Mathf.Max(0.001f, lowMidSum + highSum);
            float uWarmth = Mathf.Clamp01(lowMidSum / totalBandSum);
            float uBrightness = Mathf.Clamp01(highSum / totalBandSum);

            // Update Terrain Material uniforms
            _terrainMaterial.SetFloat(PropUTime, elapsed);
            _terrainMaterial.SetFloat(PropUSubBass, _smSubBass);
            _terrainMaterial.SetFloat(PropUBass, _smBass);
            _terrainMaterial.SetFloat(PropULowMid, _smLowMid);
            _terrainMaterial.SetFloat(PropUMid, _smMid);
            _terrainMaterial.SetFloat(PropUHighMid, _smHighMid);
            _terrainMaterial.SetFloat(PropUPresence, _smPresence);
            _terrainMaterial.SetFloat(PropUBrilliance, _smBrilliance);
            _terrainMaterial.SetFloat(PropUAir, _smAir);

            _terrainMaterial.SetFloat(PropUWarmth, uWarmth);
            _terrainMaterial.SetFloat(PropUBrightness, uBrightness);
            _terrainMaterial.SetFloat(PropUSharpness, data.sharpness);
            _terrainMaterial.SetFloat(PropUSmoothness, data.smoothness);
            _terrainMaterial.SetFloat(PropUDensity, data.density);
            _terrainMaterial.SetFloat(PropUSpectralCentroid, data.spectralCentroid);
            _terrainMaterial.SetFloat(PropUEnergy, eqEnergy);
            _terrainMaterial.SetFloat(PropUAmplitude, amplitudeMultiplier);
            _terrainMaterial.SetFloat(PropUArMode, _arBlend);
            _terrainMaterial.SetFloat(PropUArOcclusionEnabled, _arOcclusionEnabled ? 1f : 0f);
            _terrainMaterial.SetFloat(PropEnvironmentDepthBias, _arIsSnapped ? 0.032f : 0.012f);
            LastAudioEnergy = eqEnergy;
            LastBassEnergy = _smBass;
            LastWarmth = uWarmth;

            float maxRippleAge = _arBlend > 0.05f ? 11.0f : 7.5f;
            for (int r = 0; r < MAX_RIPPLES; r++)
            {
                if (_rippleState[r].x > 0.5f && (elapsed - _ripplePosTimeStrength[r].z) > maxRippleAge)
                {
                    _rippleState[r] = Vector4.zero;
                }
            }

            SetThemeUniforms(_terrainMaterial, _currentColors);
            _terrainMaterial.SetMatrix(PropPlatterMatrix, platterMatrix);
            _terrainMaterial.SetFloat(PropBoxWidth, _builtBoxWidth);
            _terrainMaterial.SetVectorArray(PropRipplePosTimeStrength, _ripplePosTimeStrength);
            _terrainMaterial.SetVectorArray(PropRippleState, _rippleState);

            // Toggle Terrain Pillar MeshRenderers (9 16-bit chunks = 24,025 pillars)
            if (_terrainChunkRenderers != null)
            {
                for (int c = 0; c < _terrainChunkRenderers.Length; c++)
                {
                    if (_terrainChunkRenderers[c] != null && _terrainChunkRenderers[c].enabled != enableTerrainPillars)
                    {
                        _terrainChunkRenderers[c].enabled = enableTerrainPillars;
                    }
                }
            }

            // Update & Toggle Floating Crystal Blocks MeshRenderer
            bool showFloatingBlocks = !_vrEffectsHidden && groundEqSettings.floatingBlocksEnabled && _floatingBlocks != null && _floatingBlocks.Length > 0;
            if (_floatingBlocksRenderer != null && _floatingBlocksRenderer.enabled != showFloatingBlocks)
            {
                _floatingBlocksRenderer.enabled = showFloatingBlocks;
            }

            if (showFloatingBlocks)
            {
                float intensity = Mathf.Clamp01(groundEqSettings.floatingBlockIntensity / 100f);
                float rawPulse = Mathf.Clamp01(data.kickEnvelope);
                float speedRate = Mathf.Lerp(3.0f, 36.0f, Mathf.Clamp01(groundEqSettings.floatingBlockSpeed / 100f));
                float pulseBlend = SonicTopographyConfig.ClampAnimationBlend(1f - Mathf.Exp(-speedRate * delta));
                _floatingBlockPulse = Mathf.Lerp(_floatingBlockPulse, rawPulse, pulseBlend);

                float minSizeNorm = Mathf.Clamp01(groundEqSettings.floatingBlockMinSize / 100f);
                float maxSizeNorm = Mathf.Max(minSizeNorm, Mathf.Clamp01(groundEqSettings.floatingBlockMaxSize / 100f));
                float minVisualScale = Mathf.Lerp(0.12f, 0.75f, minSizeNorm);
                float maxVisualScale = Mathf.Max(minVisualScale + 0.05f, Mathf.Lerp(0.45f, 3.2f, maxSizeNorm));
                float sizeMix = Mathf.Clamp01(_floatingBlockPulse * (0.5f + intensity * 1.7f));
                float pulseScale = Mathf.Lerp(minVisualScale, maxVisualScale, sizeMix);
                float liftOffset = _floatingBlockPulse * intensity * 1.4f;

                _floatingBlockMaterial.SetFloat(PropUTime, elapsed);
                _floatingBlockMaterial.SetFloat(PropUPulse, sizeMix);
                _floatingBlockMaterial.SetFloat(PropUPulseScale, pulseScale);
                _floatingBlockMaterial.SetFloat(PropULiftOffset, liftOffset);
                _floatingBlockMaterial.SetFloat(PropUWarmth, uWarmth);
                _floatingBlockMaterial.SetFloat(PropUBrightness, uBrightness);
                _floatingBlockMaterial.SetFloat(PropUSharpness, data.sharpness);
                _floatingBlockMaterial.SetFloat(PropUPresence, _smPresence);
                _floatingBlockMaterial.SetFloat(PropUBrilliance, _smBrilliance);
                _floatingBlockMaterial.SetFloat(PropUAir, _smAir);
                SetThemeUniforms(_floatingBlockMaterial, _currentColors);
                _floatingBlockMaterial.SetMatrix(PropPlatterMatrix, platterMatrix);
            }

            // Update & Toggle Meteors MeshRenderer
            Color meteorCol = Color.Lerp(_currentColors.uWarmCore, Color.white, 0.7f);
            meteorCol.a = 1f;
            _meteorMaterial.SetColor(PropBaseColor, meteorCol);
            _meteorMaterial.SetMatrix(PropPlatterMatrix, platterMatrix);

            bool anyMeteorActive = false;
            for (int i = 0; i < MAX_METEORS; i++)
            {
                if (!_meteors[i].active)
                {
                    _meteorPosActive[i] = new Vector4(0f, -1000f, 0f, 0f);
                    _meteorScale[i] = Vector4.zero;
                }
                else
                {
                    anyMeteorActive = true;
                    _meteors[i].y -= _meteors[i].speed * 60f * delta;
                    if (_meteors[i].y <= 0f)
                    {
                        _meteors[i].active = false;
                        AddRipple(_meteors[i].x, _meteors[i].z, Mathf.Min(_meteors[i].strength * 1.0f, 1.2f), true);
                        for (int pIndex = 0; pIndex < 10; pIndex++)
                        {
                            SpawnParticle(_meteors[i].x, 0.5f, _meteors[i].z, _meteors[i].speed * 1.5f);
                        }
                        _meteorPosActive[i] = new Vector4(0f, -1000f, 0f, 0f);
                        _meteorScale[i] = Vector4.zero;
                    }
                    else
                    {
                        _meteorPosActive[i] = new Vector4(_meteors[i].x, Mathf.Max(0f, _meteors[i].y), _meteors[i].z, 1f);
                        _meteorScale[i] = new Vector4(0.4f * 1.5f, 1.2f * 1.5f, 0.4f * 1.5f, 1f);
                        if (_meteors[i].y > 0f && UnityEngine.Random.value > 0.3f)
                        {
                            SpawnParticle(_meteors[i].x, _meteors[i].y, _meteors[i].z, _meteors[i].speed * 0.2f);
                        }
                    }
                }
            }

            if (_meteorsRenderer != null)
            {
                bool showMeteors = !_vrEffectsHidden && anyMeteorActive;
                _meteorsRenderer.enabled = showMeteors;
                if (showMeteors)
                {
                    _meteorMaterial.SetVectorArray(PropUItemPosActive, _meteorPosActive);
                    _meteorMaterial.SetVectorArray(PropUItemScale, _meteorScale);
                }
            }

            // Update & Toggle Particles MeshRenderer
            Color particleCol = meteorCol;
            particleCol.a = 0.6f;
            _particleMaterial.SetColor(PropBaseColor, particleCol);
            _particleMaterial.SetMatrix(PropPlatterMatrix, platterMatrix);

            bool anyParticleActive = false;
            for (int i = 0; i < MAX_PARTICLES; i++)
            {
                if (!_particles[i].active)
                {
                    _particlePosActive[i] = new Vector4(0f, -1000f, 0f, 0f);
                    _particleScale[i] = Vector4.zero;
                }
                else
                {
                    _particles[i].life += delta;
                    if (_particles[i].life >= _particles[i].maxLife)
                    {
                        _particles[i].active = false;
                        _particlePosActive[i] = new Vector4(0f, -1000f, 0f, 0f);
                        _particleScale[i] = Vector4.zero;
                    }
                    else
                    {
                        anyParticleActive = true;
                        _particles[i].x += _particles[i].vx * delta * 10f;
                        _particles[i].y += _particles[i].vy * delta * 10f;
                        _particles[i].z += _particles[i].vz * delta * 10f;
                        float s = 0.8f * _particles[i].scale * (1f - (_particles[i].life / _particles[i].maxLife));
                        _particlePosActive[i] = new Vector4(_particles[i].x, _particles[i].y, _particles[i].z, 1f);
                        _particleScale[i] = new Vector4(s, s, s, 1f);
                    }
                }
            }

            bool showParticles = !_vrEffectsHidden && enableAmbientParticles && anyParticleActive;
            if (_particlesRenderer != null)
            {
                _particlesRenderer.enabled = showParticles;
                if (showParticles)
                {
                    _particleMaterial.SetVectorArray(PropUItemPosActive, _particlePosActive);
                    _particleMaterial.SetVectorArray(PropUItemScale, _particleScale);
                }
            }
        }

        private static bool IsBandEnabled(bool[] enabledBands, int index)
        {
            return enabledBands == null || index >= enabledBands.Length || enabledBands[index];
        }

        private static Vector4 ComputeBackdropLinear(Color fogSrgb)
        {
            // Crush very dark values (< 0.02) to true OLED black (0,0,0), otherwise convert sRGB -> Linear matching the shader
            float r = fogSrgb.r < 0.02f ? 0f : Mathf.Pow(Mathf.Max(fogSrgb.r, 0f), 2.2f);
            float g = fogSrgb.g < 0.02f ? 0f : Mathf.Pow(Mathf.Max(fogSrgb.g, 0f), 2.2f);
            float b = fogSrgb.b < 0.02f ? 0f : Mathf.Pow(Mathf.Max(fogSrgb.b, 0f), 2.2f);
            return new Vector4(r, g, b, 1f);
        }

        private static void SetThemeUniforms(Material mat, SonicThemeColors c)
        {
            Vector4 backdropLinear = ComputeBackdropLinear(c.uFogColor);
            mat.SetVector(PropUBaseColor1, new Vector4(c.uBaseColor1.r, c.uBaseColor1.g, c.uBaseColor1.b, 1f));
            mat.SetVector(PropUBaseColor2, new Vector4(c.uBaseColor2.r, c.uBaseColor2.g, c.uBaseColor2.b, 1f));
            mat.SetVector(PropUFogColor, new Vector4(c.uFogColor.r, c.uFogColor.g, c.uFogColor.b, 1f));
            mat.SetVector(PropUCoolCore, new Vector4(c.uCoolCore.r, c.uCoolCore.g, c.uCoolCore.b, 1f));
            mat.SetVector(PropUCoolEdge, new Vector4(c.uCoolEdge.r, c.uCoolEdge.g, c.uCoolEdge.b, 1f));
            mat.SetVector(PropUWarmCore, new Vector4(c.uWarmCore.r, c.uWarmCore.g, c.uWarmCore.b, 1f));
            mat.SetVector(PropUWarmEdge, new Vector4(c.uWarmEdge.r, c.uWarmEdge.g, c.uWarmEdge.b, 1f));
            mat.SetVector(PropURippleColor, new Vector4(c.uRippleColor.r, c.uRippleColor.g, c.uRippleColor.b, 1f));
            mat.SetFloat(PropUGlowIntensity, c.uGlowIntensity);
            mat.SetVector(PropUBackdropLinear, backdropLinear);
        }

        // Smoothed controller ambient light state for silky-smooth wave & music response
        private Color _smCtrlSky = new Color(0.16f, 0.16f, 0.16f, 1f);
        private Color _smCtrlEquator = new Color(0.08f, 0.08f, 0.08f, 1f);
        private Color _smCtrlGround = new Color(0.03f, 0.03f, 0.03f, 1f);
        private bool _ctrlLightInitialized = false;

        private void ApplyCameraAndEnvironmentBackdrop(SonicThemeColors c)
        {
            Vector4 backdropLinearVec = ComputeBackdropLinear(c.uFogColor);
            Color bgLinear = new Color(backdropLinearVec.x, backdropLinearVec.y, backdropLinearVec.z, 1f);
            Color bgSrgb = bgLinear.gamma;
            if (SonicMixedRealityManager.IsArVisualActive || _arBlend > 0.001f)
            {
                bgSrgb = new Color(0f, 0f, 0f, 0f);
            }
            else
            {
                bgSrgb.a = 1f;
            }

            if (RenderSettings.skybox != null)
            {
                RenderSettings.skybox = null;
            }

            int camCount = Camera.allCamerasCount;
            if (camCount > _cameraBuffer.Length)
            {
                _cameraBuffer = new Camera[camCount * 2];
            }
            int fetched = Camera.GetAllCameras(_cameraBuffer);
            for (int i = 0; i < fetched; i++)
            {
                Camera cam = _cameraBuffer[i];
                if (cam == null) continue;
                if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
                if (cam.backgroundColor != bgSrgb) cam.backgroundColor = bgSrgb;
            }

            // 1. Measure active Terrain Ripples (from controller ray clicks, hold-releases, kicks, and snares)
            float now = Time.time;
            float coloredRippleWave = 0f;
            float whiteRippleWave = 0f;

            for (int i = 0; i < MAX_RIPPLES; i++)
            {
                if (_rippleState[i].x <= 0.5f) continue;

                float age = now - _ripplePosTimeStrength[i].z;
                if (age >= 0f && age < 3.2f)
                {
                    float strength = Mathf.Clamp(_ripplePosTimeStrength[i].w, 0.5f, 3.0f) / 1.35f;
                    float swell = Mathf.Sin(Mathf.Clamp01(age / 0.30f) * (Mathf.PI * 0.5f));
                    float decay = Mathf.Exp(-age * 0.90f);
                    float waveEnergy = swell * decay * strength;

                    if (_rippleState[i].y > 0.5f)
                        whiteRippleWave = Mathf.Max(whiteRippleWave, waveEnergy);
                    else
                        coloredRippleWave = Mathf.Max(coloredRippleWave, waveEnergy);
                }
            }

            // 2. Measure active Meteors streaking down
            float meteorGlow = 0f;
            for (int i = 0; i < MAX_METEORS; i++)
            {
                if (_meteors[i].active)
                {
                    float hNorm = 1f - Mathf.Clamp01(_meteors[i].y / 40f);
                    meteorGlow = Mathf.Max(meteorGlow, (0.3f + hNorm * 0.7f) * Mathf.Clamp01(_meteors[i].strength));
                }
            }

            // 3. Sensitivity curves + dynamic peak surge when effects get bright
            float rawLow = Mathf.Clamp01((_smSubBass + _smBass + _smLowMid * 0.5f) * 0.60f + _floatingBlockPulse * 0.55f);
            float rawHigh = Mathf.Clamp01((_smMid + _smHighMid + _smPresence + _smBrilliance) * 0.45f);
            float totalRipple = Mathf.Clamp01(coloredRippleWave + whiteRippleWave * 1.15f);

            float sensLow = Mathf.Pow(rawLow, 0.55f);
            float sensHigh = Mathf.Pow(rawHigh, 0.55f);
            float sensRipple = Mathf.Pow(totalRipple, 0.58f);

            // Dynamic peak boost: stays subtle during normal play, surges brighter on heavy drops/waves/meteors
            float peakIntensity = Mathf.Clamp01(Mathf.Max(rawLow, totalRipple, meteorGlow));
            float peakBoost = 1.0f + peakIntensity * 0.58f;

            Color NormalizeHue(Color col, float maxLuma)
            {
                float peak = Mathf.Max(col.r, Mathf.Max(col.g, col.b), 0.01f);
                return (col / peak) * maxLuma;
            }

            Color coolTint = NormalizeHue(Color.Lerp(c.uCoolCore, c.uCoolEdge, 0.70f), 0.45f) * peakBoost;
            Color warmTint = NormalizeHue(Color.Lerp(c.uWarmCore, c.uWarmEdge, 0.70f), 0.42f) * peakBoost;
            Color rippleTint = NormalizeHue(c.uRippleColor, 0.50f) * peakBoost;
            Color snareWhiteTint = Color.Lerp(rippleTint, new Color(0.55f, 0.55f, 0.55f, 1f) * peakBoost, 0.65f);

            // Combine active wave tint (mix of colored wave, white snare wave, and meteor glow)
            Color activeWaveColor = Color.Lerp(rippleTint, snareWhiteTint, Mathf.Clamp01(whiteRippleWave / Mathf.Max(0.01f, coloredRippleWave + whiteRippleWave)));
            activeWaveColor = Color.Lerp(activeWaveColor, warmTint, Mathf.Clamp01(meteorGlow * 0.6f));

            // Base dark-room stealth colors (when totally quiet & no ripples)
            Color idleSky = new Color(0.16f, 0.16f, 0.16f, 1f);
            Color idleEquator = Color.Lerp(new Color(0.07f, 0.07f, 0.07f, 1f), coolTint * 0.22f, 0.35f);
            Color idleGround = Color.Lerp(new Color(0.025f, 0.025f, 0.025f, 1f), coolTint * 0.25f, 0.45f);

            // Target Top (Sky): dim at rest, lifts noticeably during bright waves and high energy peaks
            float skyDrive = Mathf.Clamp01(sensHigh * 0.60f + sensLow * 0.35f + sensRipple * 0.75f + meteorGlow * 0.65f);
            Color musicSky = Color.Lerp(new Color(0.32f, 0.32f, 0.32f, 1f) * (0.85f + peakIntensity * 0.35f), Color.Lerp(warmTint, activeWaveColor, sensRipple), 0.62f);
            Color targetSky = Color.Lerp(idleSky, musicSky, skyDrive);

            // Target Sides (Equator): strong wrap-around glow on waves and beats
            float eqDrive = Mathf.Clamp01(sensLow * 0.72f + sensHigh * 0.50f + sensRipple * 0.95f + meteorGlow * 0.60f);
            Color musicEquator = Color.Lerp(Color.Lerp(coolTint, warmTint, sensHigh), activeWaveColor, Mathf.Clamp01(sensRipple * 0.85f));
            Color targetEquator = Color.Lerp(idleEquator, musicEquator * 0.92f, eqDrive);

            // Target Underside (Ground facing the terrain): strong bounce light from bright waves and bass floor
            float groundDrive = Mathf.Clamp01(sensLow * 0.85f + sensRipple * 1.0f + meteorGlow * 0.55f);
            Color musicGround = Color.Lerp(coolTint, activeWaveColor, Mathf.Clamp01(sensRipple * 0.92f));
            Color targetGround = Color.Lerp(idleGround, musicGround * 1.08f, groundDrive);

            // Silky-smooth temporal damping (glides smoothly on attack & decay)
            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
            if (!_ctrlLightInitialized)
            {
                _smCtrlSky = targetSky;
                _smCtrlEquator = targetEquator;
                _smCtrlGround = targetGround;
                _ctrlLightInitialized = true;
            }
            else
            {
                float skyLerp = 1f - Mathf.Exp(-8.5f * dt);
                float eqLerp = 1f - Mathf.Exp(-10.0f * dt);
                float groundLerp = 1f - Mathf.Exp(-11.5f * dt);

                _smCtrlSky = Color.Lerp(_smCtrlSky, targetSky, skyLerp);
                _smCtrlEquator = Color.Lerp(_smCtrlEquator, targetEquator, eqLerp);
                _smCtrlGround = Color.Lerp(_smCtrlGround, targetGround, groundLerp);
            }

            _smCtrlSky.a = 1f;
            _smCtrlEquator.a = 1f;
            _smCtrlGround.a = 1f;

            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = _smCtrlSky;
            RenderSettings.ambientEquatorColor = _smCtrlEquator;
            RenderSettings.ambientGroundColor = _smCtrlGround;
        }

        private void HandleUserInteractions(Matrix4x4 platterMatrix)
        {
            bool pointerOverUI = suppressWorldPointerInput ||
                                 (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject());

            if (pointerOverUI)
            {
                _wasRightTriggerPressed = false;
                _wasLeftTriggerPressed = false;
                _wasThemeButtonPressed = false;
                return;
            }

            // Theme cycle shortcut (Keyboard 'T' for editor / desktop testing)
            if (enableThemeCycleShortcut)
            {
                bool themeKeyTriggered = Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame;

                if (themeKeyTriggered && !_wasThemeButtonPressed)
                {
                    if (colorMode == SonicColorMode.ManualColors)
                    {
                        colorMode = SonicColorMode.PresetThemes;
                    }
                    CycleToNextPresetTheme();
                }
                _wasThemeButtonPressed = themeKeyTriggered;
            }

            if (!enableInteractiveRipples || SonicMixedRealityManager.IsArControlActive) return;

            Matrix4x4 worldToPlatter = platterMatrix.inverse;
            Plane terrainPlane = new Plane(Vector3.up, new Vector3(0f, platterWorldPosition.y + 15.0f, 0f));

            // Desktop Mouse click ripple
            if (Mouse.current != null)
            {
                if (Mouse.current.leftButton.wasPressedThisFrame && Camera.main != null)
                {
                    _mousePressStartTime = Time.time;
                    Vector2 mousePos = Mouse.current.position.ReadValue();
                    Ray ray = Camera.main.ScreenPointToRay(mousePos);
                    if (terrainPlane.Raycast(ray, out float enter) && enter < 350f)
                    {
                        Vector3 hitWorld = ray.GetPoint(enter);
                        Vector3 localPt = worldToPlatter.MultiplyPoint3x4(hitWorld);
                        if (new Vector2(localPt.x, localPt.z).magnitude <= 78f)
                        {
                            AddRipple(localPt.x, localPt.z, 1.25f, false);
                        }
                    }
                }
                else if (Mouse.current.leftButton.wasReleasedThisFrame && Camera.main != null)
                {
                    float duration = Time.time - _mousePressStartTime;
                    if (duration > 0.35f)
                    {
                        float strength = Mathf.Min(0.5f + duration * 2.5f, 3.0f);
                        Vector2 mousePos = Mouse.current.position.ReadValue();
                        Ray ray = Camera.main.ScreenPointToRay(mousePos);
                        if (terrainPlane.Raycast(ray, out float enter) && enter < 350f)
                        {
                            Vector3 hitWorld = ray.GetPoint(enter);
                            Vector3 localPt = worldToPlatter.MultiplyPoint3x4(hitWorld);
                            if (new Vector2(localPt.x, localPt.z).magnitude <= 78f)
                            {
                                AddRipple(localPt.x, localPt.z, strength, false);
                            }
                        }
                    }
                }
            }

            // VR Controller Trigger raycast ripples
            CheckXRControllerRipple(UnityEngine.XR.XRNode.RightHand, ref _wasRightTriggerPressed, ref _rightPressStartTime, terrainPlane, worldToPlatter);
            CheckXRControllerRipple(UnityEngine.XR.XRNode.LeftHand, ref _wasLeftTriggerPressed, ref _leftPressStartTime, terrainPlane, worldToPlatter);
        }

        private void CheckXRControllerRipple(
            UnityEngine.XR.XRNode node,
            ref bool wasPressed,
            ref float pressStartTime,
            Plane terrainPlane,
            Matrix4x4 worldToPlatter)
        {
            bool isRight = node == UnityEngine.XR.XRNode.RightHand;
            bool isPressed = false;

            ref UnityEngine.XR.InputDevice device = ref (isRight ? ref _rightXrDevice : ref _leftXrDevice);
            if (!device.isValid)
            {
                device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            }
            if (device.isValid)
            {
                if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool trigBtn) && trigBtn)
                {
                    isPressed = true;
                }
                else if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float trigVal) && trigVal > 0.55f)
                {
                    isPressed = true;
                }
            }

            var xrCtrl = isRight
                ? UnityEngine.InputSystem.XR.XRController.rightHand
                : UnityEngine.InputSystem.XR.XRController.leftHand;
            if (isRight)
            {
                if (xrCtrl != _cachedXrRight)
                {
                    _cachedXrRight = xrCtrl;
                    _rightTriggerAxis = xrCtrl != null ? xrCtrl.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("trigger") : null;
                    _rightTriggerBtn = xrCtrl != null ? xrCtrl.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("triggerPressed") : null;
                }
                if (xrCtrl != null)
                {
                    if (_rightTriggerAxis != null && _rightTriggerAxis.ReadValue() > 0.55f) isPressed = true;
                    if (_rightTriggerBtn != null && _rightTriggerBtn.isPressed) isPressed = true;
                }
            }
            else
            {
                if (xrCtrl != _cachedXrLeft)
                {
                    _cachedXrLeft = xrCtrl;
                    _leftTriggerAxis = xrCtrl != null ? xrCtrl.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("trigger") : null;
                    _leftTriggerBtn = xrCtrl != null ? xrCtrl.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("triggerPressed") : null;
                }
                if (xrCtrl != null)
                {
                    if (_leftTriggerAxis != null && _leftTriggerAxis.ReadValue() > 0.55f) isPressed = true;
                    if (_leftTriggerBtn != null && _leftTriggerBtn.isPressed) isPressed = true;
                }
            }

            if (isPressed && !wasPressed)
            {
                pressStartTime = Time.time;

                Ray ray = default;
                bool hasRay = false;

                // 1. Prefer the tracked Meta controller / Aim pose nozzle transform
                Transform pointerT = SonicMetaQuestController.GetPointerTransform(node);
                if (pointerT != null)
                {
                    ray = new Ray(pointerT.position, pointerT.forward);
                    hasRay = true;
                }
                else
                {
                    // 2. Fallback to tracked controller GameObject
                    string ctrlName = node == UnityEngine.XR.XRNode.RightHand ? "Right Controller" : "Left Controller";
                    GameObject ctrlGo = GameObject.Find(ctrlName);
                    if (ctrlGo != null)
                    {
                        ray = new Ray(ctrlGo.transform.position, ctrlGo.transform.forward);
                        hasRay = true;
                    }
                    else if (device.isValid &&
                             device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 devPos) &&
                             device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion devRot))
                    {
                        Transform originTransform = Camera.main != null && Camera.main.transform.parent != null
                            ? Camera.main.transform.parent
                            : null;
                        Vector3 worldPos = originTransform != null ? originTransform.TransformPoint(devPos) : devPos;
                        Quaternion worldRot = originTransform != null ? originTransform.rotation * devRot : devRot;
                        ray = new Ray(worldPos, worldRot * Quaternion.Euler(15f, 0f, 0f) * Vector3.forward);
                        hasRay = true;
                    }
                    else if (Camera.main != null)
                    {
                        ray = new Ray(Camera.main.transform.position, Camera.main.transform.forward);
                        hasRay = true;
                    }
                }

                if (hasRay && terrainPlane.Raycast(ray, out float enter) && enter > 0f && enter < 350f)
                {
                    Vector3 hitWorld = ray.GetPoint(enter);
                    Vector3 localPt = worldToPlatter.MultiplyPoint3x4(hitWorld);
                    if (new Vector2(localPt.x, localPt.z).magnitude <= 78f)
                    {
                        AddRipple(localPt.x, localPt.z, 1.25f, false);
                    }
                }
            }
            else if (!isPressed && wasPressed)
            {
                float duration = Time.time - pressStartTime;
                if (duration > 0.35f)
                {
                    float strength = Mathf.Min(0.5f + duration * 2.5f, 3.0f);
                    Ray ray = default;
                    bool hasRay = false;
                    Transform pointerT = SonicMetaQuestController.GetPointerTransform(node);
                    if (pointerT != null)
                    {
                        ray = new Ray(pointerT.position, pointerT.forward);
                        hasRay = true;
                    }
                    if (hasRay && terrainPlane.Raycast(ray, out float enter) && enter > 0f && enter < 350f)
                    {
                        Vector3 hitWorld = ray.GetPoint(enter);
                        Vector3 localPt = worldToPlatter.MultiplyPoint3x4(hitWorld);
                        if (new Vector2(localPt.x, localPt.z).magnitude <= 78f)
                        {
                            AddRipple(localPt.x, localPt.z, strength, false);
                        }
                    }
                }
            }

            wasPressed = isPressed;
        }

        private static readonly Vector3[] UnitBoxFaceVertices = new Vector3[]
        {
            // Top (+Y)
            new Vector3(-0.5f, 0.5f, -0.5f), new Vector3( 0.5f, 0.5f, -0.5f), new Vector3( 0.5f, 0.5f,  0.5f), new Vector3(-0.5f, 0.5f,  0.5f),
            // Front (-Z)
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
            // Back (+Z)
            new Vector3( 0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f,  0.5f, 0.5f), new Vector3( 0.5f,  0.5f, 0.5f),
            // Left (-X)
            new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
            // Right (+X)
            new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f,  0.5f), new Vector3(0.5f,  0.5f,  0.5f), new Vector3(0.5f,  0.5f, -0.5f),
            // Bottom (-Y)
            new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f)
        };

        private static readonly Vector3[] UnitBoxFaceNormals = new Vector3[]
        {
            Vector3.up, Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down
        };

        private static readonly Vector2[] UnitQuadUVs = new Vector2[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
        };

        private static Mesh CreateTerrainPillarChunkMesh(Vector2[] allPillarXZ, int startIdx, int count, string meshName)
        {
            const int facesPerPillar = 5;
            const int vertsPerPillar = facesPerPillar * 4; // 20
            const int trisPerPillar = facesPerPillar * 6;  // 30

            int totalVerts = count * vertsPerPillar;
            int totalTris = count * trisPerPillar;

            Vector3[] vertices = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            Vector2[] pillarXZs = new Vector2[totalVerts];
            int[] triangles = new int[totalTris];

            int v = 0;
            int t = 0;

            for (int i = 0; i < count; i++)
            {
                Vector2 xz = allPillarXZ[startIdx + i];
                for (int f = 0; f < facesPerPillar; f++)
                {
                    Vector3 n = UnitBoxFaceNormals[f];
                    int baseF = f * 4;

                    vertices[v + 0] = UnitBoxFaceVertices[baseF + 0];
                    vertices[v + 1] = UnitBoxFaceVertices[baseF + 1];
                    vertices[v + 2] = UnitBoxFaceVertices[baseF + 2];
                    vertices[v + 3] = UnitBoxFaceVertices[baseF + 3];

                    normals[v + 0] = n;
                    normals[v + 1] = n;
                    normals[v + 2] = n;
                    normals[v + 3] = n;

                    uvs[v + 0] = UnitQuadUVs[0];
                    uvs[v + 1] = UnitQuadUVs[1];
                    uvs[v + 2] = UnitQuadUVs[2];
                    uvs[v + 3] = UnitQuadUVs[3];

                    pillarXZs[v + 0] = xz;
                    pillarXZs[v + 1] = xz;
                    pillarXZs[v + 2] = xz;
                    pillarXZs[v + 3] = xz;

                    triangles[t + 0] = v + 0;
                    triangles[t + 1] = v + 2;
                    triangles[t + 2] = v + 1;
                    triangles[t + 3] = v + 0;
                    triangles[t + 4] = v + 3;
                    triangles[t + 5] = v + 2;

                    v += 4;
                    t += 6;
                }
            }

            Mesh mesh = new Mesh
            {
                name = meshName,
                indexFormat = IndexFormat.UInt16
            };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.uv2 = pillarXZs;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(300f, 300f, 300f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Mesh CreateFloatingBlocksCombinedMesh(FloatingBlockData[] blocks)
        {
            int count = blocks.Length;
            const int facesPerCube = 6;
            const int vertsPerCube = facesPerCube * 4; // 24
            const int trisPerCube = facesPerCube * 6;  // 36

            int totalVerts = count * vertsPerCube;
            int totalTris = count * trisPerCube;

            Vector3[] vertices = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            var blockPosScaleList = new System.Collections.Generic.List<Vector4>(totalVerts);
            Vector2[] blockPhaseSpd = new Vector2[totalVerts];
            int[] triangles = new int[totalTris];

            int v = 0;
            int t = 0;

            for (int i = 0; i < count; i++)
            {
                FloatingBlockData b = blocks[i];
                Vector4 posScale = new Vector4(b.x, b.y, b.z, b.baseScale);
                Vector2 phaseSpd = new Vector2(b.phase, b.rotationSpeed);

                for (int f = 0; f < facesPerCube; f++)
                {
                    Vector3 n = UnitBoxFaceNormals[f];
                    int baseF = f * 4;

                    for (int k = 0; k < 4; k++)
                    {
                        vertices[v + k] = UnitBoxFaceVertices[baseF + k];
                        normals[v + k] = n;
                        uvs[v + k] = UnitQuadUVs[k];
                        blockPosScaleList.Add(posScale);
                        blockPhaseSpd[v + k] = phaseSpd;
                    }

                    triangles[t + 0] = v + 0;
                    triangles[t + 1] = v + 2;
                    triangles[t + 2] = v + 1;
                    triangles[t + 3] = v + 0;
                    triangles[t + 4] = v + 3;
                    triangles[t + 5] = v + 2;

                    v += 4;
                    t += 6;
                }
            }

            Mesh mesh = new Mesh
            {
                name = "SonicFloatingBlocksCombined",
                indexFormat = IndexFormat.UInt16
            };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.SetUVs(1, blockPosScaleList);
            mesh.uv3 = blockPhaseSpd;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(300f, 300f, 300f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Mesh CreateIndexedCubesMesh(int count, string meshName)
        {
            const int facesPerCube = 6;
            const int vertsPerCube = facesPerCube * 4; // 24
            const int trisPerCube = facesPerCube * 6;  // 36

            int totalVerts = count * vertsPerCube;
            int totalTris = count * trisPerCube;

            Vector3[] vertices = new Vector3[totalVerts];
            Vector3[] normals = new Vector3[totalVerts];
            Vector2[] uvs = new Vector2[totalVerts];
            Vector2[] itemIndices = new Vector2[totalVerts];
            int[] triangles = new int[totalTris];

            int v = 0;
            int t = 0;

            for (int i = 0; i < count; i++)
            {
                Vector2 idxVec = new Vector2(i, 0f);
                for (int f = 0; f < facesPerCube; f++)
                {
                    Vector3 n = UnitBoxFaceNormals[f];
                    int baseF = f * 4;

                    for (int k = 0; k < 4; k++)
                    {
                        vertices[v + k] = UnitBoxFaceVertices[baseF + k];
                        normals[v + k] = n;
                        uvs[v + k] = UnitQuadUVs[k];
                        itemIndices[v + k] = idxVec;
                    }

                    triangles[t + 0] = v + 0;
                    triangles[t + 1] = v + 2;
                    triangles[t + 2] = v + 1;
                    triangles[t + 3] = v + 0;
                    triangles[t + 4] = v + 3;
                    triangles[t + 5] = v + 2;

                    v += 4;
                    t += 6;
                }
            }

            Mesh mesh = new Mesh
            {
                name = meshName,
                indexFormat = IndexFormat.UInt16
            };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.uv2 = itemIndices;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(300f, 300f, 300f));
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
