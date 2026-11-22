using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SonicTopography
{
    /// <summary>
    /// Procedural 3D animated splash screen based on the official AudioFloor icon.
    /// Pinned directly in front of the VR camera inside an OLED black void at startup.
    ///
    /// Loading Phase:
    /// - Cubes start in crisp, sleek monochrome Black & White.
    /// - Timer activates only on the first rendered frame so the user visibly sees the full icon in B&W.
    /// - Colors smoothly fill from bottom rows up to top peaks, visibly transforming the entire icon.
    /// - Positions are rock-solid (no laggy wave math during background scene/audio loading).
    ///
    /// Transition Phase:
    /// - Elegant "Zoom Out to None":
    ///   The complete logo cluster and title smoothly shrink away into the distance (scale 1.0 -> 0.0)
    ///   and dissolve to none while the black void remains pitch black.
    /// - Once the icon is completely gone, the void sphere dissolves away softly, unveiling the glass stage.
    /// - OnSplashFinished triggers the Spatial UI menu to zoom into view (scale 0.96 -> 1.00) in front of the user!
    /// </summary>
    public class SonicTopographySplashScreen : MonoBehaviour
    {
        private const float DefaultHoldDuration = 2.60f; // 0.0s - 0.5s B&W, 0.5s - 2.0s color fill, 2.0s - 2.6s full color showcase
        private const float DefaultZoomDuration = 0.55f; // 2.60s - 3.15s: Icon smoothly zooms out to none (shrinks to 0 and vanishes)
        private const float DefaultRevealDuration = 0.25f; // 3.15s - 3.40s: Black void dissolves away, unveiling stage & scene

        [Header("Timing")]
        public float holdDuration = DefaultHoldDuration;
        public float zoomDuration = DefaultZoomDuration;
        public float revealDuration = DefaultRevealDuration;

        public float fadeDuration
        {
            get => zoomDuration;
            set => zoomDuration = value;
        }

        [Header("Geometry & Spacing")]
        public float splashDistance = 1.30f;
        public float cubePitch = 0.038f; // 38mm pitch matches 38px in official SVG
        public Vector3 cubeDimensions = new Vector3(0.026f, 0.026f, 0.020f); // 26mm x 26mm x 20mm

        // Official AudioFloor Signature Color Palette
        public static readonly Color ColorCyan         = new Color(51f / 255f, 230f / 255f, 255f / 255f, 1.0f); // #33E6FF
        public static readonly Color ColorElectricBlue = new Color(26f / 255f, 102f / 255f, 255f / 255f, 1.0f); // #1A66FF
        public static readonly Color ColorPurple       = new Color(173f / 255f, 46f / 255f, 255f / 255f, 1.0f); // #AD2EFF
        public static readonly Color ColorLavender     = new Color(217f / 255f, 140f / 255f, 255f / 255f, 1.0f); // #D98CFF

        public static bool IsSplashActive { get; private set; } = false;
        public bool IsComplete { get; private set; } = false;

        public event Action OnSplashTransitionBegin;
        public event Action OnSplashFinished;

        private class CubeElement
        {
            public GameObject gameObject;
            public Transform transform;
            public MeshRenderer renderer;
            public MaterialPropertyBlock propertyBlock;
            public int colIndex;
            public int colOffset;
            public int row;
            public Vector3 baseLocalPos;
            public Color baseColor;
            public float peakFactor;
        }

        private Transform _camTransform;
        private GameObject _splashRoot;
        private GameObject _cubesRoot;
        private GameObject _voidSphere;
        private Material _voidMaterial;

        private Material _matCyan;
        private Material _matBlue;
        private Material _matPurple;
        private Material _matLavender;

        private GameObject _titleCanvasGo;
        private CanvasGroup _titleCanvasGroup;
        private TextMeshProUGUI _titleTmp;
        private Vector3 _initialTitlePos;

        private readonly List<CubeElement> _cubes = new List<CubeElement>(33);
        private float _startTime;
        private bool _firstFrameRendered = false;
        private bool _initialized = false;
        private bool _transitionBegun = false;
        private bool _completed = false;

        private static readonly int[] ColumnHeights = new int[] { 1, 3, 5, 7, 5, 3, 5, 3, 1 };

        public static SonicTopographySplashScreen CreateSplashScreen(Transform cameraTransform = null)
        {
            SonicTopographySplashScreen existing = FindFirstObjectByType<SonicTopographySplashScreen>();
            if (existing != null) return existing;

            GameObject host = new GameObject("SonicTopographySplashScreen");
            SonicTopographySplashScreen splash = host.AddComponent<SonicTopographySplashScreen>();
            if (cameraTransform != null)
            {
                splash.SetCameraTransform(cameraTransform);
            }
            return splash;
        }

        public void SetCameraTransform(Transform cam)
        {
            _camTransform = cam;
            if (_splashRoot != null && _camTransform != null)
            {
                if (_splashRoot.transform.parent != _camTransform)
                {
                    _splashRoot.transform.SetParent(_camTransform, false);
                }
                _splashRoot.transform.localPosition = new Vector3(0f, 0f, splashDistance);
                _splashRoot.transform.localRotation = Quaternion.identity;
                _splashRoot.transform.localScale = Vector3.one;
            }
            if (_voidSphere != null && _camTransform != null)
            {
                if (_voidSphere.transform.parent != _camTransform)
                {
                    _voidSphere.transform.SetParent(_camTransform, false);
                }
                _voidSphere.transform.localPosition = Vector3.zero;
                _voidSphere.transform.localRotation = Quaternion.identity;
                _voidSphere.transform.localScale = Vector3.one * 5.0f;
            }
        }

        private void Awake()
        {
            IsSplashActive = true;
            _startTime = Time.unscaledTime;
            ResolveCamera();
            BuildSplashScreenHierarchy();
        }

        private void Start()
        {
            if (!_initialized)
            {
                ResolveCamera();
                BuildSplashScreenHierarchy();
            }
        }

        private void ResolveCamera()
        {
            if (_camTransform == null)
            {
                Camera cam = Camera.main;
                if (cam == null) cam = FindFirstObjectByType<Camera>();
                if (cam != null)
                {
                    SetCameraTransform(cam.transform);
                }
            }
        }

        private void BuildSplashScreenHierarchy()
        {
            if (_initialized) return;
            _initialized = true;

            ResolveCamera();

            // 1. Root container pinned to camera
            _splashRoot = new GameObject("SplashRoot");
            if (_camTransform != null)
            {
                _splashRoot.transform.SetParent(_camTransform, false);
            }
            _splashRoot.transform.localPosition = new Vector3(0f, 0f, splashDistance);
            _splashRoot.transform.localRotation = Quaternion.identity;
            _splashRoot.transform.localScale = Vector3.one;

            // 2. Black Void 360-degree Sphere enclosing the camera
            BuildBlackVoidSphere();

            // 3. Materials
            BuildMaterials();

            // 4. Procedural 3D Cubes
            BuildCubesCluster();

            // 5. Title Text
            BuildTitleText();
        }

        private void BuildBlackVoidSphere()
        {
            Shader voidShader = Resources.Load<Shader>("Shaders/SonicSplashVoid");
            if (voidShader == null) voidShader = Shader.Find("SonicTopography/SplashVoid");
            if (voidShader == null) voidShader = Shader.Find("Universal Render Pipeline/Unlit");

            _voidMaterial = new Material(voidShader);
            _voidMaterial.name = "SonicSplashVoid_Instance";
            _voidMaterial.SetColor("_Color", Color.black);
            _voidMaterial.SetFloat("_Alpha", 1.0f);

            GameObject tempSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh sphereMesh = tempSphere.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(tempSphere);

            _voidSphere = new GameObject("SplashVoidSphere");
            if (_camTransform != null)
            {
                _voidSphere.transform.SetParent(_camTransform, false);
            }
            _voidSphere.transform.localPosition = Vector3.zero;
            _voidSphere.transform.localRotation = Quaternion.identity;
            _voidSphere.transform.localScale = Vector3.one * 5.0f; // 2.5m radius encloses camera & cubes cleanly

            MeshFilter mf = _voidSphere.AddComponent<MeshFilter>();
            mf.sharedMesh = sphereMesh;
            MeshRenderer mr = _voidSphere.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _voidMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private void BuildMaterials()
        {
            Shader cubeShader = Resources.Load<Shader>("Shaders/SonicSplashCube");
            if (cubeShader == null) cubeShader = Shader.Find("SonicTopography/SplashCube");
            if (cubeShader == null) cubeShader = Shader.Find("Universal Render Pipeline/Unlit");

            _matCyan = CreateCubeMaterial(cubeShader, "Mat_SplashCyan", ColorCyan);
            _matBlue = CreateCubeMaterial(cubeShader, "Mat_SplashBlue", ColorElectricBlue);
            _matPurple = CreateCubeMaterial(cubeShader, "Mat_SplashPurple", ColorPurple);
            _matLavender = CreateCubeMaterial(cubeShader, "Mat_SplashLavender", ColorLavender);
        }

        private Material CreateCubeMaterial(Shader s, string name, Color color)
        {
            Material m = new Material(s);
            m.name = name;
            m.SetColor("_BaseColor", color);
            m.SetColor("_EmissionColor", color);
            m.SetFloat("_ColorFill", 0.0f); // Starts as pure Black & White
            m.SetFloat("_GlowIntensity", 1.35f);
            m.SetFloat("_Alpha", 1.0f);
            m.SetFloat("_FresnelPower", 2.5f);
            m.SetFloat("_FresnelScale", 0.6f);
            return m;
        }

        private Material GetMaterialForRow(int row)
        {
            switch (Mathf.Abs(row))
            {
                case 0: return _matCyan;
                case 1: return _matBlue;
                case 2: return _matPurple;
                case 3: return _matLavender;
                default: return _matCyan;
            }
        }

        private Color GetColorForRow(int row)
        {
            switch (Mathf.Abs(row))
            {
                case 0: return ColorCyan;
                case 1: return ColorElectricBlue;
                case 2: return ColorPurple;
                case 3: return ColorLavender;
                default: return ColorCyan;
            }
        }

        private void BuildCubesCluster()
        {
            _cubesRoot = new GameObject("SplashCubesCluster");
            _cubesRoot.transform.SetParent(_splashRoot.transform, false);
            _cubesRoot.transform.localPosition = Vector3.zero;
            _cubesRoot.transform.localRotation = Quaternion.identity;
            _cubesRoot.transform.localScale = Vector3.one;

            GameObject tempCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh sharedCubeMesh = tempCube.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(tempCube);

            _cubes.Clear();

            // 9 columns: c from 0 to 8. Center column is index 4.
            for (int c = 0; c < ColumnHeights.Length; c++)
            {
                int height = ColumnHeights[c];
                int maxRow = (height - 1) / 2;
                int colOffset = c - 4; // -4 to +4

                float peakFactor = (c == 3 || c == 6) ? 1.25f : (c == 5 ? 0.85f : 1.0f);

                for (int row = -maxRow; row <= maxRow; row++)
                {
                    GameObject cubeGo = new GameObject($"Cube_C{c}_R{row}");
                    cubeGo.transform.SetParent(_cubesRoot.transform, false);

                    Vector3 basePos = new Vector3(colOffset * cubePitch, row * cubePitch, 0f);
                    cubeGo.transform.localPosition = basePos;
                    cubeGo.transform.localRotation = Quaternion.identity;
                    cubeGo.transform.localScale = cubeDimensions;

                    MeshFilter mf = cubeGo.AddComponent<MeshFilter>();
                    mf.sharedMesh = sharedCubeMesh;

                    MeshRenderer mr = cubeGo.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = GetMaterialForRow(row);
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;

                    MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                    mr.GetPropertyBlock(mpb);
                    mpb.SetFloat("_ColorFill", 0.0f); // Guaranteed Black & White at start
                    mpb.SetFloat("_GlowIntensity", 1.35f);
                    mpb.SetFloat("_Alpha", 1.0f);
                    mr.SetPropertyBlock(mpb);

                    CubeElement elem = new CubeElement
                    {
                        gameObject = cubeGo,
                        transform = cubeGo.transform,
                        renderer = mr,
                        propertyBlock = mpb,
                        colIndex = c,
                        colOffset = colOffset,
                        row = row,
                        baseLocalPos = basePos,
                        baseColor = GetColorForRow(row),
                        peakFactor = peakFactor
                    };

                    _cubes.Add(elem);
                }
            }
        }

        private void BuildTitleText()
        {
            _titleCanvasGo = new GameObject("SplashTitleCanvas");
            _titleCanvasGo.transform.SetParent(_splashRoot.transform, false);
            _initialTitlePos = new Vector3(0f, -0.21f, 0f);
            _titleCanvasGo.transform.localPosition = _initialTitlePos;
            _titleCanvasGo.transform.localRotation = Quaternion.identity;
            _titleCanvasGo.transform.localScale = Vector3.one * 0.001f;

            Canvas canvas = _titleCanvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            _titleCanvasGroup = _titleCanvasGo.AddComponent<CanvasGroup>();
            _titleCanvasGroup.alpha = 0f;

            RectTransform crt = _titleCanvasGo.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(800f, 100f);

            GameObject textGo = new GameObject("TitleText", typeof(RectTransform));
            textGo.transform.SetParent(_titleCanvasGo.transform, false);
            RectTransform trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;

            _titleTmp = textGo.AddComponent<TextMeshProUGUI>();
            _titleTmp.text = "AUDIOFLOOR";
            TMP_FontAsset font = TMP_Settings.defaultFontAsset ?? Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            if (font != null)
            {
                _titleTmp.font = font;
            }
            _titleTmp.fontSize = 28f;
            _titleTmp.fontStyle = FontStyles.Bold;
            _titleTmp.alignment = TextAlignmentOptions.Center;
            _titleTmp.characterSpacing = 28f;
            _titleTmp.color = new Color(0.88f, 0.95f, 1.0f, 1.0f);
            _titleTmp.raycastTarget = false;
            if (_titleTmp.fontMaterial != null)
            {
                _titleTmp.fontMaterial.renderQueue = 3520;
            }
        }

        private void Update()
        {
            if (_completed) return;

            // Anchor timeline to the very first actively rendered frame so background engine setup
            // never consumes the animation before the user actually sees the display
            if (!_firstFrameRendered)
            {
                _firstFrameRendered = true;
                _startTime = Time.unscaledTime;

                // Ensure every single cube begins in 100% Black & White
                for (int i = 0; i < _cubes.Count; i++)
                {
                    _cubes[i].renderer.GetPropertyBlock(_cubes[i].propertyBlock);
                    _cubes[i].propertyBlock.SetFloat("_ColorFill", 0.0f);
                    _cubes[i].renderer.SetPropertyBlock(_cubes[i].propertyBlock);
                }
            }

            ResolveCamera();
            UpdateAnimation();
        }

        private void LateUpdate()
        {
            if (_completed) return;

            ResolveCamera();

            // Ensure rock-solid camera alignment on every frame regardless of XR reprojection
            if (_camTransform != null)
            {
                if (_splashRoot != null)
                {
                    if (_splashRoot.transform.parent != _camTransform)
                    {
                        _splashRoot.transform.SetParent(_camTransform, false);
                    }
                    _splashRoot.transform.localPosition = new Vector3(0f, 0f, splashDistance);
                    _splashRoot.transform.localRotation = Quaternion.identity;
                }
                if (_voidSphere != null)
                {
                    if (_voidSphere.transform.parent != _camTransform)
                    {
                        _voidSphere.transform.SetParent(_camTransform, false);
                    }
                    _voidSphere.transform.localPosition = Vector3.zero;
                    _voidSphere.transform.localRotation = Quaternion.identity;
                }
            }
        }

        private void UpdateAnimation()
        {
            float t = Time.unscaledTime - _startTime;

            if (t < holdDuration)
            {
                // ----------------------------------------------------------------------
                // PHASE 1: Loading & Full-Color Showcase (0.0s to 2.60s)
                // - Cubes visibly showcase in pure Black & White for the first 0.5s.
                // - Colors smoothly fill from bottom rows up to top peaks (0.50s - 2.00s).
                // - Complete vibrant AudioFloor icon showcased in full glory (2.00s - 2.60s).
                // - Zero wave oscillation to guarantee 100% stable framerate during load.
                // - Black void is 100% opaque: stage, floor, world, and menu stay hidden.
                // ----------------------------------------------------------------------
                UpdateColorFill(t);

                // Gentle fade-in for title text over first 0.35s
                if (_titleCanvasGroup != null)
                {
                    float textIn = Mathf.Clamp01(t / 0.35f);
                    _titleCanvasGroup.alpha = Mathf.SmoothStep(0f, 1f, textIn);
                }

                if (_voidMaterial != null)
                {
                    _voidMaterial.SetFloat("_Alpha", 1.0f);
                }
            }
            else if (t < (holdDuration + zoomDuration))
            {
                // ----------------------------------------------------------------------
                // PHASE 2: Elegant "Zoom Out to None" Transition (2.60s to 3.15s)
                // - Icon and title smoothly shrink away into the distance (scale 1.0 -> 0.0)
                //   and dissolve into none as a cohesive unit.
                // - Black void sphere stays 100% solid black: the rest of the world and
                //   menu remain completely hidden until the icon has finished vanishing!
                // ----------------------------------------------------------------------
                if (!_transitionBegun)
                {
                    _transitionBegun = true;
                    try
                    {
                        OnSplashTransitionBegin?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }

                float zoomProgress = Mathf.Clamp01((t - holdDuration) / zoomDuration);
                ApplyZoomOutToNone(zoomProgress);

                if (_voidMaterial != null)
                {
                    _voidMaterial.SetFloat("_Alpha", 1.0f);
                }
            }
            else if (t < (holdDuration + zoomDuration + revealDuration))
            {
                // ----------------------------------------------------------------------
                // PHASE 3: Reveal Stage & Environment (3.15s to 3.40s)
                // - Icon is completely gone (scale 0, alpha 0).
                // - Void sphere smoothly dissolves away (1.0 -> 0.0), softly unveiling
                //   the glass stage and visualizer.
                // ----------------------------------------------------------------------
                EnsureIconHidden();

                float revealProgress = Mathf.Clamp01((t - (holdDuration + zoomDuration)) / revealDuration);
                float voidAlpha = 1.0f - Mathf.SmoothStep(0f, 1f, revealProgress);
                if (_voidMaterial != null)
                {
                    _voidMaterial.SetFloat("_Alpha", voidAlpha);
                }
            }
            else
            {
                // Sequence Complete (>= 3.40s) -> triggers OnSplashFinished -> Menu zooms in!
                CompleteSplashScreen();
            }
        }

        private void EnsureIconHidden()
        {
            if (_cubesRoot != null)
            {
                _cubesRoot.transform.localScale = Vector3.zero;
            }
            if (_titleCanvasGroup != null)
            {
                _titleCanvasGroup.alpha = 0f;
            }
            if (_titleCanvasGo != null)
            {
                _titleCanvasGo.transform.localScale = Vector3.zero;
            }
        }

        /// <summary>
        /// Phase 1: Rock-solid static cube formation with smooth B&W to Color progression across the WHOLE icon.
        /// Starts in 100% Black & White, then visibly fills row by row from bottom to top.
        /// </summary>
        private void UpdateColorFill(float t)
        {
            // Initial Black & White showcase for 0.50s, followed by a 1.50s smooth wave fill across all rows (0.50s to 2.00s)
            float fillStart = 0.50f;
            float fillDuration = 1.50f; // 0.50s to 2.00s
            float globalProgress = Mathf.Clamp01((t - fillStart) / fillDuration);

            for (int i = 0; i < _cubes.Count; i++)
            {
                CubeElement c = _cubes[i];
                if (c == null || c.transform == null) continue;

                // Absolute position stability during loading (zero shaking or undulating)
                c.transform.localPosition = c.baseLocalPos;
                c.transform.localRotation = Quaternion.identity;
                c.transform.localScale = cubeDimensions;

                // Normalized row coordinate from 0.0 (bottom row -3) to 1.0 (top row +3)
                float rowNorm = (c.row + 3f) / 6f;

                // Staggered rise across all rows: bottom rows begin filling immediately at 0.0,
                // middle cyan row at 0.20, and top peaks at 0.40, each completing over a 0.60 window.
                float cubeStart = rowNorm * 0.40f;
                float localFill = Mathf.Clamp01((globalProgress - cubeStart) / 0.60f);
                float smoothFill = Mathf.SmoothStep(0f, 1f, localFill);

                // Subtle energetic glow ignition pulse (1.35 -> 2.10 -> 1.50) as each row powers up
                float ignition = Mathf.Sin(smoothFill * Mathf.PI);
                float glow = 1.35f + ignition * 0.75f;

                c.renderer.GetPropertyBlock(c.propertyBlock);
                c.propertyBlock.SetFloat("_ColorFill", smoothFill);
                c.propertyBlock.SetFloat("_GlowIntensity", glow);
                c.propertyBlock.SetFloat("_Alpha", 1.0f);
                c.renderer.SetPropertyBlock(c.propertyBlock);
            }
        }

        /// <summary>
        /// Phase 2: Elegant, smooth "Zoom Out to None" exit transition.
        /// The entire logo cluster and title smoothly shrink into the distance
        /// and dissolve into none, while the background void remains pitch black.
        /// </summary>
        private void ApplyZoomOutToNone(float progress)
        {
            // Silky smooth ease for luxurious zoom-out dissolve
            float zoomEase = Mathf.SmoothStep(0f, 1f, progress);
            float alpha = Mathf.Clamp01(1.0f - Mathf.Pow(progress, 1.5f));

            // 1. Zoom entire cubes cluster smoothly away/shrink into center as a cohesive unit
            if (_cubesRoot != null)
            {
                float scale = Mathf.Lerp(1.0f, 0.0f, zoomEase);
                _cubesRoot.transform.localScale = Vector3.one * scale;
                // Move slightly into the depth of the void (0 -> +0.30m) as it shrinks away
                _cubesRoot.transform.localPosition = new Vector3(0f, 0f, Mathf.Lerp(0f, 0.30f, zoomEase));
            }

            // 2. Dissolve cube alphas smoothly to none while maintaining full color & geometry
            float glow = Mathf.Lerp(1.50f, 0.0f, progress);
            for (int i = 0; i < _cubes.Count; i++)
            {
                CubeElement c = _cubes[i];
                if (c == null || c.renderer == null) continue;

                c.transform.localPosition = c.baseLocalPos;
                c.transform.localRotation = Quaternion.identity;
                c.transform.localScale = cubeDimensions;

                c.renderer.GetPropertyBlock(c.propertyBlock);
                c.propertyBlock.SetFloat("_ColorFill", 1.0f);
                c.propertyBlock.SetFloat("_GlowIntensity", glow);
                c.propertyBlock.SetFloat("_Alpha", alpha);
                c.renderer.SetPropertyBlock(c.propertyBlock);
            }

            // 3. Title text zooms out and dissolves in unison
            if (_titleCanvasGroup != null)
            {
                _titleCanvasGroup.alpha = alpha;
            }
            if (_titleCanvasGo != null)
            {
                _titleCanvasGo.transform.localPosition = _initialTitlePos + new Vector3(0f, 0f, Mathf.Lerp(0f, 0.30f, zoomEase));
                _titleCanvasGo.transform.localScale = Vector3.one * (0.001f * Mathf.Lerp(1.0f, 0.0f, zoomEase));
            }
        }

        private void CompleteSplashScreen()
        {
            if (_completed) return;
            _completed = true;
            IsComplete = true;
            IsSplashActive = false;

            try
            {
                OnSplashFinished?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            // Clean up splash game objects
            if (_voidSphere != null)
            {
                Destroy(_voidSphere);
            }
            if (_splashRoot != null)
            {
                Destroy(_splashRoot);
            }

            Destroy(gameObject, 0.1f);
        }

        private void OnDestroy()
        {
            IsSplashActive = false;
            IsComplete = true;

            if (_voidMaterial != null) Destroy(_voidMaterial);
            if (_matCyan != null) Destroy(_matCyan);
            if (_matBlue != null) Destroy(_matBlue);
            if (_matPurple != null) Destroy(_matPurple);
            if (_matLavender != null) Destroy(_matLavender);
        }
    }
}
