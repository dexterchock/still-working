using System;
using System.Collections.Generic;
using UnityEngine;

namespace SonicTopography
{
    public enum SonicRepeatMode
    {
        NormalSequential = 0,
        RepeatAll = 1,
        RepeatOne = 2
    }

    public class SonicMusicPlayerController : MonoBehaviour
    {
        private SonicTopographyAudioEngine _audioEngine;
        private SonicTopographyVisualizer _visualizer;
        private SonicMusicLibraryManager _library;
        private AudioSource _audioSource;

        private SonicMusicTrack _currentTrack;
        private int _currentTrackIndex = -1;
        private bool _isPaused = true;
        private bool _isLoadingTrack = false;
        private bool _autoPlayWhenLoaded = false;
        private bool _shuffleEnabled = false;
        private SonicRepeatMode _repeatMode = SonicRepeatMode.RepeatAll;

        private float _volume = 0.9f;
        private float _preMuteVolume = 0.9f;
        private bool _isMuted = false;
        private float _lastKnownPlaybackTime = 0f;
        private int _loadRequestId = 0;
        private Coroutine _activeLoadCoroutine = null;

        private readonly List<int> _historyIndices = new List<int>();

        public SonicMusicTrack CurrentTrack => _currentTrack;
        public int CurrentTrackIndex => _currentTrackIndex;
        public bool IsPlaying => _audioSource != null && _audioSource.isPlaying;
        public bool IsPaused => _isPaused;
        public bool IsLoadingTrack => _isLoadingTrack;
        public bool ShuffleEnabled => _shuffleEnabled;
        public SonicRepeatMode RepeatMode => _repeatMode;
        public float Volume => _isMuted ? 0f : _volume;
        public bool IsMuted => _isMuted;

        public float CurrentTime => _audioSource != null && _audioSource.clip != null ? _audioSource.time : 0f;
        public float CurrentDuration => _audioSource != null && _audioSource.clip != null
            ? _audioSource.clip.length
            : (_currentTrack != null ? _currentTrack.durationSeconds : 0f);

        public float NormalizedProgress
        {
            get
            {
                float dur = CurrentDuration;
                return dur > 0.01f ? Mathf.Clamp01(CurrentTime / dur) : 0f;
            }
        }

        public event Action<SonicMusicTrack> OnTrackChanged;
        public event Action OnPlaybackStateChanged;
        public event Action OnLocalPlaybackStarted;

        public void Initialize(SonicTopographyAudioEngine audioEngine, SonicMusicLibraryManager library)
        {
            _audioEngine = audioEngine;
            _library = library;
            _visualizer = GetComponent<SonicTopographyVisualizer>();
            if (_visualizer == null) _visualizer = FindFirstObjectByType<SonicTopographyVisualizer>();

            if (_audioEngine != null)
            {
                _audioSource = _audioEngine.audioSource != null
                    ? _audioEngine.audioSource
                    : _audioEngine.GetComponent<AudioSource>();
            }

            if (_audioSource != null)
            {
                // Managed by SonicMusicPlayerController so sequential, repeat-all, and repeat-one work accurately
                _audioSource.loop = false;
                _volume = _audioSource.volume;
            }

            _shuffleEnabled = PlayerPrefs.GetInt("SonicTopography_Shuffle", 0) == 1;
            _repeatMode = (SonicRepeatMode)PlayerPrefs.GetInt("SonicTopography_Repeat", 0);

            if (_library != null)
            {
                _library.OnLibraryUpdated -= HandleLibraryUpdated;
                _library.OnLibraryUpdated += HandleLibraryUpdated;
                SyncInitialTrackFromAudioSource();
            }
        }

        private void SyncAlbumArtPaletteWithVisualizer()
        {
            if (_visualizer == null) return;
            if (_currentTrack != null && _currentTrack.hasRealAlbumArt)
            {
                _visualizer.SetAlbumArtPalette(true, _currentTrack.extractedPalette);
            }
            else
            {
                _visualizer.SetAlbumArtPalette(false, default);
            }
        }

        private void OnDestroy()
        {
            if (_library != null)
            {
                _library.OnLibraryUpdated -= HandleLibraryUpdated;
            }
        }

        private void SyncInitialTrackFromAudioSource()
        {
            if (_library == null || _library.Tracks.Count == 0)
            {
                SyncAlbumArtPaletteWithVisualizer();
                return;
            }

            if (_audioSource != null && _audioSource.clip != null)
            {
                for (int i = 0; i < _library.Tracks.Count; i++)
                {
                    SonicMusicTrack t = _library.Tracks[i];
                    if (t.cachedClip == _audioSource.clip ||
                        string.Equals(t.title, _audioSource.clip.name, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentTrack = t;
                        _currentTrackIndex = i;
                        t.cachedClip = _audioSource.clip;
                        t.durationSeconds = _audioSource.clip.length;
                        SyncAlbumArtPaletteWithVisualizer();
                        OnTrackChanged?.Invoke(_currentTrack);
                        OnPlaybackStateChanged?.Invoke();
                        return;
                    }
                }
            }

            // Restore last played track from user's previous session if available
            string lastTrackPath = PlayerPrefs.GetString("SonicTopography_LastTrackPath", string.Empty);
            if (!string.IsNullOrEmpty(lastTrackPath))
            {
                for (int i = 0; i < _library.Tracks.Count; i++)
                {
                    if (string.Equals(_library.Tracks[i].filePath, lastTrackPath, StringComparison.OrdinalIgnoreCase))
                    {
                        CueTrackAtIndex(i);
                        return;
                    }
                }
            }

            // First time login / no last played song: song is none
            _currentTrack = null;
            _currentTrackIndex = -1;
            SyncAlbumArtPaletteWithVisualizer();
            OnTrackChanged?.Invoke(null);
            OnPlaybackStateChanged?.Invoke();
        }

        private void HandleLibraryUpdated()
        {
            if (_library == null) return;
            if (_currentTrack != null)
            {
                _currentTrackIndex = -1;
                for (int i = 0; i < _library.Tracks.Count; i++)
                {
                    if (_library.Tracks[i].id == _currentTrack.id ||
                        string.Equals(_library.Tracks[i].filePath, _currentTrack.filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentTrackIndex = i;
                        _currentTrack = _library.Tracks[i];
                        break;
                    }
                }
            }
            else
            {
                // Restore last played track if available
                string lastTrackPath = PlayerPrefs.GetString("SonicTopography_LastTrackPath", string.Empty);
                if (!string.IsNullOrEmpty(lastTrackPath))
                {
                    for (int i = 0; i < _library.Tracks.Count; i++)
                    {
                        if (string.Equals(_library.Tracks[i].filePath, lastTrackPath, StringComparison.OrdinalIgnoreCase))
                        {
                            CueTrackAtIndex(i);
                            return;
                        }
                    }
                }

                // First time login / no saved track: song is none
                _currentTrack = null;
                _currentTrackIndex = -1;
            }
            SyncAlbumArtPaletteWithVisualizer();
            OnPlaybackStateChanged?.Invoke();
        }

        private void Update()
        {
            if (_audioSource == null || _audioSource.clip == null || _isLoadingTrack) return;

            if (_audioSource.isPlaying)
            {
                _lastKnownPlaybackTime = _audioSource.time;
            }
            else if (!_isPaused)
            {
                // Detect natural end of track
                float clipLen = _audioSource.clip.length;
                if (clipLen > 0.5f && (_lastKnownPlaybackTime >= clipLen - 0.35f || _audioSource.time >= clipLen - 0.15f))
                {
                    _lastKnownPlaybackTime = 0f;
                    HandleTrackFinishedNaturally();
                }
            }
        }

        private void HandleTrackFinishedNaturally()
        {
            if (_repeatMode == SonicRepeatMode.RepeatOne)
            {
                if (_audioSource != null && _audioSource.clip != null)
                {
                    _audioSource.time = 0f;
                    _audioSource.Play();
                    _isPaused = false;
                    OnPlaybackStateChanged?.Invoke();
                }
                return;
            }

            if (_library == null || _library.Tracks.Count == 0) return;

            int count = _library.Tracks.Count;
            if (_shuffleEnabled && count > 1)
            {
                int nextIdx = GetRandomTrackIndexExcluding(_currentTrackIndex, count);
                PlayTrackAtIndex(nextIdx);
                return;
            }

            int sequentialNext = _currentTrackIndex + 1;
            if (sequentialNext < count)
            {
                PlayTrackAtIndex(sequentialNext);
            }
            else if (_repeatMode == SonicRepeatMode.RepeatAll)
            {
                PlayTrackAtIndex(0);
            }
            else
            {
                // NormalSequential reached the end of the library queue
                _audioSource.Stop();
                _audioSource.time = 0f;
                _isPaused = true;
                OnPlaybackStateChanged?.Invoke();
            }
        }

        public void PlayTrack(SonicMusicTrack track)
        {
            if (track == null || _library == null) return;
            for (int i = 0; i < _library.Tracks.Count; i++)
            {
                if (_library.Tracks[i].id == track.id)
                {
                    PlayTrackAtIndex(i);
                    return;
                }
            }
        }

        public void CueTrackAtIndex(int index)
        {
            LoadTrackAtIndexInternal(index, autoPlay: false);
        }

        public void PlayTrackAtIndex(int index)
        {
            LoadTrackAtIndexInternal(index, autoPlay: true);
        }

        private void LoadTrackAtIndexInternal(int index, bool autoPlay)
        {
            if (_library == null || index < 0 || index >= _library.Tracks.Count) return;

            if (_currentTrackIndex >= 0 && _currentTrackIndex != index)
            {
                _historyIndices.Add(_currentTrackIndex);
                if (_historyIndices.Count > 50) _historyIndices.RemoveAt(0);
            }

            _currentTrackIndex = index;
            _currentTrack = _library.Tracks[index];
            SonicMusicTrack requestedTrack = _currentTrack;
            if (requestedTrack != null && !string.IsNullOrEmpty(requestedTrack.filePath))
            {
                PlayerPrefs.SetString("SonicTopography_LastTrackPath", requestedTrack.filePath);
                PlayerPrefs.Save();
            }
            _isLoadingTrack = true;
            _autoPlayWhenLoaded = autoPlay;
            _isPaused = !autoPlay;
            _lastKnownPlaybackTime = 0f;
            if (autoPlay)
            {
                OnLocalPlaybackStarted?.Invoke();
            }

            // Immediately stop previous audio so an old song never continues playing in the background
            // while the new track is loading or converting
            if (_audioSource != null)
            {
                _audioSource.Stop();
                _audioSource.clip = null;
            }

            SyncAlbumArtPaletteWithVisualizer();
            OnTrackChanged?.Invoke(_currentTrack);

            if (_activeLoadCoroutine != null)
            {
                StopCoroutine(_activeLoadCoroutine);
                _activeLoadCoroutine = null;
            }

            int currentRequestId = ++_loadRequestId;

            _activeLoadCoroutine = StartCoroutine(_library.LoadTrackClipCoroutine(requestedTrack, clip =>
            {
                // Discard stale load if user selected another track while this was loading
                if (currentRequestId != _loadRequestId || _currentTrack != requestedTrack)
                {
                    return;
                }

                _isLoadingTrack = false;
                _activeLoadCoroutine = null;

                if (clip != null && _audioSource != null)
                {
                    _audioSource.Stop();
                    _audioSource.clip = clip;
                    _audioSource.loop = false;
                    _audioSource.time = 0f;
                    if (_autoPlayWhenLoaded)
                    {
                        OnLocalPlaybackStarted?.Invoke();
                        _audioSource.Play();
                        _isPaused = false;
                        Debug.Log($"[SonicMusicPlayer] Playing track: {(_currentTrack != null ? _currentTrack.title : "unknown")}");
                    }
                    else
                    {
                        _isPaused = true;
                    }
                }
                else
                {
                    _isPaused = true;
                }
                SyncAlbumArtPaletteWithVisualizer();
                OnPlaybackStateChanged?.Invoke();
            }));
        }

        public void PlayFileFromBrowser(string filePath)
        {
            if (_library == null) return;
            SonicMusicTrack track = _library.RegisterOrGetFileTrack(filePath);
            if (track != null)
            {
                PlayTrack(track);
            }
        }

        public void TogglePlayPause()
        {
            if (_audioSource == null) return;

            if (_isLoadingTrack)
            {
                _autoPlayWhenLoaded = !_autoPlayWhenLoaded;
                _isPaused = !_autoPlayWhenLoaded;
                OnPlaybackStateChanged?.Invoke();
                return;
            }

            if (_audioSource.clip == null)
            {
                if (_library != null && _library.Tracks.Count > 0)
                {
                    PlayTrackAtIndex(Mathf.Max(0, _currentTrackIndex));
                }
                return;
            }

            if (_audioSource.isPlaying)
            {
                _audioSource.Pause();
                _isPaused = true;
                Debug.Log($"[SonicMusicPlayer] Paused track: {(_currentTrack != null ? _currentTrack.title : "unknown")}");
            }
            else
            {
                OnLocalPlaybackStarted?.Invoke();
                SyncAlbumArtPaletteWithVisualizer();
                _audioSource.Play();
                _isPaused = false;
                Debug.Log($"[SonicMusicPlayer] Playing track: {(_currentTrack != null ? _currentTrack.title : "unknown")}");
            }
            OnPlaybackStateChanged?.Invoke();
        }

        public void PauseIfPlaying()
        {
            if (_isLoadingTrack)
            {
                _autoPlayWhenLoaded = false;
                _isPaused = true;
                OnPlaybackStateChanged?.Invoke();
                return;
            }
            if (_audioSource != null && _audioSource.isPlaying)
            {
                _audioSource.Pause();
                _isPaused = true;
                OnPlaybackStateChanged?.Invoke();
            }
        }

        public void NextTrack()
        {
            if (_library == null || _library.Tracks.Count == 0) return;
            int count = _library.Tracks.Count;

            if (count == 1)
            {
                SeekSeconds(0f);
                if (!IsPlaying) TogglePlayPause();
                return;
            }

            if (_shuffleEnabled)
            {
                int rnd = GetRandomTrackIndexExcluding(_currentTrackIndex, count);
                PlayTrackAtIndex(rnd);
            }
            else
            {
                int next = (_currentTrackIndex + 1) % count;
                PlayTrackAtIndex(next);
            }
        }

        public void PreviousTrack()
        {
            if (_audioSource != null && _audioSource.clip != null && _audioSource.time > 3.0f)
            {
                SeekSeconds(0f);
                return;
            }

            if (_library == null || _library.Tracks.Count == 0) return;
            int count = _library.Tracks.Count;

            if (_historyIndices.Count > 0)
            {
                int prevIdx = _historyIndices[_historyIndices.Count - 1];
                _historyIndices.RemoveAt(_historyIndices.Count - 1);
                if (prevIdx >= 0 && prevIdx < count)
                {
                    _currentTrackIndex = prevIdx;
                    PlayTrackAtIndex(prevIdx);
                    return;
                }
            }

            int prev = (_currentTrackIndex - 1 + count) % count;
            PlayTrackAtIndex(prev);
        }

        public void SeekNormalized(float normalized01)
        {
            if (_audioSource == null || _audioSource.clip == null) return;
            float targetSec = Mathf.Clamp01(normalized01) * _audioSource.clip.length;
            SeekSeconds(targetSec);
        }

        public void SeekSeconds(float seconds)
        {
            if (_audioSource == null || _audioSource.clip == null) return;
            float clamped = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, _audioSource.clip.length - 0.05f));
            _audioSource.time = clamped;
            _lastKnownPlaybackTime = clamped;
        }

        public void SetVolume(float volume01)
        {
            _volume = Mathf.Clamp01(volume01);
            if (_volume > 0.001f)
            {
                _isMuted = false;
                _preMuteVolume = _volume;
            }
            if (_audioSource != null)
            {
                _audioSource.volume = _isMuted ? 0f : _volume;
            }
            OnPlaybackStateChanged?.Invoke();
        }

        public void ToggleMute()
        {
            _isMuted = !_isMuted;
            if (!_isMuted && _volume < 0.05f)
            {
                _volume = Mathf.Max(0.25f, _preMuteVolume);
            }
            if (_audioSource != null)
            {
                _audioSource.volume = _isMuted ? 0f : _volume;
            }
            OnPlaybackStateChanged?.Invoke();
        }

        public void ToggleShuffle()
        {
            _shuffleEnabled = !_shuffleEnabled;
            PlayerPrefs.SetInt("SonicTopography_Shuffle", _shuffleEnabled ? 1 : 0);
            PlayerPrefs.Save();
            OnPlaybackStateChanged?.Invoke();
        }

        public void CycleRepeatMode()
        {
            int next = ((int)_repeatMode + 1) % 3;
            _repeatMode = (SonicRepeatMode)next;
            PlayerPrefs.SetInt("SonicTopography_Repeat", (int)_repeatMode);
            PlayerPrefs.Save();
            OnPlaybackStateChanged?.Invoke();
        }

        public void SetRepeatMode(SonicRepeatMode mode)
        {
            _repeatMode = mode;
            PlayerPrefs.SetInt("SonicTopography_Repeat", (int)_repeatMode);
            PlayerPrefs.Save();
            OnPlaybackStateChanged?.Invoke();
        }

        private static int GetRandomTrackIndexExcluding(int excludeIndex, int count)
        {
            if (count <= 1) return 0;
            int candidate = UnityEngine.Random.Range(0, count - 1);
            if (candidate >= excludeIndex) candidate++;
            return candidate;
        }

        public static string FormatSeconds(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0f) seconds = 0f;
            int total = Mathf.FloorToInt(seconds);
            int mins = total / 60;
            int secs = total % 60;
            return $"{mins}:{secs:00}";
        }
    }
}
