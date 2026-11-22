using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SonicTopography
{
    /// <summary>
    /// Sonic Star River (星河) Visualizer:
    /// High-performance 3D ambient starfield and undulating celestial star-wave point clouds,
    /// ported from Mineradio's Star River preset (Preset 5 / WALLPAPER PULSE and floatGroup).
    /// Runs 100% on the GPU in single-pass URP shaders with zero per-frame CPU mesh rebuilding.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class SonicStarRiverVisualizer : MonoBehaviour
    {
        [Header("Star River Configuration")]
        [Tooltip("Overall flow and drift animation speed multiplier.")]
        public float speed = 1.0f;

        [Tooltip("Master brightness/alpha scale for the star river effect.")]
        [Range(0.0f, 1.5f)]
        public float masterAlpha = 1.0f;

        [Tooltip("Global particle size scale.")]
        [Range(0.2f, 2.5f)]
        public float particleScale = 1.0f;

        // References to audio, player, and visualizer controllers
        private SonicTopographyAudioEngine _audioEngine;
        private SonicMusicPlayerController _player;
        private SonicTopographyVisualizer _visualizer;

        // Meshes and materials
        private Mesh _waveMesh;
        private Mesh _ambientMesh;
        private Material _waveMaterial;
        private Material _ambientMaterial;

        // Child GameObjects
        private GameObject _waveObject;
        private GameObject _ambientObject;

        // Realtime animation state
        private float _starTime = 0f;
        private float _waveActivity = 0f;
        private float _smoothedBass = 0f;
        private float _smoothedMid = 0f;
        private float _smoothedTreble = 0f;
        private float _beatEnvelope = 0f;

        // Realtime palette colors harmonized with terrain
        private Color _auroraCool = new Color(0.35f, 0.65f, 0.95f, 1.0f);
        private Color _auroraWarm = new Color(0.55f, 0.40f, 0.90f, 1.0f);
        private Color _tintColor = new Color(0.85f, 0.92f, 1.0f, 1.0f);

        // Cached shader property IDs for zero-alloc updates
        private static readonly int _idStarTime = Shader.PropertyToID("_StarTime");
        private static readonly int _idSpeed = Shader.PropertyToID("_Speed");
        private static readonly int _idBass = Shader.PropertyToID("_Bass");
        private static readonly int _idMid = Shader.PropertyToID("_Mid");
        private static readonly int _idTreble = Shader.PropertyToID("_Treble");
        private static readonly int _idBeat = Shader.PropertyToID("_Beat");
        private static readonly int _idTintColor = Shader.PropertyToID("_TintColor");
        private static readonly int _idAuroraCool = Shader.PropertyToID("_AuroraCool");
        private static readonly int _idAuroraWarm = Shader.PropertyToID("_AuroraWarm");
        private static readonly int _idOverallAlpha = Shader.PropertyToID("_OverallAlpha");
        private static readonly int _idStarScale = Shader.PropertyToID("_StarScale");
        private static readonly int _idLedColor = Shader.PropertyToID("_LedColor");
        private static readonly int _idAudioPulse = Shader.PropertyToID("_AudioPulse");
        private static readonly int _idAudioBeat = Shader.PropertyToID("_AudioBeat");

        private MeshRenderer _stageRenderer;
        private Color _lastStagePeakColor = new Color(-1f, -1f, -1f, -1f);
        private bool _stageStaticUniformsInitialized = false;
        private float _nextDependencyLookupTime = -10f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInitialize()
        {
            if (FindFirstObjectByType<SonicStarRiverVisualizer>() == null)
            {
                GameObject go = new GameObject("SonicStarRiverVisualizer");
                go.AddComponent<SonicStarRiverVisualizer>();
            }
        }

        private void Awake()
        {
            InitializeVisualizer();
        }

        private void Start()
        {
            FindSceneDependencies();
            EnforceFloatingBlocksCount();
        }

        private void OnEnable()
        {
            if (_waveObject != null) _waveObject.SetActive(true);
            if (_ambientObject != null) _ambientObject.SetActive(true);
        }

        private void OnDisable()
        {
            if (_waveObject != null) _waveObject.SetActive(false);
            if (_ambientObject != null) _ambientObject.SetActive(false);
        }

        private void OnDestroy()
        {
            CleanupResources();
        }

        private void FindSceneDependencies()
        {
            if (_audioEngine == null) _audioEngine = FindFirstObjectByType<SonicTopographyAudioEngine>();
            if (_player == null) _player = FindFirstObjectByType<SonicMusicPlayerController>();
            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
        }

        private void EnforceFloatingBlocksCount()
        {
            if (_visualizer != null && _visualizer.groundEqSettings != null)
            {
                if (_visualizer.groundEqSettings.floatingBlockCount != 96)
                {
                    _visualizer.groundEqSettings.floatingBlockCount = 96;
                    _visualizer.groundEqSettings.floatingBlockIntensity = 65f;
                }
            }
        }

        private void InitializeVisualizer()
        {
            // Position root at scene origin
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            // 1. Build and configure Star River Waves Layer (uPreset == 5)
            // Delicate airy ribbon grid (60x60 = 3,600 stars = 14,400 vertices)
            Material waveMat = Resources.Load<Material>("Materials/SonicStarRiver");
            if (waveMat != null)
            {
                _waveMaterial = new Material(waveMat) { name = "SonicStarRiver_Waves_Mat" };
            }
            else
            {
                Shader waveShader = Resources.Load<Shader>("Shaders/SonicStarRiver");
                if (waveShader == null) waveShader = Shader.Find("SonicTopography/StarRiver");
                if (waveShader != null)
                {
                    _waveMaterial = new Material(waveShader) { name = "SonicStarRiver_Waves_Mat" };
                }
            }

            _waveMesh = BuildStarRiverWaveMesh(60, 60); // 3,600 stars = 14,400 vertices

            _waveObject = new GameObject("StarRiver_Waves");
            _waveObject.transform.SetParent(this.transform, false);
            // Position spanning gracefully across mid-space and sky, sweeping past user's left and right
            _waveObject.transform.localPosition = new Vector3(0f, 6.5f, 44f);
            _waveObject.transform.localRotation = Quaternion.Euler(-4f, 0f, 0f);
            _waveObject.transform.localScale = new Vector3(2.7f, 2.0f, 2.5f);

            MeshFilter waveMf = _waveObject.AddComponent<MeshFilter>();
            waveMf.sharedMesh = _waveMesh;
            MeshRenderer waveMr = _waveObject.AddComponent<MeshRenderer>();
            waveMr.sharedMaterial = _waveMaterial;
            ConfigureRendererPerformance(waveMr);

            // Active with calm ambient motion at idle, blooms on music playback
            _waveObject.SetActive(true);

            // 2. Build and configure Ambient Drifting Starfield Layer (floatGroup)
            // Enhanced to 1,200 subtle motes distributed around user flanks (left & right) and sky
            Material ambientMat = Resources.Load<Material>("Materials/SonicAmbientStarfield");
            if (ambientMat != null)
            {
                _ambientMaterial = new Material(ambientMat) { name = "SonicStarRiver_Ambient_Mat" };
            }
            else
            {
                Shader ambientShader = Resources.Load<Shader>("Shaders/SonicAmbientStarfield");
                if (ambientShader == null) ambientShader = Shader.Find("SonicTopography/AmbientStarfield");
                if (ambientShader != null)
                {
                    _ambientMaterial = new Material(ambientShader) { name = "SonicStarRiver_Ambient_Mat" };
                }
            }

            _ambientMesh = BuildAmbientStarfieldMesh(1200); // 1,200 ambient celestial stars = 4,800 vertices

            _ambientObject = new GameObject("StarRiver_AmbientStarfield");
            _ambientObject.transform.SetParent(this.transform, false);
            _ambientObject.transform.localPosition = Vector3.zero;
            _ambientObject.transform.localRotation = Quaternion.identity;
            _ambientObject.transform.localScale = Vector3.one;

            MeshFilter ambientMf = _ambientObject.AddComponent<MeshFilter>();
            ambientMf.sharedMesh = _ambientMesh;
            MeshRenderer ambientMr = _ambientObject.AddComponent<MeshRenderer>();
            ambientMr.sharedMaterial = _ambientMaterial;
            ConfigureRendererPerformance(ambientMr);
        }

        private static void ConfigureRendererPerformance(MeshRenderer mr)
        {
            if (mr == null) return;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            mr.allowOcclusionWhenDynamic = false;
        }

        /// <summary>
        /// Builds static quad mesh for the sweeping 3D Star River waves (2,304 points, 9,216 vertices).
        /// UV0 contains normalized grid coordinates (u, v) for wave and noise math.
        /// UV1 contains (quadOffset.x, quadOffset.y, aRand, laneSeed).
        /// </summary>
        private static Mesh BuildStarRiverWaveMesh(int gridW, int gridH)
        {
            int count = gridW * gridH;
            Vector3[] positions = new Vector3[count * 4];
            Vector2[] uvs = new Vector2[count * 4];
            Vector4[] uv2s = new Vector4[count * 4];
            int[] triangles = new int[count * 6];

            float invW = 1f / (float)gridW;
            float invH = 1f / (float)gridH;

            for (int i = 0; i < count; i++)
            {
                int gx = i % gridW;
                int gy = i / gridW;
                float u = (gx + 0.5f) * invW;
                float v = (gy + 0.5f) * invH;
                float rand = (float)((double)(i * 1234567 % 1000003) / 1000003.0);
                float laneSeed = (float)((double)(gy * 9876543 % 1000003) / 1000003.0);

                int vIdx = i * 4;
                positions[vIdx + 0] = Vector3.zero;
                positions[vIdx + 1] = Vector3.zero;
                positions[vIdx + 2] = Vector3.zero;
                positions[vIdx + 3] = Vector3.zero;

                Vector2 uvCoord = new Vector2(u, v);
                uvs[vIdx + 0] = uvCoord;
                uvs[vIdx + 1] = uvCoord;
                uvs[vIdx + 2] = uvCoord;
                uvs[vIdx + 3] = uvCoord;

                uv2s[vIdx + 0] = new Vector4(-1f, -1f, rand, laneSeed);
                uv2s[vIdx + 1] = new Vector4( 1f, -1f, rand, laneSeed);
                uv2s[vIdx + 2] = new Vector4( 1f,  1f, rand, laneSeed);
                uv2s[vIdx + 3] = new Vector4(-1f,  1f, rand, laneSeed);

                int tIdx = i * 6;
                triangles[tIdx + 0] = vIdx + 0;
                triangles[tIdx + 1] = vIdx + 1;
                triangles[tIdx + 2] = vIdx + 2;
                triangles[tIdx + 3] = vIdx + 0;
                triangles[tIdx + 4] = vIdx + 2;
                triangles[tIdx + 5] = vIdx + 3;
            }

            Mesh mesh = new Mesh
            {
                name = "SonicStarRiver_WaveMesh",
                indexFormat = IndexFormat.UInt16
            };
            mesh.vertices = positions;
            mesh.uv = uvs;
            mesh.SetUVs(1, uv2s);
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(new Vector3(0f, 10f, 65f), new Vector3(300f, 180f, 300f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>
        /// Builds static quad mesh for the 360-degree ambient starfield (600 points, 2,400 vertices).
        /// Position stores base (bx, by, bz) coordinates in a halo ring and celestial outer volume.
        /// Inner radius starts at 7.0m so stars never crowd user's near POV.
        /// </summary>
        private static Mesh BuildAmbientStarfieldMesh(int count)
        {
            Vector3[] positions = new Vector3[count * 4];
            Vector2[] uvs = new Vector2[count * 4];
            Vector4[] uv2s = new Vector4[count * 4];
            int[] triangles = new int[count * 6];

            System.Random rng = new System.Random(42);

            for (int i = 0; i < count; i++)
            {
                float rand = (float)rng.NextDouble();
                float amp = 0.20f + (float)rng.NextDouble() * 0.40f;
                float phaseX = (float)rng.NextDouble() * Mathf.PI * 2f;
                float phaseY = (float)rng.NextDouble() * Mathf.PI * 2f;

                Vector3 basePos;
                if (i < 450)
                {
                    // 1. User flanks: left and right sides of the user (X in [-45, -5] and [5, 45])
                    bool isLeft = (i % 2 == 0);
                    float sideDist = 5.0f + (float)rng.NextDouble() * 38.0f;
                    float x = isLeft ? -sideDist : sideDist;
                    float y = -26.0f + (float)rng.NextDouble() * 52.0f; // -26m (platter) to +26m (sky)
                    float z = -20.0f + (float)rng.NextDouble() * 65.0f; // surrounding and reaching forward
                    basePos = new Vector3(x, y, z);
                }
                else if (i < 900)
                {
                    // 2. Inner halo around terrain platter and stage
                    float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float r = 7.0f + Mathf.Pow((float)rng.NextDouble(), 0.75f) * 36.0f;
                    float height = -24.0f + (float)rng.NextDouble() * 48.0f;
                    basePos = new Vector3(
                        Mathf.Cos(angle) * r,
                        height,
                        Mathf.Sin(angle) * r + 24.0f
                    );
                }
                else
                {
                    // 3. Wide celestial outer sphere (radius 32m - 80m)
                    float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float pitch = (float)(rng.NextDouble() - 0.40) * Mathf.PI;
                    float r = 32.0f + (float)rng.NextDouble() * 48.0f;
                    basePos = new Vector3(
                        Mathf.Cos(pitch) * Mathf.Sin(angle) * r,
                        Mathf.Sin(pitch) * r + 6.0f,
                        Mathf.Cos(pitch) * Mathf.Cos(angle) * r + 25.0f
                    );
                }

                int vIdx = i * 4;
                positions[vIdx + 0] = basePos;
                positions[vIdx + 1] = basePos;
                positions[vIdx + 2] = basePos;
                positions[vIdx + 3] = basePos;

                Vector2 phase = new Vector2(phaseX, phaseY);
                uvs[vIdx + 0] = phase;
                uvs[vIdx + 1] = phase;
                uvs[vIdx + 2] = phase;
                uvs[vIdx + 3] = phase;

                uv2s[vIdx + 0] = new Vector4(-1f, -1f, rand, amp);
                uv2s[vIdx + 1] = new Vector4( 1f, -1f, rand, amp);
                uv2s[vIdx + 2] = new Vector4( 1f,  1f, rand, amp);
                uv2s[vIdx + 3] = new Vector4(-1f,  1f, rand, amp);

                int tIdx = i * 6;
                triangles[tIdx + 0] = vIdx + 0;
                triangles[tIdx + 1] = vIdx + 1;
                triangles[tIdx + 2] = vIdx + 2;
                triangles[tIdx + 3] = vIdx + 0;
                triangles[tIdx + 4] = vIdx + 2;
                triangles[tIdx + 5] = vIdx + 3;
            }

            Mesh mesh = new Mesh
            {
                name = "SonicAmbientStarfield_Mesh",
                indexFormat = IndexFormat.UInt16
            };
            mesh.vertices = positions;
            mesh.uv = uvs;
            mesh.SetUVs(1, uv2s);
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.up * 10f, new Vector3(180f, 180f, 180f));
            mesh.UploadMeshData(true);
            return mesh;
        }

        private void Update()
        {
            if ((_audioEngine == null || _player == null || _visualizer == null) && Time.unscaledTime >= _nextDependencyLookupTime)
            {
                _nextDependencyLookupTime = Time.unscaledTime + 1.0f;
                FindSceneDependencies();
                EnforceFloatingBlocksCount();
            }

            float dt = Time.deltaTime;

            // 1. Music playback activity detection:
            // When no music is playing, wave activity relaxes to 0, maintaining a calm, elegant ambient flow.
            // When music plays, wave activity smoothly rises to 1.0, blooming into full music reactivity.
            bool isPlaying = (_player != null && _player.IsPlaying) ||
                            (_audioEngine != null && (_audioEngine.IsExternalAudioActive || _audioEngine.GetAudioData().energy > 0.005f));

            float targetActivity = isPlaying ? 1.0f : 0.0f;
            float fadeSpeed = isPlaying ? 2.0f : 1.5f;
            _waveActivity = Mathf.MoveTowards(_waveActivity, targetActivity, dt * fadeSpeed);

            // Time continuously advances so the cosmos NEVER stays still!
            // When idle, time flows at a serene 0.50x pace; when music plays, it scales up dynamically with the beat.
            float dynamicSpeed = Mathf.Lerp(0.50f, 1.0f + _smoothedBass * 0.35f, _waveActivity);
            _starTime += dt * speed * dynamicSpeed;

            // 2. Audio metrics & smoothing
            if (_audioEngine != null)
            {
                SonicAudioData audio = _audioEngine.GetAudioData();
                _smoothedBass = Mathf.Lerp(_smoothedBass, audio.bass, dt * 14f);
                _smoothedMid = Mathf.Lerp(_smoothedMid, audio.mid, dt * 10f);
                _smoothedTreble = Mathf.Lerp(_smoothedTreble, audio.treble, dt * 16f);

                if (audio.kickOnset > 0.42f)
                {
                    _beatEnvelope = Mathf.Max(_beatEnvelope, Mathf.Clamp01(audio.kickOnset * 1.2f));
                }
            }
            else
            {
                _smoothedBass = Mathf.Lerp(_smoothedBass, 0f, dt * 4f);
                _smoothedMid = Mathf.Lerp(_smoothedMid, 0f, dt * 4f);
                _smoothedTreble = Mathf.Lerp(_smoothedTreble, 0f, dt * 4f);
            }

            _beatEnvelope = Mathf.Max(0f, _beatEnvelope - dt * 3.6f);

            // 3. Adaptive palette coloring harmonized with Sonic Topography active theme / album cover
            Color targetCool = new Color(0.35f, 0.65f, 0.95f, 1.0f);
            Color targetWarm = new Color(0.55f, 0.40f, 0.90f, 1.0f);
            Color targetTint = new Color(0.85f, 0.92f, 1.0f, 1.0f);

            if (_visualizer != null)
            {
                SonicThemeColors sc = _visualizer.CurrentColors;
                if (sc.uWarmCore.maxColorComponent > 0.05f || sc.uCoolCore.maxColorComponent > 0.05f)
                {
                    targetCool = sc.uCoolCore;
                    targetWarm = sc.uWarmCore;
                    targetTint = Color.Lerp(sc.uCoolEdge, Color.white, 0.65f);
                }
            }
            else if (_player != null && _player.CurrentTrack != null)
            {
                SonicThemeColors trackPalette = _player.CurrentTrack.extractedPalette;
                if (trackPalette.uWarmCore.maxColorComponent > 0.05f || trackPalette.uCoolCore.maxColorComponent > 0.05f)
                {
                    targetCool = trackPalette.uCoolCore;
                    targetWarm = trackPalette.uWarmCore;
                    targetTint = Color.Lerp(trackPalette.uRippleColor, Color.white, 0.65f);
                }
            }

            _auroraCool = Color.Lerp(_auroraCool, targetCool, dt * 2.2f);
            _auroraWarm = Color.Lerp(_auroraWarm, targetWarm, dt * 2.2f);
            _tintColor = Color.Lerp(_tintColor, targetTint, dt * 2.5f);

            // 4. Update Wave Material Uniforms:
            // At idle (no music), the Star River continues to flow serenely at an ambient baseline (~0.32 alpha).
            // When music plays, it blooms to full brightness and audio responsiveness!
            float dynamicWaveAlpha = masterAlpha * Mathf.Lerp(0.32f, 1.0f, _waveActivity);
            if (_waveMaterial != null)
            {
                _waveMaterial.SetFloat(_idStarTime, _starTime);
                _waveMaterial.SetFloat(_idSpeed, speed);
                _waveMaterial.SetFloat(_idBass, _smoothedBass);
                _waveMaterial.SetFloat(_idMid, _smoothedMid);
                _waveMaterial.SetFloat(_idTreble, _smoothedTreble);
                _waveMaterial.SetFloat(_idBeat, _beatEnvelope);
                _waveMaterial.SetColor(_idTintColor, _tintColor);
                _waveMaterial.SetColor(_idAuroraCool, _auroraCool);
                _waveMaterial.SetColor(_idAuroraWarm, _auroraWarm);
                _waveMaterial.SetFloat(_idOverallAlpha, dynamicWaveAlpha);
                _waveMaterial.SetFloat(_idStarScale, particleScale);
            }

            if (_waveObject != null && !_waveObject.activeSelf)
            {
                _waveObject.SetActive(true);
            }

            // 5. Update Ambient Starfield Uniforms (living cosmic motes, always gently drifting)
            if (_ambientMaterial != null)
            {
                _ambientMaterial.SetFloat(_idStarTime, _starTime);
                _ambientMaterial.SetFloat(_idSpeed, speed * 0.85f);
                _ambientMaterial.SetFloat(_idBass, _smoothedBass);
                _ambientMaterial.SetFloat(_idBeat, _beatEnvelope);
                _ambientMaterial.SetColor(_idTintColor, _tintColor);
                _ambientMaterial.SetFloat(_idOverallAlpha, masterAlpha * Mathf.Lerp(0.72f, 0.95f, _waveActivity));
                _ambientMaterial.SetFloat(_idStarScale, particleScale);
            }

            // 6. Update Glass Stage Holo-Deck Uniforms (LED rim & holo-etching palette sync)
            if (_stageRenderer == null && Time.unscaledTime >= _nextDependencyLookupTime)
            {
                GameObject stageObj = GameObject.Find("GlassStageCube");
                if (stageObj != null)
                {
                    _stageRenderer = stageObj.GetComponent<MeshRenderer>();
                    _stageStaticUniformsInitialized = false;
                }
            }

            if (_stageRenderer != null)
            {
                // Synchronize LED color strictly with the High Peaks color (uWarmCore) of the active sonic terrain.
                // In Custom Palette, "High Peaks" directly controls uWarmCore!
                // Stays completely steady: no audio pulsing, no beat flash, so it remains calm and non-distracting.
                Color peakColor = _auroraWarm;

                if (_visualizer != null)
                {
                    SonicThemeColors sc = _visualizer.CurrentColors;
                    if (sc.uWarmCore.maxColorComponent > 0.05f)
                    {
                        peakColor = sc.uWarmCore;
                    }
                    else if (sc.uWarmEdge.maxColorComponent > 0.05f)
                    {
                        peakColor = sc.uWarmEdge;
                    }
                    else if (sc.uCoolCore.maxColorComponent > 0.05f)
                    {
                        peakColor = sc.uCoolCore;
                    }
                }
                else if (_player != null && _player.CurrentTrack != null)
                {
                    SonicThemeColors trackPalette = _player.CurrentTrack.extractedPalette;
                    if (trackPalette.uWarmCore.maxColorComponent > 0.05f)
                    {
                        peakColor = trackPalette.uWarmCore;
                    }
                    else if (trackPalette.uWarmEdge.maxColorComponent > 0.05f)
                    {
                        peakColor = trackPalette.uWarmEdge;
                    }
                }

                Material stageMat = _stageRenderer.sharedMaterial;
                if (stageMat != null)
                {
                    if (!_stageStaticUniformsInitialized)
                    {
                        _stageStaticUniformsInitialized = true;
                        stageMat.SetFloat(_idAudioPulse, 0f);
                        stageMat.SetFloat(_idAudioBeat, 0f);
                    }
                    if (_lastStagePeakColor != peakColor)
                    {
                        _lastStagePeakColor = peakColor;
                        stageMat.SetColor(_idLedColor, peakColor);
                    }
                }
            }
        }

        private void CleanupResources()
        {
            DestroyObjectSafe(_waveMaterial);
            DestroyObjectSafe(_ambientMaterial);
            DestroyObjectSafe(_waveMesh);
            DestroyObjectSafe(_ambientMesh);
            DestroyObjectSafe(_waveObject);
            DestroyObjectSafe(_ambientObject);
        }

        private static void DestroyObjectSafe(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }
    }
}
