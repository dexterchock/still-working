#if UNITY_EDITOR
using System;
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SonicTopography.Editor
{
    [InitializeOnLoad]
    public class SonicQuestBuildTools : IPostGenerateGradleAndroidProject
    {
        private const string TriggerFilePath = "Temp/TriggerQuestBuild.txt";
        private const string StatusFilePath = "Temp/QuestBuildStatus.txt";
        private const string OutputApkRelativePath = "Builds/AudioFloor.apk";

        public int callbackOrder => 100;

        static SonicQuestBuildTools()
        {
            EditorApplication.delayCall += EnsureAppIconAssigned;
            EditorApplication.delayCall += CheckAutomatedBuildTrigger;
            EditorApplication.update += CheckAutomatedBuildTrigger;
        }

        public static void EnsureAppIconAssigned()
        {
            Texture2D icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/SonicTopography/Textures/AppIcon_1024.png");
            if (icon == null)
            {
                icon = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/SonicTopography/Textures/AppIcon_512.png");
            }
            if (icon == null) return;

            AssignIconForTarget(NamedBuildTarget.Unknown, icon);
            AssignIconForTarget(NamedBuildTarget.Android, icon);
            AssignIconForTarget(NamedBuildTarget.Standalone, icon);
        }

        private static void AssignIconForTarget(NamedBuildTarget target, Texture2D icon)
        {
            try
            {
                int[] sizes = PlayerSettings.GetIconSizes(target, IconKind.Application);
                if (sizes == null || sizes.Length == 0)
                {
                    PlayerSettings.SetIcons(target, new[] { icon }, IconKind.Application);
                    return;
                }

                Texture2D[] icons = new Texture2D[sizes.Length];
                for (int i = 0; i < sizes.Length; i++)
                {
                    icons[i] = icon;
                }
                PlayerSettings.SetIcons(target, icons, IconKind.Application);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicQuestBuild] Could not set icons for target {target.TargetName}: {ex.Message}");
            }
        }

        private static void CheckAutomatedBuildTrigger()
        {
            if (!File.Exists(TriggerFilePath)) return;

            try
            {
                File.Delete(TriggerFilePath);
            }
            catch
            {
                // Ignore delete error
            }

            BuildQuest3Apk();
        }

        [MenuItem("AudioFloor/Configure Project for Meta Quest 3", priority = 20)]
        public static void ConfigureQuest3PlayerSettings()
        {
            PlayerSettings.companyName = "Dexter Chock";
            PlayerSettings.productName = "AudioFloor";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.Dexter.AudioFloor");
            PlayerSettings.colorSpace = ColorSpace.Linear;

            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)34;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.forceSDCardPermission = false;
            PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.Auto;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity;

            // Production Keystore for Meta Horizon Store
            string keystoreDir = Path.Combine(Directory.GetCurrentDirectory(), "Keystore");
            string keystorePath = Path.Combine(keystoreDir, "audiofloor_release.keystore");
            string keystorePassPath = Path.Combine(keystoreDir, "keystore.pass");
            string keystorePass = Environment.GetEnvironmentVariable("AUDIOFLOOR_KEYSTORE_PASS");
            if (string.IsNullOrEmpty(keystorePass) && File.Exists(keystorePassPath))
            {
                keystorePass = File.ReadAllText(keystorePassPath).Trim();
            }
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystorePath;
            PlayerSettings.Android.keystorePass = keystorePass ?? string.Empty;
            PlayerSettings.Android.keyaliasName = "audiofloor";
            PlayerSettings.Android.keyaliasPass = keystorePass ?? string.Empty;

            // Target Quest 3 and Quest 3S only
            OVRProjectConfig projectConfig = OVRProjectConfig.CachedProjectConfig;
            if (projectConfig != null)
            {
                projectConfig.targetDeviceTypes.Clear();
                projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3);
                projectConfig.targetDeviceTypes.Add(OVRProjectConfig.DeviceType.Quest3S);
                projectConfig.sceneSupport = OVRProjectConfig.FeatureSupport.Supported;
                projectConfig.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled;
                projectConfig.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
                projectConfig.isPassthroughCameraAccessEnabled = true;
                projectConfig.boundaryVisibilitySupport = OVRProjectConfig.FeatureSupport.Supported;
                EditorUtility.SetDirty(projectConfig);
            }

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
            EditorUserBuildSettings.buildAppBundle = false;

            string[] urpPaths = new[]
            {
                "Assets/Settings/Project Configuration/Performance URP Config.asset",
                "Assets/Settings/Project Configuration/Quality URP Config.asset"
            };
            foreach (string urpPath in urpPaths)
            {
                var urpAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>(urpPath);
                if (urpAsset != null)
                {
                    urpAsset.renderScale = 1.5f;
                    urpAsset.supportsCameraOpaqueTexture = false;
                    urpAsset.supportsCameraDepthTexture = false;
                    urpAsset.supportsHDR = false;
                    EditorUtility.SetDirty(urpAsset);
                }
            }

            EnsureResourceShaderMaterialsExist();
            EnsureAppIconAssigned();
            AssetDatabase.SaveAssets();
            Debug.Log("[SonicQuestBuild] Configured Android PlayerSettings (1.5x URP renderScale) for standalone Meta Quest 3.");
        }

        private static void EnsureResourceShaderMaterialsExist()
        {
            const string matDir = "Assets/SonicTopography/Resources/Materials";
            if (!Directory.Exists(matDir))
            {
                Directory.CreateDirectory(matDir);
            }

            CreateOrUpdateMaterialAsset(matDir, "SonicTerrainPillars", "Shaders/SonicTerrainPillars", "SonicTopography/TerrainPillars");
            CreateOrUpdateMaterialAsset(matDir, "SonicFloatingBlocks", "Shaders/SonicFloatingBlocks", "SonicTopography/FloatingBlocks");
            CreateOrUpdateMaterialAsset(matDir, "SonicSimpleInstanced", "Shaders/SonicSimpleInstanced", "SonicTopography/SimpleInstanced");
            CreateOrUpdateMaterialAsset(matDir, "SonicSolidSkybox", "Shaders/SonicSolidSkybox", "SonicTopography/SolidSkybox");
            CreateOrUpdateMaterialAsset(matDir, "SonicGlassStage", "Shaders/SonicGlassStage", "SonicTopography/GlassStage");
            CreateOrUpdateMaterialAsset(matDir, "SonicFrostedGlassUI", "Shaders/SonicFrostedGlassUI", "SonicTopography/FrostedGlassUI");
            CreateOrUpdateMaterialAsset(matDir, "SonicStarRiver", "Shaders/SonicStarRiver", "SonicTopography/StarRiver");
            CreateOrUpdateMaterialAsset(matDir, "SonicAmbientStarfield", "Shaders/SonicAmbientStarfield", "SonicTopography/AmbientStarfield");
            CreateOrUpdateMaterialAsset(matDir, "SonicBrowserUnderlayPunchout", "Shaders/SonicBrowserUnderlayPunchout", "SonicTopography/BrowserUnderlayPunchout");
            CreateOrUpdateMaterialAsset(matDir, "SonicRoomLightWash", "Shaders/SonicRoomLightWash", "SonicTopography/RoomLightWash");
            CreateOrUpdateMaterialAsset(matDir, "SonicArVoidDissolveDome", "Shaders/SonicArVoidDissolveDome", "SonicTopography/ArVoidDissolveDome");
            CreateOrUpdateMaterialAsset(matDir, "SonicTerrainEmissionBake", "Shaders/SonicTerrainEmissionBake", "SonicTopography/TerrainEmissionBake");
        }

        private static void CreateOrUpdateMaterialAsset(string matDir, string name, string resPath, string shaderName)
        {
            Shader s = Resources.Load<Shader>(resPath);
            if (s == null) s = Shader.Find(shaderName);
            if (s == null) return;

            string assetPath = $"{matDir}/{name}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing == null)
            {
                Material m = new Material(s) { enableInstancing = true };
                AssetDatabase.CreateAsset(m, assetPath);
            }
            else
            {
                existing.shader = s;
                existing.enableInstancing = true;
                EditorUtility.SetDirty(existing);
            }
        }

        [MenuItem("AudioFloor/Build Quest 3 APK", priority = 21)]
        public static void BuildQuest3Apk()
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                Debug.Log("[SonicQuestBuild] Build already in progress, skipping duplicate request.");
                return;
            }

            try
            {
                WriteStatus("STARTING: Configuring Meta Quest 3 settings...");
                if (EditorApplication.isPlaying)
                {
                    EditorApplication.isPlaying = false;
                }

                if (UnityEngine.Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>() == null)
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
                }

                SonicTopographyEditorSetup.SetupActiveScene();
                ConfigureQuest3PlayerSettings();

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    WriteStatus("SWITCHING_TARGET: Switching active build target to Android...");
                    bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                    if (!switched)
                    {
                        WriteStatus("FAILED: Could not switch active build target to Android.");
                        return;
                    }
                }

                string buildsDir = Path.Combine(Directory.GetCurrentDirectory(), "Builds");
                if (!Directory.Exists(buildsDir))
                {
                    Directory.CreateDirectory(buildsDir);
                }

                string fullApkPath = Path.Combine(Directory.GetCurrentDirectory(), OutputApkRelativePath);

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                    locationPathName = fullApkPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None
                };

                WriteStatus($"BUILDING: Building {fullApkPath}...");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    WriteStatus($"SUCCEEDED: {fullApkPath} ({summary.totalSize} bytes, {summary.totalTime.TotalSeconds:F1}s)");
                    Debug.Log($"[SonicQuestBuild] Build succeeded: {fullApkPath} ({summary.totalSize / (1024f * 1024f):F2} MB)");
                }
                else
                {
                    WriteStatus($"FAILED: Result={summary.result} Errors={summary.totalErrors}");
                    Debug.LogError($"[SonicQuestBuild] Build failed: {summary.result} ({summary.totalErrors} errors)");
                }
            }
            catch (Exception ex)
            {
                WriteStatus($"EXCEPTION: {ex}");
                Debug.LogException(ex);
            }
        }

        private static void WriteStatus(string message)
        {
            try
            {
                Directory.CreateDirectory("Temp");
                File.WriteAllText(StatusFilePath, $"{DateTime.Now:O} | {message}");
            }
            catch
            {
                // Ignore status write error
            }
        }

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            try
            {
                string gradleRoot = Directory.Exists(Path.Combine(path, "launcher")) ? path : Directory.GetParent(path)?.FullName;
                if (!string.IsNullOrEmpty(gradleRoot))
                {
                    // 1. Add Mozilla Maven repository in settings.gradle dependencyResolutionManagement
                    string settingsGradle = Path.Combine(gradleRoot, "settings.gradle");
                    if (File.Exists(settingsGradle))
                    {
                        string settingsText = File.ReadAllText(settingsGradle);
                        if (!settingsText.Contains("maven.mozilla.org/maven2"))
                        {
                            int drmIdx = settingsText.IndexOf("dependencyResolutionManagement", StringComparison.Ordinal);
                            if (drmIdx >= 0)
                            {
                                int mcIdx = settingsText.IndexOf("mavenCentral()", drmIdx, StringComparison.Ordinal);
                                if (mcIdx >= 0)
                                {
                                    int insertPos = mcIdx + "mavenCentral()".Length;
                                    string nl = settingsText.Contains("\r\n") ? "\r\n" : "\n";
                                    settingsText = settingsText.Insert(insertPos, $"{nl}        maven {{ url \"https://maven.mozilla.org/maven2/\" }}");
                                    File.WriteAllText(settingsGradle, settingsText);
                                    Debug.Log("[SonicQuestBuild] Added Mozilla Maven repo to settings.gradle");
                                }
                            }
                        }
                    }

                    // 2. Add GeckoView (arm64-v8a only) dependency to unityLibrary/build.gradle
                    string unityLibGradle = Path.Combine(gradleRoot, "unityLibrary", "build.gradle");
                    if (File.Exists(unityLibGradle))
                    {
                        string libText = File.ReadAllText(unityLibGradle);
                        const string geckoDep = "implementation 'org.mozilla.geckoview:geckoview-arm64-v8a:133.0.20241209150345'";
                        if (!libText.Contains("org.mozilla.geckoview:geckoview-arm64-v8a"))
                        {
                            const string anchor = "implementation 'androidx.games:games-activity:4.4.0'";
                            int anchorIdx = libText.IndexOf(anchor, StringComparison.Ordinal);
                            string nl = libText.Contains("\r\n") ? "\r\n" : "\n";
                            if (anchorIdx >= 0)
                            {
                                libText = libText.Insert(anchorIdx + anchor.Length, $"{nl}    {geckoDep}");
                            }
                            else
                            {
                                int depsIdx = libText.IndexOf("dependencies {", StringComparison.Ordinal);
                                if (depsIdx >= 0)
                                {
                                    libText = libText.Insert(depsIdx + "dependencies {".Length, $"{nl}    {geckoDep}");
                                }
                            }
                            File.WriteAllText(unityLibGradle, libText);
                            Debug.Log("[SonicQuestBuild] Added GeckoView arm64-v8a dependency to unityLibrary/build.gradle");
                        }
                    }

                    // 3. Patch launcher/build.gradle to exclude 32-bit legacy assets
                    string launcherGradle = Path.Combine(gradleRoot, "launcher", "build.gradle");
                    if (File.Exists(launcherGradle))
                    {
                        string text = File.ReadAllText(launcherGradle);
                        if (!text.Contains(":armeabi-v7a"))
                        {
                            text = text.Replace("ignoreAssetsPattern = \"!.svn:!.git:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~\"",
                                                "ignoreAssetsPattern = \"!.svn:!.git:!.ds_store:!*.scc:!CVS:!thumbs.db:!picasa.ini:!*~:armeabi-v7a\"");
                            File.WriteAllText(launcherGradle, text);
                            Debug.Log("[SonicQuestBuild] Filtered armeabi-v7a from ignoreAssetsPattern in launcher/build.gradle");
                        }
                    }

                    // 4. Ensure launcher manifest strips sensitive permissions/features pulled by transitive AARs
                    string launcherManifest = Path.Combine(gradleRoot, "launcher", "src", "main", "AndroidManifest.xml");
                    if (File.Exists(launcherManifest))
                    {
                        XmlDocument launcherDoc = new XmlDocument();
                        launcherDoc.Load(launcherManifest);
                        const string androidNs = "http://schemas.android.com/apk/res/android";
                        const string toolsNs = "http://schemas.android.com/tools";
                        XmlElement launcherRoot = launcherDoc.DocumentElement;
                        if (launcherRoot != null)
                        {
                            if (string.IsNullOrEmpty(launcherRoot.GetAttribute("xmlns:tools")))
                            {
                                launcherRoot.SetAttribute("xmlns:tools", toolsNs);
                            }
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.RECORD_AUDIO");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.CAMERA");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.ACCESS_FINE_LOCATION");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.ACCESS_COARSE_LOCATION");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.MODIFY_AUDIO_SETTINGS");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "android.permission.HIGH_SAMPLING_RATE_SENSORS");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-permission", androidNs, toolsNs, "com.oculus.permission.EYE_TRACKING");
                            EnsureRemovedManifestEntry(launcherDoc, launcherRoot, "uses-feature", androidNs, toolsNs, "oculus.software.eye_tracking");
                            launcherDoc.Save(launcherManifest);
                            Debug.Log($"[SonicQuestBuild] Enforced tools:node=remove rules on launcher manifest: {launcherManifest}");
                        }
                    }
                }

                var manifestFiles = Directory.GetFiles(path, "AndroidManifest.xml", SearchOption.AllDirectories);
                foreach (string manifestPath in manifestFiles)
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(manifestPath);

                    const string androidNs = "http://schemas.android.com/apk/res/android";
                    const string toolsNs = "http://schemas.android.com/tools";
                    XmlElement manifestRoot = doc.DocumentElement;
                    if (manifestRoot == null) continue;

                    EnsurePermission(doc, manifestRoot, androidNs, "android.permission.READ_MEDIA_AUDIO");
                    EnsurePermission(doc, manifestRoot, androidNs, "android.permission.READ_EXTERNAL_STORAGE");
                    EnsurePermission(doc, manifestRoot, androidNs, "android.permission.INTERNET");
                    EnsurePermission(doc, manifestRoot, androidNs, "android.permission.ACCESS_NETWORK_STATE");
                    EnsurePermission(doc, manifestRoot, androidNs, "com.oculus.permission.USE_SCENE");
                    EnsurePermission(doc, manifestRoot, androidNs, "com.oculus.permission.USE_ANCHOR_API");
                    EnsurePermission(doc, manifestRoot, androidNs, "com.oculus.permission.BOUNDARY_VISIBILITY");
                    EnsurePermission(doc, manifestRoot, androidNs, "horizonos.permission.HEADSET_CAMERA");
                    EnsureOptionalFeature(doc, manifestRoot, androidNs, "com.oculus.feature.PASSTHROUGH");

                    XmlNode appNode = manifestRoot.SelectSingleNode("application");
                    if (appNode is XmlElement appElem)
                    {
                        appElem.SetAttribute("requestLegacyExternalStorage", androidNs, "true");
                    }

                    // Remove any non-removal eye-tracking or sensitive permission entries (preserving explicit tools:node="remove" directives)
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "com.oculus.permission.EYE_TRACKING");
                    RemoveManifestEntry(manifestRoot, "uses-feature", androidNs, toolsNs, "name", "oculus.software.eye_tracking");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.RECORD_AUDIO");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.CAMERA");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.ACCESS_FINE_LOCATION");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.ACCESS_COARSE_LOCATION");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.MODIFY_AUDIO_SETTINGS");
                    RemoveManifestEntry(manifestRoot, "uses-permission", androidNs, toolsNs, "name", "android.permission.HIGH_SAMPLING_RATE_SENSORS");

                    // Ensure target devices are strictly quest3|quest3s
                    SetOrAddMetaData(doc, appNode as XmlElement, androidNs, "com.oculus.supportedDevices", "quest3|quest3s");
                    SetOrAddMetaData(doc, appNode as XmlElement, androidNs, "com.meta.store.defaultDeviceTargets", "quest3+");

                    // UnityPlayerGameActivity extends AppCompatActivity and crashes at launch unless its theme inherits Theme.AppCompat
                    EnsureGameActivityThemeAndMetaData(doc, appNode as XmlElement, androidNs);

                    doc.Save(manifestPath);
                    Debug.Log($"[SonicQuestBuild] Processed manifest: {manifestPath}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicQuestBuild] Could not update AndroidManifest.xml: {e.Message}");
            }
        }

        private static void EnsureGameActivityThemeAndMetaData(XmlDocument doc, XmlElement appElem, string ns)
        {
            if (appElem == null) return;
            XmlNodeList activities = appElem.SelectNodes("activity");
            if (activities == null) return;

            foreach (XmlNode node in activities)
            {
                if (!(node is XmlElement activityEl)) continue;
                string actName = activityEl.GetAttribute("name", ns);
                if (string.IsNullOrEmpty(actName)) actName = activityEl.GetAttribute("android:name");
                if (actName != "com.unity3d.player.UnityPlayerGameActivity") continue;

                string theme = activityEl.GetAttribute("theme", ns);
                if (string.IsNullOrEmpty(theme) ||
                    theme == "@style/UnityThemeSelector" ||
                    theme == "@android:style/Theme.Black.NoTitleBar.Fullscreen")
                {
                    activityEl.SetAttribute("theme", ns, "@style/BaseUnityGameActivityTheme");
                }

                SetOrAddMetaData(doc, activityEl, ns, "unityplayer.UnityActivity", "true");
                SetOrAddMetaData(doc, activityEl, ns, "android.app.lib_name", "game");
            }
        }

        private static void SetOrAddMetaData(XmlDocument doc, XmlElement appElem, string ns, string name, string value)
        {
            if (appElem == null) return;
            XmlNodeList metaList = appElem.SelectNodes("meta-data");
            if (metaList != null)
            {
                foreach (XmlNode node in metaList)
                {
                    if (node is XmlElement el)
                    {
                        string n = el.GetAttribute("name", ns);
                        if (string.IsNullOrEmpty(n)) n = el.GetAttribute("android:name");
                        if (n == name)
                        {
                            el.SetAttribute("value", ns, value);
                            return;
                        }
                    }
                }
            }
            XmlElement newEl = doc.CreateElement("meta-data");
            newEl.SetAttribute("name", ns, name);
            newEl.SetAttribute("value", ns, value);
            appElem.AppendChild(newEl);
        }

        private static void RemoveManifestEntry(XmlElement root, string tagName, string ns, string toolsNs, string attrName, string attrValue)
        {
            var nodesToRemove = new System.Collections.Generic.List<XmlNode>();
            XmlNodeList list = root.SelectNodes(tagName);
            if (list != null)
            {
                foreach (XmlNode child in list)
                {
                    if (child is XmlElement el)
                    {
                        string toolsNode = el.GetAttribute("node", toolsNs);
                        if (string.IsNullOrEmpty(toolsNode)) toolsNode = el.GetAttribute("tools:node");
                        if (string.Equals(toolsNode, "remove", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string val = el.GetAttribute(attrName, ns);
                        if (string.IsNullOrEmpty(val)) val = el.GetAttribute($"android:{attrName}");
                        if (val == attrValue)
                        {
                            nodesToRemove.Add(child);
                        }
                    }
                }
            }
            foreach (var node in nodesToRemove)
            {
                root.RemoveChild(node);
            }
        }

        private static void EnsureRemovedManifestEntry(XmlDocument doc, XmlElement manifestRoot, string tagName, string androidNs, string toolsNs, string nameValue)
        {
            XmlNodeList list = manifestRoot.SelectNodes(tagName);
            if (list != null)
            {
                foreach (XmlNode node in list)
                {
                    if (node is XmlElement el)
                    {
                        string val = el.GetAttribute("name", androidNs);
                        if (string.IsNullOrEmpty(val)) val = el.GetAttribute("android:name");
                        if (val == nameValue)
                        {
                            el.SetAttribute("node", toolsNs, "remove");
                            return;
                        }
                    }
                }
            }

            XmlElement remElem = doc.CreateElement(tagName);
            remElem.SetAttribute("name", androidNs, nameValue);
            remElem.SetAttribute("node", toolsNs, "remove");
            manifestRoot.AppendChild(remElem);
        }

        private static void EnsurePermission(XmlDocument doc, XmlElement manifestRoot, string androidNs, string permName)
        {
            XmlNodeList perms = manifestRoot.SelectNodes("uses-permission");
            if (perms != null)
            {
                foreach (XmlNode node in perms)
                {
                    if (node is XmlElement el && el.GetAttribute("name", androidNs) == permName)
                    {
                        return;
                    }
                }
            }

            XmlElement permElem = doc.CreateElement("uses-permission");
            permElem.SetAttribute("name", androidNs, permName);
            manifestRoot.AppendChild(permElem);
        }

        private static void EnsureOptionalFeature(XmlDocument doc, XmlElement manifestRoot, string androidNs, string featureName)
        {
            XmlNodeList features = manifestRoot.SelectNodes("uses-feature");
            if (features != null)
            {
                foreach (XmlNode node in features)
                {
                    if (node is XmlElement el && el.GetAttribute("name", androidNs) == featureName)
                    {
                        el.SetAttribute("required", androidNs, "false");
                        return;
                    }
                }
            }

            XmlElement featElem = doc.CreateElement("uses-feature");
            featElem.SetAttribute("name", androidNs, featureName);
            featElem.SetAttribute("required", androidNs, "false");
            manifestRoot.AppendChild(featElem);
        }
    }
}
#endif
