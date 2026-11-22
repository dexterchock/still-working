using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace SonicTopography
{
    [Serializable]
    public class SonicMusicTrack
    {
        public string id;
        public string title;
        public string artist;
        public string album;
        public string folderName;
        public string filePath;
        public float durationSeconds;
        public AudioClip cachedClip;
        public Texture2D coverTexture;
        public Sprite coverSprite;
        public bool hasRealAlbumArt;
        public SonicThemeColors extractedPalette;

        public string DisplayDuration
        {
            get
            {
                float d = durationSeconds > 0.1f
                    ? durationSeconds
                    : (cachedClip != null ? cachedClip.length : 0f);
                if (d <= 0.1f) return "--:--";
                int totalSec = Mathf.FloorToInt(d);
                int mins = totalSec / 60;
                int secs = totalSec % 60;
                return $"{mins}:{secs:00}";
            }
        }
    }

    public struct SonicBrowserEntry
    {
        public string name;
        public string fullPath;
        public bool isDirectory;
        public long fileSizeBytes;
        public string extension;
    }

    public struct SonicStorageRoot
    {
        public string label;
        public string path;
    }

    public class SonicMusicLibraryManager : MonoBehaviour
    {
        public const string PrefScanFolderKey = "SonicTopography_ScanFolder";

        public static readonly string[] SupportedExtensions =
        {
            ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac"
        };

        private readonly List<SonicMusicTrack> _tracks = new List<SonicMusicTrack>();
        private readonly List<SonicMusicTrack> _cachedClipQueue = new List<SonicMusicTrack>(4);
        private const int MaxCachedAudioClips = 2;
        private const long MaxAudioCacheBytes = 512L * 1024L * 1024L;
        private readonly Dictionary<string, Sprite> _proceduralCoverCache = new Dictionary<string, Sprite>();
        private string _activeScanFolder;
        private Coroutine _activeScanCoroutine;

        public IReadOnlyList<SonicMusicTrack> Tracks => _tracks;
        public string ActiveScanFolder => _activeScanFolder;
        public bool IsScanning => _activeScanCoroutine != null;

        public event Action OnLibraryUpdated;

        private void Awake()
        {
            RequestAndroidAudioPermissionsIfNeeded();
            _activeScanFolder = GetDefaultOrSavedScanFolder();
            RefreshLibrary();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _tracks.Count == 0 && !IsScanning)
            {
                RefreshLibrary();
            }
        }

        private void OnDestroy()
        {
            if (_activeScanCoroutine != null)
            {
                StopCoroutine(_activeScanCoroutine);
                _activeScanCoroutine = null;
            }

            for (int i = 0; i < _tracks.Count; i++)
            {
                DisposeTrackResources(_tracks[i]);
            }
            _tracks.Clear();
            _cachedClipQueue.Clear();

            foreach (var kvp in _proceduralCoverCache)
            {
                Sprite spr = kvp.Value;
                if (spr != null)
                {
                    Texture2D tex = spr.texture;
                    Destroy(spr);
                    if (tex != null) Destroy(tex);
                }
            }
            _proceduralCoverCache.Clear();
        }

        private void DisposeTrackResources(SonicMusicTrack track)
        {
            if (track == null) return;
            if (track.cachedClip != null)
            {
                Destroy(track.cachedClip);
                track.cachedClip = null;
            }
            if (track.hasRealAlbumArt && track.coverSprite != null)
            {
                Destroy(track.coverSprite);
                track.coverSprite = null;
            }
            if (track.coverTexture != null)
            {
                Destroy(track.coverTexture);
                track.coverTexture = null;
            }
        }

        private void RegisterTrackCachedClip(SonicMusicTrack track, AudioClip clip)
        {
            if (track == null || clip == null) return;
            track.cachedClip = clip;
            _cachedClipQueue.Remove(track);
            _cachedClipQueue.Add(track);

            while (_cachedClipQueue.Count > MaxCachedAudioClips)
            {
                SonicMusicTrack oldest = _cachedClipQueue[0];
                _cachedClipQueue.RemoveAt(0);
                if (oldest != null && oldest != track && oldest.cachedClip != null)
                {
                    Destroy(oldest.cachedClip);
                    oldest.cachedClip = null;
                }
            }
        }

        private static string GetSafePathHash(string path)
        {
            int h = path != null ? path.GetHashCode() : 0;
            if (h == int.MinValue) h = int.MaxValue;
            return Math.Abs(h).ToString();
        }

        public static void RequestAndroidAudioPermissionsIfNeeded()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                int sdkInt = 33;
                try
                {
                    using (AndroidJavaClass version = new AndroidJavaClass("android.os.Build$VERSION"))
                    {
                        sdkInt = version.GetStatic<int>("SDK_INT");
                    }
                }
                catch
                {
                    sdkInt = 33;
                }

                string permToRequest = sdkInt >= 33
                    ? "android.permission.READ_MEDIA_AUDIO"
                    : "android.permission.READ_EXTERNAL_STORAGE";

                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permToRequest))
                {
                    UnityEngine.Android.Permission.RequestUserPermission(permToRequest);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicMusicLibrary] Permission request warning: {e.Message}");
            }
#endif
        }

        public string GetDefaultOrSavedScanFolder()
        {
            string saved = PlayerPrefs.GetString(PrefScanFolderKey, string.Empty);
            if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
            {
                return Path.GetFullPath(saved);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            string questMusic = "/storage/emulated/0/Music";
            if (Directory.Exists(questMusic)) return questMusic;
            return Application.persistentDataPath;
#else
            string projectAudio = Path.Combine(Application.dataPath, "Audio");
            if (Directory.Exists(projectAudio)) return Path.GetFullPath(projectAudio);
            string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrEmpty(myMusic) && Directory.Exists(myMusic)) return Path.GetFullPath(myMusic);
            return Path.GetFullPath(Application.persistentDataPath);
#endif
        }

        public void SetScanFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;
            _activeScanFolder = Path.GetFullPath(folderPath);
            PlayerPrefs.SetString(PrefScanFolderKey, _activeScanFolder);
            PlayerPrefs.Save();
            RefreshLibrary();
        }

        public List<SonicStorageRoot> GetStorageRoots()
        {
            List<SonicStorageRoot> roots = new List<SonicStorageRoot>();

#if UNITY_ANDROID && !UNITY_EDITOR
            AddRootIfValid(roots, "Quest Music", "/storage/emulated/0/Music");
            AddRootIfValid(roots, "Quest Downloads", "/storage/emulated/0/Download");
            AddRootIfValid(roots, "Quest Storage", "/storage/emulated/0");
            AddRootIfValid(roots, "App Storage", Application.persistentDataPath);
#else
            string projectAudio = Path.Combine(Application.dataPath, "Audio");
            AddRootIfValid(roots, "Project Audio", projectAudio);

            string userMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            AddRootIfValid(roots, "My Music", userMusic);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                AddRootIfValid(roots, "Downloads", Path.Combine(userProfile, "Downloads"));
            }

            AddRootIfValid(roots, "App Storage", Application.persistentDataPath);
#endif
            return roots;
        }

        private static void AddRootIfValid(List<SonicStorageRoot> list, string label, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (Directory.Exists(path))
                {
                    list.Add(new SonicStorageRoot { label = label, path = Path.GetFullPath(path) });
                }
            }
            catch
            {
                // Ignore inaccessible paths
            }
        }

        public void RefreshLibrary()
        {
            if (_activeScanCoroutine != null)
            {
                StopCoroutine(_activeScanCoroutine);
                _activeScanCoroutine = null;
            }

            _activeScanCoroutine = StartCoroutine(ScanLibraryAsyncCoroutine());
        }

        private IEnumerator ScanLibraryAsyncCoroutine()
        {
            if (string.IsNullOrEmpty(_activeScanFolder))
            {
                _activeScanFolder = GetDefaultOrSavedScanFolder();
            }

            if (string.IsNullOrEmpty(_activeScanFolder) || !Directory.Exists(_activeScanFolder))
            {
                for (int i = 0; i < _tracks.Count; i++)
                {
                    DisposeTrackResources(_tracks[i]);
                }
                _tracks.Clear();
                _cachedClipQueue.Clear();
                _activeScanCoroutine = null;
                OnLibraryUpdated?.Invoke();
                yield break;
            }

            // Build lookup of existing tracks by fullPath so re-scans reuse already-decoded artwork/metadata and dispose removed tracks
            Dictionary<string, SonicMusicTrack> existingByPath = new Dictionary<string, SonicMusicTrack>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _tracks.Count; i++)
            {
                SonicMusicTrack existing = _tracks[i];
                if (existing != null && !string.IsNullOrEmpty(existing.filePath) && !existingByPath.ContainsKey(existing.filePath))
                {
                    existingByPath[existing.filePath] = existing;
                }
            }

            // Gather all tracks into a temporary list so the public library is never partially populated or reshuffled
            List<SonicMusicTrack> discoveredTracks = new List<SonicMusicTrack>();
            HashSet<string> seenDirAndBaseName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<KeyValuePair<string, int>> dirQueue = new Queue<KeyValuePair<string, int>>();
            dirQueue.Enqueue(new KeyValuePair<string, int>(_activeScanFolder, 0));

            Stopwatch sw = Stopwatch.StartNew();

            while (dirQueue.Count > 0)
            {
                KeyValuePair<string, int> current = dirQueue.Dequeue();
                string currentDir = current.Key;
                int depth = current.Value;

                string[] files = null;
                try
                {
                    files = Directory.GetFiles(currentDir);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SonicMusicLibrary] Could not read files in '{currentDir}': {e.Message}");
                }

                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        string file = files[i];
                        if (!IsSupportedAudioFile(file)) continue;

                        string fullPath = Path.GetFullPath(file);
                        string dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
                        string baseName = Path.GetFileNameWithoutExtension(fullPath);
                        string dedupKey = Path.Combine(dir, baseName);

                        if (seenDirAndBaseName.Contains(dedupKey)) continue;

                        if (existingByPath.TryGetValue(fullPath, out SonicMusicTrack reusedTrack))
                        {
                            existingByPath.Remove(fullPath);
                            discoveredTracks.Add(reusedTrack);
                            seenDirAndBaseName.Add(dedupKey);
                            continue;
                        }

                        SonicMusicTrack track = CreateTrackFromFile(fullPath);
                        if (track != null)
                        {
                            discoveredTracks.Add(track);
                            seenDirAndBaseName.Add(dedupKey);
                        }

                        // Time-sliced scan budget: yield after 4ms so VR head tracking never drops a single frame
                        if (sw.ElapsedMilliseconds >= 4)
                        {
                            sw.Restart();
                            yield return null;
                        }
                    }
                }

                if (depth < 12)
                {
                    string[] subDirs = null;
                    try
                    {
                        subDirs = Directory.GetDirectories(currentDir);
                        Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[SonicMusicLibrary] Could not read subfolders in '{currentDir}': {e.Message}");
                    }

                    if (subDirs != null)
                    {
                        for (int i = 0; i < subDirs.Length; i++)
                        {
                            string sub = subDirs[i];
                            string folderName = Path.GetFileName(sub);
                            if (string.IsNullOrEmpty(folderName) || folderName.StartsWith(".")) continue;
                            dirQueue.Enqueue(new KeyValuePair<string, int>(sub, depth + 1));
                        }
                    }
                }

                if (sw.ElapsedMilliseconds >= 4)
                {
                    sw.Restart();
                    yield return null;
                }
            }

            // Dispose native textures/sprites/clips of tracks no longer in the active scan folder
            foreach (var kvp in existingByPath)
            {
                _cachedClipQueue.Remove(kvp.Value);
                DisposeTrackResources(kvp.Value);
            }

            // Sort all discovered tracks alphabetically by title once and for all
            discoveredTracks.Sort((a, b) => string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase));

            // Atomically update public _tracks list
            _tracks.Clear();
            _tracks.AddRange(discoveredTracks);

            _activeScanCoroutine = null;
            // Notify UI exactly once when the library is 100% complete and sorted
            OnLibraryUpdated?.Invoke();
        }

        public SonicMusicTrack RegisterOrGetFileTrack(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            string fullPath = Path.GetFullPath(filePath);
            string dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(fullPath);

            for (int i = 0; i < _tracks.Count; i++)
            {
                if (string.Equals(_tracks[i].filePath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return _tracks[i];
                }
                string trackDir = Path.GetDirectoryName(_tracks[i].filePath) ?? string.Empty;
                string trackBase = Path.GetFileNameWithoutExtension(_tracks[i].filePath);
                if (string.Equals(trackDir, dir, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(trackBase, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    return _tracks[i];
                }
            }

            SonicMusicTrack newTrack = CreateTrackFromFile(fullPath);
            if (newTrack != null)
            {
                _tracks.Add(newTrack);
                _tracks.Sort((a, b) => string.Compare(a.title, b.title, StringComparison.OrdinalIgnoreCase));
                OnLibraryUpdated?.Invoke();
            }
            return newTrack;
        }

        public List<SonicBrowserEntry> GetDirectoryEntries(string directoryPath)
        {
            List<SonicBrowserEntry> entries = new List<SonicBrowserEntry>();
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
            {
                return entries;
            }

            try
            {
                string[] dirs = Directory.GetDirectories(directoryPath);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
                    entries.Add(new SonicBrowserEntry
                    {
                        name = name,
                        fullPath = d,
                        isDirectory = true,
                        fileSizeBytes = 0,
                        extension = string.Empty
                    });
                }

                string[] files = Directory.GetFiles(directoryPath);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (!IsSupportedAudioFile(f)) continue;
                    FileInfo fi = new FileInfo(f);
                    entries.Add(new SonicBrowserEntry
                    {
                        name = fi.Name,
                        fullPath = fi.FullName,
                        isDirectory = false,
                        fileSizeBytes = fi.Length,
                        extension = fi.Extension.ToLowerInvariant()
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicMusicLibrary] Browse error in '{directoryPath}': {e.Message}");
            }

            return entries;
        }

        public static bool IsSupportedAudioFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext)) return false;
            for (int i = 0; i < SupportedExtensions.Length; i++)
            {
                if (string.Equals(ext, SupportedExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private SonicMusicTrack CreateTrackFromFile(string fullPath)
        {
            try
            {
                string fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);
                string dirPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
                string folderName = Path.GetFileName(dirPath);
                if (string.IsNullOrWhiteSpace(folderName)) folderName = "Music";

                string defaultArtist = "Unknown Artist";
                string defaultTitle = fileNameNoExt;

                int dashIdx = fileNameNoExt.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx > 0 && dashIdx + 3 < fileNameNoExt.Length)
                {
                    defaultArtist = fileNameNoExt.Substring(0, dashIdx).Trim();
                    defaultTitle = fileNameNoExt.Substring(dashIdx + 3).Trim();
                }

                string title = defaultTitle;
                string artist = defaultArtist;
                string album = folderName;
                Texture2D coverTex = null;

                // 1. Extract metadata & embedded cover art from this file (or sibling .m4a/.mp3 if this is a .wav)
                ExtractMetadataAndArtwork(fullPath, ref title, ref artist, ref album, out coverTex);

                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                if (coverTex == null && ext == ".wav")
                {
                    string siblingM4a = Path.ChangeExtension(fullPath, ".m4a");
                    if (File.Exists(siblingM4a))
                    {
                        ExtractMetadataAndArtwork(siblingM4a, ref title, ref artist, ref album, out coverTex);
                    }
                }

                // 2. Check for sidecar artwork (<TrackName>.jpg/.png or cover.jpg/folder.jpg) in the same folder
                if (coverTex == null)
                {
                    coverTex = TryLoadSidecarArtwork(fullPath);
                }

                if (coverTex != null && (coverTex.width > 512 || coverTex.height > 512))
                {
                    coverTex = DownscaleCoverTexture(coverTex, 512);
                }

                bool hasRealCover = coverTex != null;
                SonicThemeColors extractedPalette = hasRealCover
                    ? ExtractPaletteFromAlbumCover(coverTex)
                    : SonicTopographyConfig.GetTheme(SonicThemePreset.Nocturnal);

                Sprite coverSprite = hasRealCover
                    ? CreateRoundedAlbumSprite(coverTex)
                    : GetOrCreateProceduralCover(title, artist);

                return new SonicMusicTrack
                {
                    id = fullPath,
                    title = string.IsNullOrWhiteSpace(title) ? defaultTitle : title,
                    artist = string.IsNullOrWhiteSpace(artist) ? defaultArtist : artist,
                    album = string.IsNullOrWhiteSpace(album) ? folderName : album,
                    folderName = folderName,
                    filePath = fullPath,
                    durationSeconds = 0f,
                    cachedClip = null,
                    coverTexture = coverTex,
                    coverSprite = coverSprite,
                    hasRealAlbumArt = hasRealCover,
                    extractedPalette = extractedPalette
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicMusicLibrary] Error reading track '{fullPath}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts a rich, high-contrast Sonic Topography color palette from an album cover texture.
        /// Used when Color Mode is set to Auto (Album Art).
        /// </summary>
        public static SonicThemeColors ExtractPaletteFromAlbumCover(Texture2D tex)
        {
            if (tex == null)
            {
                return SonicTopographyConfig.GetTheme(SonicThemePreset.Nocturnal);
            }

            const int grid = 16;
            const int bins = 12;
            float[] binWeight = new float[bins];
            float[] binHueSum = new float[bins];
            float[] binSatSum = new float[bins];
            float[] binValSum = new float[bins];

            float totalChromaticWeight = 0f;
            float avgVal = 0f;

            for (int y = 0; y < grid; y++)
            {
                float v = (y + 0.5f) / grid;
                for (int x = 0; x < grid; x++)
                {
                    float u = (x + 0.5f) / grid;
                    Color c = tex.GetPixelBilinear(u, v);
                    Color.RGBToHSV(c, out float h, out float s, out float val);
                    avgVal += val;

                    // Weight vibrant mid-to-bright pixels higher so muddy shadows don't dominate
                    float weight = s * Mathf.Clamp01(val * 1.25f) * (0.35f + s * 0.65f);
                    if (s > 0.08f && val > 0.08f)
                    {
                        int bin = Mathf.Clamp(Mathf.FloorToInt(h * bins), 0, bins - 1);
                        binWeight[bin] += weight;
                        binHueSum[bin] += h * weight;
                        binSatSum[bin] += s * weight;
                        binValSum[bin] += val * weight;
                        totalChromaticWeight += weight;
                    }
                }
            }

            avgVal /= (grid * grid);

            // Monochrome / Black & White album cover handling (True OLED black void + high-contrast HDR silver/white peaks)
            if (totalChromaticWeight < 0.8f)
            {
                Color silverCore = new Color(0.68f, 0.76f, 0.88f, 1f);
                Color brightWhite = new Color(0.98f, 0.99f, 1.0f, 1f);
                return new SonicThemeColors
                {
                    uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                    uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                    uBaseColor2 = new Color(0.018f, 0.022f, 0.028f, 1f),
                    uCoolCore = silverCore,
                    uCoolEdge = Color.Lerp(silverCore, brightWhite, 0.55f),
                    uWarmCore = new Color(0.86f, 0.90f, 0.98f, 1f),
                    uWarmEdge = brightWhite,
                    uRippleColor = brightWhite,
                    uGlowIntensity = 1.25f
                };
            }

            // Find dominant hue bin
            int primaryBin = 0;
            for (int i = 1; i < bins; i++)
            {
                if (binWeight[i] > binWeight[primaryBin])
                {
                    primaryBin = i;
                }
            }

            float primaryW = Mathf.Max(0.0001f, binWeight[primaryBin]);
            float primaryHue = binHueSum[primaryBin] / primaryW;
            float primarySat = Mathf.Clamp(binSatSum[primaryBin] / primaryW, 0.62f, 0.98f);

            // Find secondary contrasting hue bin (at least 2 bins away in hue wheel if possible)
            int secondaryBin = -1;
            float bestSecScore = -1f;
            for (int i = 0; i < bins; i++)
            {
                if (i == primaryBin || binWeight[i] <= 0.01f) continue;
                int dist = Mathf.Min(Mathf.Abs(i - primaryBin), bins - Mathf.Abs(i - primaryBin));
                float contrastBoost = dist >= 2 ? 1.6f : 0.6f;
                float score = binWeight[i] * contrastBoost;
                if (score > bestSecScore)
                {
                    bestSecScore = score;
                    secondaryBin = i;
                }
            }

            bool hasRealSecondary = secondaryBin >= 0 && binWeight[secondaryBin] > primaryW * 0.16f;
            Color coolCore;
            Color coolEdge;
            Color warmCore;
            Color warmEdge;
            Color rippleCol;

            if (hasRealSecondary)
            {
                // Multi-color album cover: Extract actual secondary color from the artwork
                float secW = Mathf.Max(0.0001f, binWeight[secondaryBin]);
                float secondaryHue = binHueSum[secondaryBin] / secW;
                float secondarySat = Mathf.Clamp(binSatSum[secondaryBin] / secW, 0.68f, 0.98f);

                coolCore = Color.HSVToRGB(primaryHue, Mathf.Max(0.75f, primarySat), 0.94f);
                coolEdge = Color.HSVToRGB(primaryHue, Mathf.Max(0.55f, primarySat * 0.82f), 1.0f);

                warmCore = Color.HSVToRGB(secondaryHue, Mathf.Max(0.78f, secondarySat), 0.98f);
                warmEdge = Color.Lerp(warmCore, Color.white, 0.38f);
                rippleCol = Color.HSVToRGB(secondaryHue, Mathf.Clamp(secondarySat * 0.88f, 0.60f, 0.96f), 1.0f);
            }
            else
            {
                // Option A: Rich Tonal Chromatic Depth (True to Artwork)
                // When an album has only one dominant color (e.g. Strawberries & Cigarettes red),
                // keep the entire palette within the album's authentic hue family with high saturation,
                // preventing washed-out pastel baby-pink and yellow distortion.

                // Lows & Base: Deep, rich velvety foundation of the artwork's true color
                coolCore = Color.HSVToRGB(primaryHue, Mathf.Clamp(primarySat * 1.05f, 0.82f, 0.98f), 0.90f);
                coolEdge = Color.HSVToRGB(primaryHue, Mathf.Clamp(primarySat * 0.95f, 0.78f, 0.96f), 0.98f);

                // High Peaks: Intensely vibrant, electric hot strawberry neon (retains 72%+ saturation!)
                warmCore = Color.HSVToRGB(primaryHue, Mathf.Clamp(primarySat * 0.88f, 0.72f, 0.92f), 1.0f);

                // High Peaks Edge: Luminous crest highlight with subtle white lift (retains strong strawberry chroma)
                warmEdge = Color.Lerp(warmCore, Color.white, 0.16f);

                // Ripples: Punchy strawberry-coral shockwave
                rippleCol = Color.HSVToRGB(primaryHue, Mathf.Clamp(primarySat * 0.75f, 0.60f, 0.85f), 1.0f);
            }

            // True OLED black void floor + deep obsidian base blocks with rich chromatic undertone
            Color fogCol = new Color(0.0f, 0.0f, 0.0f, 1f);
            Color base1 = new Color(0.0f, 0.0f, 0.0f, 1f);
            Color base2 = Color.HSVToRGB(primaryHue, Mathf.Clamp(primarySat * 0.90f, 0.75f, 0.98f), 0.038f);

            return new SonicThemeColors
            {
                uFogColor = fogCol,
                uBaseColor1 = base1,
                uBaseColor2 = base2,
                uCoolCore = coolCore,
                uCoolEdge = coolEdge,
                uWarmCore = warmCore,
                uWarmEdge = warmEdge,
                uRippleColor = rippleCol,
                uGlowIntensity = Mathf.Lerp(1.25f, 1.48f, Mathf.Clamp01(avgVal))
            };
        }

        public IEnumerator LoadTrackClipCoroutine(SonicMusicTrack track, Action<AudioClip> onLoaded)
        {
            if (track == null)
            {
                onLoaded?.Invoke(null);
                yield break;
            }

            if (track.cachedClip != null)
            {
                RegisterTrackCachedClip(track, track.cachedClip);
                track.durationSeconds = track.cachedClip.length;
                onLoaded?.Invoke(track.cachedClip);
                yield break;
            }

            if (string.IsNullOrEmpty(track.filePath) || !File.Exists(track.filePath))
            {
                onLoaded?.Invoke(null);
                yield break;
            }

            string targetPath = track.filePath;
            string ext = Path.GetExtension(targetPath).ToLowerInvariant();

            // Check if a sibling .wav file exists
            if (ext == ".m4a" || ext == ".flac")
            {
                string wavSibling = Path.ChangeExtension(targetPath, ".wav");
                if (File.Exists(wavSibling))
                {
                    targetPath = wavSibling;
                    ext = ".wav";
                }
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // On Meta Quest / Android, Unity's built-in FMOD UnityWebRequestMultimedia does not decode .flac or .m4a (Dolby Atmos E-AC3, ALAC, or MP4 AAC).
            // Use Android's native hardware/OS MediaExtractor + MediaCodec (SonicAndroidAudioDecoder) on a background thread to decode into a cached stereo 16-bit WAV.
            if (ext == ".m4a" || ext == ".flac" || ext == ".aac" || ext == ".mp4" || ext == ".opus")
            {
                string cacheDir = Path.Combine(Application.temporaryCachePath, "SonicAudioCache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string safeHash = GetSafePathHash(targetPath);
                string cachedWav = Path.Combine(cacheDir, $"quest_{safeHash}.wav");

                if (!HasValidCachedWav(cachedWav))
                {
                    PruneAudioCacheIfNeeded(cacheDir, cachedWav);
                    string srcFile = targetPath;
                    string dstFile = cachedWav;
                    Task<bool> decodeTask = Task.Run(() => DecodeWithAndroidMediaCodec(srcFile, dstFile));
                    while (!decodeTask.IsCompleted)
                    {
                        yield return null;
                    }
                }

                if (File.Exists(cachedWav) && new FileInfo(cachedWav).Length >= 4096)
                {
                    targetPath = cachedWav;
                    ext = ".wav";
                }
            }
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            // On Windows, FMOD's UnityWebRequestMultimedia does not natively decode .m4a (AAC/ALAC/E-AC3) or multi-channel .flac.
            // Pre-transcode to a cached stereo 44.1kHz .wav in temporaryCachePath if ffmpeg is available so playback works seamlessly.
            if (ext == ".m4a" || ext == ".flac" || ext == ".aac")
            {
                string ffmpegExe = FindFfmpegExecutable();
                if (!string.IsNullOrEmpty(ffmpegExe))
                {
                    string cacheDir = Path.Combine(Application.temporaryCachePath, "SonicAudioCache");
                    if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                    string safeHash = GetSafePathHash(targetPath);
                    string cachedWav = Path.Combine(cacheDir, $"{Path.GetFileNameWithoutExtension(targetPath)}_{safeHash}.wav");

                    if (!HasValidCachedWav(cachedWav))
                    {
                        PruneAudioCacheIfNeeded(cacheDir, cachedWav);
                        string srcFile = targetPath;
                        string dstFile = cachedWav;
                        Task<bool> transcodeTask = Task.Run(() => TranscodeToStereoWav(ffmpegExe, srcFile, dstFile));
                        while (!transcodeTask.IsCompleted)
                        {
                            yield return null;
                        }
                    }

                    if (File.Exists(cachedWav) && new FileInfo(cachedWav).Length >= 4096)
                    {
                        targetPath = cachedWav;
                        ext = ".wav";
                    }
                }
            }
#endif

            AudioType audioType = GetUnityAudioType(ext);
            string uri = new Uri(targetPath).AbsoluteUri;

            AudioType[] tryTypes = audioType != AudioType.UNKNOWN
                ? new[] { audioType, AudioType.UNKNOWN }
                : new[] { AudioType.UNKNOWN };

            for (int t = 0; t < tryTypes.Length; t++)
            {
                using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(uri, tryTypes[t]))
                {
                    if (uwr.downloadHandler is DownloadHandlerAudioClip dh)
                    {
                        dh.streamAudio = false;
                    }

                    yield return uwr.SendWebRequest();

                    if (uwr.result == UnityWebRequest.Result.Success)
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                        if (clip != null && clip.length > 0f)
                        {
                            clip.name = track.title;
                            RegisterTrackCachedClip(track, clip);
                            track.durationSeconds = clip.length;
                            onLoaded?.Invoke(clip);
                            yield break;
                        }
                    }
                    else if (t == tryTypes.Length - 1)
                    {
                        Debug.LogWarning($"[SonicMusicLibrary] Failed loading '{targetPath}': {uwr.error}");
                    }
                }
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // Fallback for any other format (.mp3/.ogg with non-standard headers) that FMOD rejected on Android
            if (ext != ".wav")
            {
                string cacheDir = Path.Combine(Application.temporaryCachePath, "SonicAudioCache");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                string safeHash = GetSafePathHash(track.filePath);
                string cachedWav = Path.Combine(cacheDir, $"quest_fb_{safeHash}.wav");

                if (!HasValidCachedWav(cachedWav))
                {
                    PruneAudioCacheIfNeeded(cacheDir, cachedWav);
                    string srcFile = track.filePath;
                    string dstFile = cachedWav;
                    Task<bool> decodeTask = Task.Run(() => DecodeWithAndroidMediaCodec(srcFile, dstFile));
                    while (!decodeTask.IsCompleted)
                    {
                        yield return null;
                    }
                }

                if (File.Exists(cachedWav) && new FileInfo(cachedWav).Length >= 4096)
                {
                    string wavUri = new Uri(cachedWav).AbsoluteUri;
                    using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(wavUri, AudioType.WAV))
                    {
                        yield return uwr.SendWebRequest();
                        if (uwr.result == UnityWebRequest.Result.Success)
                        {
                            AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                            if (clip != null && clip.length > 0f)
                            {
                                clip.name = track.title;
                                RegisterTrackCachedClip(track, clip);
                                track.durationSeconds = clip.length;
                                onLoaded?.Invoke(clip);
                                yield break;
                            }
                        }
                    }
                }
            }
#endif

            onLoaded?.Invoke(null);
        }

        private static bool HasValidCachedWav(string cachedWav)
        {
            if (string.IsNullOrEmpty(cachedWav) || !File.Exists(cachedWav)) return false;
            try
            {
                FileInfo fi = new FileInfo(cachedWav);
                if (fi.Length >= 4096)
                {
                    File.SetLastWriteTimeUtc(cachedWav, DateTime.UtcNow);
                    return true;
                }
            }
            catch
            {
                // Ignore stat/timestamp errors
            }
            return false;
        }

        private static void PruneAudioCacheIfNeeded(string cacheDir, string protectedFilePath)
        {
            try
            {
                if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir)) return;

                FileInfo[] files = new DirectoryInfo(cacheDir).GetFiles("*.wav");
                long totalBytes = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    totalBytes += files[i].Length;
                }

                if (totalBytes <= MaxAudioCacheBytes) return;

                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long targetBytes = (MaxAudioCacheBytes * 3L) / 4L;

                for (int i = 0; i < files.Length && totalBytes > targetBytes; i++)
                {
                    FileInfo fi = files[i];
                    if (!string.IsNullOrEmpty(protectedFilePath) &&
                        string.Equals(fi.FullName, Path.GetFullPath(protectedFilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        long len = fi.Length;
                        fi.Delete();
                        totalBytes -= len;
                    }
                    catch
                    {
                        // Ignore locked or already deleted cache files
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicMusicLibrary] Cache prune warning: {e.Message}");
            }
        }

        private static Texture2D DownscaleCoverTexture(Texture2D source, int maxDim)
        {
            if (source == null) return null;
            int w = source.width;
            int h = source.height;
            if (w <= maxDim && h <= maxDim) return source;

            float scale = (float)maxDim / Mathf.Max(w, h);
            int targetW = Mathf.Max(16, Mathf.RoundToInt(w * scale));
            int targetH = Mathf.Max(16, Mathf.RoundToInt(h * scale));

            RenderTexture rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                Texture2D resized = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                resized.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                resized.Apply();
                resized.wrapMode = TextureWrapMode.Clamp;
                resized.filterMode = FilterMode.Bilinear;
                Destroy(source);
                return resized;
            }
            catch
            {
                return source;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool DecodeWithAndroidMediaCodec(string inputPath, string outputWavPath)
        {
            bool attached = false;
            try
            {
                AndroidJNI.AttachCurrentThread();
                attached = true;
                using (AndroidJavaClass decoderClass = new AndroidJavaClass("com.sonictopography.audio.SonicAndroidAudioDecoder"))
                {
                    return decoderClass.CallStatic<bool>("decodeToStereoWav", inputPath, outputWavPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SonicMusicLibrary] Android MediaCodec decode error for '{inputPath}': {e.Message}");
                return false;
            }
            finally
            {
                if (attached)
                {
                    try { AndroidJNI.DetachCurrentThread(); } catch { }
                }
            }
        }
#endif

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static string FindFfmpegExecutable()
        {
            string[] candidates =
            {
                @"C:\Program Files\File Converter\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe"
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            return null;
        }

        private static bool TranscodeToStereoWav(string ffmpegExe, string inputPath, string outputWavPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-y -v error -i \"{inputPath}\" -vn -ac 2 -ar 44100 -sample_fmt s16 \"{outputWavPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    proc.WaitForExit(25000);
                    return File.Exists(outputWavPath) && new FileInfo(outputWavPath).Length > 4096;
                }
            }
            catch
            {
                return false;
            }
        }
#endif

        private static AudioType GetUnityAudioType(string ext)
        {
            switch (ext)
            {
                case ".mp3": return AudioType.MPEG;
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".m4a":
                case ".aac": return AudioType.ACC;
                default: return AudioType.UNKNOWN;
            }
        }

        private static Texture2D TryLoadSidecarArtwork(string audioFilePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(audioFilePath);
                if (string.IsNullOrEmpty(dir)) return null;
                string baseName = Path.GetFileNameWithoutExtension(audioFilePath);

                string[] candidates =
                {
                    Path.Combine(dir, baseName + ".jpg"),
                    Path.Combine(dir, baseName + ".jpeg"),
                    Path.Combine(dir, baseName + ".png"),
                    Path.Combine(dir, "cover.jpg"),
                    Path.Combine(dir, "cover.jpeg"),
                    Path.Combine(dir, "cover.png"),
                    Path.Combine(dir, "folder.jpg"),
                    Path.Combine(dir, "folder.png"),
                    Path.Combine(dir, "albumart.jpg"),
                    Path.Combine(dir, "albumart.png"),
                    Path.Combine(dir, "album.jpg"),
                    Path.Combine(dir, "front.jpg"),
                    Path.Combine(dir, "discart.jpg")
                };

                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i]))
                    {
                        byte[] bytes = File.ReadAllBytes(candidates[i]);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(bytes))
                        {
                            tex.wrapMode = TextureWrapMode.Clamp;
                            return tex;
                        }
                        Destroy(tex);
                    }
                }
            }
            catch
            {
                // Ignore sidecar errors
            }
            return null;
        }

        private static void ExtractMetadataAndArtwork(
            string filePath,
            ref string title,
            ref string artist,
            ref string album,
            out Texture2D coverTex)
        {
            coverTex = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (ext == ".mp3" && fs.Length > 10)
                    {
                        ParseId3v2(fs, ref title, ref artist, ref album, out coverTex);
                    }
                    else if (ext == ".flac" && fs.Length > 42)
                    {
                        ParseFlacMetadata(fs, ref title, ref artist, ref album, out coverTex);
                    }
                    else if ((ext == ".m4a" || ext == ".mp4" || ext == ".aac") && fs.Length > 32)
                    {
                        // Read up to 4 MB from start and, if needed, 3 MB from end of file (in case moov/covr is at end)
                        int headLen = (int)Mathf.Min(fs.Length, 4 * 1024 * 1024);
                        byte[] headBuf = new byte[headLen];
                        int actualHead = fs.Read(headBuf, 0, headLen);
                        ParseMp4Atoms(headBuf, actualHead, ref title, ref artist, ref album, out coverTex);

                        if (coverTex == null && fs.Length > headLen)
                        {
                            int tailLen = (int)Mathf.Min(fs.Length - headLen, 3 * 1024 * 1024);
                            fs.Seek(-tailLen, SeekOrigin.End);
                            byte[] tailBuf = new byte[tailLen];
                            int actualTail = fs.Read(tailBuf, 0, tailLen);
                            ParseMp4Atoms(tailBuf, actualTail, ref title, ref artist, ref album, out coverTex);
                        }
                    }
                }
            }
            catch
            {
                // Ignore metadata parsing errors
            }
        }

        private static void ParseFlacMetadata(
            FileStream fs,
            ref string title,
            ref string artist,
            ref string album,
            out Texture2D coverTex)
        {
            coverTex = null;
            byte[] sig = new byte[4];
            if (fs.Read(sig, 0, 4) < 4) return;
            if (sig[0] != 'f' || sig[1] != 'L' || sig[2] != 'a' || sig[3] != 'C') return;

            byte[] blockHdr = new byte[4];
            int safety = 0;
            while (fs.Position + 4 <= fs.Length && safety++ < 32)
            {
                if (fs.Read(blockHdr, 0, 4) < 4) break;
                bool isLast = (blockHdr[0] & 0x80) != 0;
                int blockType = blockHdr[0] & 0x7F;
                int blockLen = (blockHdr[1] << 16) | (blockHdr[2] << 8) | blockHdr[3];

                if (blockLen <= 0 || fs.Position + blockLen > fs.Length) break;

                if (blockType == 4 && blockLen < 512 * 1024) // VORBIS_COMMENT
                {
                    byte[] data = new byte[blockLen];
                    int read = fs.Read(data, 0, blockLen);
                    if (read == blockLen && blockLen >= 8)
                    {
                        int pos = 0;
                        int vendorLen = BitConverter.ToInt32(data, pos);
                        pos += 4 + Mathf.Max(0, vendorLen);
                        if (pos + 4 <= blockLen)
                        {
                            int commentCount = BitConverter.ToInt32(data, pos);
                            pos += 4;
                            for (int c = 0; c < commentCount && pos + 4 <= blockLen; c++)
                            {
                                int cLen = BitConverter.ToInt32(data, pos);
                                pos += 4;
                                if (cLen <= 0 || pos + cLen > blockLen) break;
                                string entry = Encoding.UTF8.GetString(data, pos, cLen);
                                pos += cLen;

                                int eq = entry.IndexOf('=');
                                if (eq > 0 && eq + 1 < entry.Length)
                                {
                                    string k = entry.Substring(0, eq).Trim();
                                    string v = entry.Substring(eq + 1).Trim();
                                    if (!string.IsNullOrWhiteSpace(v))
                                    {
                                        if (k.Equals("TITLE", StringComparison.OrdinalIgnoreCase)) title = v;
                                        else if (k.Equals("ARTIST", StringComparison.OrdinalIgnoreCase)) artist = v;
                                        else if (k.Equals("ALBUM", StringComparison.OrdinalIgnoreCase)) album = v;
                                    }
                                }
                            }
                        }
                    }
                }
                else if (blockType == 6 && coverTex == null && blockLen < 8 * 1024 * 1024) // PICTURE
                {
                    byte[] data = new byte[blockLen];
                    int read = fs.Read(data, 0, blockLen);
                    if (read == blockLen)
                    {
                        coverTex = DecodeId3ApicFrame(data, 0, blockLen);
                    }
                }
                else
                {
                    fs.Seek(blockLen, SeekOrigin.Current);
                }

                if (isLast) break;
            }
        }

        private static void ParseMp4Atoms(
            byte[] buf,
            int length,
            ref string title,
            ref string artist,
            ref string album,
            out Texture2D coverTex)
        {
            coverTex = null;
            for (int i = 0; i < length - 24; i++)
            {
                // ©nam (0xA9 'n' 'a' 'm'), ©ART (0xA9 'A' 'R' 'T'), ©alb (0xA9 'a' 'l' 'b')
                if (buf[i] == 0xA9)
                {
                    if (buf[i + 1] == 'n' && buf[i + 2] == 'a' && buf[i + 3] == 'm')
                    {
                        string s = TryReadMp4DataString(buf, i, length);
                        if (!string.IsNullOrWhiteSpace(s)) title = s;
                    }
                    else if ((buf[i + 1] == 'A' || buf[i + 1] == 'a') &&
                             (buf[i + 2] == 'R' || buf[i + 2] == 'r') &&
                             (buf[i + 3] == 'T' || buf[i + 3] == 't'))
                    {
                        string s = TryReadMp4DataString(buf, i, length);
                        if (!string.IsNullOrWhiteSpace(s)) artist = s;
                    }
                    else if (buf[i + 1] == 'a' && buf[i + 2] == 'l' && buf[i + 3] == 'b')
                    {
                        string s = TryReadMp4DataString(buf, i, length);
                        if (!string.IsNullOrWhiteSpace(s)) album = s;
                    }
                }
            }

            // Look for 'covr' atom or embedded JPEG/PNG stream
            coverTex = TryExtractEmbeddedImageFromBuffer(buf, length);
        }

        private static string TryReadMp4DataString(byte[] buf, int atomNamePos, int totalLen)
        {
            // Inside ilst atom: [4B size][4B ©nam][4B dataSize]['d''a''t''a'][4B type/flags][4B reserved][UTF-8 bytes]
            int dataPos = atomNamePos + 8;
            if (dataPos + 12 >= totalLen) return null;
            if (buf[dataPos] != 'd' || buf[dataPos + 1] != 'a' || buf[dataPos + 2] != 't' || buf[dataPos + 3] != 'a')
            {
                return null;
            }

            int dataAtomSize = (buf[dataPos - 4] << 24) |
                               (buf[dataPos - 3] << 16) |
                               (buf[dataPos - 2] << 8) |
                               buf[dataPos - 1];
            int textStart = dataPos + 12;
            int textLen = dataAtomSize - 16;
            if (textLen <= 0 || textLen > 512 || textStart + textLen > totalLen) return null;

            return Encoding.UTF8.GetString(buf, textStart, textLen).Trim('\0', ' ', '\t', '\r', '\n');
        }

        private static void ParseId3v2(
            FileStream fs,
            ref string title,
            ref string artist,
            ref string album,
            out Texture2D coverTex)
        {
            coverTex = null;
            byte[] header = new byte[10];
            if (fs.Read(header, 0, 10) < 10) return;
            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return;

            int version = header[3];
            int tagSize = ((header[6] & 0x7F) << 21) |
                          ((header[7] & 0x7F) << 14) |
                          ((header[8] & 0x7F) << 7) |
                          (header[9] & 0x7F);

            int maxTagRead = Mathf.Min(tagSize, 2 * 1024 * 1024);
            byte[] tagData = new byte[maxTagRead];
            int actualRead = fs.Read(tagData, 0, maxTagRead);

            int pos = 0;
            while (pos + 10 < actualRead)
            {
                if (tagData[pos] == 0) break;
                string frameId = Encoding.ASCII.GetString(tagData, pos, 4);
                int frameSize;
                if (version == 4)
                {
                    frameSize = ((tagData[pos + 4] & 0x7F) << 21) |
                                ((tagData[pos + 5] & 0x7F) << 14) |
                                ((tagData[pos + 6] & 0x7F) << 7) |
                                (tagData[pos + 7] & 0x7F);
                }
                else
                {
                    frameSize = (tagData[pos + 4] << 24) |
                                (tagData[pos + 5] << 16) |
                                (tagData[pos + 6] << 8) |
                                tagData[pos + 7];
                }

                pos += 10;
                if (frameSize <= 0 || pos + frameSize > actualRead) break;

                if (frameId == "TIT2")
                {
                    string val = DecodeId3TextFrame(tagData, pos, frameSize);
                    if (!string.IsNullOrWhiteSpace(val)) title = val;
                }
                else if (frameId == "TPE1")
                {
                    string val = DecodeId3TextFrame(tagData, pos, frameSize);
                    if (!string.IsNullOrWhiteSpace(val)) artist = val;
                }
                else if (frameId == "TALB")
                {
                    string val = DecodeId3TextFrame(tagData, pos, frameSize);
                    if (!string.IsNullOrWhiteSpace(val)) album = val;
                }
                else if (frameId == "APIC" && coverTex == null)
                {
                    coverTex = DecodeId3ApicFrame(tagData, pos, frameSize);
                }

                pos += frameSize;
            }
        }

        private static string DecodeId3TextFrame(byte[] buf, int offset, int length)
        {
            if (length <= 1) return string.Empty;
            byte encoding = buf[offset];
            int textOffset = offset + 1;
            int textLen = length - 1;

            string raw;
            switch (encoding)
            {
                case 0: // Latin-1 (ISO-8859-1) without requiring codepage tables
                {
                    char[] chars = new char[textLen];
                    for (int i = 0; i < textLen; i++) chars[i] = (char)buf[textOffset + i];
                    raw = new string(chars);
                    break;
                }
                case 1: // UTF-16 with BOM
                case 2: // UTF-16BE
                    raw = Encoding.Unicode.GetString(buf, textOffset, textLen);
                    break;
                default: // UTF-8
                    raw = Encoding.UTF8.GetString(buf, textOffset, textLen);
                    break;
            }
            return raw.Trim('\0', ' ', '\t', '\r', '\n');
        }

        private static Texture2D DecodeId3ApicFrame(byte[] buf, int offset, int length)
        {
            int end = offset + length;
            for (int i = offset; i < end - 8; i++)
            {
                bool isJpeg = buf[i] == 0xFF && buf[i + 1] == 0xD8 && buf[i + 2] == 0xFF;
                bool isPng = buf[i] == 0x89 && buf[i + 1] == 0x50 && buf[i + 2] == 0x4E && buf[i + 3] == 0x47;
                if (isJpeg || isPng)
                {
                    int imgLen = end - i;
                    byte[] imgBytes = new byte[imgLen];
                    Buffer.BlockCopy(buf, i, imgBytes, 0, imgLen);
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tex.LoadImage(imgBytes))
                    {
                        tex.wrapMode = TextureWrapMode.Clamp;
                        return tex;
                    }
                    Destroy(tex);
                    break;
                }
            }
            return null;
        }

        private static Texture2D TryExtractEmbeddedImageFromBuffer(byte[] buf, int length)
        {
            // Look for 'covr' atom and read its inner 'data' atom length directly
            for (int i = 0; i < length - 24; i++)
            {
                if (buf[i] == 'c' && buf[i + 1] == 'o' && buf[i + 2] == 'v' && buf[i + 3] == 'r')
                {
                    int dataPos = i + 8;
                    if (dataPos + 12 < length &&
                        buf[dataPos] == 'd' && buf[dataPos + 1] == 'a' && buf[dataPos + 2] == 't' && buf[dataPos + 3] == 'a')
                    {
                        int dataAtomSize = (buf[dataPos - 4] << 24) |
                                           (buf[dataPos - 3] << 16) |
                                           (buf[dataPos - 2] << 8) |
                                           buf[dataPos - 1];
                        int imgStart = dataPos + 12;
                        int imgLen = dataAtomSize - 16;
                        if (imgLen > 256 && imgStart + imgLen <= length)
                        {
                            byte[] imgBytes = new byte[imgLen];
                            Buffer.BlockCopy(buf, imgStart, imgBytes, 0, imgLen);
                            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (tex.LoadImage(imgBytes))
                            {
                                tex.wrapMode = TextureWrapMode.Clamp;
                                return tex;
                            }
                            Destroy(tex);
                        }
                    }

                    Texture2D covrTex = DecodeId3ApicFrame(buf, i + 4, Mathf.Min(length - (i + 4), 2 * 1024 * 1024));
                    if (covrTex != null) return covrTex;
                }
            }
            return null;
        }

        /// <summary>
        /// Creates a full-resolution sprite from the album cover texture; analytical SDF corner rounding
        /// and 1:1 square center-cropping are performed at native resolution by SonicGlassSurface on the GPU.
        /// </summary>
        public static Sprite CreateRoundedAlbumSprite(Texture2D sourceTex)
        {
            if (sourceTex == null) return null;
            sourceTex.wrapMode = TextureWrapMode.Clamp;
            sourceTex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(
                sourceTex,
                new Rect(0f, 0f, sourceTex.width, sourceTex.height),
                new Vector2(0.5f, 0.5f),
                100f
            );
        }

        public Sprite GetOrCreateProceduralCover(string title, string artist)
        {
            string key = $"{title}_{artist}";
            if (_proceduralCoverCache.TryGetValue(key, out Sprite existing) && existing != null)
            {
                return existing;
            }

            int hash = Mathf.Abs(key.GetHashCode());
            float hue1 = ((hash % 360) / 360f);
            float hue2 = (((hash / 7) % 360) / 360f);
            Color c1 = Color.HSVToRGB(hue1, 0.65f, 0.85f);
            Color c2 = Color.HSVToRGB(hue2, 0.75f, 0.35f);
            Color accent = Color.HSVToRGB((hue1 + 0.15f) % 1f, 0.45f, 1.0f);

            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            for (int y = 0; y < size; y++)
            {
                float v = y / (float)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)(size - 1);
                    float diag = (u * 0.6f + v * 0.4f);
                    Color bg = Color.Lerp(c2, c1, diag);

                    float dist = Vector2.Distance(new Vector2(x, y), center) / (size * 0.5f);
                    if (dist < 0.72f && dist > 0.18f)
                    {
                        float ring = Mathf.Sin(dist * 42f) * 0.5f + 0.5f;
                        bg = Color.Lerp(bg, accent, ring * 0.14f * (1f - dist));
                    }
                    else if (dist <= 0.14f && dist >= 0.06f)
                    {
                        bg = Color.Lerp(bg, Color.white, 0.35f);
                    }

                    pixels[y * size + x] = bg;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite spr = CreateRoundedAlbumSprite(tex);
            _proceduralCoverCache[key] = spr;
            return spr;
        }
    }
}
