using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SonicTopography
{
    /// <summary>
    /// Compact, detachable visionOS-styled AR mini music controller ("Keychain").
    /// Floats cleanly outside the terrain rim by default, stays at constant readable physical size
    /// regardless of terrain scale, and supports 1-click Snap Back after being grabbed elsewhere.
    /// </summary>
    public class SonicArKeychainController : MonoBehaviour
    {
        private const float CanvasWidth = 376f;
        private const float CanvasHeight = 96f;
        private const float WorldScale = 0.00066f; // ~24.8cm x 6.3cm physical card

        private SonicMixedRealityManager _mr;
        private SonicMusicPlayerController _player;
        private SonicWebBrowserManager _browser;
        private SonicTopographyVisualizer _visualizer;

        private GameObject _rootGo;
        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private BoxCollider _grabCollider;

        private Image _albumArtImage;
        private GameObject _albumArtFallbackGo;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _subtitleText;
        private TextMeshProUGUI _playIconText;
        private Image _snapBackBgImage;
        private TextMeshProUGUI _snapBackLabel;
        private Image _glassBgImage;
        private SonicGlassSurface _glassSurface;

        private bool _isDetached = false;
        private bool _isDragging = false;
        private bool _isSnappingBack = false;
        private Vector3 _dragLocalOffsetWS;
        private float _dragRayDist = 1.0f;
        private bool _dragViaRay = false;

        private bool _hasPoseInitialized = false;
        private Vector3 _parkedPos;
        private Quaternion _parkedRot;
        private float _nextMetaSyncTime = 0f;
        private Color _lastThemeTint = Color.clear;

        private static Sprite _roundedPillSprite;
        private static Sprite _circleSprite;

        public bool IsVisible => _rootGo != null && _rootGo.activeSelf && _canvasGroup != null && _canvasGroup.alpha > 0.25f;
        public Collider GrabCollider => _grabCollider;

        public void Initialize(
            SonicMixedRealityManager mr,
            SonicMusicPlayerController player,
            SonicWebBrowserManager browser,
            SonicTopographyVisualizer visualizer)
        {
            _mr = mr;
            _player = player;
            _browser = browser;
            _visualizer = visualizer;

            BuildKeychainUiIfNeeded();

            if (_player != null)
            {
                _player.OnTrackChanged -= HandleMediaStateChanged;
                _player.OnTrackChanged += HandleMediaStateChanged;
                _player.OnPlaybackStateChanged -= HandleMediaStateChanged;
                _player.OnPlaybackStateChanged += HandleMediaStateChanged;
            }
            if (_browser != null)
            {
                _browser.OnBrowserStateChanged -= HandleMediaStateChanged;
                _browser.OnBrowserStateChanged += HandleMediaStateChanged;
            }

            SyncMetadataAndButtons();
        }

        private void OnDestroy()
        {
            if (_player != null)
            {
                _player.OnTrackChanged -= HandleMediaStateChanged;
                _player.OnPlaybackStateChanged -= HandleMediaStateChanged;
            }
            if (_browser != null)
            {
                _browser.OnBrowserStateChanged -= HandleMediaStateChanged;
            }
            if (_rootGo != null) Destroy(_rootGo);
        }

        private void HandleMediaStateChanged(SonicMusicTrack _) => SyncMetadataAndButtons();
        private void HandleMediaStateChanged() => SyncMetadataAndButtons();

        public void BeginGrab(Ray ray, Vector3 grabPointWS, bool useRay)
        {
            if (_rootGo == null) return;
            _isDragging = true;
            _isDetached = true;
            _isSnappingBack = false;
            _dragViaRay = useRay;

            if (useRay && _grabCollider != null && _grabCollider.Raycast(ray, out RaycastHit hit, 12f))
            {
                _dragRayDist = hit.distance;
                _dragLocalOffsetWS = _rootGo.transform.position - hit.point;
            }
            else if (useRay)
            {
                _dragRayDist = Mathf.Clamp(Vector3.Distance(ray.origin, _rootGo.transform.position), 0.25f, 8f);
                _dragLocalOffsetWS = _rootGo.transform.position - (ray.origin + ray.direction * _dragRayDist);
            }
            else
            {
                _dragLocalOffsetWS = _rootGo.transform.position - grabPointWS;
            }
            SyncSnapButtonVisual();
        }

        public void UpdateGrab(Ray ray, Vector3 grabPointWS)
        {
            if (_rootGo == null || !_isDragging) return;
            Vector3 desiredPos = _dragViaRay
                ? (ray.origin + ray.direction * _dragRayDist + _dragLocalOffsetWS)
                : (grabPointWS + _dragLocalOffsetWS);

            float t = 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime);
            _parkedPos = Vector3.Lerp(_rootGo.transform.position, desiredPos, t);

            if (Camera.main != null)
            {
                Vector3 toCam = Camera.main.transform.position - _parkedPos;
                if (toCam.sqrMagnitude > 0.002f)
                {
                    _parkedRot = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
                }
            }
            _rootGo.transform.SetPositionAndRotation(_parkedPos, _parkedRot);
        }

        public void SetDragged(bool dragging)
        {
            _isDragging = dragging;
        }

        public void SnapBackToTerrain()
        {
            _isDragging = false;
            _isDetached = false;
            _isSnappingBack = true;
            SyncSnapButtonVisual();
        }

        private void LateUpdate()
        {
            if (_mr == null || _rootGo == null) return;

            float blend = _mr.ArBlend;
            bool shouldBeActive = blend > 0.15f || _mr.IsArMode;
            if (_rootGo.activeSelf != shouldBeActive)
            {
                _rootGo.SetActive(shouldBeActive);
                if (shouldBeActive)
                {
                    _hasPoseInitialized = false;
                    SyncMetadataAndButtons();
                }
            }

            if (!shouldBeActive) return;

            float fadeAlpha = Mathf.InverseLerp(0.25f, 0.92f, blend);
            _canvasGroup.alpha = fadeAlpha;
            _canvasGroup.interactable = fadeAlpha > 0.65f;
            _canvasGroup.blocksRaycasts = fadeAlpha > 0.65f;
            if (_grabCollider != null) _grabCollider.enabled = fadeAlpha > 0.65f;

            // Keep crisp border glass tint synchronized with active terrain colors
            if (_visualizer != null && _glassBgImage != null)
            {
                Color c = Color.Lerp(_visualizer.CurrentColors.uCoolCore, _visualizer.CurrentColors.uRippleColor, 0.35f);
                if (!ApproximatelyColor(c, _lastThemeTint))
                {
                    _lastThemeTint = c;
                    _glassBgImage.color = new Color(0.06f + c.r * 0.08f, 0.07f + c.g * 0.08f, 0.09f + c.b * 0.09f, 0.88f);
                }
            }

            if (!_isDragging)
            {
                ComputeDockedPose(out Vector3 dockedPos, out Quaternion dockedRot);
                if (!_hasPoseInitialized)
                {
                    _hasPoseInitialized = true;
                    _rootGo.transform.SetPositionAndRotation(dockedPos, dockedRot);
                    _parkedPos = dockedPos;
                    _parkedRot = dockedRot;
                }
                else if (!_isDetached)
                {
                    float followRate = _isSnappingBack ? 12f : 10f;
                    float k = 1f - Mathf.Exp(-followRate * Time.unscaledDeltaTime);
                    Vector3 nextPos = Vector3.Lerp(_rootGo.transform.position, dockedPos, k);
                    Quaternion nextRot = Quaternion.Slerp(_rootGo.transform.rotation, dockedRot, k);
                    _rootGo.transform.SetPositionAndRotation(nextPos, nextRot);
                    if (_isSnappingBack && Vector3.Distance(nextPos, dockedPos) < 0.008f)
                    {
                        _isSnappingBack = false;
                    }
                }
                else
                {
                    // Gentle yaw face when parked independently across the room
                    if (Camera.main != null)
                    {
                        Vector3 toCam = Camera.main.transform.position - _rootGo.transform.position;
                        if (toCam.sqrMagnitude > 0.01f)
                        {
                            Quaternion targetYaw = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
                            _rootGo.transform.rotation = Quaternion.Slerp(
                                _rootGo.transform.rotation, targetYaw, 1f - Mathf.Exp(-3.5f * Time.unscaledDeltaTime));
                        }
                    }
                }
            }

            if (Time.unscaledTime >= _nextMetaSyncTime)
            {
                _nextMetaSyncTime = Time.unscaledTime + 0.25f;
                SyncMetadataAndButtons();
            }
        }

        private void ComputeDockedPose(out Vector3 dockedPos, out Quaternion dockedRot)
        {
            _mr.GetCurrentTerrainPose(out Vector3 centerWS, out Quaternion rotWS, out _, out float radiusMeters);
            Vector3 upNormal = (rotWS * Vector3.up).normalized;

            Camera cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : (centerWS - Vector3.forward);

            bool wallMounted = Mathf.Abs(upNormal.y) < 0.45f;
            if (wallMounted)
            {
                // Dock cleanly beneath wall-mounted sculpture, floating clear of bottom rim
                dockedPos = centerWS + Vector3.down * (radiusMeters + 0.115f) + upNormal * 0.06f;
            }
            else
            {
                // Dock outside nearest horizontal edge toward user so pillars & waves remain unobstructed
                Vector3 toUserXZ = camPos - centerWS;
                toUserXZ.y = 0f;
                if (toUserXZ.sqrMagnitude < 0.0004f)
                {
                    toUserXZ = cam != null ? -new Vector3(cam.transform.forward.x, 0f, cam.transform.forward.z) : -Vector3.forward;
                }
                toUserXZ.Normalize();
                dockedPos = centerWS + toUserXZ * (radiusMeters + 0.125f) + Vector3.up * 0.055f;
            }

            Vector3 lookAwayFromUser = dockedPos - camPos;
            if (lookAwayFromUser.sqrMagnitude < 0.001f) lookAwayFromUser = Vector3.forward;
            Vector3 flatLook = new Vector3(lookAwayFromUser.x, lookAwayFromUser.y * 0.45f, lookAwayFromUser.z).normalized;
            dockedRot = Quaternion.LookRotation(flatLook, Vector3.up);
        }

        private void SyncMetadataAndButtons()
        {
            if (_rootGo == null) return;

            bool useBrowser = _browser != null && _browser.IsBrowserAudioActive &&
                              (_player == null || !_player.IsPlaying);

            string title = "No Track Selected";
            string artist = "AudioFloor";
            bool playing = false;
            Sprite artSprite = null;

            if (useBrowser)
            {
                title = !string.IsNullOrEmpty(_browser.CurrentTitle) ? _browser.CurrentTitle : "Browser Audio";
                artist = !string.IsNullOrEmpty(_browser.CurrentArtist) ? _browser.CurrentArtist : "Web Player";
                playing = _browser.IsBrowserAudioPlaying;
                artSprite = _browser.CurrentArtworkSprite;
            }
            else if (_player != null && _player.CurrentTrack != null)
            {
                SonicMusicTrack tr = _player.CurrentTrack;
                title = !string.IsNullOrEmpty(tr.title) ? tr.title : "Unknown Track";
                artist = !string.IsNullOrEmpty(tr.artist) ? tr.artist : "AudioFloor";
                playing = _player.IsPlaying;
                if (tr.coverSprite != null)
                {
                    artSprite = tr.coverSprite;
                }
                else if (tr.hasRealAlbumArt && tr.coverTexture != null)
                {
                    artSprite = CreateTempArtSprite(tr.coverTexture, tr);
                }
            }

            if (_titleText != null) _titleText.text = Ellipsize(title, 18);
            if (_subtitleText != null) _subtitleText.text = Ellipsize(artist, 22);
            if (_playIconText != null) _playIconText.text = playing ? "II" : ">";

            if (_albumArtImage != null)
            {
                bool hasArt = artSprite != null;
                _albumArtImage.sprite = artSprite;
                _albumArtImage.enabled = hasArt;
                if (_albumArtFallbackGo != null) _albumArtFallbackGo.SetActive(!hasArt);
            }

            SyncSnapButtonVisual();
        }

        private void SyncSnapButtonVisual()
        {
            if (_snapBackBgImage != null)
            {
                _snapBackBgImage.color = _isDetached
                    ? new Color(0.28f, 0.62f, 0.98f, 0.88f)
                    : new Color(1f, 1f, 1f, 0.14f);
            }
            if (_snapBackLabel != null)
            {
                _snapBackLabel.color = _isDetached ? Color.white : new Color(0.75f, 0.80f, 0.86f, 0.72f);
            }
        }

        private static Sprite CreateTempArtSprite(Texture2D tex, SonicMusicTrack track)
        {
            if (tex == null) return null;
            Sprite s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            track.coverSprite = s;
            return s;
        }

        private void HandlePrevClick()
        {
            if (_browser != null && _browser.IsBrowserAudioActive && (_player == null || !_player.IsPlaying))
            {
                _browser.PrevTrack();
            }
            else
            {
                _player?.PreviousTrack();
            }
        }

        private void HandlePlayPauseClick()
        {
            if (_browser != null && _browser.IsBrowserAudioActive && (_player == null || !_player.IsPlaying))
            {
                _browser.TogglePlayPause();
            }
            else
            {
                _player?.TogglePlayPause();
            }
        }

        private void HandleNextClick()
        {
            if (_browser != null && _browser.IsBrowserAudioActive && (_player == null || !_player.IsPlaying))
            {
                _browser.NextTrack();
            }
            else
            {
                _player?.NextTrack();
            }
        }

        private void BuildKeychainUiIfNeeded()
        {
            if (_rootGo != null) return;

            EnsureSprites();

            _rootGo = new GameObject("SonicArKeychain");
            _rootGo.layer = 5; // UI layer
            _rootGo.transform.localScale = Vector3.one * WorldScale;

            _canvas = _rootGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = 15;
            _canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.TexCoord2 |
                AdditionalCanvasShaderChannels.TexCoord3;

            RectTransform canvasRt = _rootGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);

            _canvasGroup = _rootGo.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;

            CanvasScaler scaler = _rootGo.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 3.5f;

            _rootGo.AddComponent<TrackedDeviceGraphicRaycaster>();
            _rootGo.AddComponent<GraphicRaycaster>();

            _grabCollider = _rootGo.AddComponent<BoxCollider>();
            _grabCollider.size = new Vector3(CanvasWidth + 24f, CanvasHeight + 24f, 44f);
            _grabCollider.isTrigger = true;

            // Frosted visionOS pill background
            GameObject bgGo = CreateUiRect("KeychainBg", _rootGo.transform, Vector2.zero, new Vector2(CanvasWidth, CanvasHeight));
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = SonicUIIconGenerator.GetWhiteSprite();
            bgImg.type = Image.Type.Simple;
            bgImg.color = new Color(0.08f, 0.09f, 0.12f, 0.88f);
            bgImg.raycastTarget = true;
            _glassBgImage = bgImg;

            Shader glassShader = Resources.Load<Shader>("Shaders/SonicVisionGlassUI");
            if (glassShader == null) glassShader = Shader.Find("SonicTopography/SonicVisionGlassUI");
            if (glassShader != null)
            {
                Material mat = new Material(glassShader);
                mat.SetColor("_GlassColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                mat.SetColor("_GlassTopColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                mat.SetColor("_GlassBottomColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                mat.SetFloat("_BlurRadius", 0.058f);
                mat.SetFloat("_BackdropWeight", 0.30f);
                bgImg.material = mat;
            }

            _glassSurface = bgGo.AddComponent<SonicGlassSurface>();
            _glassSurface.Configure(SonicSurfaceMode.FrostedBlurWindow, 48f, pill: true, border: 0.20f);

            // 1. Album Art Thumbnail (left side)
            GameObject artSlot = CreateUiRect("AlbumArtSlot", bgGo.transform, new Vector2(-142f, 0f), new Vector2(62f, 62f));
            Image artMaskImg = artSlot.AddComponent<Image>();
            artMaskImg.sprite = _roundedPillSprite;
            artMaskImg.type = Image.Type.Sliced;
            artMaskImg.color = new Color(0.15f, 0.18f, 0.24f, 0.9f);
            artMaskImg.raycastTarget = false;
            Mask mask = artSlot.AddComponent<Mask>();
            mask.showMaskGraphic = true;

            GameObject artImgGo = CreateUiRect("AlbumArtImage", artSlot.transform, Vector2.zero, new Vector2(62f, 62f));
            _albumArtImage = artImgGo.AddComponent<Image>();
            _albumArtImage.preserveAspect = true;
            _albumArtImage.raycastTarget = false;
            _albumArtImage.enabled = false;

            _albumArtFallbackGo = CreateUiRect("ArtFallback", artSlot.transform, Vector2.zero, new Vector2(62f, 62f));
            TextMeshProUGUI noteTxt = _albumArtFallbackGo.AddComponent<TextMeshProUGUI>();
            noteTxt.text = "AF";
            noteTxt.fontSize = 20f;
            noteTxt.fontStyle = FontStyles.Bold;
            noteTxt.alignment = TextAlignmentOptions.Center;
            noteTxt.color = new Color(0.65f, 0.78f, 0.96f, 0.85f);
            noteTxt.raycastTarget = false;

            // 2. Track Title + Subtitle
            GameObject titleGo = CreateUiRect("Title", bgGo.transform, new Vector2(-36f, 13f), new Vector2(138f, 26f));
            _titleText = titleGo.AddComponent<TextMeshProUGUI>();
            _titleText.text = "AudioFloor";
            _titleText.fontSize = 15f;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.Left;
            _titleText.color = Color.white;
            _titleText.raycastTarget = false;

            GameObject subGo = CreateUiRect("Subtitle", bgGo.transform, new Vector2(-36f, -12f), new Vector2(138f, 20f));
            _subtitleText = subGo.AddComponent<TextMeshProUGUI>();
            _subtitleText.text = "Ready";
            _subtitleText.fontSize = 12f;
            _subtitleText.alignment = TextAlignmentOptions.Left;
            _subtitleText.color = new Color(0.74f, 0.80f, 0.88f, 0.82f);
            _subtitleText.raycastTarget = false;

            // 3. Playback controls: Prev, Play/Pause, Next, Snap Back
            CreateCircleButton(bgGo.transform, "PrevBtn", new Vector2(56f, 0f), 36f, "<<", 13f,
                new Color(1f, 1f, 1f, 0.14f), HandlePrevClick, out _, out _);

            CreateCircleButton(bgGo.transform, "PlayPauseBtn", new Vector2(98f, 0f), 42f, ">", 16f,
                new Color(0.32f, 0.66f, 0.98f, 0.88f), HandlePlayPauseClick, out _, out _playIconText);

            CreateCircleButton(bgGo.transform, "NextBtn", new Vector2(140f, 0f), 36f, ">>", 13f,
                new Color(1f, 1f, 1f, 0.14f), HandleNextClick, out _, out _);

            CreateCircleButton(bgGo.transform, "SnapBackBtn", new Vector2(168f, 30f), 26f, "R", 11f,
                new Color(1f, 1f, 1f, 0.14f), SnapBackToTerrain, out _snapBackBgImage, out _snapBackLabel);

            _rootGo.SetActive(false);
        }

        private static GameObject CreateUiRect(string name, Transform parent, Vector2 anchoredPos, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;
            return go;
        }

        private static void CreateCircleButton(
            Transform parent,
            string name,
            Vector2 pos,
            float size,
            string glyph,
            float fontSize,
            Color bgCol,
            UnityEngine.Events.UnityAction onClick,
            out Image bgImg,
            out TextMeshProUGUI label)
        {
            GameObject btnGo = CreateUiRect(name, parent, pos, new Vector2(size, size));
            bgImg = btnGo.AddComponent<Image>();
            bgImg.sprite = _circleSprite;
            bgImg.color = bgCol;

            Button btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = bgImg;
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            cb.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            btn.colors = cb;
            btn.onClick.AddListener(onClick);

            GameObject lblGo = CreateUiRect("Label", btnGo.transform, Vector2.zero, new Vector2(size, size));
            label = lblGo.AddComponent<TextMeshProUGUI>();
            label.text = glyph;
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
        }

        private static string Ellipsize(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string clean = text.Trim();
            if (clean.Length <= maxChars) return clean;
            return clean.Substring(0, Mathf.Max(1, maxChars - 1)).TrimEnd() + "...";
        }

        private static bool ApproximatelyColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.008f &&
                   Mathf.Abs(a.g - b.g) < 0.008f &&
                   Mathf.Abs(a.b - b.b) < 0.008f;
        }

        private static void EnsureSprites()
        {
            if (_circleSprite == null)
            {
                const int sz = 64;
                Texture2D tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                float r = sz * 0.5f - 1f;
                Vector2 center = new Vector2((sz - 1) * 0.5f, (sz - 1) * 0.5f);
                for (int y = 0; y < sz; y++)
                {
                    for (int x = 0; x < sz; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x, y), center) - r;
                        float a = Mathf.Clamp01(0.5f - d);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply(false, true);
                _circleSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f));
            }

            if (_roundedPillSprite == null)
            {
                const int sz = 64;
                const float rad = 16f;
                Texture2D tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                Vector2 half = new Vector2(sz * 0.5f - 1f, sz * 0.5f - 1f);
                Vector2 center = new Vector2((sz - 1) * 0.5f, (sz - 1) * 0.5f);
                for (int y = 0; y < sz; y++)
                {
                    for (int x = 0; x < sz; x++)
                    {
                        Vector2 p = new Vector2(x, y) - center;
                        Vector2 q = new Vector2(Mathf.Abs(p.x) - half.x + rad, Mathf.Abs(p.y) - half.y + rad);
                        float d = Mathf.Min(Mathf.Max(q.x, q.y), 0f) + new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude - rad;
                        float a = Mathf.Clamp01(0.5f - d);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply(false, true);
                _roundedPillSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
            }
        }
    }
}
