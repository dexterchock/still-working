using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SonicTopography
{
    public enum SonicUISection
    {
        Music = 0,
        Browse = 1,
        Browser = 2,
        Settings = 3
    }

    public class SonicVisionHoverEffect : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Image targetImage;
        public Image targetIcon;
        public Image targetGlow;
        public SonicGlassSurface targetSurface;
        public SonicGlassSurface secondarySurface;
        public TextMeshProUGUI targetLabel;
        public Color normalColor;
        public Color hoverColor;
        public Color normalGlowColor = Color.clear;
        public Color hoverGlowColor = Color.clear;
        public Color normalIconColor = Color.white;
        public Color hoverIconColor = Color.white;
        public Color normalLabelColor = Color.white;
        public Color hoverLabelColor = Color.white;
        public float normalBorder = 0.10f;
        public float hoverBorder = 0.28f;
        public float normalSecondaryBorder = 0.10f;
        public float hoverSecondaryBorder = 0.38f;
        public bool isHovered = false;

        public void Configure(
            Image img,
            SonicGlassSurface surf,
            Color normal,
            Color hover,
            float normBorder,
            float hovBorder,
            Image icon = null,
            Color? normIconCol = null,
            Color? hovIconCol = null)
        {
            targetImage = img;
            targetSurface = surf;
            normalColor = normal;
            hoverColor = hover;
            normalBorder = normBorder;
            hoverBorder = hovBorder;
            targetIcon = icon;
            if (icon != null && normIconCol.HasValue && hovIconCol.HasValue)
            {
                normalIconColor = normIconCol.Value;
                hoverIconColor = hovIconCol.Value;
            }
        }

        public void SetNormalState(Color col, float border)
        {
            normalColor = col;
            normalBorder = border;
            hoverColor = col;
            hoverColor.r = Mathf.Clamp01(col.r + 0.08f);
            hoverColor.g = Mathf.Clamp01(col.g + 0.08f);
            hoverColor.b = Mathf.Clamp01(col.b + 0.08f);
            hoverColor.a = Mathf.Clamp01(col.a + (col.a > 0.01f ? 0.08f : 0.08f));
            if (!isHovered)
            {
                Apply(normalColor, normalBorder, normalIconColor);
            }
        }

        public void SetThemeCardState(bool active, Color darkBg)
        {
            normalColor = darkBg;
            hoverColor = new Color(darkBg.r + 0.03f, darkBg.g + 0.03f, darkBg.b + 0.03f, darkBg.a);
            normalBorder = active ? 0.40f : 0.08f;
            hoverBorder = active ? 0.48f : 0.22f;
            if (!isHovered)
            {
                Apply(normalColor, normalBorder, normalIconColor);
            }
            else
            {
                Apply(hoverColor, hoverBorder, hoverIconColor);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            isHovered = true;
            Apply(hoverColor, hoverBorder, hoverIconColor);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            isHovered = false;
            Apply(normalColor, normalBorder, normalIconColor);
        }

        private void Apply(Color col, float border, Color iconCol)
        {
            if (targetImage != null) targetImage.color = col;
            if (targetSurface != null)
            {
                targetSurface.borderIntensity = border;
                targetSurface.SetVerticesDirty();
            }
            if (secondarySurface != null)
            {
                secondarySurface.borderIntensity = isHovered ? hoverSecondaryBorder : normalSecondaryBorder;
                secondarySurface.SetVerticesDirty();
            }
            if (targetGlow != null)
            {
                targetGlow.color = isHovered ? hoverGlowColor : normalGlowColor;
            }
            if (targetIcon != null && hoverIconColor.a > 0.01f)
            {
                targetIcon.color = isHovered ? hoverIconColor : normalIconColor;
            }
            if (targetLabel != null)
            {
                targetLabel.color = isHovered ? hoverLabelColor : normalLabelColor;
            }
        }
    }

    public class SonicQuestVirtualKeyboardOpener : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, ISelectHandler
    {
        public InputField inputField;
        public string placeholderText = "Search songs";
        public TouchScreenKeyboardType keyboardType = TouchScreenKeyboardType.Default;
        public Action<string> onKeyboardDone;
        public bool isUrlField = false;

        private TouchScreenKeyboard _keyboard;
        private bool _ownsKeyboard = false;
        private int _pendingFallbackFrames = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
        private int _nativeSessionId = 0;
        private int _lastPolledTextSeq = -1;
        private static SonicQuestVirtualKeyboardOpener _activeNativeOpener;
#endif
        private static bool _localKeyboardActive;
        private static bool _externalKeyboardActive;

        public static bool IsKeyboardActive =>
            _localKeyboardActive || _externalKeyboardActive || SonicQuestNativeKeyboard.IsActive;
        public static float LastKeyboardActivityTime { get; private set; } = -100f;

        public static void SetExternalKeyboardActive(bool active)
        {
            _externalKeyboardActive = active;
            if (active)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                _activeNativeOpener = null;
#endif
                _localKeyboardActive = false;
                MarkKeyboardActivity();
            }
        }

        public static bool IsKeyboardActiveOrRecentlyUsed(float cooldownSeconds = 1.0f)
        {
            if (IsKeyboardActive || TouchScreenKeyboard.visible)
            {
                return true;
            }
            return (Time.unscaledTime - LastKeyboardActivityTime) < cooldownSeconds;
        }

        public static void MarkKeyboardActivity()
        {
            LastKeyboardActivityTime = Time.unscaledTime;
        }

        public void Initialize(InputField input, string placeholder = "Search songs", TouchScreenKeyboardType kbType = TouchScreenKeyboardType.Default, Action<string> onDone = null, bool isUrl = false)
        {
            inputField = input;
            placeholderText = string.IsNullOrEmpty(placeholder) ? "Type text" : placeholder;
            keyboardType = kbType;
            onKeyboardDone = onDone;
            isUrlField = isUrl;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (inputField != null && SonicQuestNativeKeyboard.IsSupported)
            {
                // Disable UGUI InputField interaction on Quest so Unity never opens GameActivity's
                // TouchScreenKeyboard (which lacks getExtractedText/updateExtractedText), while keeping
                // programmatic text assignment & visual rendering intact.
                inputField.transition = Selectable.Transition.None;
                inputField.enabled = false;
            }
#endif
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            MarkKeyboardActivity();
            OpenVirtualKeyboard();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            MarkKeyboardActivity();
            OpenVirtualKeyboard();
        }

        public void OnSelect(BaseEventData eventData)
        {
            MarkKeyboardActivity();
            OpenVirtualKeyboard();
        }

        private float _lastOpenTime = -10f;
        private bool _isOpeningKeyboard = false;

        public void OpenVirtualKeyboard()
        {
            MarkKeyboardActivity();
            _localKeyboardActive = true;

            // Cancel any in-progress or pending jump immediately when input is clicked
            AudioFloorControllerActions.CancelAnyActiveJumpImmediate("OpenVirtualKeyboard");

            if (_isOpeningKeyboard || inputField == null) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (SonicQuestNativeKeyboard.IsSupported)
            {
                if (_nativeSessionId != 0 && SonicQuestNativeKeyboard.IsSessionActive(_nativeSessionId))
                {
                    return;
                }
                if (_activeNativeOpener == this && (Time.unscaledTime - _lastOpenTime) < 0.25f)
                {
                    return;
                }

                _isOpeningKeyboard = true;
                try
                {
                    _lastOpenTime = Time.unscaledTime;
                    _activeNativeOpener = this;
                    _nativeSessionId = SonicQuestNativeKeyboard.Open(
                        inputField.text ?? string.Empty,
                        placeholderText,
                        isPassword: false,
                        isUrl: isUrlField);
                    _lastPolledTextSeq = SonicQuestNativeKeyboard.GetTextSeq();
                    _localKeyboardActive = _nativeSessionId > 0;
                }
                finally
                {
                    _isOpeningKeyboard = false;
                }
                return;
            }
#endif

            if (_keyboard != null && _keyboard.active)
            {
                return;
            }
            if ((Time.unscaledTime - _lastOpenTime) < 0.25f && _pendingFallbackFrames > 0)
            {
                return;
            }

            _isOpeningKeyboard = true;
            try
            {
                _lastOpenTime = Time.unscaledTime;
                if (!inputField.isFocused)
                {
                    inputField.Select();
                    inputField.ActivateInputField();
                }

                if (TouchScreenKeyboard.isSupported)
                {
                    TouchScreenKeyboard.hideInput = false;
                    if (inputField.touchScreenKeyboard != null && inputField.touchScreenKeyboard.active)
                    {
                        _keyboard = inputField.touchScreenKeyboard;
                        _ownsKeyboard = false;
                        _pendingFallbackFrames = 0;
                    }
                    else
                    {
                        _pendingFallbackFrames = 2;
                    }
                }
            }
            finally
            {
                _isOpeningKeyboard = false;
            }
        }

        public void ClearKeyboardText()
        {
            MarkKeyboardActivity();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_nativeSessionId != 0 && SonicQuestNativeKeyboard.IsSessionActive(_nativeSessionId))
            {
                SonicQuestNativeKeyboard.SetText(_nativeSessionId, string.Empty);
                _lastPolledTextSeq = SonicQuestNativeKeyboard.GetTextSeq();
            }
#endif
            if (_keyboard != null)
            {
                _keyboard.text = string.Empty;
            }
        }

        public void CloseVirtualKeyboard()
        {
            MarkKeyboardActivity();
            _localKeyboardActive = false;
            _ownsKeyboard = false;
            _pendingFallbackFrames = 0;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (_nativeSessionId != 0)
            {
                SonicQuestNativeKeyboard.Close(_nativeSessionId);
                _nativeSessionId = 0;
                _lastPolledTextSeq = -1;
            }
            if (_activeNativeOpener == this)
            {
                _activeNativeOpener = null;
            }
#endif

            if (_keyboard != null)
            {
                _keyboard.active = false;
                _keyboard = null;
            }
            if (inputField != null && inputField.isFocused)
            {
                inputField.DeactivateInputField();
            }
        }

        private void ApplySyncedTextToInputField(string text)
        {
            if (inputField == null) return;
            string safe = text ?? string.Empty;
            if (!string.Equals(inputField.text, safe, StringComparison.Ordinal))
            {
                inputField.text = safe;
            }
            inputField.caretPosition = safe.Length;
            inputField.ForceLabelUpdate();
            if (inputField.placeholder != null)
            {
                inputField.placeholder.enabled = string.IsNullOrEmpty(safe);
            }
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (SonicQuestNativeKeyboard.IsSupported)
            {
                if (_nativeSessionId != 0)
                {
                    bool active = SonicQuestNativeKeyboard.IsSessionActive(_nativeSessionId);
                    int activeId = SonicQuestNativeKeyboard.ActiveSessionId;

                    if (active)
                    {
                        MarkKeyboardActivity();
                        _localKeyboardActive = true;

                        int seq = SonicQuestNativeKeyboard.GetTextSeq();
                        if (seq != _lastPolledTextSeq && activeId == _nativeSessionId)
                        {
                            _lastPolledTextSeq = seq;
                            ApplySyncedTextToInputField(SonicQuestNativeKeyboard.GetText());
                        }
                    }
                    else
                    {
                        bool replacedByAnotherSession = activeId != 0 && activeId != _nativeSessionId;
                        string finalText = inputField != null ? inputField.text : string.Empty;
                        bool submitted = false;

                        if (!replacedByAnotherSession)
                        {
                            int seq = SonicQuestNativeKeyboard.GetTextSeq();
                            if (seq != _lastPolledTextSeq)
                            {
                                _lastPolledTextSeq = seq;
                                finalText = SonicQuestNativeKeyboard.GetText();
                                ApplySyncedTextToInputField(finalText);
                            }
                            submitted = SonicQuestNativeKeyboard.WasSubmitPressed(_nativeSessionId);
                        }

                        Debug.Log($"[JUMP_DIAG] NativeKeyboard closed/finished (sid={_nativeSessionId}, submitted={submitted}, replaced={replacedByAnotherSession}) at t={Time.unscaledTime:F3}s");
                        _nativeSessionId = 0;
                        _lastPolledTextSeq = -1;
                        if (_activeNativeOpener == this)
                        {
                            _activeNativeOpener = null;
                            _localKeyboardActive = false;
                        }
                        MarkKeyboardActivity();
                        AudioFloorControllerActions.CancelAnyActiveJumpImmediate("NativeKeyboard.Closed");
                        if (!replacedByAnotherSession && submitted)
                        {
                            onKeyboardDone?.Invoke(finalText);
                        }
                    }
                }
                else if (_activeNativeOpener == null)
                {
                    _localKeyboardActive = false;
                }
                return;
            }
#endif

            if (_keyboard == null && inputField != null && inputField.touchScreenKeyboard != null && inputField.touchScreenKeyboard.active)
            {
                _keyboard = inputField.touchScreenKeyboard;
                _ownsKeyboard = false;
                _pendingFallbackFrames = 0;
            }
            else if (_keyboard == null && _pendingFallbackFrames > 0)
            {
                MarkKeyboardActivity();
                _localKeyboardActive = true;
                _pendingFallbackFrames--;
                if (_pendingFallbackFrames == 0 && TouchScreenKeyboard.isSupported && inputField != null)
                {
                    TouchScreenKeyboard.hideInput = false;
                    _keyboard = TouchScreenKeyboard.Open(
                        inputField.text ?? string.Empty,
                        TouchScreenKeyboardType.Default,
                        false,
                        false,
                        false,
                        false,
                        placeholderText
                    );
                    _ownsKeyboard = true;
                }
            }

            if (TouchScreenKeyboard.visible || (inputField != null && inputField.isFocused))
            {
                MarkKeyboardActivity();
            }

            if (_keyboard != null)
            {
                MarkKeyboardActivity();
                _localKeyboardActive = true;

                if (_ownsKeyboard && inputField != null && _keyboard.text != null && _keyboard.text != inputField.text)
                {
                    inputField.text = _keyboard.text;
                    inputField.caretPosition = inputField.text.Length;
                }

                if (_keyboard.status == TouchScreenKeyboard.Status.Done ||
                    _keyboard.status == TouchScreenKeyboard.Status.Canceled ||
                    !_keyboard.active)
                {
                    bool isDone = _keyboard.status == TouchScreenKeyboard.Status.Done;
                    string submittedText = _keyboard.text ?? (inputField != null ? inputField.text : string.Empty);
                    Debug.Log($"[JUMP_DIAG] TouchScreenKeyboard closed/finished (status={_keyboard.status}, active={_keyboard.active}) at t={Time.unscaledTime:F3}s");
                    _keyboard = null;
                    _ownsKeyboard = false;
                    _pendingFallbackFrames = 0;
                    _localKeyboardActive = false;
                    MarkKeyboardActivity();
                    AudioFloorControllerActions.CancelAnyActiveJumpImmediate("TouchScreenKeyboard.Closed");
                    if (isDone)
                    {
                        onKeyboardDone?.Invoke(submittedText);
                    }
                    if (inputField != null && inputField.isFocused)
                    {
                        inputField.DeactivateInputField();
                    }
                    if (EventSystem.current != null && inputField != null && EventSystem.current.currentSelectedGameObject == inputField.gameObject)
                    {
                        EventSystem.current.SetSelectedGameObject(null);
                    }
                }
            }
            else if (_pendingFallbackFrames <= 0)
            {
                _localKeyboardActive = TouchScreenKeyboard.visible;
            }
        }
    }

    public class SonicSpatialUIManager : MonoBehaviour
    {
        [Header("Spatial Placement Settings")]
        [Tooltip("Distance in meters in front of the user's head when summoned")]
        public float spawnDistance = 1.22f;

        [Tooltip("Vertical offset relative to eye level in meters")]
        public float verticalEyeOffset = -0.05f;

        [Tooltip("World scale of the spatial UI canvas")]
        public float canvasWorldScale = 0.00095f;

        private SonicTopographyVisualizer _visualizer;
        private SonicTopographyAudioEngine _audioEngine;
        private SonicMusicLibraryManager _library;
        private SonicMusicPlayerController _player;
        private SonicWebBrowserManager _browserManager;

        // Spatial Canvas & Animation state
        private GameObject _canvasObject;
        private RectTransform _canvasRect;
        private CanvasGroup _rootCanvasGroup;
        private bool _isVisible = false;
        private float _visibilityBlend = 0f;
        private Vector3 _baseCanvasScale;
        private Vector3 _menuSummonHeadPos;
        private bool _wasLeftMenuButtonPressed = false;
        private bool _wasLeftHandMenuGesturePressed = false;

        // Shared Fonts, Material & Sprites
        private TMP_FontAsset _fontAsset;
        private Material _glassMaterial;
        private Sprite _whiteSprite;

        // Navigation state
        private SonicUISection _activeSection = SonicUISection.Music;
        private readonly Image[] _navButtonBgs = new Image[4];
        private readonly Image[] _navButtonIcons = new Image[4];
        private GameObject _musicViewRoot;
        private GameObject _browseViewRoot;
        private GameObject _browserViewRoot;
        private GameObject _settingsViewRoot;

        // Browser View state & widgets
        private InputField _browserUrlInput;
        private SonicQuestVirtualKeyboardOpener _browserUrlOpener;
        private Image _browserBackBg;
        private Image _browserBackIcon;
        private Image _browserFwdBg;
        private Image _browserFwdIcon;
        private Image _browserDefaultBg;
        private TextMeshProUGUI _browserDefaultLabel;
        private float _browserDefaultFeedbackUntil = -10f;
        private TextMeshProUGUI _browserStatusLabel;
        private GameObject _browserPlaceholderRoot;
        private RawImage _browserPunchoutRawImage;
        private RectTransform _browserViewportRect;

        // Music View state & widgets
        private InputField _searchInput;
        private SonicQuestVirtualKeyboardOpener _searchOpener;
        private string _searchQuery = string.Empty;
        private TextMeshProUGUI _musicSubtitleLabel;
        private RectTransform _musicGridContent;
        private RectMask2D _musicMask;
        private readonly List<GameObject> _musicCardObjects = new List<GameObject>();

        // Browse View state & widgets
        private string _currentBrowsePath;
        private TextMeshProUGUI _browsePathLabel;
        private RectTransform _browseListContent;
        private RectMask2D _browseMask;
        private readonly List<GameObject> _browseRowObjects = new List<GameObject>();

        // Settings View state & widgets
        private TextMeshProUGUI _settingsFolderTitleLabel;
        private TextMeshProUGUI _settingsFolderSubLabel;
        private Slider _rotationSlider;
        private Slider _amplitudeSlider;
        private Slider _motionSpeedSlider;

        private readonly Image[] _colorModeBtnBgs = new Image[3];
        private readonly TextMeshProUGUI[] _colorModeBtnLabels = new TextMeshProUGUI[3];
        private TextMeshProUGUI _colorStatusLabel;
        private readonly Image[] _activePaletteSwatches = new Image[4];
        private GameObject _presetThemesContainer;
        private GameObject _manualColorsContainer;
        private readonly List<Image> _themePresetPillBgs = new List<Image>();
        private readonly List<SonicThemePreset> _themePresetValues = new List<SonicThemePreset>();

        // Manual color Hue state (Atmosphere, Low/Mid, High Peaks, Ripples)
        private float _manualBaseHue = 0.72f;
        private float _manualCoolHue = 0.55f;
        private float _manualWarmHue = 0.92f;
        private float _manualRippleHue = 0.48f;
        private Image _manualBasePreview;
        private Image _manualCoolPreview;
        private Image _manualWarmPreview;
        private Image _manualRipplePreview;

        // Effect Toggle Switches
        private struct EffectToggleBinding
        {
            public Image pillBg;
            public Image knobImg;
            public RectTransform knobRect;
            public Func<bool> getter;
            public Action<bool> setter;
        }
        private readonly List<EffectToggleBinding> _effectToggles = new List<EffectToggleBinding>();

        // Equalizer widgets for now-playing indicator
        private struct SonicEqualizerWidget
        {
            public GameObject badgeRoot;
            public RectTransform bar1;
            public RectTransform bar2;
            public RectTransform bar3;
        }
        private readonly List<SonicEqualizerWidget> _activeEqualizers = new List<SonicEqualizerWidget>();

        // Bottom Player Bar widgets
        private GameObject _bottomMusicControlsRoot;
        private GameObject _bottomBrowserNavRoot;
        private Image _playerArtworkImage;
        private SonicGlassSurface _playerArtworkSurface;
        private TextMeshProUGUI _playerTitleLabel;
        private TextMeshProUGUI _playerArtistLabel;
        private TextMeshProUGUI _playerTimeLabel;
        private Image _playPauseIconImage;
        private Image _shuffleBgImage;
        private Image _shuffleIconImage;
        private Image _repeatBgImage;
        private Image _repeatIconImage;
        private Slider _progressSlider;
        private float _lastProgressInteractionTime = -10f;
        private Image _volumeIconImage;
        private Slider _volumeSlider;
        private bool _suppressSliderCallbacks = false;

        // Apple visionOS / iOS 18 Uniform Dark Charcoal & Monochrome White Palette Constants (100% Pure Neutral Black/Grey)
        private static readonly Color ColTextPrimary = new Color(0.98f, 0.98f, 0.98f, 0.98f);
        private static readonly Color ColTextSecondary = new Color(0.72f, 0.72f, 0.72f, 0.82f);
        private static readonly Color ColTextMuted = new Color(0.50f, 0.50f, 0.50f, 0.60f);
        private static readonly Color ColRecessedDark = new Color(0.020f, 0.020f, 0.020f, 0.85f);
        private static readonly Color ColSurfaceCard = new Color(0.045f, 0.045f, 0.045f, 0.75f);
        private static readonly Color ColSurfaceElevated = new Color(0.075f, 0.075f, 0.075f, 0.65f);
        private static readonly Color ColSurfaceActive = new Color(1.00f, 1.00f, 1.00f, 0.06f);
        private static readonly Color ColPillIdle = new Color(1.00f, 1.00f, 1.00f, 0.04f);
        private static readonly Color ColPillActive = new Color(1.00f, 1.00f, 1.00f, 0.07f);
        private static readonly Color ColAccentWhite = new Color(0.96f, 0.96f, 0.96f, 0.96f);
        private static readonly Color ColToggleActive = new Color(0.94f, 0.94f, 0.94f, 0.96f);
        private static readonly Color ColToggleInactive = new Color(0.035f, 0.035f, 0.035f, 0.85f);

        public bool IsUIVisible => _isVisible;
        public bool IsSearchFocused =>
            (_searchInput != null && _searchInput.isFocused) ||
            (_browserUrlInput != null && _browserUrlInput.isFocused) ||
            TouchScreenKeyboard.visible ||
            SonicQuestVirtualKeyboardOpener.IsKeyboardActive;

        private bool UseBrowserPlayerBar =>
            _browserManager != null &&
            _browserManager.IsBrowserAudioActive &&
            (_player == null || !_player.IsPlaying);

        public void Initialize(
            SonicTopographyVisualizer visualizer,
            SonicTopographyAudioEngine audioEngine,
            SonicMusicLibraryManager library,
            SonicMusicPlayerController player)
        {
            _visualizer = visualizer;
            _audioEngine = audioEngine;
            _library = library;
            _player = player;

            _browserManager = GetComponent<SonicWebBrowserManager>();
            if (_browserManager == null) _browserManager = FindFirstObjectByType<SonicWebBrowserManager>();
            if (_browserManager == null) _browserManager = gameObject.AddComponent<SonicWebBrowserManager>();
            _browserManager.Initialize(_audioEngine, _player, _visualizer);

            EnsureXRUIEventSystemReady();
            LoadSharedResources();
            BuildSpatialUIRig();

            if (_library != null)
            {
                _currentBrowsePath = _library.ActiveScanFolder;
                _library.OnLibraryUpdated += HandleLibraryUpdated;
            }

            if (_player != null)
            {
                _player.OnTrackChanged += HandleTrackChanged;
                _player.OnPlaybackStateChanged += HandlePlaybackStateChanged;
                _player.OnLocalPlaybackStarted += HandleLocalPlaybackStarted;
            }

            if (_browserManager != null)
            {
                _browserManager.OnBrowserStateChanged += HandleBrowserStateChanged;
            }

            if (PlayerPrefs.HasKey("SonicTopography_HueBase"))
                _manualBaseHue = PlayerPrefs.GetFloat("SonicTopography_HueBase", 0.72f);
            if (PlayerPrefs.HasKey("SonicTopography_HueCool"))
                _manualCoolHue = PlayerPrefs.GetFloat("SonicTopography_HueCool", 0.52f);
            if (PlayerPrefs.HasKey("SonicTopography_HueWarm"))
                _manualWarmHue = PlayerPrefs.GetFloat("SonicTopography_HueWarm", 0.08f);
            if (PlayerPrefs.HasKey("SonicTopography_HueRipple"))
                _manualRippleHue = PlayerPrefs.GetFloat("SonicTopography_HueRipple", 0.15f);

            if (_visualizer != null && _visualizer.colorMode == SonicColorMode.ManualColors)
            {
                ApplyManualHuesToVisualizer(switchToManualMode: false);
            }

            RefreshMusicGrid();
            RefreshBrowseList();
            RefreshSettingsUI();
            RefreshPlayerBarUI();

            foreach (var detector in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Samples.Hands.MetaSystemGestureDetector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (detector != null)
                {
                    if (detector.aimFlagsAction.action != null && !detector.aimFlagsAction.action.enabled)
                    {
                        detector.aimFlagsAction.action.Enable();
                    }
                    detector.menuPressed.RemoveListener(ToggleUIVisibility);
                    detector.menuPressed.AddListener(ToggleUIVisibility);
                }
            }

            // Start hidden so the splash screen displays cleanly; SonicTopographyVRBootstrap opens the menu after splash completes
            SetUIVisibleImmediate(false);
        }

        private void OnDestroy()
        {
            if (_library != null) _library.OnLibraryUpdated -= HandleLibraryUpdated;
            if (_player != null)
            {
                _player.OnTrackChanged -= HandleTrackChanged;
                _player.OnPlaybackStateChanged -= HandlePlaybackStateChanged;
                _player.OnLocalPlaybackStarted -= HandleLocalPlaybackStarted;
            }
            if (_browserManager != null)
            {
                _browserManager.OnBrowserStateChanged -= HandleBrowserStateChanged;
            }
            if (_glassMaterial != null) Destroy(_glassMaterial);

            foreach (var detector in FindObjectsByType<UnityEngine.XR.Interaction.Toolkit.Samples.Hands.MetaSystemGestureDetector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (detector != null)
                {
                    detector.menuPressed.RemoveListener(ToggleUIVisibility);
                }
            }
        }

        private static void EnsureXRUIEventSystemReady()
        {
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                es = FindFirstObjectByType<EventSystem>();
            }

            if (es == null)
            {
                GameObject esGo = new GameObject("EventSystem");
                es = esGo.AddComponent<EventSystem>();
                XRUIInputModule xrInput = esGo.AddComponent<XRUIInputModule>();
                xrInput.enableXRInput = true;
#if UNITY_EDITOR
                xrInput.enableMouseInput = true;
                xrInput.enableTouchInput = false;
#else
                xrInput.enableMouseInput = false;
                xrInput.enableTouchInput = false;
#endif
            }
            else
            {
                XRUIInputModule xrInput = es.GetComponent<XRUIInputModule>();
                if (xrInput != null)
                {
                    xrInput.enableXRInput = true;
#if UNITY_EDITOR
                    xrInput.enableMouseInput = true;
                    xrInput.enableTouchInput = false;
#else
                    xrInput.enableMouseInput = false;
                    xrInput.enableTouchInput = false;
#endif
                }
            }
        }

        private void LoadSharedResources()
        {
            TMP_FontAsset[] loadedFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < loadedFonts.Length; i++)
            {
                if (loadedFonts[i] != null && loadedFonts[i].name.IndexOf("Inter", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _fontAsset = loadedFonts[i];
                    break;
                }
            }
            if (_fontAsset == null)
            {
                _fontAsset = TMP_Settings.defaultFontAsset;
            }

            EnsureCJKFontSupport();

            _whiteSprite = SonicUIIconGenerator.GetWhiteSprite();

            Shader glassShader = Resources.Load<Shader>("Shaders/SonicVisionGlassUI");
            if (glassShader == null)
            {
                glassShader = Shader.Find("SonicTopography/SonicVisionGlassUI");
            }
            if (glassShader != null)
            {
                _glassMaterial = new Material(glassShader)
                {
                    name = "SonicVisionGlassSharedMat"
                };
                _glassMaterial.SetColor("_GlassColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetColor("_GlassTopColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetColor("_GlassBottomColor", new Color(0.022f, 0.022f, 0.022f, 0.96f));
                _glassMaterial.SetFloat("_BlurRadius", 0.058f);
                _glassMaterial.SetFloat("_BackdropWeight", 0.30f);
            }
        }

        private static bool _cjkFontsInitialized = false;
        private static readonly List<TMP_FontAsset> _cachedCjkFontAssets = new List<TMP_FontAsset>();

        /// <summary>
        /// Discovers and configures Dynamic CJK (Chinese, Japanese, Korean) fallback font assets for TextMeshPro.
        /// Searches bundled StreamingAssets fonts (ARIALUNI.ttf), APK extraction on Quest / Android,
        /// Android Horizon OS system fonts (/system/fonts/), and Windows system fonts to ensure song titles
        /// like '边个' and Japanese/Korean glyphs render sharply across all UI views.
        /// </summary>
        private void EnsureCJKFontSupport()
        {
            if (_fontAsset == null)
            {
                _fontAsset = TMP_Settings.defaultFontAsset;
            }
            if (_fontAsset == null) return;

            // If we have already created the fallback font assets in memory, ensure they are attached.
            if (_cjkFontsInitialized && _cachedCjkFontAssets.Count > 0)
            {
                for (int i = 0; i < _cachedCjkFontAssets.Count; i++)
                {
                    AddFallbackFontAsset(_cachedCjkFontAssets[i]);
                }
                return;
            }

            if (_fontAsset.fallbackFontAssetTable == null)
            {
                _fontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }

            List<string> candidatePaths = new List<string>();

            // 1. Android APK bundled font extraction (Meta Quest / Horizon OS)
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                string apkPath = Application.dataPath;
                string destFontPath = Path.Combine(Application.persistentDataPath, "ARIALUNI.ttf");
                if (!File.Exists(destFontPath))
                {
                    if (File.Exists(apkPath))
                    {
                        using (var archive = System.IO.Compression.ZipFile.OpenRead(apkPath))
                        {
                            var entry = archive.GetEntry("assets/Fonts/ARIALUNI.ttf");
                            if (entry != null)
                            {
                                using (var entryStream = entry.Open())
                                using (var outStream = File.Create(destFontPath))
                                {
                                    entryStream.CopyTo(outStream);
                                }
                            }
                        }
                    }
                }
                if (File.Exists(destFontPath))
                {
                    candidatePaths.Add(destFontPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicUI] APK font extraction check: {ex.Message}");
            }
#endif

            // 2. Bundled StreamingAssets/Fonts/ (Arial Unicode MS covering CJK on Windows / PC)
            try
            {
                string streamingFontDir = Path.Combine(Application.streamingAssetsPath, "Fonts");
                if (Directory.Exists(streamingFontDir))
                {
                    string[] streamingFiles = Directory.GetFiles(streamingFontDir, "*.*");
                    for (int i = 0; i < streamingFiles.Length; i++)
                    {
                        string ext = Path.GetExtension(streamingFiles[i]).ToLowerInvariant();
                        if (ext == ".ttf" || ext == ".otf" || ext == ".ttc")
                        {
                            candidatePaths.Add(streamingFiles[i]);
                        }
                    }
                }
            }
            catch { }

            // 3. Android / Meta Quest (Horizon OS) system fonts
            candidatePaths.Add("/system/fonts/NotoSansCJK-Regular.ttc");
            candidatePaths.Add("/system/fonts/NotoSansSC-Regular.otf");
            candidatePaths.Add("/system/fonts/NotoSansJP-Regular.otf");
            candidatePaths.Add("/system/fonts/NotoSansKR-Regular.otf");
            candidatePaths.Add("/system/fonts/NotoSansTC-Regular.otf");
            candidatePaths.Add("/system/fonts/DroidSansFallback.ttf");
            candidatePaths.Add("/system/fonts/NotoSansCJK.ttc");
            candidatePaths.Add("/apex/com.android.i18n/etc/fonts/NotoSansCJK-Regular.ttc");
            candidatePaths.Add("/apex/com.android.i18n/etc/fonts/NotoSansSC-Regular.otf");
            candidatePaths.Add("/apex/com.android.i18n/etc/fonts/NotoSansJP-Regular.otf");
            candidatePaths.Add("/apex/com.android.i18n/etc/fonts/NotoSansKR-Regular.otf");

            // 4. Windows system fonts (Unity Editor & PC-VR)
            candidatePaths.Add(@"C:\Windows\Fonts\ARIALUNI.ttf");
            candidatePaths.Add(@"C:\Windows\Fonts\msyh.ttc");
            candidatePaths.Add(@"C:\Windows\Fonts\msyh.ttf");
            candidatePaths.Add(@"C:\Windows\Fonts\msgothic.ttc");
            candidatePaths.Add(@"C:\Windows\Fonts\malgun.ttf");
            candidatePaths.Add(@"C:\Windows\Fonts\simsun.ttc");
            candidatePaths.Add(@"C:\Windows\Fonts\simhei.ttf");
            candidatePaths.Add(@"C:\Windows\Fonts\meiryo.ttc");

            // 5. Scan OS installed fonts returned by Unity
            try
            {
                string[] osPaths = Font.GetPathsToOSFonts();
                if (osPaths != null)
                {
                    for (int i = 0; i < osPaths.Length; i++)
                    {
                        string p = osPaths[i];
                        if (string.IsNullOrEmpty(p)) continue;
                        string lower = Path.GetFileName(p).ToLowerInvariant();
                        if (lower.Contains("cjk") || lower.Contains("sc") || lower.Contains("jp") || lower.Contains("kr") ||
                            lower.Contains("yahei") || lower.Contains("sim") || lower.Contains("arialuni") ||
                            lower.Contains("malgun") || lower.Contains("gothic") || lower.Contains("meiryo") ||
                            lower.Contains("han") || lower.Contains("pingfang"))
                        {
                            if (!candidatePaths.Contains(p)) candidatePaths.Add(p);
                        }
                    }
                }
            }
            catch { }

            int addedCount = 0;
            for (int i = 0; i < candidatePaths.Count; i++)
            {
                string path = candidatePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (File.Exists(path))
                    {
                        TMP_FontAsset cjkAsset = TMP_FontAsset.CreateFontAsset(
                            path, 0, 64, 9,
                            UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                            1024, 1024
                        );

                        if (cjkAsset != null)
                        {
                            cjkAsset.name = $"CJK_Fallback_{Path.GetFileNameWithoutExtension(path)}";
                            cjkAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                            cjkAsset.isMultiAtlasTexturesEnabled = true;

                            if (!_cachedCjkFontAssets.Contains(cjkAsset))
                            {
                                _cachedCjkFontAssets.Add(cjkAsset);
                            }

                            AddFallbackFontAsset(cjkAsset);
                            addedCount++;
                            if (addedCount >= 4) break;
                        }
                    }
                }
                catch { }
            }

            // 6. If file paths didn't find enough, try by family name via TMP_FontAsset.CreateFontAsset(family, "Regular")
            if (addedCount < 3)
            {
                string[] candidateFamilies = new string[]
                {
                    "Noto Sans CJK SC",
                    "Noto Sans SC",
                    "Microsoft YaHei",
                    "SimSun",
                    "SimHei",
                    "Noto Sans CJK JP",
                    "Noto Sans JP",
                    "MS Gothic",
                    "Meiryo",
                    "Noto Sans CJK KR",
                    "Noto Sans KR",
                    "Malgun Gothic",
                    "Arial Unicode MS",
                    "Droid Sans Fallback"
                };

                for (int i = 0; i < candidateFamilies.Length; i++)
                {
                    try
                    {
                        TMP_FontAsset cjkAsset = TMP_FontAsset.CreateFontAsset(candidateFamilies[i], "Regular", 64);
                        if (cjkAsset != null)
                        {
                            cjkAsset.name = $"CJK_Fallback_{candidateFamilies[i]}";
                            cjkAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                            cjkAsset.isMultiAtlasTexturesEnabled = true;

                            if (!_cachedCjkFontAssets.Contains(cjkAsset))
                            {
                                _cachedCjkFontAssets.Add(cjkAsset);
                            }

                            AddFallbackFontAsset(cjkAsset);
                            addedCount++;
                            if (addedCount >= 4) break;
                        }
                    }
                    catch { }
                }
            }

            _cjkFontsInitialized = true;
        }

        private void AddFallbackFontAsset(TMP_FontAsset fallbackAsset)
        {
            if (fallbackAsset == null) return;

            if (_fontAsset != null)
            {
                if (_fontAsset.fallbackFontAssetTable == null) _fontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!_fontAsset.fallbackFontAssetTable.Contains(fallbackAsset))
                {
                    _fontAsset.fallbackFontAssetTable.Add(fallbackAsset);
                }
            }

            if (TMP_Settings.defaultFontAsset != null && TMP_Settings.defaultFontAsset != _fontAsset)
            {
                if (TMP_Settings.defaultFontAsset.fallbackFontAssetTable == null)
                    TMP_Settings.defaultFontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!TMP_Settings.defaultFontAsset.fallbackFontAssetTable.Contains(fallbackAsset))
                {
                    TMP_Settings.defaultFontAsset.fallbackFontAssetTable.Add(fallbackAsset);
                }
            }

            if (TMP_Settings.fallbackFontAssets == null)
            {
                TMP_Settings.fallbackFontAssets = new List<TMP_FontAsset>();
            }
            if (!TMP_Settings.fallbackFontAssets.Contains(fallbackAsset))
            {
                TMP_Settings.fallbackFontAssets.Add(fallbackAsset);
            }

            // Also attach to all font assets currently loaded in memory
            try
            {
                TMP_FontAsset[] allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                for (int i = 0; i < allFonts.Length; i++)
                {
                    TMP_FontAsset fa = allFonts[i];
                    if (fa != null && fa != fallbackAsset)
                    {
                        if (fa.fallbackFontAssetTable == null) fa.fallbackFontAssetTable = new List<TMP_FontAsset>();
                        if (!fa.fallbackFontAssetTable.Contains(fallbackAsset))
                        {
                            fa.fallbackFontAssetTable.Add(fallbackAsset);
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureLabelCJKFont(TextMeshProUGUI label)
        {
            if (label == null) return;
            if (label.font == null)
            {
                label.font = _fontAsset ?? TMP_Settings.defaultFontAsset;
            }
            if (label.font != null && _cachedCjkFontAssets != null && _cachedCjkFontAssets.Count > 0)
            {
                if (label.font.fallbackFontAssetTable == null)
                {
                    label.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                }
                for (int i = 0; i < _cachedCjkFontAssets.Count; i++)
                {
                    TMP_FontAsset cjk = _cachedCjkFontAssets[i];
                    if (cjk != null && !label.font.fallbackFontAssetTable.Contains(cjk))
                    {
                        label.font.fallbackFontAssetTable.Add(cjk);
                    }
                }
            }
        }

        private static readonly int PropTopClipFade = Shader.PropertyToID("_TopClipFade");
        private float _lastTopFade = -1f;
        private int _lastMaskSoftnessY = -1;
        private SonicUISection _lastFadeSection = (SonicUISection)(-1);

        private Camera _cachedMainCamera;
        private UnityEngine.XR.InputDevice _leftHandDevice;
        private UnityEngine.InputSystem.XR.XRController _cachedXrLeft;
        private UnityEngine.InputSystem.Controls.ButtonControl _leftMenuControl;

        private int _lastDisplayedTimeSec = -1;
        private int _lastDisplayedDurSec = -1;
        private bool _wasDisplayingLoading = false;

        private Camera GetMainCamera()
        {
            if (_cachedMainCamera == null || !_cachedMainCamera.isActiveAndEnabled)
            {
                _cachedMainCamera = Camera.main;
                if (_cachedMainCamera == null) _cachedMainCamera = FindFirstObjectByType<Camera>();
            }
            return _cachedMainCamera;
        }

        private void SetCameraOpaqueCaptureEnabled(bool enabled)
        {
            Camera cam = GetMainCamera();
            if (cam == null) return;
            UniversalAdditionalCameraData camData = cam.GetComponent<UniversalAdditionalCameraData>();
            if (camData != null)
            {
                // Keep Opaque/Depth capture Off so Quest 3 renders in a single on-chip GMEM tile pass at 1.5x 72fps
                camData.requiresColorOption = CameraOverrideOption.Off;
                camData.requiresColorTexture = false;
                camData.requiresDepthOption = CameraOverrideOption.Off;
                camData.requiresDepthTexture = false;
            }
        }

        private void Update()
        {
            CheckMenuToggleInput();
            CheckAutoDismissOnMoveAway();
            UpdateVisibilityAnimation();
            UpdateLivePlayerProgress();
            UpdateEqualizerWidgets();
            UpdateDynamicScrollTopFade();
            if (_browserDefaultFeedbackUntil > 0f && Time.unscaledTime >= _browserDefaultFeedbackUntil)
            {
                _browserDefaultFeedbackUntil = -10f;
                if (_activeSection == SonicUISection.Browser)
                {
                    RefreshBrowserUI();
                }
            }
        }

        private void CloseAllVirtualKeyboards()
        {
            if (_searchInput != null && _searchInput.isFocused)
            {
                _searchInput.DeactivateInputField();
            }
            if (_browserUrlInput != null && _browserUrlInput.isFocused)
            {
                _browserUrlInput.DeactivateInputField();
            }
            foreach (var opener in GetComponentsInChildren<SonicQuestVirtualKeyboardOpener>(true))
            {
                if (opener != null) opener.CloseVirtualKeyboard();
            }
            _browserManager?.CloseWebVirtualKeyboard();
            SonicQuestNativeKeyboard.Close();
        }

        private void UpdateDynamicScrollTopFade()
        {
            if (!_isVisible) return;

            float scrollY = 0f;
            if (_activeSection == SonicUISection.Music && _musicGridContent != null)
            {
                scrollY = _musicGridContent.anchoredPosition.y;
            }
            else if (_activeSection == SonicUISection.Browse && _browseListContent != null)
            {
                scrollY = _browseListContent.anchoredPosition.y;
            }

            // At the top of the list (scrollY <= 1f), topFade is 1f with 0 softness (Row 1 is 100% bright, zero top fade overlap).
            // As the user scrolls down (scrollY > 1f), smoothly ramp topFade from 1f up to 28f below the header.
            float topFade = scrollY <= 1f ? 1f : Mathf.Clamp(scrollY, 1f, 28f);
            int maskSoftnessY = scrollY <= 1f ? 0 : Mathf.RoundToInt(Mathf.Clamp(scrollY, 0f, 28f));

            if (_glassMaterial != null && !Mathf.Approximately(_lastTopFade, topFade))
            {
                _lastTopFade = topFade;
                _glassMaterial.SetFloat(PropTopClipFade, topFade);
            }

            if (_lastMaskSoftnessY != maskSoftnessY || _lastFadeSection != _activeSection)
            {
                _lastMaskSoftnessY = maskSoftnessY;
                _lastFadeSection = _activeSection;
                if (_activeSection == SonicUISection.Music && _musicMask != null)
                {
                    _musicMask.softness = new Vector2Int(0, maskSoftnessY);
                }
                else if (_activeSection == SonicUISection.Browse && _browseMask != null)
                {
                    _browseMask.softness = new Vector2Int(0, maskSoftnessY);
                }
            }
        }

        private void CheckAutoDismissOnMoveAway()
        {
            if (!_isVisible) return;
            if (IsSearchFocused) return;

            Camera cam = GetMainCamera();
            if (cam == null) return;

            // Auto-hide when user moves far away from menu (> 3.5m from summon position or stepping far back from canvas)
            if (_hasLockedCanvasPose)
            {
                float distFromSummon = Vector3.Distance(cam.transform.position, _menuSummonHeadPos);
                float distFromCanvas = _canvasRect != null ? Vector3.Distance(cam.transform.position, _canvasRect.position) : 0f;
                if (distFromSummon > 3.5f || (_canvasRect != null && distFromCanvas > (spawnDistance + 3.0f)))
                {
                    SetUIVisible(false);
                }
            }
        }

        private void CheckMenuToggleInput()
        {
            if (SonicTopographySplashScreen.IsSplashActive) return;

            bool toggleTriggered = false;

            // 1. Left Quest Controller Menu Button (OpenXR / Quest XRNode.LeftHand CommonUsages.menuButton)
            bool leftMenuPressed = false;
            if (!_leftHandDevice.isValid)
            {
                _leftHandDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            }
            if (_leftHandDevice.isValid &&
                _leftHandDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out bool xrMenuVal))
            {
                leftMenuPressed |= xrMenuVal;
            }

            var xrLeft = UnityEngine.InputSystem.XR.XRController.leftHand;
            if (xrLeft != _cachedXrLeft)
            {
                _cachedXrLeft = xrLeft;
                _leftMenuControl = xrLeft != null
                    ? xrLeft.TryGetChildControl<UnityEngine.InputSystem.Controls.ButtonControl>("menu")
                    : null;
            }
            if (xrLeft != null && _leftMenuControl != null && _leftMenuControl.isPressed)
            {
                leftMenuPressed = true;
            }

            if (leftMenuPressed && !_wasLeftMenuButtonPressed)
            {
                toggleTriggered = true;
            }
            _wasLeftMenuButtonPressed = leftMenuPressed;

            // 2. Left Hand Tracking Menu Pinch Gesture (Meta Hand Tracking Aim / MetaAimHand.left MenuPressed)
            if (UnityEngine.XR.Hands.MetaAimHand.left != null && UnityEngine.XR.Hands.MetaAimHand.left.added)
            {
                ulong flags = (ulong)UnityEngine.XR.Hands.MetaAimHand.left.aimFlags.ReadValue();
                bool handMenuPressed = (flags & (1UL << 8)) != 0; // MetaAimFlags.MenuPressed
                if (handMenuPressed && !_wasLeftHandMenuGesturePressed)
                {
                    toggleTriggered = true;
                }
                _wasLeftHandMenuGesturePressed = handMenuPressed;
            }

            // 3. Desktop / Editor / ADB keyboard fallback (M or Tab when not typing in search or browser inputs)
            if (Keyboard.current != null && !IsSearchFocused)
            {
                if (Keyboard.current.mKey.wasPressedThisFrame || Keyboard.current.tabKey.wasPressedThisFrame)
                {
                    toggleTriggered = true;
                }
            }

            if (toggleTriggered)
            {
                ToggleUIVisibility();
            }
        }

        private float _lastToggleTime = -10f;
        private Vector3 _lockedCanvasPos;
        private Quaternion _lockedCanvasRot;
        private bool _hasLockedCanvasPose = false;

        public void ToggleUIVisibility()
        {
            if (Time.unscaledTime - _lastToggleTime < 0.25f) return;
            _lastToggleTime = Time.unscaledTime;
            SetUIVisible(!_isVisible);
        }

        public void SetUIVisible(bool visible)
        {
            if (_isVisible == visible) return;
            _isVisible = visible;
            SetCameraOpaqueCaptureEnabled(_isVisible);
            Debug.Log($"[SonicUI] SetUIVisible({_isVisible})");

            if (_isVisible)
            {
                // Position spatial rig in front of user FIRST while canvas is still invisible
                PositionSpatialRigInFrontOfUser();

                // Ensure initial alpha starts at absolute 0 for zero-flash smooth fade
                _visibilityBlend = 0f;
                if (_rootCanvasGroup != null)
                {
                    _rootCanvasGroup.alpha = 0f;
                }

                if (_canvasObject != null && !_canvasObject.activeSelf)
                {
                    _canvasObject.SetActive(true);
                }

                // Populate cards while alpha is 0
                RefreshMusicGrid(resetScroll: false);
                RefreshPlayerBarUI();
                Canvas.ForceUpdateCanvases();
            }

            if (_visualizer != null)
            {
                _visualizer.suppressWorldPointerInput = false;
            }

            if (_rootCanvasGroup != null)
            {
                _rootCanvasGroup.interactable = _isVisible;
                _rootCanvasGroup.blocksRaycasts = _isVisible;
            }

            if (!_isVisible)
            {
                _hasLockedCanvasPose = false;
                CloseAllVirtualKeyboards();
            }
            SyncBrowserVisibilityState();
        }

        private void SetUIVisibleImmediate(bool visible)
        {
            _isVisible = visible;
            _visibilityBlend = visible ? 1f : 0f;
            SetCameraOpaqueCaptureEnabled(visible);

            if (visible)
            {
                PositionSpatialRigInFrontOfUser();
            }

            if (_rootCanvasGroup != null)
            {
                _rootCanvasGroup.alpha = _visibilityBlend;
                _rootCanvasGroup.interactable = visible;
                _rootCanvasGroup.blocksRaycasts = visible;
            }
            if (_canvasObject != null)
            {
                _canvasObject.SetActive(visible);
            }
            if (_visualizer != null)
            {
                _visualizer.suppressWorldPointerInput = false;
            }
            SyncBrowserVisibilityState();
        }

        private void SyncBrowserVisibilityState()
        {
            if (_browserManager == null) return;
            float alpha = _rootCanvasGroup != null ? _rootCanvasGroup.alpha : 0f;
            bool showBrowser = _isVisible && _activeSection == SonicUISection.Browser;
            _browserManager.SetViewportVisible(showBrowser, alpha);
        }

        private void LateUpdate()
        {
            if (_isVisible && _hasLockedCanvasPose && _canvasRect != null)
            {
                _canvasRect.position = _lockedCanvasPos;
                _canvasRect.rotation = _lockedCanvasRot;
                if (_browserManager != null)
                {
                    _browserManager.SyncOverlayPoseNow();
                }
            }
        }

        private void UpdateVisibilityAnimation()
        {
            if (_canvasObject == null || _rootCanvasGroup == null) return;

            float target = _isVisible ? 1f : 0f;
            if (Mathf.Approximately(_visibilityBlend, target))
            {
                if (!_isVisible && _canvasObject.activeSelf)
                {
                    _canvasObject.SetActive(false);
                }
                SyncBrowserVisibilityState();
                return;
            }

            // Smooth, luxurious fade: 1.6f speed gives ~0.62s smooth ease-in; closing is slightly faster (3.0f)
            float speed = _isVisible ? 1.6f : 3.0f;
            _visibilityBlend = Mathf.MoveTowards(_visibilityBlend, target, Time.unscaledDeltaTime * speed);
            float smooth = Mathf.SmoothStep(0f, 1f, _visibilityBlend);

            _rootCanvasGroup.alpha = smooth;
            float scaleFactor = Mathf.Lerp(0.96f, 1.0f, smooth);
            if (_canvasRect != null)
            {
                _canvasRect.localScale = _baseCanvasScale * scaleFactor;
            }

            SyncBrowserVisibilityState();

            if (!_isVisible && _visibilityBlend <= 0.001f)
            {
                _canvasObject.SetActive(false);
            }
        }

        public void PositionSpatialRigInFrontOfUser()
        {
            Camera cam = GetMainCamera();
            if (cam == null || _canvasRect == null) return;

            Vector3 headPos = cam.transform.position;
            float currentGround = (headPos.y < -5f) ? -11.0f : 0f;
            if (headPos.y < currentGround + 0.5f)
            {
                headPos.y = currentGround + 1.35f;
            }
            Vector3 flatForward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.001f)
            {
                flatForward = cam.transform.forward;
            }
            flatForward.Normalize();

            Vector3 targetPos = headPos + flatForward * spawnDistance + new Vector3(0f, verticalEyeOffset, 0f);
            Quaternion lookRot = Quaternion.LookRotation(flatForward, Vector3.up) * Quaternion.Euler(4.5f, 0f, 0f);

            _canvasRect.position = targetPos;
            _canvasRect.rotation = lookRot;
            _lockedCanvasPos = targetPos;
            _lockedCanvasRot = lookRot;
            _hasLockedCanvasPose = true;
            _menuSummonHeadPos = headPos;
        }

        private void UpdateLivePlayerProgress()
        {
            if (!_isVisible) return;

            if (UseBrowserPlayerBar)
            {
                float bTime = _browserManager.CurrentTime;
                float bDur = _browserManager.CurrentDuration;
                float norm = bDur > 0.01f ? Mathf.Clamp01(bTime / bDur) : 0f;
                if (_progressSlider != null && (Time.unscaledTime - _lastProgressInteractionTime > 0.25f))
                {
                    _suppressSliderCallbacks = true;
                    _progressSlider.value = norm;
                    _suppressSliderCallbacks = false;
                }
                if (_playerTimeLabel != null)
                {
                    int curSec = Mathf.FloorToInt(Mathf.Max(0f, bTime));
                    int durSec = Mathf.FloorToInt(Mathf.Max(0f, bDur));
                    if (_wasDisplayingLoading || curSec != _lastDisplayedTimeSec || durSec != _lastDisplayedDurSec)
                    {
                        _wasDisplayingLoading = false;
                        _lastDisplayedTimeSec = curSec;
                        _lastDisplayedDurSec = durSec;
                        _playerTimeLabel.text = bDur > 0.01f
                            ? $"{FormatTimeSeconds(bTime)} / {FormatTimeSeconds(bDur)}"
                            : "LIVE";
                    }
                }
                return;
            }

            if (_player == null) return;

            if (_player.IsLoadingTrack)
            {
                if (_playerTimeLabel != null && !_wasDisplayingLoading)
                {
                    _wasDisplayingLoading = true;
                    _lastDisplayedTimeSec = -1;
                    _lastDisplayedDurSec = -1;
                    _playerTimeLabel.text = "Loading...";
                }
                if (_progressSlider != null && (Time.unscaledTime - _lastProgressInteractionTime > 0.25f))
                {
                    _suppressSliderCallbacks = true;
                    _progressSlider.value = 0f;
                    _suppressSliderCallbacks = false;
                }
                return;
            }

            if (_progressSlider != null && (Time.unscaledTime - _lastProgressInteractionTime > 0.25f))
            {
                _suppressSliderCallbacks = true;
                _progressSlider.value = _player.NormalizedProgress;
                _suppressSliderCallbacks = false;
            }

            if (_playerTimeLabel != null)
            {
                if (_player.CurrentTrack != null && _player.CurrentDuration > 0.01f)
                {
                    float rawTime = _player.CurrentTime;
                    float rawDur = _player.CurrentDuration;
                    int curSec = (float.IsNaN(rawTime) || float.IsInfinity(rawTime) || rawTime < 0f) ? 0 : Mathf.FloorToInt(rawTime);
                    int durSec = (float.IsNaN(rawDur) || float.IsInfinity(rawDur) || rawDur < 0f) ? 0 : Mathf.FloorToInt(rawDur);
                    if (_wasDisplayingLoading || curSec != _lastDisplayedTimeSec || durSec != _lastDisplayedDurSec)
                    {
                        _wasDisplayingLoading = false;
                        _lastDisplayedTimeSec = curSec;
                        _lastDisplayedDurSec = durSec;
                        _playerTimeLabel.text = $"{FormatTimeSeconds(rawTime)} / {FormatTimeSeconds(rawDur)}";
                    }
                }
                else if (_wasDisplayingLoading || _lastDisplayedTimeSec != 0 || _lastDisplayedDurSec != 0)
                {
                    _wasDisplayingLoading = false;
                    _lastDisplayedTimeSec = 0;
                    _lastDisplayedDurSec = 0;
                    _playerTimeLabel.text = "0:00 / 0:00";
                }
            }
        }

        private static string FormatTimeSeconds(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
            {
                seconds = 0f;
            }
            int totalSec = Mathf.FloorToInt(seconds);
            int mins = totalSec / 60;
            int secs = totalSec % 60;
            return $"{mins}:{secs:00}";
        }

        // =====================================================================
        // SPATIAL UI HIERARCHY CONSTRUCTION (APPLE VISIONOS SPATIAL DESIGN)
        // =====================================================================

        private void BuildSpatialUIRig()
        {
            _canvasObject = new GameObject("SonicVisionSpatialUI");
            _canvasObject.transform.SetParent(transform, false);
            _canvasObject.SetActive(false);

            Canvas canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            canvas.sortingOrder = 25;
            // Enable UV1, UV2, UV3 channels so SonicGlassSurface passes exact analytical SDF geometry to shader
            canvas.additionalShaderChannels =
                AdditionalCanvasShaderChannels.TexCoord1 |
                AdditionalCanvasShaderChannels.TexCoord2 |
                AdditionalCanvasShaderChannels.TexCoord3;

            CanvasScaler scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            scaler.dynamicPixelsPerUnit = 10f;

            // In VR, use TrackedDeviceGraphicRaycaster only to prevent pointer conflicts and sticky states
            _canvasObject.AddComponent<TrackedDeviceGraphicRaycaster>();

            _rootCanvasGroup = _canvasObject.AddComponent<CanvasGroup>();
            _rootCanvasGroup.alpha = 0f;
            _rootCanvasGroup.interactable = false;
            _rootCanvasGroup.blocksRaycasts = false;

            _canvasRect = _canvasObject.GetComponent<RectTransform>();
            _canvasRect.sizeDelta = new Vector2(1260f, 780f);
            _baseCanvasScale = Vector3.one * canvasWorldScale;
            _canvasRect.localScale = _baseCanvasScale;

            // 1. Left Vertical Navigation Pill (Frosted Backdrop-Blur Glass)
            BuildLeftNavigationPill(_canvasRect);

            // 2. Central Floating Glass Window (Frosted Backdrop-Blur Glass)
            BuildCentralMainWindow(_canvasRect);

            // 3. Bottom Persistent Player Bar Ornament (Frosted Backdrop-Blur Glass)
            BuildBottomPlayerBar(_canvasRect);

            SwitchSection(SonicUISection.Music);
        }

        private void BuildLeftNavigationPill(RectTransform parent)
        {
            RectTransform pillRect = CreateRect("LeftNavigationPill", parent, new Vector2(-512f, 52f), new Vector2(80f, 284f));
            AddGlassSurface(pillRect.gameObject, Color.white, 40f, isPill: true, border: 0.18f, SonicSurfaceMode.FrostedBlurWindow);

            SonicIconType[] icons =
            {
                SonicIconType.MusicNote,
                SonicIconType.Folder,
                SonicIconType.Globe,
                SonicIconType.SettingsSliders
            };
            string[] names = { "NavMusicBtn", "NavBrowseBtn", "NavBrowserBtn", "NavSettingsBtn" };
            float[] yOffsets = { 102f, 34f, -34f, -102f };

            for (int i = 0; i < 4; i++)
            {
                int sectionIdx = i;
                RectTransform btnRect = CreateRect(names[i], pillRect, new Vector2(0f, yOffsets[i]), new Vector2(56f, 56f));

                Image btnBg = AddGlassSurface(btnRect.gameObject, ColPillIdle, 28f, isPill: true, border: 0.08f);
                _navButtonBgs[i] = btnBg;

                Button btn = btnRect.gameObject.AddComponent<Button>();
                ConfigureVisionButtonColors(btn, btnBg);
                btn.onClick.AddListener(() => SwitchSection((SonicUISection)sectionIdx));

                RectTransform iconRect = CreateRect("Icon", btnRect, Vector2.zero, new Vector2(26f, 26f));
                Image iconImg = iconRect.gameObject.AddComponent<Image>();
                iconImg.sprite = SonicUIIconGenerator.GetIcon(icons[i]);
                iconImg.color = ColTextSecondary;
                iconImg.raycastTarget = false;
                _navButtonIcons[i] = iconImg;

                AddHoverHighlight(btnRect.gameObject, btnBg, iconImg, ColPillIdle, 0.08f, 0.22f);
            }
        }

        private void BuildCentralMainWindow(RectTransform parent)
        {
            RectTransform winRect = CreateRect("CentralGlassWindow", parent, new Vector2(28f, 52f), new Vector2(960f, 564f));
            AddGlassSurface(winRect.gameObject, Color.white, 38f, isPill: false, border: 0.18f, SonicSurfaceMode.FrostedBlurWindow);

            _musicViewRoot = BuildMusicView(winRect);
            _browseViewRoot = BuildBrowseView(winRect);
            _browserViewRoot = BuildBrowserView(winRect);
            _settingsViewRoot = BuildSettingsView(winRect);

            if (_browseViewRoot != null) _browseViewRoot.SetActive(false);
            if (_browserViewRoot != null) _browserViewRoot.SetActive(false);
            if (_settingsViewRoot != null) _settingsViewRoot.SetActive(false);
            if (_musicViewRoot != null) _musicViewRoot.SetActive(true);

            SwitchSection(SonicUISection.Music);
        }

        private static Font CreateLegacyInputFont(int fontSize)
        {
            Font legacyFont = null;
            string[] cjkFontFamilies = new string[]
            {
                "Noto Sans CJK SC", "Noto Sans SC", "Microsoft YaHei", "SimSun", "SimHei",
                "PingFang SC", "Noto Sans CJK JP", "Noto Sans JP", "MS Gothic", "Meiryo",
                "Noto Sans CJK KR", "Noto Sans KR", "Malgun Gothic", "Arial Unicode MS", "Arial"
            };

            try
            {
                legacyFont = Font.CreateDynamicFontFromOSFont(cjkFontFamilies, fontSize);
            }
            catch { }

            if (legacyFont == null)
            {
                legacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (legacyFont == null) legacyFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            if (legacyFont != null)
            {
                try { legacyFont.fontNames = cjkFontFamilies; } catch { }
            }

            return legacyFont;
        }

        private GameObject BuildBrowserView(RectTransform winParent)
        {
            RectTransform viewRect = CreateStretchRect("BrowserView", winParent, Vector4.zero);

            // Edge-to-edge rounded Browser Viewport (960x564 matching CentralGlassWindow, r=38px; nav bar lives in BottomPlayerBar)
            _browserViewportRect = CreateStretchRect("BrowserViewport", viewRect, Vector4.zero);
            Image vpHit = _browserViewportRect.gameObject.AddComponent<Image>();
            vpHit.sprite = _whiteSprite;
            vpHit.color = new Color(0f, 0f, 0f, 0.002f);
            vpHit.raycastTarget = true;

            SonicBrowserViewportInput vpInput = _browserViewportRect.gameObject.AddComponent<SonicBrowserViewportInput>();
            vpInput.Initialize(_browserManager);

            RectTransform punchRect = CreateStretchRect("BrowserPunchout", _browserViewportRect, Vector4.zero);
            _browserPunchoutRawImage = punchRect.gameObject.AddComponent<RawImage>();
            _browserPunchoutRawImage.color = Color.white;
            if (_browserPunchoutRawImage.canvasRenderer != null)
            {
                _browserPunchoutRawImage.canvasRenderer.cullTransparentMesh = false;
            }
            _browserPunchoutRawImage.raycastTarget = false;
            _browserPunchoutRawImage.enabled = false;

            SonicGlassSurface punchSurf = punchRect.gameObject.AddComponent<SonicGlassSurface>();
            punchSurf.Configure(SonicSurfaceMode.SdfShape, 38f, pill: false, border: 0f);

            RectTransform phRect = CreateStretchRect("BrowserPlaceholder", _browserViewportRect, Vector4.zero);
            _browserPlaceholderRoot = phRect.gameObject;
            Image phBg = AddGlassSurface(phRect.gameObject, ColRecessedDark, 38f, isPill: false, border: 0.18f);
            phBg.raycastTarget = false;

#if UNITY_ANDROID && !UNITY_EDITOR
            const string initialStatus = "Starting GeckoView browser...";
#else
            const string initialStatus = "Built-in GeckoView browser runs natively on Meta Quest 3.\nYouTube Music / YouTube / SoundCloud render here in headset.";
#endif
            _browserStatusLabel = CreateLabel("BrowserStatusLabel", phRect, initialStatus, 14f, FontStyles.Normal, TextAlignmentOptions.Center);
            _browserStatusLabel.color = ColTextSecondary;
            _browserStatusLabel.textWrappingMode = TextWrappingModes.Normal;
            SetRectAnchored(_browserStatusLabel.rectTransform, Vector2.zero, new Vector2(640f, 80f), new Vector2(0.5f, 0.5f));

            _browserManager?.BindViewport(_browserViewportRect, _browserPunchoutRawImage, _browserPlaceholderRoot);

            return viewRect.gameObject;
        }

        private void NavigateBrowserUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string trimmed = url.Trim();
            if (_browserUrlInput != null && !_browserUrlInput.isFocused)
            {
                _browserUrlInput.text = trimmed;
            }
            _browserManager?.Navigate(trimmed);
        }

        // =====================================================================
        // SECTION 1: MUSIC VIEW (CLEAN VISIONOS LIBRARY + PILL SEARCH + 4-COLUMN GRID)
        // =====================================================================

        private GameObject BuildMusicView(RectTransform winParent)
        {
            RectTransform viewRect = CreateStretchRect("MusicView", winParent, new Vector4(40f, 24f, 40f, 26f));

            // 1. Clean Apple visionOS Header: Title + Subtitle on top-left, Pill Search Bar beside it on top-right
            RectTransform headerRect = CreateRect("Header", viewRect, new Vector2(0f, 224f), new Vector2(880f, 56f));

            TextMeshProUGUI titleText = CreateLabel("Title", headerRect, "Music", 30f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(titleText.rectTransform, new Vector2(-440f, 11f), new Vector2(320f, 34f), new Vector2(0f, 0.5f));

            _musicSubtitleLabel = CreateLabel("Subtitle", headerRect, "Here's your local music library", 13f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            _musicSubtitleLabel.color = ColTextSecondary;
            SetRectAnchored(_musicSubtitleLabel.rectTransform, new Vector2(-440f, -15f), new Vector2(340f, 22f), new Vector2(0f, 0.5f));

            // 2. Pill Search Bar right beside the "Music" header text (saves vertical space for the album grid)
            RectTransform searchBoxRect = CreateRect("SearchBar", headerRect, new Vector2(180f, 0f), new Vector2(520f, 44f));
            Image searchBoxBg = AddGlassSurface(searchBoxRect.gameObject, ColRecessedDark, 22f, isPill: true, border: 0.10f);
            searchBoxBg.raycastTarget = true;

            RectTransform searchIconRect = CreateRect("SearchIcon", searchBoxRect, new Vector2(-232f, 0f), new Vector2(20f, 20f));
            Image searchIcon = searchIconRect.gameObject.AddComponent<Image>();
            searchIcon.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.Search);
            searchIcon.color = ColTextSecondary;
            searchIcon.raycastTarget = false;

            RectTransform textArea = CreateRect("TextArea", searchBoxRect, new Vector2(-4f, 0f), new Vector2(412f, 34f));
            textArea.gameObject.AddComponent<RectMask2D>();

            Font legacyFont = CreateLegacyInputFont(15);

            RectTransform placeholderRect = CreateRect("Placeholder", textArea, Vector2.zero, Vector2.zero);
            StretchFull(placeholderRect);
            Text placeholder = placeholderRect.gameObject.AddComponent<Text>();
            placeholder.font = legacyFont;
            placeholder.text = "Search songs";
            placeholder.fontSize = 15;
            placeholder.fontStyle = UnityEngine.FontStyle.Normal;
            placeholder.alignment = TextAnchor.MiddleLeft;
            placeholder.color = ColTextMuted;
            placeholder.raycastTarget = false;

            RectTransform inputTextRect = CreateRect("Text", textArea, Vector2.zero, Vector2.zero);
            StretchFull(inputTextRect);
            Text inputText = inputTextRect.gameObject.AddComponent<Text>();
            inputText.font = legacyFont;
            inputText.text = string.Empty;
            inputText.fontSize = 15;
            inputText.fontStyle = UnityEngine.FontStyle.Normal;
            inputText.alignment = TextAnchor.MiddleLeft;
            inputText.color = ColTextPrimary;
            inputText.supportRichText = false;
            inputText.raycastTarget = false;

            // Icon-only Clear button on the right edge of the search pill
            RectTransform clearBtnRect = CreateIconButton(searchBoxRect, "ClearBtn", new Vector2(232f, 0f), new Vector2(30f, 30f),
                SonicIconType.CloseX, new Color(1f, 1f, 1f, 0.12f), ColTextSecondary, out Image clearBg, out Image clearIcon, () =>
                {
                    if (_searchInput != null) _searchInput.text = string.Empty;
                    var opener = searchBoxRect.GetComponent<SonicQuestVirtualKeyboardOpener>();
                    if (opener != null) opener.ClearKeyboardText();
                    _searchQuery = string.Empty;
                    RefreshMusicGrid(resetScroll: true);
                });
            AddHoverHighlight(clearBtnRect.gameObject, clearBg, clearIcon, new Color(1f, 1f, 1f, 0.12f), 0.10f, 0.24f);
            clearBtnRect.gameObject.SetActive(false);

            _searchInput = searchBoxRect.gameObject.AddComponent<InputField>();
            _searchInput.textComponent = inputText;
            _searchInput.placeholder = placeholder;
            _searchInput.targetGraphic = searchBoxBg;
            _searchInput.lineType = InputField.LineType.SingleLine;
            _searchInput.keyboardType = TouchScreenKeyboardType.Default;
            _searchInput.shouldHideMobileInput = false;

            _searchInput.onValueChanged.AddListener(val =>
            {
                _searchQuery = val ?? string.Empty;
                clearBtnRect.gameObject.SetActive(!string.IsNullOrEmpty(_searchQuery));
                RefreshMusicGrid(resetScroll: true);
            });

            _searchInput.onEndEdit.AddListener(val =>
            {
                if (UnityEngine.EventSystems.EventSystem.current != null &&
                    UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject == searchBoxRect.gameObject)
                {
                    UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
                }
            });

            _searchOpener = searchBoxRect.gameObject.AddComponent<SonicQuestVirtualKeyboardOpener>();
            _searchOpener.Initialize(_searchInput);

            AddHoverHighlight(searchBoxRect.gameObject, searchBoxBg, searchIcon, ColRecessedDark, 0.10f, 0.28f);

            // 3. Expanded ScrollView for 4-column Album Grid (444px tall, kept safely below the header)
            RectTransform scrollRectTransform = CreateRect("MusicGridScrollView", viewRect, new Vector2(0f, -34f), new Vector2(880f, 444f));
            ScrollRect scrollRect = scrollRectTransform.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Elastic;
            scrollRect.elasticity = 0.08f;
            scrollRect.scrollSensitivity = 28f;

            RectTransform viewport = CreateStretchRect("Viewport", scrollRectTransform, Vector4.zero);
            _musicMask = viewport.gameObject.AddComponent<RectMask2D>();
            _musicMask.softness = Vector2Int.zero;
            Image viewportHit = viewport.gameObject.AddComponent<Image>();
            viewportHit.color = new Color(0f, 0f, 0f, 0.002f);

            RectTransform content = CreateRect("Content", viewport, Vector2.zero, new Vector2(0f, 444f));
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 444f);

            GridLayoutGroup grid = content.gameObject.AddComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(4, 4, 6, 16);
            grid.cellSize = new Vector2(204f, 212f);
            grid.spacing = new Vector2(18f, 14f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            grid.childAlignment = TextAnchor.UpperLeft;

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.viewport = viewport;
            scrollRect.content = content;
            _musicGridContent = content;

            return viewRect.gameObject;
        }

        public void RefreshMusicGrid(bool resetScroll = false)
        {
            if (_musicGridContent == null || _library == null) return;
            Vector2 savedPos = _musicGridContent.anchoredPosition;

            for (int i = 0; i < _musicCardObjects.Count; i++)
            {
                if (_musicCardObjects[i] != null)
                {
                    _musicCardObjects[i].SetActive(false);
                    _musicCardObjects[i].transform.SetParent(null, false);
                    Destroy(_musicCardObjects[i]);
                }
            }
            _musicCardObjects.Clear();

            IReadOnlyList<SonicMusicTrack> tracksToShow = _library.Tracks;

            SonicMusicTrack activeTrack = _player != null ? _player.CurrentTrack : null;

            int shownCount = 0;
            for (int i = 0; i < tracksToShow.Count; i++)
            {
                SonicMusicTrack track = tracksToShow[i];

                if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    bool matchTitle = track.title != null && track.title.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchArtist = track.artist != null && track.artist.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchAlbum = track.album != null && track.album.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchFolder = track.folderName != null && track.folderName.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchTitle && !matchArtist && !matchAlbum && !matchFolder) continue;
                }

                shownCount++;
                bool isCurrent = activeTrack != null && activeTrack.id == track.id;
                GameObject card = CreateMusicTrackCard(_musicGridContent, track, isCurrent);
                _musicCardObjects.Add(card);
            }

            if (shownCount == 0)
            {
                GameObject emptyCard = CreateEmptyMusicCard(_musicGridContent);
                _musicCardObjects.Add(emptyCard);
            }

            if (_musicSubtitleLabel != null)
            {
                int total = _library.Tracks.Count;
                if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    _musicSubtitleLabel.text = shownCount == 1 ? "1 matching song" : $"{shownCount} matching songs";
                }
                else
                {
                    _musicSubtitleLabel.text = total == 1 ? "1 song in your library" : $"{total} songs in your library";
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_musicGridContent);
            _musicGridContent.anchoredPosition = resetScroll ? Vector2.zero : savedPos;
        }

        private GameObject CreateEmptyMusicCard(RectTransform parent)
        {
            GameObject cardGo = new GameObject("EmptyLibraryCard", typeof(RectTransform));
            cardGo.transform.SetParent(parent, false);

            Image cardBg = AddGlassSurface(cardGo, ColSurfaceCard, 24f, isPill: false, border: 0.10f);
            AddHoverHighlight(cardGo, cardBg, null, ColSurfaceCard, 0.10f, 0.22f);

            Button btn = cardGo.AddComponent<Button>();
            ConfigureVisionButtonColors(btn, cardBg);
            btn.onClick.AddListener(() => SwitchSection(SonicUISection.Browse));

            RectTransform rt = cardGo.GetComponent<RectTransform>();
            RectTransform iconRect = CreateRect("FolderIcon", rt, new Vector2(0f, 36f), new Vector2(40f, 40f));
            Image iconImg = iconRect.gameObject.AddComponent<Image>();
            iconImg.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.Folder);
            iconImg.color = ColTextSecondary;
            iconImg.raycastTarget = false;

            TextMeshProUGUI title = CreateLabel("EmptyTitle", rt, "No Songs Found", 15f, FontStyles.Bold, TextAlignmentOptions.Center);
            SetRectAnchored(title.rectTransform, new Vector2(0f, -14f), new Vector2(180f, 24f), new Vector2(0.5f, 0.5f));

            TextMeshProUGUI desc = CreateLabel("EmptyDesc", rt, "Tap to browse folders", 12f, FontStyles.Normal, TextAlignmentOptions.Center);
            desc.color = ColTextSecondary;
            SetRectAnchored(desc.rectTransform, new Vector2(0f, -38f), new Vector2(180f, 20f), new Vector2(0.5f, 0.5f));

            return cardGo;
        }

        private GameObject CreateMusicTrackCard(RectTransform parent, SonicMusicTrack track, bool isCurrent)
        {
            GameObject cardGo = new GameObject($"TrackCard_{track.title}", typeof(RectTransform));
            cardGo.transform.SetParent(parent, false);

            // Transparent hit area - no outer box around album art and text
            Image cardHit = cardGo.AddComponent<Image>();
            cardHit.sprite = _whiteSprite;
            cardHit.color = Color.clear;
            cardHit.raycastTarget = true;

            RectTransform cardRect = cardGo.GetComponent<RectTransform>();

            // 1:1 Analytical SDF Rounded Square Album Cover (170 x 170, radius 20) with razor-sharp crisp 1px border (no blur)
            RectTransform artFrame = CreateRect("ArtworkFrame", cardRect, new Vector2(0f, 18f), new Vector2(170f, 170f));
            Sprite artSprite = track.coverSprite != null
                ? track.coverSprite
                : _library.GetOrCreateProceduralCover(track.title, track.artist);
            float artBorder = isCurrent ? 0.88f : 0.08f;
            float hoverArtBorder = isCurrent ? 0.95f : 0.38f;
            Image artImage = AddRoundedArtworkImage(artFrame.gameObject, artSprite, 20f, border: artBorder);

            // Track Title directly under the album cover
            TextMeshProUGUI titleLbl = CreateLabel("TrackTitle", cardRect, track.title, 14f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            titleLbl.color = isCurrent ? Color.white : ColTextPrimary;
            titleLbl.overflowMode = TextOverflowModes.Truncate;
            titleLbl.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(titleLbl.rectTransform, new Vector2(0f, -77f), new Vector2(168f, 24f), new Vector2(0.5f, 0.5f));

            // Artist Subtitle directly under the Track Title
            TextMeshProUGUI subLbl = CreateLabel("ArtistSubtitle", cardRect, track.artist, 12f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            subLbl.color = isCurrent ? Color.white : ColTextSecondary;
            subLbl.overflowMode = TextOverflowModes.Truncate;
            subLbl.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(subLbl.rectTransform, new Vector2(0f, -95f), new Vector2(168f, 20f), new Vector2(0.5f, 0.5f));

            Button cardBtn = cardGo.AddComponent<Button>();
            cardBtn.targetGraphic = cardHit;
            ConfigureVisionButtonColors(cardBtn, cardHit);

            SonicVisionHoverEffect hover = cardGo.AddComponent<SonicVisionHoverEffect>();
            hover.targetImage = null;
            hover.normalColor = Color.clear;
            hover.hoverColor = Color.clear;
            hover.secondarySurface = artFrame.GetComponent<SonicGlassSurface>();
            hover.normalSecondaryBorder = artBorder;
            hover.hoverSecondaryBorder = hoverArtBorder;
            hover.targetLabel = titleLbl;
            hover.normalLabelColor = isCurrent ? Color.white : ColTextPrimary;
            hover.hoverLabelColor = Color.white;

            cardBtn.onClick.AddListener(() =>
            {
                if (_player != null)
                {
                    if (_player.CurrentTrack != null && _player.CurrentTrack.id == track.id)
                    {
                        _player.TogglePlayPause();
                    }
                    else
                    {
                        _player.PlayTrack(track);
                    }
                }
            });

            // Animated 3-bar Equalizer Badge for currently playing track
            if (isCurrent)
            {
                CreateEqualizerBadge(artFrame, new Vector2(58f, -58f));
            }

            return cardGo;
        }

        // =====================================================================
        // SECTION 2: BROWSE VIEW (CLEAN, ICON-DRIVEN FILESYSTEM NAVIGATION)
        // =====================================================================

        private GameObject BuildBrowseView(RectTransform winParent)
        {
            RectTransform viewRect = CreateStretchRect("BrowseView", winParent, new Vector4(40f, 24f, 40f, 26f));

            // Header row: "Browse" title + subtitle on left, Path Navigation Bar (Up + Current Scan Folder Path + Set Scan Folder) on right
            RectTransform headerRect = CreateRect("Header", viewRect, new Vector2(0f, 224f), new Vector2(880f, 56f));
            TextMeshProUGUI titleText = CreateLabel("Title", headerRect, "Browse", 30f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(titleText.rectTransform, new Vector2(-440f, 11f), new Vector2(240f, 34f), new Vector2(0f, 0.5f));

            TextMeshProUGUI subText = CreateLabel("Subtitle", headerRect, "Browse folders & albums", 13f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            subText.color = ColTextSecondary;
            SetRectAnchored(subText.rectTransform, new Vector2(-440f, -15f), new Vector2(240f, 22f), new Vector2(0f, 0.5f));

            // Left of path bar: Icon-only Back / Up Directory Circle Button
            RectTransform upBtnRect = CreateIconButton(headerRect, "UpDirectoryBtn", new Vector2(-136f, 0f), new Vector2(46f, 46f),
                SonicIconType.ChevronLeft, new Color(1f, 1f, 1f, 0.06f), ColTextSecondary, out Image upBg, out Image upIcon, NavigateBrowseUp, border: 0.08f);
            AddHoverHighlight(upBtnRect.gameObject, upBg, upIcon, new Color(1f, 1f, 1f, 0.06f), 0.08f, 0.22f);

            // Center: Recessed Path Display Capsule
            RectTransform pathPillRect = CreateRect("PathCapsule", headerRect, new Vector2(140f, 0f), new Vector2(486f, 44f));
            AddGlassSurface(pathPillRect.gameObject, ColRecessedDark, 22f, isPill: true, border: 0.10f);

            RectTransform folderIconRect = CreateRect("FolderIcon", pathPillRect, new Vector2(-216f, 0f), new Vector2(20f, 20f));
            Image folderIconImg = folderIconRect.gameObject.AddComponent<Image>();
            folderIconImg.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.Folder);
            folderIconImg.color = ColAccentWhite;
            folderIconImg.raycastTarget = false;

            _browsePathLabel = CreateLabel("CurrentPathText", pathPillRect, "/", 13.5f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            _browsePathLabel.color = ColTextSecondary;
            _browsePathLabel.overflowMode = TextOverflowModes.Truncate;
            _browsePathLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_browsePathLabel.rectTransform, new Vector2(12f, 0f), new Vector2(418f, 32f), new Vector2(0.5f, 0.5f));

            // Right: Icon-only "Set as Library Scan Folder" Checkmark Circle Button (White / Silver Accent)
            RectTransform useFolderRect = CreateIconButton(headerRect, "UseFolderBtn", new Vector2(417f, 0f), new Vector2(46f, 46f),
                SonicIconType.Check, new Color(1f, 1f, 1f, 0.07f), Color.white, out Image useFolderBg, out Image useFolderIcon, () =>
                {
                    if (_library != null && !string.IsNullOrEmpty(_currentBrowsePath))
                    {
                        _library.SetScanFolder(_currentBrowsePath);
                        RefreshSettingsUI();
                        RefreshMusicGrid();
                    }
                }, border: 0.34f);
            AddHoverHighlight(useFolderRect.gameObject, useFolderBg, useFolderIcon, new Color(1f, 1f, 1f, 0.07f), 0.34f, 0.44f);

            // Expanded Scrollable File & Folder List with dynamic top fade (safely below path capsule)
            RectTransform listScrollRect = CreateRect("BrowseScrollView", viewRect, new Vector2(0f, -34f), new Vector2(880f, 444f));
            ScrollRect sr = listScrollRect.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Elastic;
            sr.elasticity = 0.08f;
            sr.scrollSensitivity = 28f;

            RectTransform viewport = CreateStretchRect("Viewport", listScrollRect, Vector4.zero);
            _browseMask = viewport.gameObject.AddComponent<RectMask2D>();
            _browseMask.softness = Vector2Int.zero;
            Image vpHit = viewport.gameObject.AddComponent<Image>();
            vpHit.color = new Color(0f, 0f, 0f, 0.002f);

            RectTransform content = CreateRect("Content", viewport, Vector2.zero, new Vector2(0f, 444f));
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 444f);

            VerticalLayoutGroup vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(2, 2, 6, 14);
            vlg.spacing = 8f;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            sr.viewport = viewport;
            sr.content = content;
            _browseListContent = content;

            return viewRect.gameObject;
        }

        private void NavigateToBrowseDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            _currentBrowsePath = Path.GetFullPath(path);
            RefreshBrowseList(resetScroll: true);
        }

        private void NavigateBrowseUp()
        {
            if (string.IsNullOrEmpty(_currentBrowsePath)) return;
            try
            {
                DirectoryInfo parent = Directory.GetParent(_currentBrowsePath);
                if (parent != null && parent.Exists)
                {
                    _currentBrowsePath = parent.FullName;
                    RefreshBrowseList(resetScroll: true);
                }
            }
            catch
            {
                // Ignore root parent access errors on Android
            }
        }

        public void RefreshBrowseList(bool resetScroll = false)
        {
            if (_browseListContent == null || _library == null) return;
            Vector2 savedBrowsePos = _browseListContent.anchoredPosition;

            if (string.IsNullOrEmpty(_currentBrowsePath) || !Directory.Exists(_currentBrowsePath))
            {
                _currentBrowsePath = _library.ActiveScanFolder;
            }

            if (_browsePathLabel != null)
            {
                _browsePathLabel.text = _currentBrowsePath;
            }

            for (int i = 0; i < _browseRowObjects.Count; i++)
            {
                if (_browseRowObjects[i] != null)
                {
                    _browseRowObjects[i].SetActive(false);
                    _browseRowObjects[i].transform.SetParent(null, false);
                    Destroy(_browseRowObjects[i]);
                }
            }
            _browseRowObjects.Clear();

            List<SonicBrowserEntry> entries = _library.GetDirectoryEntries(_currentBrowsePath);
            if (entries.Count == 0)
            {
                GameObject emptyRow = CreateBrowseRow(_browseListContent, "Empty Folder", true, false, null);
                _browseRowObjects.Add(emptyRow);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_browseListContent);
                _browseListContent.anchoredPosition = resetScroll ? Vector2.zero : savedBrowsePos;
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                SonicBrowserEntry entry = entries[i];
                if (entry.isDirectory)
                {
                    string targetDir = entry.fullPath;
                    GameObject row = CreateBrowseRow(_browseListContent, entry.name, true, false, () => NavigateToBrowseDirectory(targetDir));
                    _browseRowObjects.Add(row);
                }
                else
                {
                    string targetFile = entry.fullPath;
                    string cleanTitle = Path.GetFileNameWithoutExtension(entry.name);
                    bool isPlayingThisFile = _player != null && _player.CurrentTrack != null &&
                        !string.IsNullOrEmpty(_player.CurrentTrack.filePath) &&
                        string.Equals(_player.CurrentTrack.filePath, targetFile, StringComparison.OrdinalIgnoreCase);

                    GameObject row = CreateBrowseRow(_browseListContent, cleanTitle, false, isPlayingThisFile, () =>
                    {
                        if (_player != null)
                        {
                            _player.PlayFileFromBrowser(targetFile);
                            RefreshBrowseList(resetScroll: false);
                        }
                    });
                    _browseRowObjects.Add(row);
                }
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(_browseListContent);
            _browseListContent.anchoredPosition = resetScroll ? Vector2.zero : savedBrowsePos;
        }

        private GameObject CreateBrowseRow(RectTransform parent, string title, bool isDirectory, bool isCurrent, Action onClick)
        {
            GameObject rowGo = new GameObject($"Row_{title}", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);

            LayoutElement le = rowGo.AddComponent<LayoutElement>();
            le.preferredHeight = 50f;
            le.minHeight = 50f;

            Color baseBg = isCurrent ? ColSurfaceActive : (isDirectory ? ColSurfaceCard : ColSurfaceElevated);
            float borderVal = isCurrent ? 0.38f : 0.08f;

            Image rowBg = AddGlassSurface(
                rowGo,
                baseBg,
                16f,
                isPill: false,
                border: borderVal);

            if (onClick != null)
            {
                Button btn = rowGo.AddComponent<Button>();
                ConfigureVisionButtonColors(btn, rowBg);
                btn.onClick.AddListener(() => onClick());
            }

            AddHoverHighlight(rowGo, rowBg, null, baseBg, borderVal, isCurrent ? 0.48f : 0.24f);

            RectTransform rowRect = rowGo.GetComponent<RectTransform>();

            // Left Icon
            RectTransform iconRect = CreateRect("Icon", rowRect, new Vector2(-412f, 0f), new Vector2(20f, 20f));
            Image iconImg = iconRect.gameObject.AddComponent<Image>();
            iconImg.sprite = SonicUIIconGenerator.GetIcon(isDirectory ? SonicIconType.Folder : SonicIconType.MusicNote);
            iconImg.color = isCurrent ? Color.white : (isDirectory ? ColAccentWhite : ColTextSecondary);
            iconImg.raycastTarget = false;

            // Entry Name
            TextMeshProUGUI nameLbl = CreateLabel("Name", rowRect, title, 14.5f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            nameLbl.color = isCurrent ? Color.white : ColTextSecondary;
            nameLbl.overflowMode = TextOverflowModes.Truncate;
            nameLbl.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(nameLbl.rectTransform, new Vector2(-10f, 0f), new Vector2(740f, 32f), new Vector2(0.5f, 0.5f));

            // Right Disclosure Icon / Equalizer
            if (isCurrent)
            {
                CreateEqualizerBars(rowRect, new Vector2(414f, 0f));
            }
            else if (onClick != null)
            {
                RectTransform rightIconRect = CreateRect("ActionIcon", rowRect, new Vector2(414f, 0f), new Vector2(15f, 15f));
                Image rightIcon = rightIconRect.gameObject.AddComponent<Image>();
                rightIcon.sprite = SonicUIIconGenerator.GetIcon(isDirectory ? SonicIconType.ChevronRight : SonicIconType.Play);
                rightIcon.color = ColTextMuted;
                rightIcon.raycastTarget = false;
            }

            return rowGo;
        }

        // =====================================================================
        // SECTION 3: SETTINGS VIEW (CLEAN IOS / VISIONOS CONTROL CENTER LAYOUT)
        // =====================================================================

        private GameObject BuildSettingsView(RectTransform winParent)
        {
            RectTransform viewRect = CreateStretchRect("SettingsView", winParent, Vector4.zero);

            // Header - aligned with Left panel edge at X = -444f
            RectTransform headerRect = CreateRect("Header", viewRect, new Vector2(0f, 226f), new Vector2(888f, 36f));
            TextMeshProUGUI titleText = CreateLabel("Title", headerRect, "Settings", 28f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(titleText.rectTransform, new Vector2(-444f, 0f), new Vector2(300f, 36f), new Vector2(0f, 0.5f));

            // --- LEFT COLUMN: CONTROLS & DYNAMICS (426 x 442 Obsidian Glass Panel) ---
            RectTransform leftPanel = CreateRect("LeftControlsPanel", viewRect, new Vector2(-231f, -25f), new Vector2(426f, 442f));
            AddGlassSurface(leftPanel.gameObject, ColSurfaceCard, 22f, isPill: false, border: 0.10f);

            // 1. Music Folder Inset Row (394 x 52) at y = 178f
            RectTransform folderRow = CreateRect("LibraryFolderRow", leftPanel, new Vector2(0f, 178f), new Vector2(394f, 52f));
            Image folderRowBg = AddGlassSurface(folderRow.gameObject, ColSurfaceElevated, 16f, isPill: false, border: 0.10f);
            Button folderRowBtn = folderRow.gameObject.AddComponent<Button>();
            ConfigureVisionButtonColors(folderRowBtn, folderRowBg);
            folderRowBtn.onClick.AddListener(() => SwitchSection(SonicUISection.Browse));
            AddHoverHighlight(folderRow.gameObject, folderRowBg, null, ColSurfaceElevated, 0.10f, 0.24f);

            RectTransform folderBadge = CreateRect("FolderBadge", folderRow, new Vector2(-166f, 0f), new Vector2(36f, 36f));
            AddGlassSurface(folderBadge.gameObject, new Color(1f, 1f, 1f, 0.10f), 18f, isPill: true, border: 0.10f);
            RectTransform fIconRect = CreateRect("Icon", folderBadge, Vector2.zero, new Vector2(18f, 18f));
            Image fIcon = fIconRect.gameObject.AddComponent<Image>();
            fIcon.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.Folder);
            fIcon.color = ColAccentWhite;
            fIcon.raycastTarget = false;

            _settingsFolderTitleLabel = CreateLabel("FolderName", folderRow, "Music Folder", 14f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            _settingsFolderTitleLabel.overflowMode = TextOverflowModes.Truncate;
            _settingsFolderTitleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_settingsFolderTitleLabel.rectTransform, new Vector2(0f, 8f), new Vector2(266f, 20f), new Vector2(0.5f, 0.5f));

            _settingsFolderSubLabel = CreateLabel("FolderSub", folderRow, "", 11.5f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            _settingsFolderSubLabel.color = ColTextSecondary;
            _settingsFolderSubLabel.overflowMode = TextOverflowModes.Truncate;
            _settingsFolderSubLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_settingsFolderSubLabel.rectTransform, new Vector2(0f, -10f), new Vector2(266f, 16f), new Vector2(0.5f, 0.5f));

            RectTransform folderChevRect = CreateRect("Chevron", folderRow, new Vector2(172f, 0f), new Vector2(14f, 14f));
            Image folderChev = folderChevRect.gameObject.AddComponent<Image>();
            folderChev.sprite = SonicUIIconGenerator.GetIcon(SonicIconType.ChevronRight);
            folderChev.color = ColTextMuted;
            folderChev.raycastTarget = false;

            // Subtle Section Divider
            CreateDividerLine(leftPanel, new Vector2(0f, 138f), 394f);

            // 2. Terrain Motion Section Header + Reset Button
            TextMeshProUGUI motionHeaderLbl = CreateLabel("MotionHeader", leftPanel, "Terrain Motion", 13f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            motionHeaderLbl.color = ColTextSecondary;
            SetRectAnchored(motionHeaderLbl.rectTransform, new Vector2(-102f, 114f), new Vector2(190f, 22f), new Vector2(0.5f, 0.5f));

            RectTransform resetMotionBtnRect = CreateRect("ResetMotionBtn", leftPanel, new Vector2(154f, 114f), new Vector2(76f, 26f));
            Image resetMotionBg = AddGlassSurface(resetMotionBtnRect.gameObject, ColPillIdle, 13f, isPill: true, border: 0.10f);
            Button resetMotionBtn = resetMotionBtnRect.gameObject.AddComponent<Button>();
            ConfigureVisionButtonColors(resetMotionBtn, resetMotionBg);
            resetMotionBtn.onClick.AddListener(() =>
            {
                if (_visualizer != null)
                {
                    _visualizer.ResetMotionToDefaults();
                }
                RefreshSettingsUI();
            });
            AddHoverHighlight(resetMotionBtnRect.gameObject, resetMotionBg, null, ColPillIdle, 0.10f, 0.24f);
            TextMeshProUGUI resetMotionTxt = CreateLabel("ResetTxt", resetMotionBtnRect, "Reset", 12f, FontStyles.Bold, TextAlignmentOptions.Center);
            StretchFull(resetMotionTxt.rectTransform);

            _rotationSlider = CreateLabeledSliderRow(
                leftPanel,
                new Vector2(0f, 76f),
                "Rotation",
                0f,
                0.8f,
                _visualizer != null ? _visualizer.rotationSpeed : SonicTopographyVisualizer.DefaultRotationSpeed,
                val =>
                {
                    if (_visualizer != null) _visualizer.rotationSpeed = val;
                    PlayerPrefs.SetFloat("SonicTopography_MotionRotation", val);
                    PlayerPrefs.Save();
                });

            _amplitudeSlider = CreateLabeledSliderRow(
                leftPanel,
                new Vector2(0f, 38f),
                "Elevation",
                10f,
                95f,
                _visualizer != null ? _visualizer.groundEqSettings.amplitude : SonicTopographyVisualizer.DefaultAmplitude,
                val =>
                {
                    if (_visualizer != null) _visualizer.groundEqSettings.amplitude = val;
                    PlayerPrefs.SetFloat("SonicTopography_MotionElevation", val);
                    PlayerPrefs.Save();
                });

            _motionSpeedSlider = CreateLabeledSliderRow(
                leftPanel,
                new Vector2(0f, 0f),
                "Wave Speed",
                0f,
                100f,
                _visualizer != null ? _visualizer.groundEqSettings.motionSpeed : SonicTopographyVisualizer.DefaultMotionSpeed,
                val =>
                {
                    if (_visualizer != null) _visualizer.groundEqSettings.motionSpeed = val;
                    PlayerPrefs.SetFloat("SonicTopography_MotionSpeed", val);
                    PlayerPrefs.Save();
                });

            // Subtle Section Divider
            CreateDividerLine(leftPanel, new Vector2(0f, -26f), 394f);

            // 3. Visual Effects Section Header
            TextMeshProUGUI fxHeaderLbl = CreateLabel("FxHeader", leftPanel, "Visual Effects", 13f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            fxHeaderLbl.color = ColTextSecondary;
            SetRectAnchored(fxHeaderLbl.rectTransform, new Vector2(-102f, -50f), new Vector2(190f, 22f), new Vector2(0.5f, 0.5f));

            _effectToggles.Clear();
            CreateEffectToggleItem(leftPanel, new Vector2(-98f, -86f), "Floating Blocks",
                () => _visualizer == null || _visualizer.groundEqSettings.floatingBlocksEnabled,
                v =>
                {
                    if (_visualizer != null) _visualizer.groundEqSettings.floatingBlocksEnabled = v;
                    PlayerPrefs.SetInt("SonicTopography_FxFloatingBlocks", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            CreateEffectToggleItem(leftPanel, new Vector2(98f, -86f), "Kick Pulse",
                () => _audioEngine == null || _audioEngine.pulseTrigger.enabled,
                v =>
                {
                    if (_audioEngine != null) _audioEngine.pulseTrigger.enabled = v;
                    PlayerPrefs.SetInt("SonicTopography_FxKickPulse", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            CreateEffectToggleItem(leftPanel, new Vector2(-98f, -130f), "Snare Ripples",
                () => _audioEngine == null || _audioEngine.snareTrigger.enabled,
                v =>
                {
                    if (_audioEngine != null) _audioEngine.snareTrigger.enabled = v;
                    PlayerPrefs.SetInt("SonicTopography_FxSnareRipples", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            CreateEffectToggleItem(leftPanel, new Vector2(98f, -130f), "Meteors",
                () => _audioEngine == null || _audioEngine.meteorTrigger.enabled,
                v =>
                {
                    if (_audioEngine != null) _audioEngine.meteorTrigger.enabled = v;
                    PlayerPrefs.SetInt("SonicTopography_FxMeteors", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            CreateEffectToggleItem(leftPanel, new Vector2(-98f, -174f), "Ambient Dust",
                () => _visualizer == null || _visualizer.enableAmbientParticles,
                v =>
                {
                    if (_visualizer != null) _visualizer.enableAmbientParticles = v;
                    PlayerPrefs.SetInt("SonicTopography_FxAmbientDust", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            CreateEffectToggleItem(leftPanel, new Vector2(98f, -174f), "Ray Ripples",
                () => _visualizer == null || _visualizer.enableInteractiveRipples,
                v =>
                {
                    if (_visualizer != null) _visualizer.enableInteractiveRipples = v;
                    PlayerPrefs.SetInt("SonicTopography_FxRayRipples", v ? 1 : 0);
                    PlayerPrefs.Save();
                });

            // --- RIGHT COLUMN: APPEARANCE & COLORS CARD (426 x 442) ---
            RectTransform colorCard = CreateRect("ColorsAndThemesCard", viewRect, new Vector2(231f, -25f), new Vector2(426f, 442f));
            AddGlassSurface(colorCard.gameObject, ColSurfaceCard, 22f, isPill: false, border: 0.10f);

            // Top Segmented Control Bar: Auto | Themes | Custom
            RectTransform segBar = CreateRect("SegmentedColorBar", colorCard, new Vector2(0f, 184f), new Vector2(390f, 38f));
            AddGlassSurface(segBar.gameObject, ColRecessedDark, 19f, isPill: true, border: 0.10f);

            string[] colorModes = { "Auto", "Themes", "Custom" };
            for (int i = 0; i < 3; i++)
            {
                int modeIdx = i;
                RectTransform modeBtnRect = CreateRect($"ColorMode_{colorModes[i]}", segBar, new Vector2(-128f + i * 128f, 0f), new Vector2(124f, 30f));
                Image modeBg = AddGlassSurface(modeBtnRect.gameObject, Color.clear, 15f, isPill: true, border: 0.08f);
                _colorModeBtnBgs[i] = modeBg;

                Button modeBtn = modeBtnRect.gameObject.AddComponent<Button>();
                ConfigureVisionButtonColors(modeBtn, modeBg);
                AddHoverHighlight(modeBtnRect.gameObject, modeBg, null, Color.clear, 0.08f, 0.22f);
                modeBtn.onClick.AddListener(() =>
                {
                    if (_visualizer != null)
                    {
                        _visualizer.colorMode = (SonicColorMode)modeIdx;
                        PlayerPrefs.SetInt("SonicTopography_ColorMode", modeIdx);
                        PlayerPrefs.Save();
                        if (_visualizer.colorMode == SonicColorMode.ManualColors)
                        {
                            ApplyManualHuesToVisualizer(switchToManualMode: true);
                        }
                    }
                    RefreshSettingsUI();
                });

                TextMeshProUGUI lbl = CreateLabel("Txt", modeBtnRect, colorModes[i], 13f, FontStyles.Bold, TextAlignmentOptions.Center);
                lbl.color = ColTextSecondary;
                StretchFull(lbl.rectTransform);
                _colorModeBtnLabels[i] = lbl;
            }

            // Minimal Active Palette Status Row (with 4 live color dots)
            RectTransform statusRow = CreateRect("PaletteStatusRow", colorCard, new Vector2(0f, 140f), new Vector2(386f, 24f));
            _colorStatusLabel = CreateLabel("StatusLabel", statusRow, "Matching Album Artwork", 12.5f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            _colorStatusLabel.color = ColTextSecondary;
            _colorStatusLabel.overflowMode = TextOverflowModes.Truncate;
            _colorStatusLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_colorStatusLabel.rectTransform, new Vector2(-42f, 0f), new Vector2(296f, 22f), new Vector2(0.5f, 0.5f));

            for (int d = 0; d < 4; d++)
            {
                RectTransform dotRect = CreateRect($"Swatch_{d}", statusRow, new Vector2(124f + d * 18f, 0f), new Vector2(13f, 13f));
                Image dotImg = AddGlassSurface(dotRect.gameObject, Color.white, 6.5f, isPill: true, border: 0.20f);
                dotImg.raycastTarget = false;
                _activePaletteSwatches[d] = dotImg;
            }

            // Sub-container A: 12 Theme Cards in a clean 3x4 Grid
            RectTransform presetsRect = CreateStretchRect("PresetThemesGrid", colorCard, Vector4.zero);
            _presetThemesContainer = presetsRect.gameObject;

            SonicThemePreset[] presets = (SonicThemePreset[])Enum.GetValues(typeof(SonicThemePreset));
            _themePresetPillBgs.Clear();
            _themePresetValues.Clear();

            int maxPresetsToShow = Mathf.Min(presets.Length, 12);
            for (int i = 0; i < maxPresetsToShow; i++)
            {
                SonicThemePreset preset = presets[i];
                SonicThemeColors previewCols = SonicTopographyConfig.GetTheme(preset);

                int col = i % 3;
                int row = i / 3;
                Vector2 pos = new Vector2(-130f + col * 130f, 58f - row * 72f);

                RectTransform pBtnRect = CreateRect($"Theme_{preset}", presetsRect, pos, new Vector2(122f, 62f));
                Image pBg = AddGlassSurface(pBtnRect.gameObject, ColSurfaceElevated, 14f, isPill: false, border: 0.10f);
                _themePresetPillBgs.Add(pBg);
                _themePresetValues.Add(preset);

                Button pBtn = pBtnRect.gameObject.AddComponent<Button>();
                pBtn.transition = Selectable.Transition.None;
                AddHoverHighlight(pBtnRect.gameObject, pBg, null, ColSurfaceElevated, 0.08f, 0.22f);
                pBtn.onClick.AddListener(() =>
                {
                    if (_visualizer != null)
                    {
                        _visualizer.activeTheme = preset;
                        PlayerPrefs.SetInt("SonicTopography_ActiveTheme", (int)preset);
                        if (_visualizer.colorMode == SonicColorMode.AutoAlbumArt)
                        {
                            _visualizer.noCoverFallbackMode = SonicColorFallbackMode.DefaultPresetTheme;
                        }
                        else
                        {
                            _visualizer.colorMode = SonicColorMode.PresetThemes;
                            PlayerPrefs.SetInt("SonicTopography_ColorMode", (int)SonicColorMode.PresetThemes);
                        }
                        PlayerPrefs.Save();
                    }
                    RefreshSettingsUI();
                });

                // Clean color-only theme preset cards: 3 larger circles centered horizontally & vertically, no text name label
                CreateColorDot(pBtnRect, new Vector2(-28f, 0f), previewCols.uCoolEdge, 24f);
                CreateColorDot(pBtnRect, new Vector2(0f, 0f), previewCols.uWarmEdge, 24f);
                CreateColorDot(pBtnRect, new Vector2(28f, 0f), previewCols.uRippleColor, 24f);
            }

            // Sub-container B: Custom Color Hue Sliders
            RectTransform manualRect = CreateStretchRect("ManualColorsContainer", colorCard, Vector4.zero);
            _manualColorsContainer = manualRect.gameObject;

            CreateManualColorSliderRow(manualRect, new Vector2(0f, 68f), "Atmosphere", _manualBaseHue, out _manualBasePreview, h =>
            {
                _manualBaseHue = h;
                PlayerPrefs.SetFloat("SonicTopography_HueBase", h);
                PlayerPrefs.Save();
                ApplyManualHuesToVisualizer(switchToManualMode: _visualizer == null || _visualizer.colorMode != SonicColorMode.AutoAlbumArt);
                RefreshSettingsUI();
            });

            CreateManualColorSliderRow(manualRect, new Vector2(0f, 0f), "Low & Mid", _manualCoolHue, out _manualCoolPreview, h =>
            {
                _manualCoolHue = h;
                PlayerPrefs.SetFloat("SonicTopography_HueCool", h);
                PlayerPrefs.Save();
                ApplyManualHuesToVisualizer(switchToManualMode: _visualizer == null || _visualizer.colorMode != SonicColorMode.AutoAlbumArt);
                RefreshSettingsUI();
            });

            CreateManualColorSliderRow(manualRect, new Vector2(0f, -68f), "High Peaks", _manualWarmHue, out _manualWarmPreview, h =>
            {
                _manualWarmHue = h;
                PlayerPrefs.SetFloat("SonicTopography_HueWarm", h);
                PlayerPrefs.Save();
                ApplyManualHuesToVisualizer(switchToManualMode: _visualizer == null || _visualizer.colorMode != SonicColorMode.AutoAlbumArt);
                RefreshSettingsUI();
            });

            CreateManualColorSliderRow(manualRect, new Vector2(0f, -136f), "Ripples", _manualRippleHue, out _manualRipplePreview, h =>
            {
                _manualRippleHue = h;
                PlayerPrefs.SetFloat("SonicTopography_HueRipple", h);
                PlayerPrefs.Save();
                ApplyManualHuesToVisualizer(switchToManualMode: _visualizer == null || _visualizer.colorMode != SonicColorMode.AutoAlbumArt);
                RefreshSettingsUI();
            });

            return viewRect.gameObject;
        }

        private static string GetThemeDisplayName(SonicThemePreset preset)
        {
            switch (preset)
            {
                case SonicThemePreset.Nocturnal: return "Cosmos";
                case SonicThemePreset.BlueHour: return "Solaris";
                case SonicThemePreset.NeonTokyo: return "Cyberpunk";
                case SonicThemePreset.CyberForest: return "Emerald Abyss";
                case SonicThemePreset.MinimalMonochrome: return "Platinum";
                case SonicThemePreset.KoiPond: return "Sakura Dusk";
                case SonicThemePreset.CoralReef: return "Aurora";
                case SonicThemePreset.MossGlass: return "Verdant Glow";
                case SonicThemePreset.WineSignal: return "Velvet Twilight";
                case SonicThemePreset.GlacierDay: return "Glacial Ice";
                case SonicThemePreset.PorcelainTeal: return "Ocean Trench";
                case SonicThemePreset.DaybreakLime: return "Volcanic Magma";
                case SonicThemePreset.InkWash: return "Obsidian Ink";
                default: return preset.ToString();
            }
        }

        private static RectTransform CreateDividerLine(RectTransform parent, Vector2 pos, float width)
        {
            RectTransform divRect = CreateRect("Divider", parent, pos, new Vector2(width, 1f));
            Image img = divRect.gameObject.AddComponent<Image>();
            img.sprite = SonicUIIconGenerator.GetWhiteSprite();
            img.color = new Color(1f, 1f, 1f, 0.06f);
            img.raycastTarget = false;
            return divRect;
        }

        private void ApplyManualHuesToVisualizer(bool switchToManualMode)
        {
            Color baseCol = Color.HSVToRGB(_manualBaseHue, 0.65f, 0.22f);
            Color coolCol = Color.HSVToRGB(_manualCoolHue, 0.82f, 0.88f);
            Color warmCol = Color.HSVToRGB(_manualWarmHue, 0.85f, 0.96f);
            Color rippleCol = Color.HSVToRGB(_manualRippleHue, 0.75f, 1.0f);

            if (_manualBasePreview != null) _manualBasePreview.color = Color.HSVToRGB(_manualBaseHue, 0.65f, 0.65f);
            if (_manualCoolPreview != null) _manualCoolPreview.color = coolCol;
            if (_manualWarmPreview != null) _manualWarmPreview.color = warmCol;
            if (_manualRipplePreview != null) _manualRipplePreview.color = rippleCol;

            if (_visualizer != null)
            {
                if (switchToManualMode)
                {
                    _visualizer.colorMode = SonicColorMode.ManualColors;
                }
                _visualizer.noCoverFallbackMode = SonicColorFallbackMode.UserManualColors;
                _visualizer.SetManualPalette(baseCol, coolCol, warmCol, rippleCol, 1.25f);
            }
        }

        public void RefreshSettingsUI()
        {
            if (_library != null)
            {
                string folderPath = _library.ActiveScanFolder ?? string.Empty;
                string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;

                if (_settingsFolderTitleLabel != null)
                {
                    _settingsFolderTitleLabel.text = folderName;
                }
                if (_settingsFolderSubLabel != null)
                {
                    int count = _library.Tracks.Count;
                    _settingsFolderSubLabel.text = count == 1 ? $"1 song  •  {folderPath}" : $"{count} songs  •  {folderPath}";
                }
            }

            if (_visualizer != null)
            {
                _suppressSliderCallbacks = true;
                if (_rotationSlider != null) _rotationSlider.value = _visualizer.rotationSpeed;
                if (_amplitudeSlider != null) _amplitudeSlider.value = _visualizer.groundEqSettings.amplitude;
                if (_motionSpeedSlider != null) _motionSpeedSlider.value = _visualizer.groundEqSettings.motionSpeed;
                _suppressSliderCallbacks = false;

                int activeMode = (int)_visualizer.colorMode;
                for (int i = 0; i < 3; i++)
                {
                    bool active = (i == activeMode);
                    if (_colorModeBtnBgs[i] != null)
                    {
                        Color modeCol = active ? ColPillActive : Color.clear;
                        _colorModeBtnBgs[i].color = modeCol;
                        var hover = _colorModeBtnBgs[i].GetComponent<SonicVisionHoverEffect>();
                        if (hover != null)
                        {
                            hover.normalLabelColor = active ? Color.white : ColTextSecondary;
                            hover.hoverLabelColor = Color.white;
                            hover.SetNormalState(modeCol, active ? 0.38f : 0.08f);
                        }
                        else
                        {
                            var surf = _colorModeBtnBgs[i].GetComponent<SonicGlassSurface>();
                            if (surf != null) surf.SetBorder(active ? 0.38f : 0.08f);
                        }
                    }
                    if (_colorModeBtnLabels[i] != null)
                    {
                        _colorModeBtnLabels[i].color = active ? Color.white : ColTextSecondary;
                    }
                }

                SonicThemeColors activePal = _visualizer.CurrentColors;
                if (_activePaletteSwatches[0] != null) _activePaletteSwatches[0].color = activePal.uBaseColor2;
                if (_activePaletteSwatches[1] != null) _activePaletteSwatches[1].color = activePal.uCoolEdge;
                if (_activePaletteSwatches[2] != null) _activePaletteSwatches[2].color = activePal.uWarmEdge;
                if (_activePaletteSwatches[3] != null) _activePaletteSwatches[3].color = activePal.uRippleColor;

                if (_colorStatusLabel != null)
                {
                    if (_visualizer.colorMode == SonicColorMode.AutoAlbumArt)
                    {
                        string syncedTitle = null;
                        if (UseBrowserPlayerBar)
                        {
                            if (_visualizer.HasActiveAlbumPalette && _browserManager.HasArtworkPalette)
                            {
                                syncedTitle = !string.IsNullOrEmpty(_browserManager.CurrentTitle) ? _browserManager.CurrentTitle : "Browser";
                            }
                        }
                        else
                        {
                            SonicMusicTrack curTrack = _player != null ? _player.CurrentTrack : null;
                            if (_visualizer.HasActiveAlbumPalette && curTrack != null)
                            {
                                syncedTitle = curTrack.title;
                            }
                        }

                        if (!string.IsNullOrEmpty(syncedTitle))
                        {
                            _colorStatusLabel.text = $"Synced to {syncedTitle}";
                        }
                        else
                        {
                            string fb = _visualizer.noCoverFallbackMode == SonicColorFallbackMode.UserManualColors
                                ? "Custom"
                                : GetThemeDisplayName(_visualizer.activeTheme);
                            _colorStatusLabel.text = $"Auto  •  Fallback: {fb}";
                        }
                    }
                    else if (_visualizer.colorMode == SonicColorMode.PresetThemes)
                    {
                        _colorStatusLabel.text = $"Theme: {GetThemeDisplayName(_visualizer.activeTheme)}";
                    }
                    else
                    {
                        _colorStatusLabel.text = "Custom Palette";
                    }
                }

                bool showManual = (_visualizer.colorMode == SonicColorMode.ManualColors);
                if (_presetThemesContainer != null) _presetThemesContainer.SetActive(!showManual);
                if (_manualColorsContainer != null) _manualColorsContainer.SetActive(showManual);

                for (int i = 0; i < _themePresetPillBgs.Count; i++)
                {
                    bool active = _themePresetValues[i] == _visualizer.activeTheme &&
                                  (_visualizer.colorMode == SonicColorMode.PresetThemes ||
                                   _visualizer.noCoverFallbackMode == SonicColorFallbackMode.DefaultPresetTheme);
                    var hover = _themePresetPillBgs[i].GetComponent<SonicVisionHoverEffect>();
                    if (hover != null)
                    {
                        hover.SetThemeCardState(active, ColSurfaceElevated);
                    }
                    else
                    {
                        _themePresetPillBgs[i].color = ColSurfaceElevated;
                        var surf = _themePresetPillBgs[i].GetComponent<SonicGlassSurface>();
                        if (surf != null)
                        {
                            surf.borderIntensity = active ? 0.40f : 0.08f;
                            surf.SetVerticesDirty();
                        }
                    }
                }
            }

            for (int i = 0; i < _effectToggles.Count; i++)
            {
                UpdateEffectToggleVisual(_effectToggles[i]);
            }
        }

        // =====================================================================
        // BOTTOM CENTER PERSISTENT PLAYER BAR (VISIONOS FLOATING ORNAMENT)
        // =====================================================================

        private void BuildBottomPlayerBar(RectTransform parent)
        {
            RectTransform barRect = CreateRect("BottomPlayerBar", parent, new Vector2(28f, -298f), new Vector2(880f, 96f));
            AddGlassSurface(barRect.gameObject, Color.white, 48f, isPill: true, border: 0.18f, SonicSurfaceMode.FrostedBlurWindow);

            RectTransform musicControlsRect = CreateStretchRect("BottomMusicControls", barRect, Vector4.zero);
            _bottomMusicControlsRoot = musicControlsRect.gameObject;

            // 1. Left Cluster: Larger VR-friendly Icon-only Previous | Play/Pause | Next
            RectTransform prevRect = CreateIconButton(musicControlsRect, "PrevBtn", new Vector2(-386f, 0f), new Vector2(48f, 48f),
                SonicIconType.Previous, new Color(1f, 1f, 1f, 0.05f), ColTextSecondary, out Image prevBg, out Image prevIcon, () =>
                {
                    if (UseBrowserPlayerBar) _browserManager.PrevTrack();
                    else if (_player != null) _player.PreviousTrack();
                }, border: 0.08f);
            AddHoverHighlight(prevRect.gameObject, prevBg, prevIcon, new Color(1f, 1f, 1f, 0.05f), 0.08f, 0.22f);

            RectTransform playPauseRect = CreateIconButton(musicControlsRect, "PlayPauseBtn", new Vector2(-324f, 0f), new Vector2(58f, 58f),
                SonicIconType.Pause, new Color(1f, 1f, 1f, 0.07f), Color.white, out Image playPauseBg, out _playPauseIconImage, () =>
                {
                    if (UseBrowserPlayerBar) _browserManager.TogglePlayPause();
                    else if (_player != null) _player.TogglePlayPause();
                }, border: 0.38f);
            AddHoverHighlight(playPauseRect.gameObject, playPauseBg, _playPauseIconImage, new Color(1f, 1f, 1f, 0.07f), 0.38f, 0.48f);

            RectTransform nextRect = CreateIconButton(musicControlsRect, "NextBtn", new Vector2(-262f, 0f), new Vector2(48f, 48f),
                SonicIconType.Next, new Color(1f, 1f, 1f, 0.05f), ColTextSecondary, out Image nextBg, out Image nextIcon, () =>
                {
                    if (UseBrowserPlayerBar) _browserManager.NextTrack();
                    else if (_player != null) _player.NextTrack();
                }, border: 0.08f);
            AddHoverHighlight(nextRect.gameObject, nextBg, nextIcon, new Color(1f, 1f, 1f, 0.05f), 0.08f, 0.22f);

            // 2. Center Now Playing Area
            RectTransform artFrame = CreateRect("PlayerArtwork", musicControlsRect, new Vector2(-190f, 0f), new Vector2(62f, 62f));
            _playerArtworkImage = AddRoundedArtworkImage(artFrame.gameObject, null, 14f, border: 0.10f);
            _playerArtworkSurface = artFrame.GetComponent<SonicGlassSurface>();

            _playerTitleLabel = CreateLabel("PlayerTitle", musicControlsRect, "Not Playing", 14.5f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            _playerTitleLabel.overflowMode = TextOverflowModes.Truncate;
            _playerTitleLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_playerTitleLabel.rectTransform, new Vector2(-38f, 18f), new Vector2(204f, 26f), new Vector2(0.5f, 0.5f));

            _playerArtistLabel = CreateLabel("PlayerArtist", musicControlsRect, "", 12.5f, FontStyles.Normal, TextAlignmentOptions.MidlineLeft);
            _playerArtistLabel.color = ColTextSecondary;
            _playerArtistLabel.overflowMode = TextOverflowModes.Truncate;
            _playerArtistLabel.textWrappingMode = TextWrappingModes.NoWrap;
            SetRectAnchored(_playerArtistLabel.rectTransform, new Vector2(-38f, -3f), new Vector2(204f, 22f), new Vector2(0.5f, 0.5f));

            _playerTimeLabel = CreateLabel("PlayerTime", musicControlsRect, "0:00 / 0:00", 12.5f, FontStyles.Bold, TextAlignmentOptions.MidlineRight);
            _playerTimeLabel.color = ColTextSecondary;
            SetRectAnchored(_playerTimeLabel.rectTransform, new Vector2(106f, 7f), new Vector2(88f, 22f), new Vector2(0.5f, 0.5f));

            _progressSlider = CreateInteractiveSlider(
                musicControlsRect,
                "ProgressSeekSlider",
                new Vector2(5f, -22f),
                new Vector2(290f, 7f),
                knobSize: 14f,
                min: 0f,
                max: 1f,
                initial: 0f,
                onValueChanged: val =>
                {
                    if (_suppressSliderCallbacks || _player == null) return;
                    _lastProgressInteractionTime = Time.unscaledTime;
                    _player.SeekNormalized(val);
                });

            // 3. Right Cluster: Plain Icon Shuffle | Repeat | Mute | Volume Bar
            RectTransform shufRect = CreateIconButton(musicControlsRect, "ShuffleBtn", new Vector2(188f, 0f), new Vector2(44f, 44f),
                SonicIconType.Shuffle, Color.clear, ColTextMuted, out _shuffleBgImage, out _shuffleIconImage, () =>
                {
                    if (_player != null) _player.ToggleShuffle();
                });
            AddHoverHighlight(shufRect.gameObject, _shuffleBgImage, _shuffleIconImage, Color.clear, 0.0f, 0.16f);

            RectTransform repRect = CreateIconButton(musicControlsRect, "RepeatBtn", new Vector2(238f, 0f), new Vector2(44f, 44f),
                SonicIconType.Repeat, Color.clear, ColTextMuted, out _repeatBgImage, out _repeatIconImage, () =>
                {
                    if (_player != null) _player.CycleRepeatMode();
                });
            AddHoverHighlight(repRect.gameObject, _repeatBgImage, _repeatIconImage, Color.clear, 0.0f, 0.16f);

            RectTransform muteRect = CreateIconButton(musicControlsRect, "MuteBtn", new Vector2(288f, 0f), new Vector2(44f, 44f),
                SonicIconType.VolumeHigh, Color.clear, ColTextPrimary, out Image muteBg, out _volumeIconImage, () =>
                {
                    if (UseBrowserPlayerBar)
                    {
                        float curVol = _browserManager.Volume;
                        if (curVol > 0.001f)
                        {
                            _browserPreMuteVolume = curVol;
                            _browserManager.SetVolume(0f);
                        }
                        else
                        {
                            _browserManager.SetVolume(Mathf.Max(0.5f, _browserPreMuteVolume));
                        }
                    }
                    else if (_player != null)
                    {
                        _player.ToggleMute();
                    }
                });
            AddHoverHighlight(muteRect.gameObject, muteBg, _volumeIconImage, Color.clear, 0.0f, 0.16f);

            _volumeSlider = CreateInteractiveSlider(
                musicControlsRect,
                "VolumeSlider",
                new Vector2(354f, 0f),
                new Vector2(76f, 7f),
                knobSize: 15f,
                min: 0f,
                max: 1f,
                initial: 0.9f,
                onValueChanged: val =>
                {
                    if (_suppressSliderCallbacks) return;
                    if (UseBrowserPlayerBar)
                    {
                        if (val > 0.001f) _browserPreMuteVolume = val;
                        _browserManager.SetVolume(val);
                    }
                    else if (_player != null)
                    {
                        _player.SetVolume(val);
                    }
                });

            RectTransform browserNavRect = CreateStretchRect("BottomBrowserNavBar", barRect, Vector4.zero);
            _bottomBrowserNavRoot = browserNavRect.gameObject;
            BuildBottomBrowserNavControls(browserNavRect);
            _bottomBrowserNavRoot.SetActive(false);
        }

        private void BuildBottomBrowserNavControls(RectTransform parent)
        {
            RectTransform backRect = CreateIconButton(parent, "BrowserBackBtn", new Vector2(-394f, 0f), new Vector2(44f, 44f),
                SonicIconType.ChevronLeft, new Color(1f, 1f, 1f, 0.06f), ColTextSecondary, out _browserBackBg, out _browserBackIcon,
                () => _browserManager?.GoBack(), border: 0.08f);
            AddHoverHighlight(backRect.gameObject, _browserBackBg, _browserBackIcon, new Color(1f, 1f, 1f, 0.06f), 0.08f, 0.22f);

            RectTransform fwdRect = CreateIconButton(parent, "BrowserForwardBtn", new Vector2(-342f, 0f), new Vector2(44f, 44f),
                SonicIconType.ChevronRight, new Color(1f, 1f, 1f, 0.06f), ColTextSecondary, out _browserFwdBg, out _browserFwdIcon,
                () => _browserManager?.GoForward(), border: 0.08f);
            AddHoverHighlight(fwdRect.gameObject, _browserFwdBg, _browserFwdIcon, new Color(1f, 1f, 1f, 0.06f), 0.08f, 0.22f);

            RectTransform reloadRect = CreateIconButton(parent, "BrowserReloadBtn", new Vector2(-290f, 0f), new Vector2(44f, 44f),
                SonicIconType.Refresh, new Color(1f, 1f, 1f, 0.06f), ColTextPrimary, out Image relBg, out Image relIcon,
                () => _browserManager?.Reload(), border: 0.08f);
            AddHoverHighlight(reloadRect.gameObject, relBg, relIcon, new Color(1f, 1f, 1f, 0.06f), 0.08f, 0.22f);

            string initialUrl = SonicWebBrowserManager.SavedDefaultHomeUrl;

            // URL Input Capsule with inline Return / Enter submit button
            RectTransform urlBoxRect = CreateRect("BrowserUrlBar", parent, new Vector2(4f, 0f), new Vector2(520f, 48f));
            Image urlBoxBg = AddGlassSurface(urlBoxRect.gameObject, ColRecessedDark, 24f, isPill: true, border: 0.10f);
            urlBoxBg.raycastTarget = true;

            RectTransform urlTextArea = CreateRect("TextArea", urlBoxRect, new Vector2(-18f, 0f), new Vector2(446f, 34f));
            urlTextArea.gameObject.AddComponent<RectMask2D>();

            Font urlFont = CreateLegacyInputFont(13);

            RectTransform urlPhRect = CreateRect("Placeholder", urlTextArea, Vector2.zero, Vector2.zero);
            StretchFull(urlPhRect);
            Text urlPh = urlPhRect.gameObject.AddComponent<Text>();
            urlPh.font = urlFont;
            urlPh.text = "Enter URL or search...";
            urlPh.fontSize = 13;
            urlPh.alignment = TextAnchor.MiddleLeft;
            urlPh.color = ColTextMuted;
            urlPh.raycastTarget = false;

            RectTransform urlTxtRect = CreateRect("Text", urlTextArea, Vector2.zero, Vector2.zero);
            StretchFull(urlTxtRect);
            Text urlTxt = urlTxtRect.gameObject.AddComponent<Text>();
            urlTxt.font = urlFont;
            urlTxt.text = initialUrl;
            urlTxt.fontSize = 13;
            urlTxt.alignment = TextAnchor.MiddleLeft;
            urlTxt.color = ColTextPrimary;
            urlTxt.supportRichText = false;
            urlTxt.raycastTarget = false;

            _browserUrlInput = urlBoxRect.gameObject.AddComponent<InputField>();
            _browserUrlInput.textComponent = urlTxt;
            _browserUrlInput.placeholder = urlPh;
            _browserUrlInput.targetGraphic = urlBoxBg;
            _browserUrlInput.lineType = InputField.LineType.SingleLine;
            _browserUrlInput.keyboardType = TouchScreenKeyboardType.Default;
            _browserUrlInput.shouldHideMobileInput = false;
            _browserUrlInput.text = initialUrl;

            _browserUrlInput.onEndEdit.AddListener(val =>
            {
                if (Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
                {
                    NavigateBrowserUrl(val);
                }
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == urlBoxRect.gameObject)
                {
                    EventSystem.current.SetSelectedGameObject(null);
                }
            });

            _browserUrlOpener = urlBoxRect.gameObject.AddComponent<SonicQuestVirtualKeyboardOpener>();
            _browserUrlOpener.Initialize(_browserUrlInput, "Enter URL or search", TouchScreenKeyboardType.Default, onDone: null, isUrl: true);
            AddHoverHighlight(urlBoxRect.gameObject, urlBoxBg, null, ColRecessedDark, 0.10f, 0.28f);

            RectTransform goBtnRect = CreateIconButton(urlBoxRect, "BrowserUrlGoBtn", new Vector2(234f, 0f), new Vector2(36f, 36f),
                SonicIconType.ReturnArrow, new Color(1f, 1f, 1f, 0.10f), ColTextPrimary, out Image goBg, out Image goIcon,
                () =>
                {
                    _browserUrlOpener?.CloseVirtualKeyboard();
                    NavigateBrowserUrl(_browserUrlInput != null ? _browserUrlInput.text : string.Empty);
                }, border: 0.24f);
            AddHoverHighlight(goBtnRect.gameObject, goBg, goIcon, new Color(1f, 1f, 1f, 0.10f), 0.24f, 0.42f);

            RectTransform defBtnRect = CreateRect("BrowserDefaultBtn", parent, new Vector2(346f, 0f), new Vector2(140f, 44f));
            _browserDefaultBg = AddGlassSurface(defBtnRect.gameObject, new Color(1f, 1f, 1f, 0.06f), 22f, isPill: true, border: 0.10f);
            Button defBtn = defBtnRect.gameObject.AddComponent<Button>();
            ConfigureVisionButtonColors(defBtn, _browserDefaultBg);
            defBtn.onClick.AddListener(OnClickSetDefaultBrowserUrl);

            _browserDefaultLabel = CreateLabel("Label", defBtnRect, "Set Default", 13f, FontStyles.Bold, TextAlignmentOptions.Center);
            _browserDefaultLabel.color = ColTextPrimary;
            StretchFull(_browserDefaultLabel.rectTransform);
            AddHoverHighlight(defBtnRect.gameObject, _browserDefaultBg, null, new Color(1f, 1f, 1f, 0.06f), 0.10f, 0.28f);
        }

        private void OnClickSetDefaultBrowserUrl()
        {
            if (_browserManager == null) return;
            string currentPageUrl = _browserManager.CurrentUrl;
            string typedUrl = _browserUrlInput != null ? _browserUrlInput.text : string.Empty;
            bool hasDifferentTypedUrl = !string.IsNullOrWhiteSpace(typedUrl) &&
                !string.Equals(
                    SonicWebBrowserManager.NormalizeUrl(typedUrl).TrimEnd('/'),
                    SonicWebBrowserManager.NormalizeUrl(currentPageUrl).TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase);

            string targetUrl = hasDifferentTypedUrl ? typedUrl.Trim() : currentPageUrl;
            _browserManager.SetSavedDefaultHomeUrl(targetUrl);
            if (hasDifferentTypedUrl)
            {
                NavigateBrowserUrl(targetUrl);
            }
            _browserDefaultFeedbackUntil = Time.unscaledTime + 1.8f;
            RefreshBrowserUI();
        }

        private float _browserPreMuteVolume = 1f;

        public void RefreshPlayerBarUI()
        {
            if (UseBrowserPlayerBar)
            {
                string title = !string.IsNullOrEmpty(_browserManager.CurrentTitle) ? _browserManager.CurrentTitle : "Web Browser";
                string artist = !string.IsNullOrEmpty(_browserManager.CurrentArtist) ? _browserManager.CurrentArtist : "Browser Audio";

                if (_playerTitleLabel != null)
                {
                    EnsureLabelCJKFont(_playerTitleLabel);
                    _playerTitleLabel.text = title;
                    _playerTitleLabel.ForceMeshUpdate();
                }
                if (_playerArtistLabel != null)
                {
                    EnsureLabelCJKFont(_playerArtistLabel);
                    _playerArtistLabel.text = artist;
                    _playerArtistLabel.ForceMeshUpdate();
                }
                if (_playerTimeLabel != null)
                {
                    _lastDisplayedTimeSec = -1;
                    _lastDisplayedDurSec = -1;
                    _wasDisplayingLoading = false;
                    float dur = _browserManager.CurrentDuration;
                    _playerTimeLabel.text = dur > 0.01f
                        ? $"{FormatTimeSeconds(_browserManager.CurrentTime)} / {FormatTimeSeconds(dur)}"
                        : "LIVE";
                }
                if (_playerArtworkImage != null)
                {
                    Sprite artSprite = _browserManager.CurrentArtworkSprite;
                    if (artSprite == null && _library != null)
                    {
                        artSprite = _library.GetOrCreateProceduralCover(title, artist);
                    }
                    _playerArtworkImage.sprite = artSprite;
                    if (_playerArtworkSurface != null) _playerArtworkSurface.SetVerticesDirty();
                }
                if (_playPauseIconImage != null)
                {
                    _playPauseIconImage.sprite = SonicUIIconGenerator.GetIcon(_browserManager.IsBrowserAudioPlaying ? SonicIconType.Pause : SonicIconType.Play);
                }
                if (_volumeIconImage != null)
                {
                    bool bMuted = _browserManager.Volume <= 0.001f;
                    _volumeIconImage.sprite = SonicUIIconGenerator.GetIcon(bMuted ? SonicIconType.VolumeMuted : SonicIconType.VolumeHigh);
                }
                if (_volumeSlider != null)
                {
                    _suppressSliderCallbacks = true;
                    _volumeSlider.value = _browserManager.Volume;
                    _suppressSliderCallbacks = false;
                }
                return;
            }

            if (_player == null) return;

            SonicMusicTrack track = _player.CurrentTrack;
            if (track != null)
            {
                if (_playerTitleLabel != null)
                {
                    EnsureLabelCJKFont(_playerTitleLabel);
                    _playerTitleLabel.text = track.title != null ? track.title.Trim('\uFEFF') : string.Empty;
                    _playerTitleLabel.ForceMeshUpdate();
                }
                if (_playerArtistLabel != null)
                {
                    EnsureLabelCJKFont(_playerArtistLabel);
                    _playerArtistLabel.text = track.artist != null ? track.artist.Trim('\uFEFF') : string.Empty;
                    _playerArtistLabel.ForceMeshUpdate();
                }
                if (_playerTimeLabel != null)
                {
                    _lastDisplayedTimeSec = -1;
                    _lastDisplayedDurSec = -1;
                    if (_player.IsLoadingTrack)
                    {
                        _wasDisplayingLoading = true;
                        _playerTimeLabel.text = "Loading...";
                    }
                    else
                    {
                        _wasDisplayingLoading = false;
                        _playerTimeLabel.text = $"{FormatTimeSeconds(_player.CurrentTime)} / {FormatTimeSeconds(_player.CurrentDuration)}";
                    }
                }
                if (_playerArtworkImage != null)
                {
                    _playerArtworkImage.sprite = track.coverSprite != null
                        ? track.coverSprite
                        : (_library != null ? _library.GetOrCreateProceduralCover(track.title, track.artist) : null);
                    if (_playerArtworkSurface != null) _playerArtworkSurface.SetVerticesDirty();
                }
            }
            else
            {
                if (_playerTitleLabel != null)
                {
                    EnsureLabelCJKFont(_playerTitleLabel);
                    _playerTitleLabel.text = "Not Playing";
                }
                if (_playerArtistLabel != null)
                {
                    EnsureLabelCJKFont(_playerArtistLabel);
                    _playerArtistLabel.text = "";
                }
                if (_playerTimeLabel != null) _playerTimeLabel.text = "0:00 / 0:00";
                if (_playerArtworkImage != null && _library != null)
                {
                    _playerArtworkImage.sprite = _library.GetOrCreateProceduralCover("Audio", "Floor");
                    if (_playerArtworkSurface != null) _playerArtworkSurface.SetVerticesDirty();
                }
            }

            if (_progressSlider != null && _player.IsLoadingTrack)
            {
                _suppressSliderCallbacks = true;
                _progressSlider.value = 0f;
                _suppressSliderCallbacks = false;
            }

            if (_playPauseIconImage != null)
            {
                _playPauseIconImage.sprite = SonicUIIconGenerator.GetIcon(_player.IsPlaying ? SonicIconType.Pause : SonicIconType.Play);
            }

            if (_shuffleBgImage != null && _shuffleIconImage != null)
            {
                bool shuf = _player.ShuffleEnabled;
                _shuffleBgImage.color = Color.clear;
                _shuffleIconImage.color = shuf ? Color.white : ColTextMuted;
                var hover = _shuffleBgImage.GetComponent<SonicVisionHoverEffect>();
                if (hover != null)
                {
                    hover.SetNormalState(Color.clear, 0f);
                    hover.normalIconColor = shuf ? Color.white : ColTextMuted;
                    hover.hoverIconColor = Color.white;
                }
            }

            if (_repeatBgImage != null && _repeatIconImage != null)
            {
                bool isRepeatActive = _player.RepeatMode != SonicRepeatMode.NormalSequential;
                _repeatBgImage.color = Color.clear;
                _repeatIconImage.color = isRepeatActive ? Color.white : ColTextMuted;
                _repeatIconImage.sprite = SonicUIIconGenerator.GetIcon(_player.RepeatMode == SonicRepeatMode.RepeatOne ? SonicIconType.RepeatOne : SonicIconType.Repeat);
                var hover = _repeatBgImage.GetComponent<SonicVisionHoverEffect>();
                if (hover != null)
                {
                    hover.SetNormalState(Color.clear, 0f);
                    hover.normalIconColor = isRepeatActive ? Color.white : ColTextMuted;
                    hover.hoverIconColor = Color.white;
                }
            }

            if (_volumeIconImage != null)
            {
                bool muted = _player.IsMuted || _player.Volume <= 0.001f;
                _volumeIconImage.sprite = SonicUIIconGenerator.GetIcon(muted ? SonicIconType.VolumeMuted : SonicIconType.VolumeHigh);
            }

            if (_volumeSlider != null)
            {
                _suppressSliderCallbacks = true;
                _volumeSlider.value = _player.Volume;
                _suppressSliderCallbacks = false;
            }
        }

        public void SwitchSection(SonicUISection section)
        {
            if (_activeSection != section)
            {
                CloseAllVirtualKeyboards();
            }
            _activeSection = section;

            if (_musicViewRoot != null) _musicViewRoot.SetActive(section == SonicUISection.Music);
            if (_browseViewRoot != null) _browseViewRoot.SetActive(section == SonicUISection.Browse);
            if (_browserViewRoot != null) _browserViewRoot.SetActive(section == SonicUISection.Browser);
            if (_settingsViewRoot != null) _settingsViewRoot.SetActive(section == SonicUISection.Settings);
            if (_bottomMusicControlsRoot != null) _bottomMusicControlsRoot.SetActive(section != SonicUISection.Browser);
            if (_bottomBrowserNavRoot != null) _bottomBrowserNavRoot.SetActive(section == SonicUISection.Browser);

            for (int i = 0; i < 4; i++)
            {
                bool active = (i == (int)section);
                if (_navButtonBgs[i] != null)
                {
                    Color bgCol = active ? ColPillActive : Color.clear;
                    _navButtonBgs[i].color = bgCol;
                    var hover = _navButtonBgs[i].GetComponent<SonicVisionHoverEffect>();
                    if (hover != null)
                    {
                        hover.normalIconColor = active ? Color.white : ColTextSecondary;
                        hover.hoverIconColor = Color.white;
                        hover.SetNormalState(bgCol, active ? 0.38f : 0.08f);
                    }
                    else
                    {
                        var surf = _navButtonBgs[i].GetComponent<SonicGlassSurface>();
                        if (surf != null) surf.SetBorder(active ? 0.38f : 0.08f);
                    }
                }
                if (_navButtonIcons[i] != null)
                {
                    _navButtonIcons[i].color = active ? Color.white : ColTextSecondary;
                }
            }

            if (section == SonicUISection.Music) RefreshMusicGrid();
            else if (section == SonicUISection.Browse) RefreshBrowseList();
            else if (section == SonicUISection.Browser)
            {
                if (_player == null || !_player.IsPlaying)
                {
                    _browserManager?.ApplyBrowserThemeToVisualizer(forceClearIfNoArt: true);
                }
                RefreshBrowserUI();
            }
            else if (section == SonicUISection.Settings) RefreshSettingsUI();

            SyncBrowserVisibilityState();
            UpdateDynamicScrollTopFade();
        }

        private void HandleLibraryUpdated()
        {
            if (_library != null)
            {
                _currentBrowsePath = _library.ActiveScanFolder;
            }
            RefreshActiveSection();
        }

        private void HandleTrackChanged(SonicMusicTrack track)
        {
            RefreshPlayerBarUI();
            RefreshActiveSection();
        }

        private void HandleLocalPlaybackStarted()
        {
            _browserManager?.OnLocalPlaybackStarted();
            RefreshPlayerBarUI();
        }

        private void HandleBrowserStateChanged()
        {
            RefreshPlayerBarUI();
            if (_activeSection == SonicUISection.Browser)
            {
                RefreshBrowserUI();
            }
        }

        private void RefreshBrowserUI()
        {
            if (_browserManager == null) return;

            if (_browserUrlInput != null && !_browserUrlInput.isFocused && !SonicQuestVirtualKeyboardOpener.IsKeyboardActiveOrRecentlyUsed(1.0f))
            {
                string curUrl = _browserManager.CurrentUrl;
                if (!string.IsNullOrEmpty(curUrl) && _browserUrlInput.text != curUrl)
                {
                    _browserUrlInput.text = curUrl;
                }
            }

            if (_browserBackIcon != null)
            {
                _browserBackIcon.color = _browserManager.CanGoBack ? ColTextPrimary : ColTextMuted;
            }
            if (_browserFwdIcon != null)
            {
                _browserFwdIcon.color = _browserManager.CanGoForward ? ColTextPrimary : ColTextMuted;
            }

            if (_browserDefaultLabel != null)
            {
                bool showSaved = Time.unscaledTime < _browserDefaultFeedbackUntil;
                bool isDefault = _browserManager.IsCurrentUrlSavedDefault();
                _browserDefaultLabel.text = showSaved ? "Default Saved" : (isDefault ? "Default" : "Set Default");

                float targetBorder = (showSaved || isDefault) ? 0.36f : 0.10f;
                if (_browserDefaultBg != null)
                {
                    var hover = _browserDefaultBg.GetComponent<SonicVisionHoverEffect>();
                    if (hover != null)
                    {
                        hover.SetNormalState(new Color(1f, 1f, 1f, 0.06f), targetBorder);
                    }
                    else
                    {
                        var surf = _browserDefaultBg.GetComponent<SonicGlassSurface>();
                        if (surf != null) surf.SetBorder(targetBorder);
                    }
                }
            }

            if (_browserStatusLabel != null && _browserPlaceholderRoot != null && _browserPlaceholderRoot.activeSelf)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (_browserManager.IsDrmSilent)
                {
                    _browserStatusLabel.text = "Protected DRM stream detected; Web Audio FFT unavailable.";
                }
                else if (_browserManager.IsLoading)
                {
                    _browserStatusLabel.text = "Loading web page...";
                }
                else
                {
                    _browserStatusLabel.text = _browserManager.IsSurfaceAttached
                        ? "Ready"
                        : "Initializing GeckoView compositor surface...";
                }
#endif
            }
        }

        private void RefreshActiveSection()
        {
            if (_activeSection == SonicUISection.Music) RefreshMusicGrid();
            else if (_activeSection == SonicUISection.Browse) RefreshBrowseList();
            else if (_activeSection == SonicUISection.Browser) RefreshBrowserUI();
            else if (_activeSection == SonicUISection.Settings) RefreshSettingsUI();
        }

        private void HandlePlaybackStateChanged()
        {
            RefreshPlayerBarUI();
        }

        // =====================================================================
        // NOW PLAYING ANIMATED EQUALIZER WIDGETS
        // =====================================================================

        private void CreateEqualizerBadge(RectTransform parent, Vector2 pos)
        {
            RectTransform badgeRect = CreateRect("EqBadge", parent, pos, new Vector2(28f, 28f));
            AddGlassSurface(badgeRect.gameObject, new Color(0.04f, 0.04f, 0.04f, 0.88f), 14f, isPill: true, border: 0.18f);
            CreateEqualizerBars(badgeRect, Vector2.zero, badgeRect.gameObject);
        }

        private void CreateEqualizerBars(RectTransform parent, Vector2 pos, GameObject rootToToggle = null)
        {
            RectTransform eqGroup = CreateRect("EqBars", parent, pos, new Vector2(16f, 16f));

            RectTransform b1 = CreateRect("Bar1", eqGroup, new Vector2(-4.5f, 0f), new Vector2(2.5f, 10f));
            Image img1 = b1.gameObject.AddComponent<Image>();
            img1.sprite = _whiteSprite;
            img1.color = Color.white;
            img1.raycastTarget = false;

            RectTransform b2 = CreateRect("Bar2", eqGroup, new Vector2(0f, 0f), new Vector2(2.5f, 14f));
            Image img2 = b2.gameObject.AddComponent<Image>();
            img2.sprite = _whiteSprite;
            img2.color = Color.white;
            img2.raycastTarget = false;

            RectTransform b3 = CreateRect("Bar3", eqGroup, new Vector2(4.5f, 0f), new Vector2(2.5f, 8f));
            Image img3 = b3.gameObject.AddComponent<Image>();
            img3.sprite = _whiteSprite;
            img3.color = Color.white;
            img3.raycastTarget = false;

            GameObject rootObj = rootToToggle != null ? rootToToggle : eqGroup.gameObject;
            bool hideForBrowser = (_player == null || !_player.IsPlaying) &&
                                  (_browserManager != null && _browserManager.IsBrowserAudioPlaying);
            if (rootObj != null)
            {
                rootObj.SetActive(!hideForBrowser);
            }

            _activeEqualizers.Add(new SonicEqualizerWidget
            {
                badgeRoot = rootObj,
                bar1 = b1,
                bar2 = b2,
                bar3 = b3
            });
        }

        private void UpdateEqualizerWidgets()
        {
            if (_activeEqualizers.Count == 0) return;

            for (int i = _activeEqualizers.Count - 1; i >= 0; i--)
            {
                if (_activeEqualizers[i].bar1 == null || _activeEqualizers[i].bar2 == null || _activeEqualizers[i].bar3 == null)
                {
                    _activeEqualizers.RemoveAt(i);
                }
            }

            if (!_isVisible) return;

            bool isLocalPlaying = _player != null && _player.IsPlaying;
            bool isBrowserPlaying = !isLocalPlaying && _browserManager != null && _browserManager.IsBrowserAudioPlaying;
            float t = Time.unscaledTime * 8.5f;

            for (int i = 0; i < _activeEqualizers.Count; i++)
            {
                SonicEqualizerWidget eq = _activeEqualizers[i];
                if (eq.badgeRoot != null && eq.badgeRoot.activeSelf == isBrowserPlaying)
                {
                    eq.badgeRoot.SetActive(!isBrowserPlaying);
                }
                if (isBrowserPlaying) continue;
                if (eq.bar1 == null || eq.bar2 == null || eq.bar3 == null) continue;

                if (isLocalPlaying)
                {
                    float h1 = Mathf.Lerp(4f, 13f, Mathf.Abs(Mathf.Sin(t + i * 0.7f)));
                    float h2 = Mathf.Lerp(5f, 15f, Mathf.Abs(Mathf.Sin(t * 1.3f + 1.2f + i * 0.5f)));
                    float h3 = Mathf.Lerp(4f, 12f, Mathf.Abs(Mathf.Sin(t * 0.85f + 2.5f + i * 0.3f)));

                    eq.bar1.sizeDelta = new Vector2(2.5f, h1);
                    eq.bar2.sizeDelta = new Vector2(2.5f, h2);
                    eq.bar3.sizeDelta = new Vector2(2.5f, h3);
                }
                else
                {
                    eq.bar1.sizeDelta = new Vector2(2.5f, 6f);
                    eq.bar2.sizeDelta = new Vector2(2.5f, 10f);
                    eq.bar3.sizeDelta = new Vector2(2.5f, 6f);
                }
            }
        }

        // =====================================================================
        // ANALYTICAL SDF & VISIONOS UI HELPER BUILDERS
        // =====================================================================

        private static SonicVisionHoverEffect AddHoverHighlight(
            GameObject go,
            Image img,
            Image icon,
            Color normalCol,
            float normalBorder = 0.10f,
            float hoverBorder = 0.28f)
        {
            if (go == null || img == null) return null;
            SonicVisionHoverEffect hover = go.GetComponent<SonicVisionHoverEffect>();
            if (hover == null) hover = go.AddComponent<SonicVisionHoverEffect>();

            Color hovCol = normalCol;
            hovCol.r = Mathf.Clamp01(normalCol.r + 0.08f);
            hovCol.g = Mathf.Clamp01(normalCol.g + 0.08f);
            hovCol.b = Mathf.Clamp01(normalCol.b + 0.08f);
            hovCol.a = Mathf.Clamp01(normalCol.a + (normalCol.a > 0.01f ? 0.08f : 0.08f));

            SonicGlassSurface surf = go.GetComponent<SonicGlassSurface>();
            if (surf == null) surf = go.GetComponentInChildren<SonicGlassSurface>();
            Color? normIcon = icon != null ? (Color?)icon.color : null;
            Color? hovIcon = icon != null ? (Color?)Color.white : null;
            hover.Configure(img, surf, normalCol, hovCol, normalBorder, hoverBorder, icon, normIcon, hovIcon);
            return hover;
        }

        private Image AddGlassSurface(
            GameObject go,
            Color color,
            float radius,
            bool isPill,
            float border = 0.24f,
            SonicSurfaceMode mode = SonicSurfaceMode.SdfShape)
        {
            Image img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.sprite = _whiteSprite;
            img.type = Image.Type.Simple;
            if (_glassMaterial != null) img.material = _glassMaterial;
            img.color = color;

            SonicGlassSurface surf = go.GetComponent<SonicGlassSurface>();
            if (surf == null) surf = go.AddComponent<SonicGlassSurface>();
            surf.Configure(mode, radius, isPill, border, squareCrop: false);
            return img;
        }

        private Image AddRoundedArtworkImage(
            GameObject go,
            Sprite artworkSprite,
            float radius = 20f,
            float border = 0.24f)
        {
            Image img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.sprite = artworkSprite != null ? artworkSprite : _whiteSprite;
            img.type = Image.Type.Simple;
            if (_glassMaterial != null) img.material = _glassMaterial;
            img.color = Color.white;
            img.raycastTarget = false;

            SonicGlassSurface surf = go.GetComponent<SonicGlassSurface>();
            if (surf == null) surf = go.AddComponent<SonicGlassSurface>();
            surf.Configure(SonicSurfaceMode.RoundedTexture, radius, pill: false, border: border, squareCrop: true);
            return img;
        }

        private static RectTransform CreateRect(string name, RectTransform parent, Vector2 anchoredPos, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            return rt;
        }

        private static RectTransform CreateStretchRect(string name, RectTransform parent, Vector4 paddingLeftBottomRightTop)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(paddingLeftBottomRightTop.x, paddingLeftBottomRightTop.y);
            rt.offsetMax = new Vector2(-paddingLeftBottomRightTop.z, -paddingLeftBottomRightTop.w);
            return rt;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void SetRectAnchored(RectTransform rt, Vector2 pos, Vector2 size, Vector2 pivot)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private TextMeshProUGUI CreateLabel(string name, RectTransform parent, string text, float fontSize, FontStyles style, TextAlignmentOptions align)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.overflowMode = TextOverflowModes.Truncate;
            if (_fontAsset == null)
            {
                _fontAsset = TMP_Settings.defaultFontAsset;
                EnsureCJKFontSupport();
            }
            if (_fontAsset != null) tmp.font = _fontAsset;
            EnsureLabelCJKFont(tmp);
            tmp.text = text != null ? text.Trim('\uFEFF') : string.Empty;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = align;
            tmp.color = ColTextPrimary;
            tmp.raycastTarget = false;
            return tmp;
        }

        private void CreateColorDot(RectTransform parent, Vector2 pos, Color c, float size = 24f)
        {
            RectTransform dot = CreateRect("Dot", parent, pos, new Vector2(size, size));
            c.a = 1f;
            Image img = AddGlassSurface(dot.gameObject, c, size * 0.5f, isPill: true, border: 0.35f);
            img.raycastTarget = false;
        }

        private static void ConfigureVisionButtonColors(Button btn, Image targetGraphic)
        {
            btn.targetGraphic = targetGraphic;
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            cb.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            cb.selectedColor = Color.white;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
        }

        private RectTransform CreateIconButton(
            RectTransform parent,
            string name,
            Vector2 pos,
            Vector2 size,
            SonicIconType iconType,
            Color bgColor,
            Color iconColor,
            out Image bgImg,
            out Image iconImg,
            Action onClick,
            float border = -1f)
        {
            RectTransform btnRect = CreateRect(name, parent, pos, size);
            float borderVal = border >= 0f ? border : (bgColor.a > 0.04f ? 0.08f : 0.0f);
            bgImg = AddGlassSurface(btnRect.gameObject, bgColor, Mathf.Min(size.x, size.y) * 0.5f, isPill: true, border: borderVal);

            Button btn = btnRect.gameObject.AddComponent<Button>();
            ConfigureVisionButtonColors(btn, bgImg);
            btn.onClick.AddListener(() => onClick?.Invoke());

            float iconSize = Mathf.Min(size.x, size.y) * 0.54f;
            RectTransform iconRect = CreateRect("Icon", btnRect, Vector2.zero, new Vector2(iconSize, iconSize));
            iconImg = iconRect.gameObject.AddComponent<Image>();
            iconImg.sprite = SonicUIIconGenerator.GetIcon(iconType);
            iconImg.color = iconColor;
            iconImg.raycastTarget = false;
            return btnRect;
        }

        private Slider CreateInteractiveSlider(
            RectTransform parent,
            string name,
            Vector2 pos,
            Vector2 trackSize,
            float knobSize,
            float min,
            float max,
            float initial,
            Action<float> onValueChanged)
        {
            RectTransform hitRect = CreateRect(name, parent, pos, new Vector2(trackSize.x, Mathf.Max(26f, trackSize.y)));
            Image hitImg = hitRect.gameObject.AddComponent<Image>();
            hitImg.sprite = _whiteSprite;
            hitImg.color = new Color(0f, 0f, 0f, 0.002f);

            RectTransform trackRect = CreateRect("Track", hitRect, Vector2.zero, trackSize);
            Image trackBg = AddGlassSurface(trackRect.gameObject, new Color(1f, 1f, 1f, 0.18f), trackSize.y * 0.5f, isPill: true, border: 0.15f);
            trackBg.raycastTarget = false;

            RectTransform fillArea = CreateStretchRect("FillArea", trackRect, Vector4.zero);
            RectTransform fillRect = CreateStretchRect("Fill", fillArea, Vector4.zero);
            Image fillImg = AddGlassSurface(fillRect.gameObject, new Color(0.96f, 0.98f, 1.0f, 0.95f), trackSize.y * 0.5f, isPill: true, border: 0.20f);
            fillImg.raycastTarget = false;

            RectTransform handleSlideArea = CreateStretchRect("HandleSlideArea", trackRect, Vector4.zero);
            RectTransform handleRect = CreateRect("Handle", handleSlideArea, Vector2.zero, new Vector2(knobSize, knobSize));
            Image handleImg = AddGlassSurface(handleRect.gameObject, Color.white, knobSize * 0.5f, isPill: true, border: 0.45f);

            Slider slider = hitRect.gameObject.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = initial;
            slider.onValueChanged.AddListener(v => onValueChanged?.Invoke(v));

            return slider;
        }

        private Slider CreateLabeledSliderRow(
            RectTransform parent,
            Vector2 pos,
            string label,
            float min,
            float max,
            float initial,
            Action<float> onChanged)
        {
            RectTransform row = CreateRect($"Row_{label}", parent, pos, new Vector2(386f, 34f));

            TextMeshProUGUI lbl = CreateLabel("Label", row, label, 13.5f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(lbl.rectTransform, new Vector2(-126f, 0f), new Vector2(132f, 26f), new Vector2(0.5f, 0.5f));

            Slider s = CreateInteractiveSlider(
                row,
                "Slider",
                new Vector2(68f, 0f),
                new Vector2(236f, 8f),
                knobSize: 18f,
                min: min,
                max: max,
                initial: initial,
                onValueChanged: val =>
                {
                    if (_suppressSliderCallbacks) return;
                    onChanged?.Invoke(val);
                });

            return s;
        }

        private void CreateManualColorSliderRow(
            RectTransform parent,
            Vector2 pos,
            string label,
            float initialHue,
            out Image previewSwatch,
            Action<float> onHueChanged)
        {
            RectTransform row = CreateRect($"ManualColor_{label}", parent, pos, new Vector2(382f, 58f));
            Image rowBg = AddGlassSurface(row.gameObject, ColSurfaceElevated, 16f, isPill: false, border: 0.10f);
            AddHoverHighlight(row.gameObject, rowBg, null, ColSurfaceElevated, 0.10f, 0.22f);

            TextMeshProUGUI lbl = CreateLabel("Label", row, label, 13f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(lbl.rectTransform, new Vector2(-100f, 12f), new Vector2(160f, 20f), new Vector2(0.5f, 0.5f));

            RectTransform swatchRect = CreateRect("Swatch", row, new Vector2(158f, 0f), new Vector2(28f, 28f));
            previewSwatch = AddGlassSurface(swatchRect.gameObject, Color.HSVToRGB(initialHue, 0.8f, 0.9f), 14f, isPill: true, border: 0.20f);
            previewSwatch.raycastTarget = false;

            CreateInteractiveSlider(
                row,
                "HueSlider",
                new Vector2(-24f, -11f),
                new Vector2(312f, 8f),
                knobSize: 18f,
                min: 0f,
                max: 1f,
                initial: initialHue,
                onValueChanged: h => onHueChanged?.Invoke(h));
        }

        private void CreateEffectToggleItem(
            RectTransform parent,
            Vector2 pos,
            string label,
            Func<bool> getter,
            Action<bool> setter)
        {
            RectTransform itemRect = CreateRect($"Toggle_{label}", parent, pos, new Vector2(188f, 38f));

            Image itemHit = itemRect.gameObject.AddComponent<Image>();
            itemHit.sprite = _whiteSprite;
            itemHit.color = Color.clear;

            TextMeshProUGUI lbl = CreateLabel("Label", itemRect, label, 12.5f, FontStyles.Bold, TextAlignmentOptions.MidlineLeft);
            SetRectAnchored(lbl.rectTransform, new Vector2(-28f, 0f), new Vector2(126f, 32f), new Vector2(0.5f, 0.5f));
            lbl.color = ColTextPrimary;

            RectTransform pillRect = CreateRect("SwitchPill", itemRect, new Vector2(64f, 0f), new Vector2(44f, 24f));
            Image pillBg = AddGlassSurface(pillRect.gameObject, ColToggleActive, 12f, isPill: true, border: 0.10f);
            pillBg.raycastTarget = false;

            RectTransform knobRect = CreateRect("Knob", pillRect, new Vector2(10f, 0f), new Vector2(18f, 18f));
            Image knobImg = AddGlassSurface(knobRect.gameObject, Color.white, 9f, isPill: true, border: 0.18f);
            knobImg.raycastTarget = false;

            EffectToggleBinding binding = new EffectToggleBinding
            {
                pillBg = pillBg,
                knobImg = knobImg,
                knobRect = knobRect,
                getter = getter,
                setter = setter
            };
            _effectToggles.Add(binding);

            Button btn = itemRect.gameObject.AddComponent<Button>();
            ConfigureVisionButtonColors(btn, itemHit);
            AddHoverHighlight(itemRect.gameObject, pillBg, null, ColToggleInactive, 0.10f, 0.24f);
            btn.onClick.AddListener(() =>
            {
                bool nextState = !binding.getter();
                binding.setter(nextState);
                UpdateEffectToggleVisual(binding);
            });

            UpdateEffectToggleVisual(binding);
        }

        private static void UpdateEffectToggleVisual(EffectToggleBinding b)
        {
            bool isOn = b.getter != null && b.getter();
            Color pillCol = isOn ? ColToggleActive : ColToggleInactive;
            if (b.pillBg != null)
            {
                b.pillBg.color = pillCol;
                var hover = b.pillBg.GetComponentInParent<SonicVisionHoverEffect>();
                if (hover != null) hover.SetNormalState(pillCol, 0.10f);
            }
            if (b.knobImg != null)
            {
                b.knobImg.color = isOn ? new Color(0.12f, 0.12f, 0.12f, 1.0f) : new Color(0.72f, 0.72f, 0.72f, 0.85f);
            }
            if (b.knobRect != null)
            {
                b.knobRect.anchoredPosition = new Vector2(isOn ? 10f : -10f, 0f);
            }
        }
    }
}