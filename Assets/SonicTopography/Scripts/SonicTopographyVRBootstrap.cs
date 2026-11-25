using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace SonicTopography
{
    public class SonicTopographyVRBootstrap : MonoBehaviour
    {
        [Header("Glass Stage Cube Settings")]
        public Vector3 glassCubeSize = new Vector3(3.4f, 0.12f, 3.4f);
        public Color glassTintColor = new Color(0.01f, 0.015f, 0.025f, 0.025f);
        public Color glassEdgeColor = new Color(0.24f, 0.30f, 0.38f, 0.09f);
        public bool hideTemplateRoomEnvironment = true;

        [Header("Animated Splash Screen Settings")]
        public bool enableAnimatedSplashScreen = true;
        private SonicTopographySplashScreen _splashScreen;

        private Material _glassMaterial;
        private SonicMusicLibraryManager _library;
        private SonicSpatialUIManager _spatialUI;
        private bool _menuOpenedOnBoot = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnAfterSceneLoad()
        {
            if (FindAnyObjectByType<SonicTopographyVisualizer>() == null)
            {
                GameObject host = new GameObject("SonicTopographyManager");
                host.AddComponent<SonicTopographyAudioEngine>();
                host.AddComponent<SonicTopographyVisualizer>();
                host.AddComponent<SonicTopographyVRBootstrap>();
            }
            else if (FindAnyObjectByType<SonicTopographyVRBootstrap>() == null)
            {
                SonicTopographyVisualizer vis = FindAnyObjectByType<SonicTopographyVisualizer>();
                vis.gameObject.AddComponent<SonicTopographyVRBootstrap>();
            }
        }

        private void Awake()
        {
            ConfigureXRPerformanceAndResolution();

            if (hideTemplateRoomEnvironment)
            {
                CleanUpTemplateSceneObjects();
            }

            ConfigureMainCamera();

            if (enableAnimatedSplashScreen)
            {
                EnsureSplashScreen();
            }

            EnsureGlassStageCube();
            EnsureXRInputReady();
            SonicMetaQuestController.EnsureControllerVisuals();
            EnsureControllerActionsReady();
        }

        public void EnsureControllerActionsReady()
        {
            AudioFloorControllerActions actions = GetComponent<AudioFloorControllerActions>();
            if (actions == null)
            {
                actions = gameObject.AddComponent<AudioFloorControllerActions>();
            }
            actions.enabled = true;
            actions.ResolveDependencies();
        }

        public void EnsureSplashScreen()
        {
            if (_splashScreen == null)
            {
                _splashScreen = FindFirstObjectByType<SonicTopographySplashScreen>();
                if (_splashScreen == null)
                {
                    Camera cam = Camera.main;
                    if (cam == null) cam = FindFirstObjectByType<Camera>();
                    _splashScreen = SonicTopographySplashScreen.CreateSplashScreen(cam != null ? cam.transform : null);
                }
            }
            if (_splashScreen != null)
            {
                _splashScreen.OnSplashFinished -= OnSplashFinished;
                _splashScreen.OnSplashFinished += OnSplashFinished;
            }
        }

        private void OnSplashFinished()
        {
            if (!_menuOpenedOnBoot && _spatialUI != null)
            {
                _menuOpenedOnBoot = true;
                _spatialUI.SetUIVisible(true);
            }
        }

        public static void ConfigureHandAndControllerInteractions()
        {
            // Ensure XRUIInputModule has XR and Mouse inputs enabled, with mobile touch disabled (VR uses XRPokeInteractor via XRInput)
            foreach (var xrInputModule in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (xrInputModule == null) continue;
                xrInputModule.enabled = true;
                xrInputModule.enableXRInput = true;
#if UNITY_EDITOR
                xrInputModule.enableMouseInput = true;
#else
                xrInputModule.enableMouseInput = false;
#endif
                xrInputModule.enableTouchInput = false;
            }

            int teleportLayer = UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask.GetMask("Teleport");
            if (teleportLayer == 0) teleportLayer = 1 << 31;

            // 1. Ensure TeleportationArea on stage strictly only responds to Teleport layer (bit 31)
            foreach (var teleArea in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (teleArea != null)
                {
                    teleArea.interactionLayers = teleportLayer;
                    teleArea.teleportTrigger = UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.BaseTeleportationInteractable.TeleportTrigger.OnSelectExited;
                }
            }

            // 2. Strictly exclude Teleport layer from ALL NearFarInteractors and XRPokeInteractors so hands NEVER teleport
            foreach (var nearFar in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nearFar == null) continue;
                nearFar.interactionLayers &= ~teleportLayer;
                nearFar.interactionLayers &= ~(1 << 31);

                var caster = nearFar.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.Casters.CurveInteractionCaster>(true);
                if (caster != null)
                {
                    caster.raycastMask &= ~teleportLayer;
                    caster.raycastMask &= ~(1 << 31);
                }
            }

            foreach (var poke in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Interactors.XRPokeInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (poke == null) continue;
                poke.interactionLayers &= ~teleportLayer;
                poke.interactionLayers &= ~(1 << 31);
            }
        }

        private static readonly System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem> _displaySubsystems =
            new System.Collections.Generic.List<UnityEngine.XR.XRDisplaySubsystem>(2);

        public static void ConfigureXRPerformanceAndResolution()
        {
            Application.targetFrameRate = 72;
            QualitySettings.vSyncCount = 0;
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = 1.5f;

            // Ensure URP pipeline asset renderScale is locked to 1.5x (URP overwrites XRDisplaySubsystem.scaleOfAllRenderTargets every frame with asset.renderScale)
            var urpAsset = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (urpAsset != null)
            {
                urpAsset.renderScale = 1.5f;
                urpAsset.supportsCameraOpaqueTexture = false;
                urpAsset.supportsCameraDepthTexture = false;
                urpAsset.supportsHDR = false;
            }
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset gfxUrp)
            {
                gfxUrp.renderScale = 1.5f;
                gfxUrp.supportsCameraOpaqueTexture = false;
                gfxUrp.supportsCameraDepthTexture = false;
                gfxUrp.supportsHDR = false;
            }
            if (QualitySettings.renderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset qualUrp)
            {
                qualUrp.renderScale = 1.5f;
                qualUrp.supportsCameraOpaqueTexture = false;
                qualUrp.supportsCameraDepthTexture = false;
                qualUrp.supportsHDR = false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                OVRPlugin.systemDisplayFrequency = 72.0f;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SonicVR] OVRPlugin frequency set: {ex.Message}");
            }

            try
            {
                OVRManager.SetDynamicFoveatedRenderingEnabled(false);
                OVRManager.SetFoveatedRenderingLevel(OVRManager.FoveatedRenderingLevel.Low);
            }
            catch { }

            try
            {
                OVRPlugin.suggestedCpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.SustainedHigh;
                OVRPlugin.suggestedGpuPerfLevel = OVRPlugin.ProcessorPerformanceLevel.SustainedHigh;
            }
            catch { }
#endif

            try
            {
                UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.XrPerformanceSettingsFeature.SetPerformanceLevelHint(
                    UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.PerformanceDomain.Cpu,
                    UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.PerformanceLevelHint.SustainedHigh);
                UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.XrPerformanceSettingsFeature.SetPerformanceLevelHint(
                    UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.PerformanceDomain.Gpu,
                    UnityEngine.XR.OpenXR.Features.Extensions.PerformanceSettings.PerformanceLevelHint.Boost);
            }
            catch { }

            _displaySubsystems.Clear();
            SubsystemManager.GetSubsystems(_displaySubsystems);
            for (int i = 0; i < _displaySubsystems.Count; i++)
            {
                var sub = _displaySubsystems[i];
                if (sub != null)
                {
                    sub.scaleOfAllRenderTargets = 1.5f;
                    sub.scaleOfAllViewports = 1.0f;
                }
            }
        }

        private System.Collections.IEnumerator ApplyXRPerformanceRoutine()
        {
            for (int i = 0; i < 8; i++)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                ConfigureXRPerformanceAndResolution();
                if (i == 4 || i == 7)
                {
                    var urp = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
                    Debug.Log($"[SonicVR] XR 1.5x Resolution Verified: eyeTex={UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}, xrScale={UnityEngine.XR.XRSettings.eyeTextureResolutionScale:F2}, urpScale={(urp != null ? urp.renderScale : -1f):F2}");
                }
            }
        }

        private float _bootstrapStartTime;

        private void Start()
        {
            _bootstrapStartTime = Time.unscaledTime;
            ConfigureXRPerformanceAndResolution();
            StartCoroutine(ApplyXRPerformanceRoutine());
            EnsureXRInputReady();
            InitializeMusicPlayerAndSpatialUI();
            SonicMetaQuestController.EnsureControllerVisuals();
        }

        private void Update()
        {
            // Open the menu once the splash sequence has completed (at t >= 3.30s),
            // guaranteeing zero jump after Meta Quest compositor handoff at t ~= 2.7s.
            if (!_menuOpenedOnBoot && _spatialUI != null)
            {
                bool splashDone = !enableAnimatedSplashScreen ||
                                  (_splashScreen != null && _splashScreen.IsComplete) ||
                                  (Time.unscaledTime - _bootstrapStartTime >= 6.0f);
                if (splashDone)
                {
                    _menuOpenedOnBoot = true;
                    _spatialUI.SetUIVisible(true);
                }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            AudioFloorControllerActions.CancelAnyActiveJumpImmediate($"Bootstrap.OnApplicationFocus({hasFocus})");

            // When losing focus because the Meta System Virtual Keyboard is opening on the search bar,
            // do NOT deselect the InputField or disable XRUIInputModule (which would kill the keyboard session).
            if (!hasFocus && SonicQuestVirtualKeyboardOpener.IsKeyboardActiveOrRecentlyUsed(1.0f))
            {
                ResetPinchAndSelectionStatesOnly();
                return;
            }

            // When regaining focus right after the virtual keyboard closes, keep keyboard cooldown active
            if (hasFocus && SonicQuestVirtualKeyboardOpener.IsKeyboardActiveOrRecentlyUsed(1.0f))
            {
                SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
            }

            ResetAllInteractionAndUIStates();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            AudioFloorControllerActions.CancelAnyActiveJumpImmediate($"Bootstrap.OnApplicationPause({pauseStatus})");
            if (pauseStatus)
            {
                ResetAllInteractionAndUIStates();
            }
        }

        public static void ResetPinchAndSelectionStatesOnly()
        {
            // Cancel any lingering 3D interactable selections on XRInteractionManager
            var interactionManagers = FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (interactionManagers != null && interactionManagers.Length > 0)
            {
                var interactors = FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var interactionManager in interactionManagers)
                {
                    if (interactionManager == null) continue;
                    foreach (var interactor in interactors)
                    {
                        if (interactor != null && interactor.hasSelection)
                        {
                            try
                            {
                                interactionManager.CancelInteractableSelection(interactor.firstInteractableSelected);
                            }
                            catch { }
                        }
                    }
                }
            }

            // Reset all ReleaseThresholdButtonReaders so pinch/click states never stick across focus loss
            foreach (var reader in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Samples.Hands.ReleaseThresholdButtonReader>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (reader != null)
                {
                    reader.ResetState();
                }
            }
        }

        public static void ResetAllInteractionAndUIStates()
        {
            // 1. Clear selected UI elements to remove active drag/press references in EventSystem
            // (Skip if the search InputField virtual keyboard is currently active)
            if (!SonicQuestVirtualKeyboardOpener.IsKeyboardActive && UnityEngine.EventSystems.EventSystem.current != null)
            {
                UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
            }

            // 2. Cycle XRUIInputModule to clear internal pointer states, drag states, and tracked touch cache
            foreach (var xrInputModule in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (xrInputModule != null && xrInputModule.enabled)
                {
                    xrInputModule.enabled = false;
                    xrInputModule.enabled = true;
                }
            }

            // 3. Cancel 3D selections & reset hand pinch readers
            ResetPinchAndSelectionStatesOnly();
        }

        private void EnsureXRInputReady()
        {
            Application.runInBackground = true;
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;

            foreach (var actionManager in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Inputs.InputActionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (actionManager == null) continue;
                if (!actionManager.gameObject.activeSelf)
                {
                    actionManager.gameObject.SetActive(true);
                }
                actionManager.enabled = true;
                actionManager.EnableInput();
            }

            foreach (var modalityManager in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (modalityManager == null) continue;
                modalityManager.enabled = true;
            }

            foreach (var xrInputModule in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (xrInputModule == null) continue;
                xrInputModule.enabled = true;
                xrInputModule.enableXRInput = true;
#if UNITY_EDITOR
                xrInputModule.enableMouseInput = true;
#else
                xrInputModule.enableMouseInput = false;
#endif
                xrInputModule.enableTouchInput = false;
            }

            ConfigureHandAndControllerInteractions();
        }

        public void InitializeMusicPlayerAndSpatialUI()
        {
            SonicTopographyAudioEngine audioEngine = GetComponent<SonicTopographyAudioEngine>();
            if (audioEngine == null) audioEngine = FindFirstObjectByType<SonicTopographyAudioEngine>();

            SonicTopographyVisualizer visualizer = GetComponent<SonicTopographyVisualizer>();
            if (visualizer == null) visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();

            _library = GetComponent<SonicMusicLibraryManager>();
            if (_library == null) _library = FindFirstObjectByType<SonicMusicLibraryManager>();
            if (_library == null) _library = gameObject.AddComponent<SonicMusicLibraryManager>();

            SonicMusicPlayerController player = GetComponent<SonicMusicPlayerController>();
            if (player == null) player = FindFirstObjectByType<SonicMusicPlayerController>();
            if (player == null) player = gameObject.AddComponent<SonicMusicPlayerController>();
            player.Initialize(audioEngine, _library);

            _spatialUI = GetComponent<SonicSpatialUIManager>();
            if (_spatialUI == null) _spatialUI = FindFirstObjectByType<SonicSpatialUIManager>();
            if (_spatialUI == null) _spatialUI = gameObject.AddComponent<SonicSpatialUIManager>();
            _spatialUI.Initialize(visualizer, audioEngine, _library, player);

            SonicStarRiverVisualizer starRiver = GetComponent<SonicStarRiverVisualizer>();
            if (starRiver == null) starRiver = FindFirstObjectByType<SonicStarRiverVisualizer>();

            AudioFloorControllerActions controllerActions = GetComponent<AudioFloorControllerActions>();
            if (controllerActions == null) controllerActions = FindFirstObjectByType<AudioFloorControllerActions>();

            SonicWebBrowserManager browserManager = GetComponent<SonicWebBrowserManager>();
            if (browserManager == null) browserManager = FindFirstObjectByType<SonicWebBrowserManager>();

            SonicMixedRealityManager mrManager = GetComponent<SonicMixedRealityManager>();
            if (mrManager == null) mrManager = FindFirstObjectByType<SonicMixedRealityManager>();
            if (mrManager == null) mrManager = gameObject.AddComponent<SonicMixedRealityManager>();
            mrManager.Initialize(visualizer, audioEngine, starRiver, controllerActions, browserManager);

            SonicArKeychainController keychain = GetComponent<SonicArKeychainController>();
            if (keychain == null) keychain = FindFirstObjectByType<SonicArKeychainController>();
            if (keychain == null) keychain = gameObject.AddComponent<SonicArKeychainController>();
            keychain.Initialize(mrManager, player, browserManager, visualizer);

            SonicArTerrainManipulator manipulator = GetComponent<SonicArTerrainManipulator>();
            if (manipulator == null) manipulator = FindFirstObjectByType<SonicArTerrainManipulator>();
            if (manipulator == null) manipulator = gameObject.AddComponent<SonicArTerrainManipulator>();
            manipulator.Initialize(mrManager, visualizer, keychain);

            _spatialUI.BindMixedRealityManager(mrManager);
        }

        private void OnDestroy()
        {
            if (_splashScreen != null)
            {
                _splashScreen.OnSplashFinished -= OnSplashFinished;
            }

            if (_glassMaterial != null)
            {
                Destroy(_glassMaterial);
            }
        }

        public static void CleanUpTemplateSceneObjects()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid()) return;

            GameObject[] roots = activeScene.GetRootGameObjects();

            foreach (GameObject root in roots)
            {
                if (root == null) continue;

                string n = root.name;
                if (n == "Plane" ||
                    n == "Environment" ||
                    n == "Interactables" ||
                    n == "UI" ||
                    n == "Lighting" ||
                    n == "Directional Light" ||
                    n == "Teleport Area Setup" ||
                    n.Contains("Coaching") ||
                    n.Contains("Affordance") ||
                    n.Contains("Spatial Panel"))
                {
                    root.SetActive(false);
                }

                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                {
                    if (child == null) continue;
                    string cn = child.name;
                    if (cn.Contains("TunnelingVignette"))
                    {
                        foreach (Renderer r in child.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r != null) r.enabled = false;
                        }
                    }
                    else if (cn.Contains("Affordance Callouts") || cn.Contains("Affordance Callout"))
                    {
                        foreach (Canvas c in child.GetComponentsInChildren<Canvas>(true))
                        {
                            if (c != null) c.enabled = false;
                        }
                        foreach (Renderer r in child.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r != null) r.enabled = false;
                        }
                    }
                }

                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    string typeName = mb.GetType().Name;
                    if (typeName.Contains("TunnelingVignette"))
                    {
                        mb.enabled = false;
                    }
                }
            }

            foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (l != null) l.enabled = false;
            }
            foreach (ReflectionProbe rp in FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (rp != null)
                {
                    rp.enabled = false;
                    rp.gameObject.SetActive(false);
                }
            }

            SonicTopographyVisualizer vis = FindFirstObjectByType<SonicTopographyVisualizer>();
            if (vis != null && vis.platterWorldPosition.z < 30f)
            {
                vis.platterWorldPosition = new Vector3(0f, -26f, 64f);
            }

            SonicTopographyAudioEngine audioEngine = FindFirstObjectByType<SonicTopographyAudioEngine>();
            if (audioEngine != null)
            {
                audioEngine.autoPlayOnBoot = false;
            }

            LightmapSettings.lightmaps = new LightmapData[0];
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.22f, 0.22f, 0.22f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.09f, 0.09f, 0.09f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.02f, 0.02f, 0.02f, 1f);
        }

        public void EnsureGlassStageCube()
        {
            GameObject existingStage = GameObject.Find("GlassStageCube");
            if (existingStage == null)
            {
                existingStage = GameObject.CreatePrimitive(PrimitiveType.Cube);
                existingStage.name = "GlassStageCube";
                existingStage.transform.position = new Vector3(0f, -glassCubeSize.y * 0.5f, 0f);
                existingStage.transform.rotation = Quaternion.identity;
                existingStage.transform.localScale = glassCubeSize;
                existingStage.isStatic = true;
            }

            Shader glassShader = Resources.Load<Shader>("Shaders/SonicGlassStage");
            if (glassShader == null)
            {
                glassShader = Shader.Find("SonicTopography/GlassStage");
            }

            if (glassShader != null)
            {
                MeshRenderer mr = existingStage.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    if (mr.sharedMaterial == null)
                    {
                        if (_glassMaterial == null)
                        {
                            _glassMaterial = new Material(glassShader);
                        }
                        _glassMaterial.SetColor("_TintColor", glassTintColor);
                        _glassMaterial.SetColor("_EdgeColor", glassEdgeColor);
                        _glassMaterial.SetFloat("_GridCrossThickness", 0.0f);
                        _glassMaterial.SetFloat("_LedIntensity", 1.0f);
                        mr.sharedMaterial = _glassMaterial;
                    }
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
            }

            // Ensure solid floor collider for walking, gravity, and jumping
            BoxCollider col = existingStage.GetComponent<BoxCollider>();
            if (col == null)
            {
                col = existingStage.AddComponent<BoxCollider>();
            }
            col.center = Vector3.zero;
            col.size = Vector3.one;
            col.isTrigger = false;
            col.enabled = true;

            // Ensure TeleportationArea so right joystick teleport ray turns valid on the glass stage
            TeleportationArea teleportArea = existingStage.GetComponent<TeleportationArea>();
            if (teleportArea == null)
            {
                teleportArea = existingStage.AddComponent<TeleportationArea>();
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

        private static void ConfigureMainCamera()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowHDR = false;
                if (cam.nearClipPlane > 0.01f)
                {
                    cam.nearClipPlane = 0.01f;
                }
                if (cam.farClipPlane < 350f)
                {
                    cam.farClipPlane = 350f;
                }

                var camData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (camData != null)
                {
                    camData.renderPostProcessing = false;
                    camData.renderShadows = false;
                    camData.requiresColorOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
                    camData.requiresColorTexture = false;
                    camData.requiresDepthOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
                    camData.requiresDepthTexture = false;
                    camData.allowHDROutput = false;
                }
            }
        }
    }
}