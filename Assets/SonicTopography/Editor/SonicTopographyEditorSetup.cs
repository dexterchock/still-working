#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace SonicTopography.Editor
{
    [InitializeOnLoad]
    public static class SonicTopographyEditorSetup
    {
        private const string SceneTriggerFilePath = "Temp/TriggerSceneSetup.txt";

        private static readonly string[] LeftoverTemplateRootNames = new string[]
        {
            "Plane",
            "Environment",
            "Interactables",
            "UI",
            "Teleport Area Setup"
        };

        private static readonly string[] RequiredOpenXRFeatureTypeNames = new string[]
        {
            "OculusTouchControllerProfile",
            "OculusTouchControllerProximityProfile",
            "MetaQuestTouchPlusControllerProfile",
            "MetaQuestTouchProControllerProfile",
            "HandInteractionProfile",
            "HandTracking",
            "MetaHandTrackingAim",
            "PalmPoseInteraction",
            "MetaQuestFeature",
            "MetaXRSubsampledLayout",
            "MetaXRFoveationFeature",
            "FoveatedRenderingFeature",
            "XrPerformanceSettingsFeature"
        };

        static SonicTopographyEditorSetup()
        {
            EditorApplication.delayCall += AutoCleanAndSetupOpenScene;
            EditorApplication.update += CheckSetupTrigger;
        }

        private static void CheckSetupTrigger()
        {
            if (File.Exists(SceneTriggerFilePath))
            {
                try
                {
                    File.Delete(SceneTriggerFilePath);
                }
                catch
                {
                    // Ignore delete error
                }
                SetupActiveScene();
            }
        }

        private static void AutoCleanAndSetupOpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            EnsureOpenXRInteractionProfilesEnabled();

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded) return;

            // Check if any leftover VR Template root objects, Edit-Mode controller overrides, or missing SonicTopographyManager exist
            bool hasLeftovers = false;
            foreach (GameObject root in activeScene.GetRootGameObjects())
            {
                if (root != null && Array.IndexOf(LeftoverTemplateRootNames, root.name) >= 0)
                {
                    hasLeftovers = true;
                    break;
                }
            }

            bool hasManager = UnityEngine.Object.FindAnyObjectByType<SonicTopographyVisualizer>() != null;
            bool hasSpatialUI = UnityEngine.Object.FindAnyObjectByType<SonicSpatialUIManager>() != null;
            bool hasEditModeControllerOverrides = HasEditModeControllerArtifacts(activeScene);
            if (hasLeftovers || !hasManager || !hasSpatialUI || hasEditModeControllerOverrides)
            {
                SetupActiveScene();
            }

            CloseTutorialWindowsIfOpen();
        }

        private static bool HasEditModeControllerArtifacts(Scene scene)
        {
            if (!scene.IsValid()) return false;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == "Quest3ButtonCap")
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static readonly string[] RequiredOpenXRFeatureFullNames = new string[]
        {
            "UnityEngine.XR.OpenXR.Features.Meta.AROcclusionFeature",
            "UnityEngine.XR.OpenXR.Features.Meta.ARSessionFeature",
            "UnityEngine.XR.OpenXR.Features.Meta.OpenXRLifeCycleFeature"
        };

        public static void EnsureOpenXRInteractionProfilesEnabled()
        {
            const string settingsPath = "Assets/XR/Settings/OpenXR Package Settings.asset";
            UnityEngine.Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(settingsPath);
            if (allAssets == null || allAssets.Length == 0) return;

            bool anyChanged = false;
            foreach (UnityEngine.Object asset in allAssets)
            {
                if (asset == null) continue;
                string typeName = asset.GetType().Name;
                string fullName = asset.GetType().FullName ?? typeName;
                bool shouldEnable = false;
                for (int i = 0; i < RequiredOpenXRFeatureTypeNames.Length; i++)
                {
                    if (string.Equals(typeName, RequiredOpenXRFeatureTypeNames[i], StringComparison.Ordinal))
                    {
                        shouldEnable = true;
                        break;
                    }
                }
                if (!shouldEnable)
                {
                    for (int i = 0; i < RequiredOpenXRFeatureFullNames.Length; i++)
                    {
                        if (string.Equals(fullName, RequiredOpenXRFeatureFullNames[i], StringComparison.Ordinal) &&
                            asset.name != null && asset.name.EndsWith("Android", StringComparison.Ordinal))
                        {
                            shouldEnable = true;
                            break;
                        }
                    }
                }

                if (!shouldEnable) continue;

                SerializedObject so = new SerializedObject(asset);
                SerializedProperty enabledProp = so.FindProperty("m_enabled");
                if (enabledProp != null && !enabledProp.boolValue)
                {
                    enabledProp.boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(asset);
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[Sonic Topography] Enabled OpenXR controller & hand interaction profiles for Standalone (Meta XR Simulator) and Android (Quest 3).");
            }
        }

        private static void CloseTutorialWindowsIfOpen()
        {
            foreach (EditorWindow win in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (win == null) continue;
                string typeName = win.GetType().FullName ?? "";
                if (typeName.Contains("Unity.Tutorials"))
                {
                    win.Close();
                }
            }
        }

        [MenuItem("AudioFloor/Clean VR Template Leftovers & Setup Scene", priority = 10)]
        public static void SetupActiveScene()
        {
            if (BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode) return;

            EnsureOpenXRInteractionProfilesEnabled();

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded) return;

            // Never modify or save a scene if XROrigin is missing (e.g., temporary build scene)
            if (UnityEngine.Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>() == null)
            {
                return;
            }

            bool sceneModified = false;

            // Remove leftover VR Template root objects from the scene hierarchy
            foreach (GameObject root in activeScene.GetRootGameObjects())
            {
                if (root == null) continue;

                if (Array.IndexOf(LeftoverTemplateRootNames, root.name) >= 0)
                {
                    Undo.DestroyObjectImmediate(root);
                    sceneModified = true;
                }
                else if (root.name == "Lighting")
                {
                    foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                    {
                        if (probe != null)
                        {
                            Undo.DestroyObjectImmediate(probe.gameObject);
                            sceneModified = true;
                        }
                    }
                }
                else if (root.name.Contains("XR Origin") || root.name.Contains("XR Rig"))
                {
                    List<GameObject> capsToDestroy = new List<GameObject>();
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t != null && t.name == "Quest3ButtonCap")
                        {
                            capsToDestroy.Add(t.gameObject);
                        }
                    }
                    for (int i = 0; i < capsToDestroy.Count; i++)
                    {
                        if (capsToDestroy[i] != null)
                        {
                            UnityEngine.Object.DestroyImmediate(capsToDestroy[i]);
                            sceneModified = true;
                        }
                    }

                    // 1. Root Transform at (0, 0, 0)
                    root.transform.position = Vector3.zero;
                    root.transform.rotation = Quaternion.identity;
                    root.transform.localScale = Vector3.one;
                    EditorUtility.SetDirty(root.transform);

                    // 2. Configure XROrigin: RequestedTrackingOriginMode = Floor (2), CameraYOffset = 1.68, Camera Offset at (0, 1.68, 0)
                    var origin = root.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>();
                    Transform cameraOffset = null;
                    if (origin != null)
                    {
                        origin.RequestedTrackingOriginMode = Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
                        origin.CameraYOffset = 1.68f;

                        cameraOffset = origin.CameraFloorOffsetObject != null
                            ? origin.CameraFloorOffsetObject.transform
                            : null;

                        if (cameraOffset == null)
                        {
                            var cam = root.GetComponentInChildren<Camera>();
                            if (cam != null && cam.transform.parent != null && cam.transform.parent != root.transform)
                            {
                                cameraOffset = cam.transform.parent;
                                origin.CameraFloorOffsetObject = cameraOffset.gameObject;
                            }
                        }

                        if (cameraOffset != null)
                        {
                            cameraOffset.localPosition = new Vector3(0f, 1.68f, 0f);
                            cameraOffset.localRotation = Quaternion.identity;
                            cameraOffset.localScale = Vector3.one;
                            EditorUtility.SetDirty(cameraOffset);
                        }
                        EditorUtility.SetDirty(origin);
                    }

                    // 3. Configure CharacterController on XR Origin: resting cleanly at y = 0 with skinWidth = 0.001
                    CharacterController cc = root.GetComponent<CharacterController>();
                    if (cc == null)
                    {
                        cc = root.AddComponent<CharacterController>();
                    }
                    cc.enabled = true;
                    cc.skinWidth = 0.001f;
                    cc.height = 1.7f;
                    cc.radius = 0.2f;
                    cc.center = new Vector3(0f, 0.851f, 0f); // 1.7f * 0.5f + 0.001f skinWidth = 0.851f -> bottom cleanly at y = 0
                    cc.minMoveDistance = 0f;
                    cc.stepOffset = 0.3f;
                    cc.slopeLimit = 45f;
                    EditorUtility.SetDirty(cc);

                    // 4. Ensure Locomotion, Move, Turn, Jump providers are enabled, while Climb and redundant Gravity are disabled
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb == null) continue;
                        string tName = mb.GetType().Name;
                        if (tName.Contains("Climb"))
                        {
                            mb.enabled = false;
                            EditorUtility.SetDirty(mb);
                        }
                        else if (mb is UnityEngine.XR.Interaction.Toolkit.Locomotion.Gravity.GravityProvider grav)
                        {
                            grav.useGravity = false;
                            EditorUtility.SetDirty(grav);
                        }
                        else if (tName.Contains("Move") || tName.Contains("Turn") || tName.Contains("Locomotion") ||
                            tName.Contains("Jump"))
                        {
                            mb.enabled = true;
                            EditorUtility.SetDirty(mb);
                        }
                    }

                    if (PrefabUtility.IsPartOfPrefabInstance(root))
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(root.transform);
                        if (origin != null)
                        {
                            PrefabUtility.RecordPrefabInstancePropertyModifications(origin);
                        }
                        if (cameraOffset != null)
                        {
                            PrefabUtility.RecordPrefabInstancePropertyModifications(cameraOffset);
                        }
                        PrefabUtility.RecordPrefabInstancePropertyModifications(cc);
                    }

                    sceneModified = true;
                }
            }

            // Ensure SonicTopographyManager exists in scene
            SonicTopographyVisualizer visualizer = UnityEngine.Object.FindAnyObjectByType<SonicTopographyVisualizer>();
            GameObject managerGo;
            if (visualizer == null)
            {
                managerGo = new GameObject("SonicTopographyManager");
                Undo.RegisterCreatedObjectUndo(managerGo, "Create SonicTopographyManager");
                managerGo.AddComponent<SonicTopographyAudioEngine>();
                visualizer = managerGo.AddComponent<SonicTopographyVisualizer>();
                managerGo.AddComponent<SonicTopographyVRBootstrap>();
                sceneModified = true;
            }
            else
            {
                managerGo = visualizer.gameObject;
            }

            SonicTopographyAudioEngine audioEngine = managerGo.GetComponent<SonicTopographyAudioEngine>();
            if (audioEngine != null)
            {
                if (audioEngine.autoPlayOnBoot)
                {
                    audioEngine.autoPlayOnBoot = false;
                    EditorUtility.SetDirty(audioEngine);
                    sceneModified = true;
                }
                AudioSource src = managerGo.GetComponent<AudioSource>();
                if (src == null) src = managerGo.AddComponent<AudioSource>();
                if (src.clip != null || src.playOnAwake)
                {
                    src.clip = null;
                    src.playOnAwake = false;
                    src.loop = false;
                    src.spatialBlend = 0f;
                    audioEngine.audioSource = src;
                    sceneModified = true;
                }
            }

            if (visualizer != null && visualizer.platterWorldPosition.z < 30f)
            {
                visualizer.platterWorldPosition = new Vector3(0f, -26f, 64f);
                EditorUtility.SetDirty(visualizer);
                sceneModified = true;
            }

            SonicTopographyVRBootstrap bootstrap = managerGo.GetComponent<SonicTopographyVRBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = managerGo.AddComponent<SonicTopographyVRBootstrap>();
                sceneModified = true;
            }
            if (bootstrap.glassCubeSize.x > 4.5f || bootstrap.glassEdgeColor.a > 0.25f)
            {
                bootstrap.glassCubeSize = new Vector3(3.4f, 0.12f, 3.4f);
                bootstrap.glassTintColor = new Color(0.01f, 0.015f, 0.025f, 0.025f);
                bootstrap.glassEdgeColor = new Color(0.24f, 0.30f, 0.38f, 0.09f);
                EditorUtility.SetDirty(bootstrap);
                sceneModified = true;
            }
            if (!bootstrap.enableAnimatedSplashScreen)
            {
                bootstrap.enableAnimatedSplashScreen = true;
                EditorUtility.SetDirty(bootstrap);
                sceneModified = true;
            }

            if (managerGo.GetComponent<SonicMusicLibraryManager>() == null)
            {
                managerGo.AddComponent<SonicMusicLibraryManager>();
                sceneModified = true;
            }

            if (managerGo.GetComponent<SonicMusicPlayerController>() == null)
            {
                managerGo.AddComponent<SonicMusicPlayerController>();
                sceneModified = true;
            }

            if (managerGo.GetComponent<SonicSpatialUIManager>() == null)
            {
                managerGo.AddComponent<SonicSpatialUIManager>();
                sceneModified = true;
            }

            if (managerGo.GetComponent<AudioFloorControllerActions>() == null)
            {
                managerGo.AddComponent<AudioFloorControllerActions>();
                sceneModified = true;
            }

            var xrInputModule = UnityEngine.Object.FindAnyObjectByType<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
            if (xrInputModule != null)
            {
                xrInputModule.enableXRInput = true;
                xrInputModule.enableMouseInput = true;
                xrInputModule.enableTouchInput = false;
                EditorUtility.SetDirty(xrInputModule);
                sceneModified = true;
            }

            // 1. Configure GlassStageCube as the permanent static floor directly in SampleScene.unity
            GameObject glassCube = GameObject.Find("GlassStageCube");
            if (glassCube == null)
            {
                glassCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                glassCube.name = "GlassStageCube";
                sceneModified = true;
            }

            glassCube.transform.position = new Vector3(0f, -0.06f, 0f);
            glassCube.transform.rotation = Quaternion.identity;
            glassCube.transform.localScale = new Vector3(3.4f, 0.12f, 3.4f);
            glassCube.isStatic = true;
            GameObjectUtility.SetStaticEditorFlags(glassCube, (StaticEditorFlags)~0);

            BoxCollider box = glassCube.GetComponent<BoxCollider>();
            if (box != null)
            {
                box.center = Vector3.zero;
                box.size = Vector3.one;
                box.isTrigger = false;
                box.enabled = true;
                EditorUtility.SetDirty(box);
            }

            TeleportationArea teleportArea = glassCube.GetComponent<TeleportationArea>();
            if (teleportArea == null)
            {
                teleportArea = glassCube.AddComponent<TeleportationArea>();
                sceneModified = true;
            }
            teleportArea.enabled = true;
            int teleportLayer = UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask.GetMask("Teleport");
            if (teleportLayer == 0) teleportLayer = 1 << 31;
            teleportArea.interactionLayers = teleportLayer;
            teleportArea.matchOrientation = MatchOrientation.WorldSpaceUp;
            teleportArea.teleportTrigger = BaseTeleportationInteractable.TeleportTrigger.OnSelectExited;
            if (box != null && !teleportArea.colliders.Contains(box))
            {
                teleportArea.colliders.Add(box);
            }
            EditorUtility.SetDirty(teleportArea);

            // Persist a material asset for GlassStageCube so it renders as glass
            Shader glassShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/SonicTopography/Resources/Shaders/SonicGlassStage.shader");
            if (glassShader != null)
            {
                const string matDir = "Assets/SonicTopography/Resources/Materials";
                const string matPath = matDir + "/GlassStageMaterial.mat";
                if (!AssetDatabase.IsValidFolder(matDir))
                {
                    AssetDatabase.CreateFolder("Assets/SonicTopography/Resources", "Materials");
                }
                Material glassMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (glassMat == null)
                {
                    glassMat = new Material(glassShader);
                    glassMat.SetColor("_TintColor", bootstrap.glassTintColor);
                    glassMat.SetColor("_EdgeColor", bootstrap.glassEdgeColor);
                    glassMat.SetFloat("_GridCrossThickness", 0.0f);
                    AssetDatabase.CreateAsset(glassMat, matPath);
                }
                else
                {
                    glassMat.SetColor("_TintColor", bootstrap.glassTintColor);
                    glassMat.SetColor("_EdgeColor", bootstrap.glassEdgeColor);
                    glassMat.SetFloat("_GridCrossThickness", 0.0f);
                    EditorUtility.SetDirty(glassMat);
                }
                MeshRenderer mr = glassCube.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    mr.sharedMaterial = glassMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    EditorUtility.SetDirty(mr);
                }
            }
            EditorUtility.SetDirty(glassCube.transform);
            EditorUtility.SetDirty(glassCube);

            // 2. Permanently disable unused template objects directly in SampleScene.unity
            string[] templateNames = new string[] { "Plane", "Environment", "Interactables", "UI", "Lighting", "Directional Light", "Teleport Area Setup" };
            foreach (string tName in templateNames)
            {
                GameObject tGo = GameObject.Find(tName);
                if (tGo != null)
                {
                    tGo.SetActive(false);
                    EditorUtility.SetDirty(tGo);
                    sceneModified = true;
                }
            }

            // Clear VR template skybox & baked lightmaps
            if (RenderSettings.skybox != null || RenderSettings.fog ||
                (LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0) ||
                LightmapSettings.lightmapsMode != LightmapsMode.NonDirectional)
            {
                RenderSettings.skybox = null;
                RenderSettings.fog = false;
                LightmapSettings.lightmaps = new LightmapData[0];
                LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
                sceneModified = true;
            }

            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = Color.black;
                mainCam.cullingMask = -1; // Normal culling mask (all layers visible)
                mainCam.nearClipPlane = 0.01f;
                mainCam.farClipPlane = 350f;
                EditorUtility.SetDirty(mainCam);
            }

            EnsureControllerPrefabsAndVisuals();
            sceneModified = true;

            if (sceneModified)
            {
                EditorSceneManager.MarkSceneDirty(activeScene);
                EditorSceneManager.SaveScene(activeScene);
                Debug.Log("[Sonic Topography] Removed VR Template leftovers and saved clean Sonic Topography VR scene.");
            }
        }

        public static void EnsureControllerPrefabsAndVisuals()
        {
            const string prefabDir = "Assets/SonicTopography/Resources/Controllers";
            if (!Directory.Exists(prefabDir))
            {
                Directory.CreateDirectory(prefabDir);
                AssetDatabase.Refresh();
            }

            EnsureSingleControllerPrefab(prefabDir, true);
            EnsureSingleControllerPrefab(prefabDir, false);

            CleanAndRecreateControllerVisualsInScene();
        }

        public static void CleanAndRecreateControllerVisualsInScene()
        {
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

            // Remove any leftover controller placeholder meshes or caps under XRI's Left Controller / Right Controller or Camera Offset
            foreach (var renderer in cameraOffset.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                string n = renderer.gameObject.name;
                Transform p = renderer.transform.parent;
                string pn = p != null ? p.name : "";

                if (n.Contains("UniversalController") || pn.Contains("UniversalController") ||
                    n.Contains("Controller_Base") || n == "ThumbStick" || n == "ThumbStick_Base" ||
                    n == "TouchPad" || n == "Button_A" || n == "Button_B" || n == "Button_Home" ||
                    (n == "Trigger" && p != null && p.name.Contains("Controller")) ||
                    (n == "Bumper" && p != null && p.name.Contains("Controller")) ||
                    n == "Quest3ButtonCap")
                {
                    Undo.DestroyObjectImmediate(renderer.gameObject);
                }
            }

            // Completely delete existing Left and Right Controller Grip Anchors to clear any stale scene overrides
            List<GameObject> existingAnchors = new List<GameObject>();
            foreach (Transform child in cameraOffset)
            {
                if (child != null && (child.name == "Left Controller Grip Anchor" || child.name == "Right Controller Grip Anchor"))
                {
                    existingAnchors.Add(child.gameObject);
                }
            }
            for (int i = 0; i < existingAnchors.Count; i++)
            {
                Undo.DestroyObjectImmediate(existingAnchors[i]);
            }

            // Cleanly recreate both anchors symmetrically
            RecreateEditorGripAnchor(cameraOffset, SonicMetaQuestController.ControllerHand.Left, "Left Controller Grip Anchor");
            RecreateEditorGripAnchor(cameraOffset, SonicMetaQuestController.ControllerHand.Right, "Right Controller Grip Anchor");
        }

        private static void RecreateEditorGripAnchor(Transform cameraOffset, SonicMetaQuestController.ControllerHand hand, string anchorName)
        {
            GameObject anchorGo = new GameObject(anchorName);
            Undo.RegisterCreatedObjectUndo(anchorGo, $"Create {anchorName}");
            anchorGo.transform.SetParent(cameraOffset, false);
            anchorGo.transform.localPosition = Vector3.zero;
            anchorGo.transform.localRotation = Quaternion.identity;
            anchorGo.transform.localScale = Vector3.one;

            SonicMetaQuestController ctrl = anchorGo.AddComponent<SonicMetaQuestController>();

            string containerName = hand == SonicMetaQuestController.ControllerHand.Right ? "RightController_Container" : "LeftController_Container";
            GameObject containerGo = new GameObject(containerName);
            Undo.RegisterCreatedObjectUndo(containerGo, $"Create {containerName}");
            containerGo.transform.SetParent(anchorGo.transform, false);
            containerGo.transform.localPosition = Vector3.zero;
            containerGo.transform.localRotation = Quaternion.identity;
            containerGo.transform.localScale = Vector3.one;

            string modelName = hand == SonicMetaQuestController.ControllerHand.Right ? "MetaQuestTouchPlus_Right" : "MetaQuestTouchPlus_Left";
            string fbxPath = hand == SonicMetaQuestController.ControllerHand.Right
                ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Right.fbx"
                : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx != null)
            {
                GameObject modelGo = (GameObject)PrefabUtility.InstantiatePrefab(fbx, containerGo.transform);
                Undo.RegisterCreatedObjectUndo(modelGo, $"Create {modelName}");
                modelGo.name = modelName;
                modelGo.transform.localPosition = Vector3.zero;
                modelGo.transform.localRotation = Quaternion.identity;
                modelGo.transform.localScale = Vector3.one;

                string animPath = hand == SonicMetaQuestController.ControllerHand.Right
                    ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusRightAnimation.controller"
                    : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusLeftAnimation.controller";

                var animController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(animPath);
                Animator anim = modelGo.GetComponent<Animator>();
                if (anim != null && animController != null)
                {
                    anim.runtimeAnimatorController = animController;
                }

                string matPath = hand == SonicMetaQuestController.ControllerHand.Right
                    ? "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Right_URP.mat"
                    : "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Left_URP.mat";

                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                SkinnedMeshRenderer smr = SonicMetaQuestController.FindMainControllerMeshRenderer(modelGo);
                if (smr != null && mat != null)
                {
                    smr.sharedMaterial = mat;
                }

                modelGo.SetActive(false);
                ctrl.Configure(hand, containerGo.transform, modelGo, anim, smr);
                EditorUtility.SetDirty(ctrl);
            }
        }

        private static void EnsureSingleControllerPrefab(string prefabDir, bool rightHand)
        {
            string name = rightHand ? "MetaQuestTouchPlus_Right" : "MetaQuestTouchPlus_Left";
            string assetPath = $"{prefabDir}/{name}.prefab";
            if (File.Exists(assetPath)) return;

            string fbxPath = rightHand
                ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Right.fbx"
                : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/MetaQuestTouchPlus_Left.fbx";

            GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null) return;

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            instance.name = name;
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            string animPath = rightHand
                ? "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusRightAnimation.controller"
                : "Packages/com.meta.xr.sdk.core/Meshes/MetaQuestTouchPlus/Animation/MetaQuestTouchPlusLeftAnimation.controller";
            var animCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(animPath);
            Animator anim = instance.GetComponent<Animator>();
            if (anim != null && animCtrl != null)
            {
                anim.runtimeAnimatorController = animCtrl;
            }

            string matPath = rightHand
                ? "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Right_URP.mat"
                : "Assets/SonicTopography/Resources/Controllers/MetaQuestTouchPlus_Left_URP.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            SkinnedMeshRenderer smr = SonicMetaQuestController.FindMainControllerMeshRenderer(instance);
            if (smr != null && mat != null)
            {
                smr.sharedMaterial = mat;
            }

            PrefabUtility.SaveAsPrefabAsset(instance, assetPath);
            UnityEngine.Object.DestroyImmediate(instance);
            Debug.Log($"[Sonic Topography] Created official controller prefab at {assetPath}");
        }
    }
}
#endif
