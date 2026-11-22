using System;
using UnityEngine;

namespace SonicTopography
{
    /// <summary>
    /// Managed wrapper for Android's hidden EditText soft-input bridge (<c>SonicQuestSoftInputBridge</c>).
    /// Bypasses Unity 6 GameActivity's <c>gti.InputConnection</c>, which lacks <c>getExtractedText</c> and
    /// <c>updateExtractedText</c> notifications needed to keep Horizon OS's keyboard preview box synced.
    /// </summary>
    public static class SonicQuestNativeKeyboard
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _bridge;
        private static bool _initAttempted = false;
        private static bool _lastKnownActive = false;

        public static bool IsSupported => true;
#else
        public static bool IsSupported => false;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterQuittingHook()
        {
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject EnsureBridge()
        {
            if (_bridge != null) return _bridge;
            if (_initAttempted) return null;
            _initAttempted = true;

            try
            {
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    if (activity != null)
                    {
                        _bridge = new AndroidJavaObject("com.dexter.audiofloor.SonicQuestSoftInputBridge", activity);
                    }
                    else
                    {
                        _initAttempted = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SonicSoftInput] Failed creating SonicQuestSoftInputBridge: {ex.Message}");
            }

            return _bridge;
        }
#endif

        public static bool IsActive
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (_bridge == null) return false;
                try
                {
                    _lastKnownActive = _bridge.Call<bool>("isActive");
                    return _lastKnownActive;
                }
                catch
                {
                    return _lastKnownActive;
                }
#else
                return false;
#endif
            }
        }

        public static int ActiveSessionId
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (_bridge == null) return 0;
                try
                {
                    return _bridge.Call<int>("getActiveSessionId");
                }
                catch
                {
                    return 0;
                }
#else
                return 0;
#endif
            }
        }

        public static int Open(string initialText, string hint, bool isPassword = false, bool isUrl = false)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaObject bridge = EnsureBridge();
            if (bridge == null) return 0;
            try
            {
                int sid = bridge.Call<int>(
                    "open",
                    initialText ?? string.Empty,
                    hint ?? string.Empty,
                    isPassword,
                    isUrl);
                _lastKnownActive = sid > 0;
                return sid;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SonicSoftInput] Open failed: {ex.Message}");
                return 0;
            }
#else
            return 0;
#endif
        }

        public static void SetText(int sessionId, string text)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            try
            {
                _bridge.Call("setText", sessionId, text ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicSoftInput] SetText failed: {ex.Message}");
            }
#endif
        }

        public static void Close(int sessionId = 0)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return;
            try
            {
                _bridge.Call("close", sessionId);
                if (sessionId <= 0)
                {
                    _lastKnownActive = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SonicSoftInput] Close failed: {ex.Message}");
            }
#endif
        }

        public static bool IsSessionActive(int sessionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null || sessionId <= 0) return false;
            try
            {
                bool active = _bridge.Call<bool>("isSessionActive", sessionId);
                if (active) _lastKnownActive = true;
                return active;
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public static int GetTextSeq()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return 0;
            try
            {
                return _bridge.Call<int>("getTextSeq");
            }
            catch
            {
                return 0;
            }
#else
            return 0;
#endif
        }

        public static string GetText()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null) return string.Empty;
            try
            {
                return _bridge.Call<string>("getText") ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
#else
            return string.Empty;
#endif
        }

        public static bool WasSubmitPressed(int sessionId)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge == null || sessionId <= 0) return false;
            try
            {
                return _bridge.Call<bool>("wasSubmitPressed", sessionId);
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public static void Shutdown()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _lastKnownActive = false;
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
            _initAttempted = false;
#endif
        }
    }
}
