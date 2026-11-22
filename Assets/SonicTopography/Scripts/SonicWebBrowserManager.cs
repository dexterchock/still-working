using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SonicTopography
{
    [Serializable]
    public class SonicBrowserStatus
    {
        public string url = "";
        public string title = "";
        public string artist = "";
        public string artworkUrl = "";
        public bool playing = false;
        public bool canGoBack = false;
        public bool canGoForward = false;
        public bool drmSilent = false;
        public bool loading = false;
        public bool hasSurface = false;
        public bool extReady = false;
        public bool portReady = false;
        public float volume = 1f;
        public double currentTime = 0.0;
        public double duration = 0.0;
        public int softInputSeq = 0;
        public int softInputValSeq = 0;
        public bool softInputPass = false;
        public string softInputVal = "";
    }

    public class SonicWebBrowserManager : MonoBehaviour
    {
        public const int SurfaceWidth = 1280;
        public const int SurfaceHeight = 752;
        public const string DefaultHomeUrl = "https://music.youtube.com";
        public const string DefaultHomeUrlPrefKey = "SonicTopography_BrowserDefaultUrl";

        public static string SavedDefaultHomeUrl
        {
            get
            {
                string saved = PlayerPrefs.GetString(DefaultHomeUrlPrefKey, DefaultHomeUrl);
                return string.IsNullOrWhiteSpace(saved) ? DefaultHomeUrl : saved;
            }
        }

        public static string NormalizeUrl(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return DefaultHomeUrl;
            string trimmed = rawUrl.Trim();
            if (trimmed.Contains("://")) return trimmed;
            if (trimmed.Contains(".") && !trimmed.Contains(" ")) return "https://" + trimmed;
            return "https://music.youtube.com/search?q=" + Uri.EscapeDataString(trimmed);
        }

        private SonicTopographyAudioEngine _audioEngine;
        private SonicMusicPlayerController _player;
        private SonicTopographyVisualizer _visualizer;

        private RectTransform _viewportRect;
        private RawImage _punchoutGraphic;
        private Material _punchoutMaterial;
        private GameObject _placeholderRoot;

        private OVROverlay _overlay;
        private GameObject _overlayGo;

        private bool _initialized = false;
        private bool _surfaceAttached = false;
        private bool _isViewportVisible = false;
        private float _canvasAlpha = 0f;
        private string _pendingUrl = DefaultHomeUrl;

        private string _loadedArtworkUrl = string.Empty;
        private Texture2D _artworkTexture;
        private Sprite _artworkSprite;
        private bool _hasArtworkPalette = false;
        private SonicThemeColors _artworkPalette;
        private int _artworkRequestId = 0;
        private Coroutine _artworkCoroutine;

        private SonicBrowserStatus _status = new SonicBrowserStatus();
        private bool _preferBrowserControls = false;

        private TouchScreenKeyboard _webKeyboard;
        private string _lastWebKeyboardText = string.Empty;
#if UNITY_ANDROID && !UNITY_EDITOR
        private int _webNativeSessionId = 0;
        private int _webNativeLastSeq = -1;
        private float _webNativeOpenedTime = -10f;
#endif

        private readonly Vector3[] _worldCorners = new Vector3[4];

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool _wasPlaying = false;
        private float _nextStatusPollTime = 0f;
        private int _lastFftSeq = -1;
        private int _lastHandledSoftInputSeq = 0;
        private int _lastHandledSoftInputValSeq = 0;
        private bool _webUserHasTypedInSession = false;
        private bool _webNativeIsPassword = false;
        private AndroidJavaObject _bridge;
        private IntPtr _attachSurfaceMethodId = IntPtr.Zero;
#endif

        public bool IsBrowserInitialized => _initialized;
        public bool IsBrowserAudioPlaying => _status != null && _status.playing;
        public bool IsBrowserAudioActive => _preferBrowserControls || IsBrowserAudioPlaying;
        public string CurrentUrl => _status != null && !string.IsNullOrEmpty(_status.url) ? _status.url : _pendingUrl;
        public string CurrentTitle => _status != null ? _status.title : string.Empty;
        public string CurrentArtist => _status != null ? _status.artist : string.Empty;
        public float CurrentTime => _status != null ? (float)_status.currentTime : 0f;
        public float CurrentDuration => _status != null ? (float)_status.duration : 0f;
        public float Volume => _status != null ? _status.volume : 1f;
        public bool CanGoBack => _status != null && _status.canGoBack;
        public bool CanGoForward => _status != null && _status.canGoForward;
        public bool IsLoading => _status != null && _status.loading;
        public bool IsDrmSilent => _status != null && _status.drmSilent;
        public bool IsSurfaceAttached => _surfaceAttached;
        public Sprite CurrentArtworkSprite => _artworkSprite;
        public bool HasArtworkPalette => _hasArtworkPalette;

        public event Action OnBrowserStateChanged;

        private void Awake()
        {
            _pendingUrl = SavedDefaultHomeUrl;
        }

        public void Initialize(SonicTopographyAudioEngine audioEngine, SonicMusicPlayerController player, SonicTopographyVisualizer visualizer = null)
        {
            _audioEngine = audioEngine;
            _player = player;
            _visualizer = visualizer != null ? visualizer : FindFirstObjectByType<SonicTopographyVisualizer>();
            if (!_initialized)
            {
                _pendingUrl = SavedDefaultHomeUrl;
            }
        }

        public void SetSavedDefaultHomeUrl(string rawUrl)
        {
            string normalized = NormalizeUrl(rawUrl);
            PlayerPrefs.SetString(DefaultHomeUrlPrefKey, normalized);
            PlayerPrefs.Save();
            _pendingUrl = normalized;
            OnBrowserStateChanged?.Invoke();
        }

        public bool IsCurrentUrlSavedDefault()
        {
            string cur = NormalizeUrl(CurrentUrl).TrimEnd('/');
            string saved = NormalizeUrl(SavedDefaultHomeUrl).TrimEnd('/');
            return string.Equals(cur, saved, StringComparison.OrdinalIgnoreCase);
        }

        public void ApplyBrowserThemeToVisualizer(bool forceClearIfNoArt)
        {
            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
            if (_visualizer == null) return;

            if (_hasArtworkPalette)
            {
                _visualizer.SetAlbumArtPalette(true, _artworkPalette);
            }
            else if (forceClearIfNoArt)
            {
                _visualizer.SetAlbumArtPalette(false, default);
            }
        }

        private void ClearCachedArtwork()
        {
            _artworkRequestId++;
            if (_artworkCoroutine != null)
            {
                StopCoroutine(_artworkCoroutine);
                _artworkCoroutine = null;
            }
            _loadedArtworkUrl = string.Empty;
            _hasArtworkPalette = false;
            if (_artworkSprite != null)
            {
                Destroy(_artworkSprite);
                _artworkSprite = null;
            }
            if (_artworkTexture != null)
            {
                Destroy(_artworkTexture);
                _artworkTexture = null;
            }
        }

        private void SyncBrowserArtworkAndTheme(string artworkUrl, bool forceApply)
        {
            if (_player != null && _player.IsPlaying) return;
            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();

            if (string.IsNullOrWhiteSpace(artworkUrl))
            {
                ClearCachedArtwork();
                _visualizer?.SetAlbumArtPalette(false, default);
                return;
            }

            if (string.Equals(artworkUrl, _loadedArtworkUrl, StringComparison.Ordinal) && _hasArtworkPalette)
            {
                if (forceApply)
                {
                    _visualizer?.SetAlbumArtPalette(true, _artworkPalette);
                }
                return;
            }

            // Clear any prior local song album palette right away while the browser artwork loads.
            _visualizer?.SetAlbumArtPalette(false, default);
            if (_artworkCoroutine != null)
            {
                StopCoroutine(_artworkCoroutine);
                _artworkCoroutine = null;
            }
            _loadedArtworkUrl = artworkUrl;
            _hasArtworkPalette = false;
            int reqId = ++_artworkRequestId;
            _artworkCoroutine = StartCoroutine(FetchBrowserArtworkCoroutine(artworkUrl, reqId));
        }

        private IEnumerator FetchBrowserArtworkCoroutine(string url, int requestId)
        {
            using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(url, false))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();

                if (requestId != _artworkRequestId) yield break;
                _artworkCoroutine = null;

                if (req.result == UnityWebRequest.Result.Success)
                {
                    Texture2D tex = DownloadHandlerTexture.GetContent(req);
                    if (tex != null)
                    {
                        SonicThemeColors palette = SonicMusicLibraryManager.ExtractPaletteFromAlbumCover(tex);
                        if (_artworkSprite != null) Destroy(_artworkSprite);
                        if (_artworkTexture != null) Destroy(_artworkTexture);

                        _artworkTexture = tex;
                        _artworkSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        _artworkPalette = palette;
                        _hasArtworkPalette = true;

                        if (IsBrowserAudioActive && (_player == null || !_player.IsPlaying))
                        {
                            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();
                            _visualizer?.SetAlbumArtPalette(true, _artworkPalette);
                        }
                        OnBrowserStateChanged?.Invoke();
                    }
                }
            }
        }

        public void BindViewport(RectTransform viewportRect, RawImage punchoutGraphic, GameObject placeholderRoot)
        {
            _viewportRect = viewportRect;
            _punchoutGraphic = punchoutGraphic;
            _placeholderRoot = placeholderRoot;

            if (_punchoutGraphic != null)
            {
                // Non-zero vertex alpha + cullTransparentMesh=false required so CanvasRenderer still
                // submits the quad to URP where SonicBrowserUnderlayPunchout clears RGB+A to (0,0,0,0).
                _punchoutGraphic.color = Color.white;
                if (_punchoutGraphic.canvasRenderer != null)
                {
                    _punchoutGraphic.canvasRenderer.cullTransparentMesh = false;
                }

                Shader punchShader = Resources.Load<Shader>("Shaders/SonicBrowserUnderlayPunchout");
                if (punchShader == null) punchShader = Shader.Find("SonicTopography/BrowserUnderlayPunchout");
                if (punchShader != null)
                {
                    if (_punchoutMaterial == null)
                    {
                        _punchoutMaterial = new Material(punchShader);
                    }
                    _punchoutGraphic.material = _punchoutMaterial;
                    Debug.Log("[SonicBrowser] Bound punchout shader to BrowserPunchout RawImage.");
                }
                else
                {
                    Debug.LogWarning("[SonicBrowser] Underlay punchout shader missing; browser viewport may appear tinted.");
                }
                _punchoutGraphic.enabled = false;
            }

            if (_placeholderRoot != null)
            {
                _placeholderRoot.SetActive(true);
            }
        }

        public void EnsureBrowserInitialized()
        {
            if (_initialized) return;
            _initialized = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureOVRManagerReady();
            CreateOverlayLayer();

            try
            {
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    if (activity != null)
                    {
                        _bridge = new AndroidJavaObject("com.dexter.audiofloor.SonicGeckoBrowserBridge", activity, _pendingUrl);
                        Debug.Log("[SonicBrowser] Created SonicGeckoBrowserBridge for GeckoView");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SonicBrowser] Failed creating SonicGeckoBrowserBridge: {ex}");
            }

            if (_overlay != null && _overlay.externalSurfaceObject != IntPtr.Zero)
            {
                AttachOverlaySurfaceToJava();
            }
#else
            Debug.Log("[SonicBrowser] Initialized Editor preview placeholder mode.");
#endif
        }

        public void SetViewportVisible(bool visible, float canvasAlpha)
        {
            _isViewportVisible = visible;
            _canvasAlpha = canvasAlpha;
            if (visible && !_initialized)
            {
                EnsureBrowserInitialized();
            }
            SyncOverlayPoseAndVisibility();
        }

        public void OnLocalPlaybackStarted()
        {
            if (_preferBrowserControls || IsBrowserAudioPlaying)
            {
                _preferBrowserControls = false;
                PauseBrowserAudio();
                OnBrowserStateChanged?.Invoke();
            }
        }

        public void PauseBrowserAudio()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("pauseAudio");
#endif
            if (_audioEngine != null)
            {
                _audioEngine.ClearExternalAudio();
            }
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _pendingUrl = url.Trim();
            EnsureBrowserInitialized();
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("navigate", _pendingUrl);
#else
            if (_status != null)
            {
                _status.url = _pendingUrl;
                OnBrowserStateChanged?.Invoke();
            }
#endif
        }

        public void GoBack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("goBack");
#endif
        }

        public void GoForward()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("goForward");
#endif
        }

        public void Reload()
        {
            EnsureBrowserInitialized();
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("reload");
#endif
        }

        public void TogglePlayPause()
        {
            _preferBrowserControls = true;
            if (_player != null && _player.IsPlaying)
            {
                _player.PauseIfPlaying();
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("togglePlayPause");
#endif
        }

        public void NextTrack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("nextTrack");
#endif
        }

        public void PrevTrack()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("prevTrack");
#endif
        }

        public void SetVolume(float volume01)
        {
            float clamped = Mathf.Clamp01(volume01);
            if (_status != null) _status.volume = clamped;
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("setVolume", clamped);
#endif
            OnBrowserStateChanged?.Invoke();
        }

        public void RequestManualKeyboard()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge != null)
            {
                _bridge.Call("requestActiveInputFocus");
            }
#endif
            OpenWebVirtualKeyboard(_status != null ? _status.softInputVal : string.Empty,
                _status != null && _status.softInputPass);
        }

        public void SendEnterKey()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Call("sendEnterKey");
#endif
        }

        public void SendTouchDown(Vector2 uvTopLeft)
        {
            SendTouchInternal(0, uvTopLeft); // MotionEvent.ACTION_DOWN = 0
        }

        public void SendTouchMove(Vector2 uvTopLeft)
        {
            SendTouchInternal(2, uvTopLeft); // MotionEvent.ACTION_MOVE = 2
        }

        public void SendTouchUp(Vector2 uvTopLeft)
        {
            SendTouchInternal(1, uvTopLeft); // MotionEvent.ACTION_UP = 1
        }

        public void SendScroll(Vector2 uvTopLeft, float vScroll, float hScroll)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            float xPx = Mathf.Clamp01(uvTopLeft.x) * SurfaceWidth;
            float yPx = Mathf.Clamp01(uvTopLeft.y) * SurfaceHeight;
            _bridge.Call("sendScrollEvent", xPx, yPx, vScroll, hScroll);
#endif
        }

        private void SendTouchInternal(int action, Vector2 uvTopLeft)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            float xPx = Mathf.Clamp01(uvTopLeft.x) * SurfaceWidth;
            float yPx = Mathf.Clamp01(uvTopLeft.y) * SurfaceHeight;
            _bridge.Call("sendTouchEvent", action, xPx, yPx);
#endif
        }

        public void SyncOverlayPoseNow()
        {
            SyncOverlayPoseAndVisibility();
        }

        private void Update()
        {
            if (!_initialized) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (_overlay != null && _overlay.externalSurfaceObject != IntPtr.Zero && !_surfaceAttached)
            {
                AttachOverlaySurfaceToJava();
            }

            if (_bridge != null)
            {
                int seq = _bridge.Call<int>("getFftSequence");
                if (seq != _lastFftSeq)
                {
                    _lastFftSeq = seq;
                    bool playing = _bridge.Call<bool>("isFftPlaying");
                    byte[] bins = _bridge.Call<byte[]>("getLatestSpectrumBytes");
                    if (_audioEngine != null)
                    {
                        if (playing && bins != null && bins.Length >= 512)
                        {
                            _audioEngine.SetExternalAudioSpectrum(bins, true);
                        }
                        else if (!playing)
                        {
                            _audioEngine.ClearExternalAudio();
                        }
                    }
                }

                if (Time.unscaledTime >= _nextStatusPollTime)
                {
                    _nextStatusPollTime = Time.unscaledTime + 0.12f;
                    PollStatusFromJava();
                }
            }
#endif

            PollWebVirtualKeyboard();
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            SyncOverlayPoseAndVisibility();
        }

        private void SyncOverlayPoseAndVisibility()
        {
            bool showOverlay = _isViewportVisible && _surfaceAttached && _canvasAlpha > 0.85f;

            if (_overlay != null)
            {
                _overlay.hidden = !showOverlay;
            }

            if (_punchoutGraphic != null && _punchoutGraphic.enabled != showOverlay)
            {
                _punchoutGraphic.enabled = showOverlay;
            }

            if (_placeholderRoot != null && _placeholderRoot.activeSelf == showOverlay)
            {
                _placeholderRoot.SetActive(!showOverlay);
            }

            if (_overlayGo != null && _viewportRect != null && showOverlay)
            {
                _viewportRect.GetWorldCorners(_worldCorners);
                Vector3 center = (_worldCorners[0] + _worldCorners[2]) * 0.5f;
                float worldW = Vector3.Distance(_worldCorners[0], _worldCorners[3]);
                float worldH = Vector3.Distance(_worldCorners[0], _worldCorners[1]);

                Rect r = _viewportRect.rect;
                float scaleX = r.width > 2f ? (r.width - 1f) / r.width : 1f;
                float scaleY = r.height > 2f ? (r.height - 1f) / r.height : 1f;

                Transform t = _overlayGo.transform;
                t.position = center;
                t.rotation = _viewportRect.rotation;
                t.localScale = new Vector3(Mathf.Max(0.01f, worldW * scaleX), Mathf.Max(0.01f, worldH * scaleY), 1f);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void EnsureOVRManagerReady()
        {
            if (OVRManager.instance == null)
            {
                OVRManager mgr = FindFirstObjectByType<OVRManager>(FindObjectsInactive.Include);
                if (mgr == null)
                {
                    mgr = gameObject.AddComponent<OVRManager>();
                }
                if (mgr != null)
                {
                    mgr.enableDynamicResolution = false;
                    mgr.isInsightPassthroughEnabled = false;
                    mgr.resetTrackerOnLoad = false;
                    mgr.launchSimultaneousHandsControllersOnStartup = false;
                    // Leave trackingOriginType untouched: on OpenXR >= 1.11, setting Stage calls
                    // OpenXRSettings.RefreshRecenterSpace() and shifts XROrigin away from (0,0,0).
                }
            }

            SonicTopographyVRBootstrap.ConfigureXRPerformanceAndResolution();
        }

        private void CreateOverlayLayer()
        {
            if (_overlayGo != null) return;
            _overlayGo = new GameObject("SonicBrowserOverlay");
            _overlayGo.transform.SetParent(null, false);

            _overlay = _overlayGo.AddComponent<OVROverlay>();
            _overlay.currentOverlayType = OVROverlay.OverlayType.Underlay;
            _overlay.currentOverlayShape = OVROverlay.OverlayShape.Quad;
            _overlay.isExternalSurface = true;
            _overlay.externalSurfaceWidth = SurfaceWidth;
            _overlay.externalSurfaceHeight = SurfaceHeight;
            _overlay.noDepthBufferTesting = true;
            _overlay.useAutomaticFiltering = true;
            _overlay.isAlphaPremultiplied = false;
            _overlay.hidden = true;
            _overlay.externalSurfaceObjectCreated += OnExternalSurfaceObjectCreated;
        }

        private void OnExternalSurfaceObjectCreated()
        {
            Debug.Log("[SonicBrowser] External surface created");
            AttachOverlaySurfaceToJava();
        }

        private void AttachOverlaySurfaceToJava()
        {
            if (_overlay == null || _overlay.externalSurfaceObject == IntPtr.Zero || _bridge == null)
            {
                return;
            }

            try
            {
                IntPtr bridgePtr = _bridge.GetRawObject();
                IntPtr bridgeClass = _bridge.GetRawClass();
                if (bridgePtr == IntPtr.Zero || bridgeClass == IntPtr.Zero) return;

                if (_attachSurfaceMethodId == IntPtr.Zero)
                {
                    _attachSurfaceMethodId = AndroidJNI.GetMethodID(bridgeClass, "attachSurface", "(Landroid/view/Surface;II)V");
                }
                if (_attachSurfaceMethodId == IntPtr.Zero)
                {
                    Debug.LogError("[SonicBrowser] Failed resolving attachSurface methodID");
                    return;
                }

                jvalue[] args = new jvalue[3];
                args[0].l = _overlay.externalSurfaceObject;
                args[1].i = SurfaceWidth;
                args[2].i = SurfaceHeight;
                AndroidJNI.CallVoidMethod(bridgePtr, _attachSurfaceMethodId, args);

                IntPtr jniEx = AndroidJNI.ExceptionOccurred();
                if (jniEx != IntPtr.Zero)
                {
                    AndroidJNI.ExceptionDescribe();
                    AndroidJNI.ExceptionClear();
                    AndroidJNI.DeleteLocalRef(jniEx);
                    return;
                }

                _surfaceAttached = true;
                SyncOverlayPoseAndVisibility();
                Debug.Log("[SonicBrowser] Bound OVROverlay external Surface to GeckoDisplay");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SonicBrowser] AttachOverlaySurfaceToJava failed: {ex}");
            }
        }

        private void PollStatusFromJava()
        {
            if (_bridge == null) return;
            string json = _bridge.Call<string>("getStatusJson");
            if (string.IsNullOrEmpty(json)) return;

            SonicBrowserStatus nextStatus = null;
            try
            {
                nextStatus = JsonUtility.FromJson<SonicBrowserStatus>(json);
            }
            catch
            {
                return;
            }
            if (nextStatus == null) return;

            bool changed = _status == null ||
                           _status.url != nextStatus.url ||
                           _status.title != nextStatus.title ||
                           _status.artist != nextStatus.artist ||
                           _status.playing != nextStatus.playing ||
                           _status.canGoBack != nextStatus.canGoBack ||
                           _status.canGoForward != nextStatus.canGoForward ||
                           _status.loading != nextStatus.loading ||
                           Mathf.Abs(_status.volume - nextStatus.volume) > 0.02f;

            _status = nextStatus;

            if (nextStatus.playing && !_wasPlaying)
            {
                _preferBrowserControls = true;
                if (_player != null && _player.IsPlaying)
                {
                    _player.PauseIfPlaying();
                }
                SyncBrowserArtworkAndTheme(nextStatus.artworkUrl, forceApply: true);
            }
            else if (nextStatus.playing && !string.Equals(nextStatus.artworkUrl ?? string.Empty, _loadedArtworkUrl, StringComparison.Ordinal))
            {
                SyncBrowserArtworkAndTheme(nextStatus.artworkUrl, forceApply: false);
            }
            _wasPlaying = nextStatus.playing;

            bool isWebKbActive = SonicQuestNativeKeyboard.IsSupported
                ? (_webNativeSessionId != 0 && SonicQuestNativeKeyboard.IsSessionActive(_webNativeSessionId))
                : (_webKeyboard != null && _webKeyboard.active);

            if (nextStatus.softInputSeq != _lastHandledSoftInputSeq && nextStatus.softInputSeq > 0)
            {
                _lastHandledSoftInputSeq = nextStatus.softInputSeq;
                if (!isWebKbActive)
                {
                    _lastHandledSoftInputValSeq = nextStatus.softInputValSeq;
                    OpenWebVirtualKeyboard(nextStatus.softInputVal ?? string.Empty, nextStatus.softInputPass);
                    isWebKbActive = SonicQuestNativeKeyboard.IsSupported
                        ? (_webNativeSessionId != 0 && SonicQuestNativeKeyboard.IsSessionActive(_webNativeSessionId))
                        : (_webKeyboard != null && _webKeyboard.active);
                }
            }

            if (isWebKbActive && SonicQuestNativeKeyboard.IsSupported && nextStatus.softInputValSeq != _lastHandledSoftInputValSeq)
            {
                _lastHandledSoftInputValSeq = nextStatus.softInputValSeq;
                if (!_webUserHasTypedInSession && (Time.unscaledTime - _webNativeOpenedTime) < 0.6f)
                {
                    string nextVal = nextStatus.softInputVal ?? string.Empty;
                    if (nextStatus.softInputPass != _webNativeIsPassword)
                    {
                        OpenWebVirtualKeyboard(nextVal, nextStatus.softInputPass);
                    }
                    else if (!string.Equals(nextVal, _lastWebKeyboardText, StringComparison.Ordinal))
                    {
                        _lastWebKeyboardText = nextVal;
                        SonicQuestNativeKeyboard.SetText(_webNativeSessionId, nextVal);
                        _webNativeLastSeq = SonicQuestNativeKeyboard.GetTextSeq();
                    }
                }
            }

            if (changed)
            {
                OnBrowserStateChanged?.Invoke();
            }
        }
#endif

        private void OpenWebVirtualKeyboard(string initialText, bool isPassword)
        {
            SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
            SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(true);
            AudioFloorControllerActions.CancelAnyActiveJumpImmediate("BrowserWebInput.Open");

#if UNITY_ANDROID && !UNITY_EDITOR
            if (SonicQuestNativeKeyboard.IsSupported)
            {
                _lastWebKeyboardText = initialText ?? string.Empty;
                _webUserHasTypedInSession = false;
                _webNativeIsPassword = isPassword;
                _webNativeSessionId = SonicQuestNativeKeyboard.Open(
                    _lastWebKeyboardText,
                    isPassword ? "Enter password" : "Type text",
                    isPassword,
                    isUrl: false);
                _webNativeLastSeq = SonicQuestNativeKeyboard.GetTextSeq();
                _webNativeOpenedTime = Time.unscaledTime;
                if (_webNativeSessionId <= 0)
                {
                    SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(false);
                }
                return;
            }
#endif

            if (!TouchScreenKeyboard.isSupported) return;

            TouchScreenKeyboard.hideInput = false;
            _lastWebKeyboardText = initialText ?? string.Empty;
            _webKeyboard = TouchScreenKeyboard.Open(
                _lastWebKeyboardText,
                TouchScreenKeyboardType.Default,
                false,
                false,
                isPassword,
                false,
                isPassword ? "Enter password" : "Type text"
            );
        }

        public void CloseWebVirtualKeyboard()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_webNativeSessionId != 0)
            {
                SonicQuestNativeKeyboard.Close(_webNativeSessionId);
                _webNativeSessionId = 0;
                _webNativeLastSeq = -1;
            }
#endif
            if (_webKeyboard != null)
            {
                _webKeyboard.active = false;
                _webKeyboard = null;
            }
            SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(false);
        }

        private void PollWebVirtualKeyboard()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (SonicQuestNativeKeyboard.IsSupported)
            {
                if (_webNativeSessionId == 0) return;

                bool active = SonicQuestNativeKeyboard.IsSessionActive(_webNativeSessionId);
                int activeId = SonicQuestNativeKeyboard.ActiveSessionId;
                bool belongsToUs = activeId == _webNativeSessionId || (!active && activeId == 0);

                if (belongsToUs)
                {
                    int seq = SonicQuestNativeKeyboard.GetTextSeq();
                    if (seq != _webNativeLastSeq)
                    {
                        _webNativeLastSeq = seq;
                        _webUserHasTypedInSession = true;
                        string current = SonicQuestNativeKeyboard.GetText() ?? string.Empty;
                        if (!string.Equals(current, _lastWebKeyboardText, StringComparison.Ordinal))
                        {
                            _lastWebKeyboardText = current;
                            _bridge?.Call("sendTextToPage", current, false);
                        }
                    }
                }

                if (active)
                {
                    SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
                    SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(true);
                }
                else
                {
                    bool submitted = belongsToUs && SonicQuestNativeKeyboard.WasSubmitPressed(_webNativeSessionId);
                    Debug.Log($"[SonicBrowser] WebNativeKeyboard closed (session={_webNativeSessionId}, submitted={submitted}, activeId={activeId})");
                    _webNativeSessionId = 0;
                    _webNativeLastSeq = -1;
                    SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(false);
                    SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
                    AudioFloorControllerActions.CancelAnyActiveJumpImmediate("BrowserWebInput.Close");
                }
                return;
            }
#endif

            if (_webKeyboard == null) return;

            SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
            SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(_webKeyboard.active);

            string kbCurrent = _webKeyboard.text ?? string.Empty;
            if (!string.Equals(kbCurrent, _lastWebKeyboardText, StringComparison.Ordinal))
            {
                _lastWebKeyboardText = kbCurrent;
#if UNITY_ANDROID && !UNITY_EDITOR
                _bridge?.Call("sendTextToPage", kbCurrent, false);
#endif
            }

            if (_webKeyboard.status == TouchScreenKeyboard.Status.Done ||
                _webKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                !_webKeyboard.active)
            {
                // Closing the Quest virtual keyboard reports Status.Done even on dismiss/X;
                // never auto-submit Enter on close so users can use the dedicated Return button.
                Debug.Log($"[SonicBrowser] WebKeyboard closed (status={_webKeyboard.status}, active={_webKeyboard.active})");
                _webKeyboard = null;
                SonicQuestVirtualKeyboardOpener.SetExternalKeyboardActive(false);
                SonicQuestVirtualKeyboardOpener.MarkKeyboardActivity();
                AudioFloorControllerActions.CancelAnyActiveJumpImmediate("BrowserWebInput.Close");
            }
        }

        private void OnDestroy()
        {
            CloseWebVirtualKeyboard();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_overlay != null)
            {
                _overlay.externalSurfaceObjectCreated -= OnExternalSurfaceObjectCreated;
            }
            if (_bridge != null)
            {
                try
                {
                    _bridge.Call("shutdown");
                    _bridge.Dispose();
                }
                catch { }
                _bridge = null;
            }
#endif
            if (_overlayGo != null)
            {
                Destroy(_overlayGo);
                _overlayGo = null;
            }
            if (_punchoutMaterial != null)
            {
                Destroy(_punchoutMaterial);
                _punchoutMaterial = null;
            }
            ClearCachedArtwork();
        }
    }

    /// <summary>
    /// Converts XR pointer / UI drag & scroll events on the Browser viewport rect
    /// into normalized top-left UVs for GeckoView PanZoomController.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class SonicBrowserViewportInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
    {
        private SonicWebBrowserManager _browser;
        private RectTransform _rect;
        private Vector2 _lastUv = new Vector2(0.5f, 0.5f);

        public void Initialize(SonicWebBrowserManager browser)
        {
            _browser = browser;
            _rect = GetComponent<RectTransform>();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_browser == null || eventData == null) return;
            _browser.SendTouchDown(ComputeTopLeftUv(eventData));
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_browser == null || eventData == null) return;
            _browser.SendTouchMove(ComputeTopLeftUv(eventData));
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_browser == null || eventData == null) return;
            _browser.SendTouchUp(ComputeTopLeftUv(eventData));
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (_browser == null || eventData == null) return;
            if (Mathf.Abs(eventData.scrollDelta.y) < 0.75f && Mathf.Abs(eventData.scrollDelta.x) < 0.75f) return;
            Vector2 uv = ComputeTopLeftUv(eventData);
            _browser.SendScroll(uv, eventData.scrollDelta.y * 0.35f, -eventData.scrollDelta.x * 0.35f);
        }

        private Vector2 ComputeTopLeftUv(PointerEventData eventData)
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            if (_rect == null) return _lastUv;

            Vector2 localPt;
            if (eventData.pointerCurrentRaycast.isValid && eventData.pointerCurrentRaycast.worldPosition != Vector3.zero)
            {
                Vector3 lp3 = _rect.InverseTransformPoint(eventData.pointerCurrentRaycast.worldPosition);
                localPt = new Vector2(lp3.x, lp3.y);
            }
            else if (eventData.pointerPressRaycast.isValid && eventData.pointerPressRaycast.worldPosition != Vector3.zero)
            {
                Vector3 lp3 = _rect.InverseTransformPoint(eventData.pointerPressRaycast.worldPosition);
                localPt = new Vector2(lp3.x, lp3.y);
            }
            else
            {
                Camera cam = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
                if (cam == null) cam = Camera.main;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position, cam, out localPt))
                {
                    return _lastUv;
                }
            }

            Rect r = _rect.rect;
            float u = Mathf.Clamp01((localPt.x - r.xMin) / Mathf.Max(1f, r.width));
            float vFromBottom = Mathf.Clamp01((localPt.y - r.yMin) / Mathf.Max(1f, r.height));
            _lastUv = new Vector2(u, 1f - vFromBottom);
            return _lastUv;
        }
    }
}
