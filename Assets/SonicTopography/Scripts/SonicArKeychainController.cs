using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SonicTopography
{
    /// <summary>
    /// Compact visionOS-styled AR mini music controller ("Keychain").
    /// Smoothly docks outside the AR terrain rim at constant readable physical size
    /// regardless of terrain scale, with crisp vector transport icons and SDF rounded artwork.
    /// </summary>
    public class SonicArKeychainController : MonoBehaviour
    {
        private const float CanvasWidth = 548f;
        private const float CanvasHeight = 104f;
        private const float WorldScale = 0.00064f; // ~35.1cm x 6.7cm physical floating card

        private static readonly Color ColTextPrimary = new Color(0.98f, 0.98f, 0.98f, 0.98f);
        private static readonly Color ColTextSecondary = new Color(0.72f, 0.72f, 0.72f, 0.82f);

        private SonicMixedRealityManager _mr;
        private SonicMusicPlayerController _player;
        private SonicWebBrowserManager _browser;
        private SonicTopographyVisualizer _visualizer;
        private SonicSpatialUIManager _spatialUI;

        private GameObject _rootGo;
        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private Material _glassMaterial;

        private Image _albumArtImage;
        private SonicGlassSurface _albumArtSurface;
        private GameObject _albumArtFallbackGo;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _subtitleText;
        private Image _playPauseIconImage;
        private Image _glassBgImage;
        private SonicGlassSurface _glassSurface;

        private bool _hasPoseInitialized = false;
        private float _mainUiHideBlend = 1f;
        private float _nextMetaSyncTime = 0f;

        public bool IsVisible => _rootGo != null && _rootGo.activeSelf && _canvasGroup != null && _canvasGroup.alpha > 0.25f;

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
            if (_glassMaterial != null) Destroy(_glassMaterial);
        }

        private void HandleMediaStateChanged(SonicMusicTrack _) => SyncMetadataAndButtons();
        private void HandleMediaStateChanged() => SyncMetadataAndButtons();

        private void LateUpdate()
        {
            if (_mr == null || _rootGo == null) return;

            if (_spatialUI == null)
            {
                _spatialUI = FindFirstObjectByType<SonicSpatialUIManager>(FindObjectsInactive.Include);
            }

            float blend = _mr.ArBlend;
            bool mainPanelShowing = _spatialUI != null && _spatialUI.IsUIVisible;
            _mainUiHideBlend = Mathf.MoveTowards(_mainUiHideBlend, mainPanelShowing ? 0f : 1f, Time.unscaledDeltaTime * 5.0f);

            bool shouldBeActive = (blend > 0.15f || _mr.IsArMode) && _mainUiHideBlend > 0.01f;
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

            float fadeAlpha = Mathf.InverseLerp(0.25f, 0.92f, blend) * _mainUiHideBlend;
            _canvasGroup.alpha = fadeAlpha;
            _canvasGroup.interactable = !mainPanelShowing && fadeAlpha > 0.65f;
            _canvasGroup.blocksRaycasts = !mainPanelShowing && fadeAlpha > 0.65f;

            ComputeDockedPose(out Vector3 dockedPos, out Quaternion dockedRot);
            if (!_hasPoseInitialized)
            {
                _hasPoseInitialized = true;
                _rootGo.transform.SetPositionAndRotation(dockedPos, dockedRot);
            }
            else
            {
                float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
                Vector3 nextPos = Vector3.Lerp(_rootGo.transform.position, dockedPos, k);
                Quaternion nextRot = Quaternion.Slerp(_rootGo.transform.rotation, dockedRot, k);
                _rootGo.transform.SetPositionAndRotation(nextPos, nextRot);
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

            if (_titleText != null)
            {
                SonicSpatialUIManager.ApplySharedCJKFont(_titleText);
                _titleText.text = title.Trim();
            }
            if (_subtitleText != null)
            {
                SonicSpatialUIManager.ApplySharedCJKFont(_subtitleText);
                _subtitleText.text = artist.Trim();
            }
            if (_playPauseIconImage != null)
            {
                _playPauseIconImage.sprite = SonicUIIconGenerator.GetIcon(playing ? SonicIconType.Pause : SonicIconType.Play);
            }

            if (_albumArtImage != null)
            {
                bool hasArt = artSprite != null;
                Sprite targetSprite = hasArt ? artSprite : SonicUIIconGenerator.GetWhiteSprite();
                bool spriteChanged = _albumArtImage.sprite != targetSprite;
                _albumArtImage.sprite = targetSprite;
                _albumArtImage.enabled = hasArt;
                if (spriteChanged)
                {
                    _albumArtSurface?.SetVerticesDirty();
                }
                if (_albumArtFallbackGo != null) _albumArtFallbackGo.SetActive(!hasArt);
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

            Sprite whiteSprite = SonicUIIconGenerator.GetWhiteSprite();

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

            Shader glassShader = Resources.Load<Shader>("Shaders/SonicVisionGlassUI");
            if (glassShader == null) glassShader = Shader.Find("SonicTopography/SonicVisionGlassUI");
            if (glassShader != null)
            {
                _glassMaterial = new Material(glassShader);
                _glassMaterial.SetColor("_GlassColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetColor("_GlassTopColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetColor("_GlassBottomColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetFloat("_BlurRadius", 0.058f);
                _glassMaterial.SetFloat("_BackdropWeight", 0.30f);
            }

            // Sleek dark visionOS frosted pill background
            GameObject bgGo = CreateUiRect("KeychainBg", _rootGo.transform, Vector2.zero, new Vector2(CanvasWidth, CanvasHeight));
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.sprite = whiteSprite;
            bgImg.type = Image.Type.Simple;
            bgImg.color = Color.white;
            bgImg.raycastTarget = true;
            if (_glassMaterial != null) bgImg.material = _glassMaterial;
            _glassBgImage = bgImg;

            _glassSurface = bgGo.AddComponent<SonicGlassSurface>();
            _glassSurface.Configure(SonicSurfaceMode.FrostedBlurWindow, 52f, pill: true, border: 0.18f);

            // 1. Smooth Analytical SDF Rounded Album Art Thumbnail (left side, 64x64 with breathing room around r=52 pill edge)
            GameObject artSlot = CreateUiRect("AlbumArtSlot", bgGo.transform, new Vector2(-208f, 0f), new Vector2(64f, 64f));

            GameObject artImgGo = CreateUiRect("AlbumArtImage", artSlot.transform, Vector2.zero, new Vector2(64f, 64f));
            _albumArtImage = artImgGo.AddComponent<Image>();
            _albumArtImage.sprite = whiteSprite;
            _albumArtImage.type = Image.Type.Simple;
            _albumArtImage.color = Color.white;
            if (_glassMaterial != null) _albumArtImage.material = _glassMaterial;
            _albumArtImage.raycastTarget = false;
            _albumArtImage.enabled = false;

            _albumArtSurface = artImgGo.AddComponent<SonicGlassSurface>();
            _albumArtSurface.Configure(SonicSurfaceMode.RoundedTexture, 16f, pill: false, border: 0.14f, squareCrop: true);

            // Fallback: rounded dark glass square with centered MusicNote icon
            _albumArtFallbackGo = CreateUiRect("ArtFallback", artSlot.transform, Vector2.zero, new Vector2(64f, 64f));
            Image fbBg = _albumArtFallbackGo.AddComponent<Image>();
            fbBg.sprite = whiteSprite;
            fbBg.type = Image.Type.Simple;
            fbBg.color = new Color(0.05f, 0.05f, 0.05f, 0.85f);
            if (_glassMaterial != null) fbBg.material = _glassMaterial;
            fbBg.raycastTarget = false;

            SonicGlassSurface fbSurf = _albumArtFallbackGo.AddComponent<SonicGlassSurface>();
            fbSurf.Configure(SonicSurfaceMode.SdfShape, 16f, pill: false, border: 0.12f);

            GameObject fbIconGo = CreateUiRect("MusicNoteIcon", _albumArtFallbackGo.transform, Vector2.zero, new Vector2(26f, 26f));
            Image fbIcon = fbIconGo.AddComponent<Image>();
            fbIcon.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.MusicNote);
            fbIcon.color = ColTextSecondary;
            fbIcon.raycastTarget = false;

            // 2. Full Track Title + Subtitle with pixel-edge ellipsis and shared CJK font fallback
            GameObject titleGo = CreateUiRect("Title", bgGo.transform, new Vector2(-35f, 14f), new Vector2(246f, 30f));
            _titleText = titleGo.AddComponent<TextMeshProUGUI>();
            _titleText.text = "AudioFloor";
            _titleText.fontSize = 18f;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.MidlineLeft;
            _titleText.overflowMode = TextOverflowModes.Ellipsis;
            _titleText.textWrappingMode = TextWrappingModes.NoWrap;
            _titleText.color = ColTextPrimary;
            _titleText.raycastTarget = false;
            SonicSpatialUIManager.ApplySharedCJKFont(_titleText);

            GameObject subGo = CreateUiRect("Subtitle", bgGo.transform, new Vector2(-35f, -14f), new Vector2(246f, 24f));
            _subtitleText = subGo.AddComponent<TextMeshProUGUI>();
            _subtitleText.text = "Ready";
            _subtitleText.fontSize = 14f;
            _subtitleText.fontStyle = FontStyles.Normal;
            _subtitleText.alignment = TextAlignmentOptions.MidlineLeft;
            _subtitleText.overflowMode = TextOverflowModes.Ellipsis;
            _subtitleText.textWrappingMode = TextWrappingModes.NoWrap;
            _subtitleText.color = ColTextSecondary;
            _subtitleText.raycastTarget = false;
            SonicSpatialUIManager.ApplySharedCJKFont(_subtitleText);

            // 3. Crisp Vector Playback Controls matching BottomPlayerBar
            CreateGlassIconButton(
                bgGo.transform,
                "PrevBtn",
                new Vector2(122f, 0f),
                44f,
                20f,
                SonicIconType.Previous,
                new Color(1f, 1f, 1f, 0.05f),
                0.08f,
                0.22f,
                ColTextSecondary,
                Color.white,
                HandlePrevClick,
                out _);

            CreateGlassIconButton(
                bgGo.transform,
                "PlayPauseBtn",
                new Vector2(179f, 0f),
                54f,
                24f,
                SonicIconType.Play,
                new Color(1f, 1f, 1f, 0.08f),
                0.38f,
                0.48f,
                Color.white,
                Color.white,
                HandlePlayPauseClick,
                out _playPauseIconImage);

            CreateGlassIconButton(
                bgGo.transform,
                "NextBtn",
                new Vector2(236f, 0f),
                44f,
                20f,
                SonicIconType.Next,
                new Color(1f, 1f, 1f, 0.05f),
                0.08f,
                0.22f,
                ColTextSecondary,
                Color.white,
                HandleNextClick,
                out _);

            _rootGo.SetActive(false);
        }

        private void CreateGlassIconButton(
            Transform parent,
            string name,
            Vector2 pos,
            float size,
            float iconSize,
            SonicIconType iconType,
            Color bgCol,
            float normalBorder,
            float hoverBorder,
            Color normalIconCol,
            Color hoverIconCol,
            UnityEngine.Events.UnityAction onClick,
            out Image iconImg)
        {
            GameObject btnGo = CreateUiRect(name, parent, pos, new Vector2(size, size));
            Image bgImg = btnGo.AddComponent<Image>();
            bgImg.sprite = SonicUIIconGenerator.GetWhiteSprite();
            bgImg.type = Image.Type.Simple;
            bgImg.color = bgCol;
            if (_glassMaterial != null) bgImg.material = _glassMaterial;

            SonicGlassSurface surf = btnGo.AddComponent<SonicGlassSurface>();
            surf.Configure(SonicSurfaceMode.SdfShape, size * 0.5f, pill: true, border: normalBorder);

            Button btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = bgImg;
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(onClick);

            GameObject iconGo = CreateUiRect("Icon", btnGo.transform, Vector2.zero, new Vector2(iconSize, iconSize));
            iconImg = iconGo.AddComponent<Image>();
            iconImg.sprite = SonicUIIconGenerator.GetIcon(iconType);
            iconImg.color = normalIconCol;
            iconImg.raycastTarget = false;

            Color hovCol = new Color(
                Mathf.Clamp01(bgCol.r + 0.08f),
                Mathf.Clamp01(bgCol.g + 0.08f),
                Mathf.Clamp01(bgCol.b + 0.08f),
                Mathf.Clamp01(bgCol.a + 0.08f));

            SonicVisionHoverEffect hover = btnGo.AddComponent<SonicVisionHoverEffect>();
            hover.Configure(bgImg, surf, bgCol, hovCol, normalBorder, hoverBorder, iconImg, normalIconCol, hoverIconCol);
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
    }
}
