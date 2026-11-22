package com.dexter.audiofloor;

import android.app.Activity;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Base64;
import android.util.Log;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import org.json.JSONObject;
import org.mozilla.geckoview.AllowOrDeny;
import org.mozilla.geckoview.GeckoDisplay;
import org.mozilla.geckoview.GeckoResult;
import org.mozilla.geckoview.GeckoRuntime;
import org.mozilla.geckoview.GeckoRuntimeSettings;
import org.mozilla.geckoview.GeckoSession;
import org.mozilla.geckoview.GeckoSessionSettings;
import org.mozilla.geckoview.WebExtension;

import java.util.List;

public class SonicGeckoBrowserBridge {
    private static final String TAG = "SonicBrowser";
    private static final String EXT_URI = "resource://android/assets/sonic_browser_ext/";
    private static final String EXT_ID = "audiofloor-bridge@audiofloor.app";
    private static final String NATIVE_APP = "sonic_browser";

    private static GeckoRuntime sRuntime;

    private final Activity mActivity;
    private final Handler mUiHandler;
    private final Object mStateLock = new Object();

    private GeckoSession mPrimarySession;
    private GeckoSession mPopupSession;
    private GeckoSession mActiveSession;
    private GeckoDisplay mDisplay;
    private WebExtension.Port mPort;

    private Surface mSurface;
    private int mSurfaceWidth = 1280;
    private int mSurfaceHeight = 752;
    private boolean mSurfaceBound = false;

    private boolean mInitialized = false;
    private boolean mShutdown = false;
    private boolean mExtReady = false;

    private String mCurrentUrl = "https://music.youtube.com";
    private String mPageTitle = "";
    private boolean mCanGoBack = false;
    private boolean mCanGoForward = false;
    private boolean mIsLoading = false;

    private String mMediaTitle = "";
    private String mMediaArtist = "";
    private String mArtworkUrl = "";
    private boolean mIsPlaying = false;
    private boolean mDrmSilent = false;
    private float mVolume = 1.0f;
    private double mCurrentTime = 0.0;
    private double mDuration = 0.0;

    private int mSoftInputSeq = 0;
    private int mSoftInputValSeq = 0;
    private boolean mSoftInputIsPassword = false;
    private String mSoftInputInitialText = "";

    private final byte[] mSpectrumBytes = new byte[512];
    private int mFftSeq = 0;
    private boolean mLoggedFftActive = false;
    private boolean mLoggedDrmSilent = false;

    private long mTouchDownTimeMs = 0L;

    public SonicGeckoBrowserBridge(@NonNull Activity activity, @Nullable String initialUrl) {
        mActivity = activity;
        mUiHandler = new Handler(Looper.getMainLooper());
        final String startUrl = (initialUrl != null && !initialUrl.isEmpty())
                ? initialUrl
                : "https://music.youtube.com";
        mCurrentUrl = startUrl;

        mUiHandler.post(() -> initOnUiThread(startUrl));
    }

    private void initOnUiThread(@NonNull String startUrl) {
        if (mShutdown || mInitialized) return;
        try {
            if (sRuntime == null) {
                GeckoRuntimeSettings runtimeSettings = new GeckoRuntimeSettings.Builder()
                        .consoleOutput(true)
                        .javaScriptEnabled(true)
                        .remoteDebuggingEnabled(false)
                        .build();
                sRuntime = GeckoRuntime.create(mActivity.getApplicationContext(), runtimeSettings);
                Log.i(TAG, "GeckoRuntime created");
            }

            sRuntime.getWebExtensionController()
                    .ensureBuiltIn(EXT_URI, EXT_ID)
                    .accept(
                            extension -> {
                                if (extension == null) {
                                    Log.e(TAG, "ensureBuiltIn returned null extension");
                                    return;
                                }
                                mExtReady = true;
                                Log.i(TAG, "Built-in WebExtension installed: " + extension.id);
                                extension.setMessageDelegate(new WebExtension.MessageDelegate() {
                                    @Override
                                    public void onConnect(@NonNull WebExtension.Port port) {
                                        Log.i(TAG, "Port connected: " + NATIVE_APP);
                                        mPort = port;
                                        port.setDelegate(mPortDelegate);
                                    }
                                }, NATIVE_APP);
                            },
                            error -> Log.e(TAG, "WebExtension ensureBuiltIn failed", error)
                    );

            mPrimarySession = createConfiguredSession();
            mPrimarySession.open(sRuntime);
            mActiveSession = mPrimarySession;
            mInitialized = true;

            bindSurfaceToActiveSessionIfReady();
            mPrimarySession.setActive(true);
            mPrimarySession.loadUri(startUrl);
            Log.i(TAG, "GeckoSession initialized and loading: " + startUrl);
        } catch (Throwable t) {
            Log.e(TAG, "Failed initializing GeckoView bridge", t);
        }
    }

    private GeckoSession createConfiguredSession() {
        GeckoSessionSettings settings = new GeckoSessionSettings.Builder()
                .usePrivateMode(false)
                .userAgentMode(GeckoSessionSettings.USER_AGENT_MODE_DESKTOP)
                .viewportMode(GeckoSessionSettings.VIEWPORT_MODE_DESKTOP)
                .allowJavascript(true)
                .build();

        GeckoSession session = new GeckoSession(settings);
        session.setPermissionDelegate(mPermissionDelegate);
        session.setNavigationDelegate(mNavigationDelegate);
        session.setProgressDelegate(mProgressDelegate);
        session.setContentDelegate(mContentDelegate);
        session.getTextInput().setDelegate(mTextInputDelegate);
        return session;
    }

    private void bindSurfaceToActiveSessionIfReady() {
        if (mShutdown || mActiveSession == null || mSurface == null || !mSurface.isValid()) {
            return;
        }
        try {
            if (mDisplay == null) {
                mDisplay = mActiveSession.acquireDisplay();
            }
            mDisplay.surfaceChanged(
                    new GeckoDisplay.SurfaceInfo.Builder(mSurface)
                            .size(mSurfaceWidth, mSurfaceHeight)
                            .build()
            );
            mSurfaceBound = true;
            mActiveSession.setActive(true);
            Log.i(TAG, "attachSurface bound to GeckoDisplay (" + mSurfaceWidth + "x" + mSurfaceHeight + ")");
        } catch (Throwable t) {
            Log.e(TAG, "Failed binding Surface to GeckoDisplay", t);
        }
    }

    private void detachDisplayFromSession(@Nullable GeckoSession session) {
        if (mDisplay != null) {
            try {
                mDisplay.surfaceDestroyed();
            } catch (Throwable ignored) {}
            if (session != null) {
                try {
                    session.releaseDisplay(mDisplay);
                } catch (Throwable ignored) {}
            }
            mDisplay = null;
        }
        mSurfaceBound = false;
    }

    private void switchActiveSession(@NonNull GeckoSession nextSession) {
        if (mActiveSession == nextSession) return;
        GeckoSession prev = mActiveSession;
        detachDisplayFromSession(prev);
        mActiveSession = nextSession;
        bindSurfaceToActiveSessionIfReady();
    }

    public void attachSurface(@Nullable Surface surface, int width, int height) {
        mUiHandler.post(() -> {
            if (mShutdown) return;
            mSurface = surface;
            if (width > 0) mSurfaceWidth = width;
            if (height > 0) mSurfaceHeight = height;
            if (surface == null) {
                detachDisplayFromSession(mActiveSession);
                return;
            }
            bindSurfaceToActiveSessionIfReady();
        });
    }

    public void navigate(@Nullable String rawUrl) {
        if (rawUrl == null) return;
        final String trimmed = rawUrl.trim();
        if (trimmed.isEmpty()) return;

        final String target;
        if (trimmed.contains("://")) {
            target = trimmed;
        } else if (trimmed.contains(".") && !trimmed.contains(" ")) {
            target = "https://" + trimmed;
        } else {
            target = "https://music.youtube.com/search?q=" + android.net.Uri.encode(trimmed);
        }

        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            mActiveSession.loadUri(target);
        });
    }

    public void goBack() {
        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            if (mActiveSession == mPopupSession && !mCanGoBack) {
                closePopupSessionLocked();
                return;
            }
            mActiveSession.goBack();
        });
    }

    public void goForward() {
        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            mActiveSession.goForward();
        });
    }

    public void reload() {
        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            mActiveSession.reload();
        });
    }

    private void closePopupSessionLocked() {
        if (mPopupSession == null) return;
        GeckoSession popup = mPopupSession;
        mPopupSession = null;
        if (mActiveSession == popup) {
            detachDisplayFromSession(popup);
            mActiveSession = mPrimarySession;
        }
        try {
            popup.close();
        } catch (Throwable ignored) {}
        synchronized (mStateLock) {
            if (mPrimarySession != null) {
                mCanGoBack = true;
            }
        }
        bindSurfaceToActiveSessionIfReady();
    }

    public void sendTouchEvent(int action, float xPx, float yPx) {
        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            long now = SystemClock.uptimeMillis();
            if (action == MotionEvent.ACTION_DOWN) {
                mTouchDownTimeMs = now;
            } else if (mTouchDownTimeMs <= 0L) {
                mTouchDownTimeMs = now;
            }
            MotionEvent ev = MotionEvent.obtain(mTouchDownTimeMs, now, action, xPx, yPx, 0);
            ev.setSource(InputDevice.SOURCE_TOUCHSCREEN);
            try {
                mActiveSession.getPanZoomController().onTouchEvent(ev);
            } catch (Throwable t) {
                Log.w(TAG, "onTouchEvent error: " + t.getMessage());
            } finally {
                ev.recycle();
            }
        });
    }

    public void sendScrollEvent(float xPx, float yPx, float vScroll, float hScroll) {
        mUiHandler.post(() -> {
            if (mShutdown || mActiveSession == null) return;
            long now = SystemClock.uptimeMillis();
            MotionEvent.PointerProperties[] props = new MotionEvent.PointerProperties[1];
            props[0] = new MotionEvent.PointerProperties();
            props[0].id = 0;
            props[0].toolType = MotionEvent.TOOL_TYPE_MOUSE;

            MotionEvent.PointerCoords[] coords = new MotionEvent.PointerCoords[1];
            coords[0] = new MotionEvent.PointerCoords();
            coords[0].x = xPx;
            coords[0].y = yPx;
            coords[0].setAxisValue(MotionEvent.AXIS_VSCROLL, vScroll);
            coords[0].setAxisValue(MotionEvent.AXIS_HSCROLL, hScroll);

            MotionEvent ev = MotionEvent.obtain(
                    now,
                    now,
                    MotionEvent.ACTION_SCROLL,
                    1,
                    props,
                    coords,
                    0,
                    0,
                    1.0f,
                    1.0f,
                    0,
                    0,
                    InputDevice.SOURCE_CLASS_POINTER,
                    0
            );
            try {
                mActiveSession.getPanZoomController().onMotionEvent(ev);
            } catch (Throwable t) {
                Log.w(TAG, "onMotionEvent scroll error: " + t.getMessage());
            } finally {
                ev.recycle();
            }
        });
    }

    public void togglePlayPause() {
        postCommandToExtension("playPause", null, null, false);
    }

    public void pauseAudio() {
        postCommandToExtension("pause", null, null, false);
    }

    public void nextTrack() {
        postCommandToExtension("next", null, null, false);
    }

    public void prevTrack() {
        postCommandToExtension("prev", null, null, false);
    }

    public void setVolume(float volume01) {
        float clamped = Math.max(0f, Math.min(1f, volume01));
        synchronized (mStateLock) {
            mVolume = clamped;
        }
        postCommandToExtension("setVolume", null, clamped, false);
    }

    public void requestActiveInputFocus() {
        postCommandToExtension("requestActiveValue", null, null, false);
    }

    public void sendEnterKey() {
        mUiHandler.post(() -> {
            if (mShutdown) return;
            if (mPort != null) {
                postCommandOnUi("keyEnter", null, null, true);
            } else if (mActiveSession != null) {
                KeyEvent down = new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER);
                KeyEvent up = new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER);
                mActiveSession.getTextInput().onKeyDown(KeyEvent.KEYCODE_ENTER, down);
                mActiveSession.getTextInput().onKeyUp(KeyEvent.KEYCODE_ENTER, up);
            }
        });
    }

    public void sendTextToPage(@Nullable String text, boolean pressEnter) {
        final String safeText = text != null ? text : "";
        synchronized (mStateLock) {
            mSoftInputInitialText = safeText;
        }
        mUiHandler.post(() -> {
            if (mShutdown) return;
            if (mPort != null) {
                postCommandOnUi("commitText", safeText, null, pressEnter);
            } else if (mActiveSession != null && pressEnter) {
                KeyEvent down = new KeyEvent(KeyEvent.ACTION_DOWN, KeyEvent.KEYCODE_ENTER);
                KeyEvent up = new KeyEvent(KeyEvent.ACTION_UP, KeyEvent.KEYCODE_ENTER);
                mActiveSession.getTextInput().onKeyDown(KeyEvent.KEYCODE_ENTER, down);
                mActiveSession.getTextInput().onKeyUp(KeyEvent.KEYCODE_ENTER, up);
            }
        });
    }

    private void postCommandToExtension(@NonNull String cmd, @Nullable String text, @Nullable Float floatVal, boolean boolVal) {
        mUiHandler.post(() -> postCommandOnUi(cmd, text, floatVal, boolVal));
    }

    private void postCommandOnUi(@NonNull String cmd, @Nullable String text, @Nullable Float floatVal, boolean boolVal) {
        if (mPort == null) return;
        try {
            JSONObject obj = new JSONObject();
            obj.put("cmd", cmd);
            if (text != null) obj.put("text", text);
            if (floatVal != null) obj.put("value", floatVal.doubleValue());
            obj.put("enter", boolVal);
            mPort.postMessage(obj);
        } catch (Throwable t) {
            Log.w(TAG, "postCommand failed for " + cmd + ": " + t.getMessage());
        }
    }

    public int getFftSequence() {
        synchronized (mStateLock) {
            return mFftSeq;
        }
    }

    public boolean isFftPlaying() {
        synchronized (mStateLock) {
            return mIsPlaying;
        }
    }

    public byte[] getLatestSpectrumBytes() {
        byte[] copy = new byte[512];
        synchronized (mStateLock) {
            System.arraycopy(mSpectrumBytes, 0, copy, 0, 512);
        }
        return copy;
    }

    public String getStatusJson() {
        synchronized (mStateLock) {
            try {
                JSONObject obj = new JSONObject();
                obj.put("url", mCurrentUrl);
                String title = (mMediaTitle != null && !mMediaTitle.isEmpty()) ? mMediaTitle : mPageTitle;
                obj.put("title", title != null ? title : "");
                obj.put("artist", mMediaArtist != null ? mMediaArtist : "");
                obj.put("artworkUrl", mArtworkUrl != null ? mArtworkUrl : "");
                obj.put("playing", mIsPlaying);
                obj.put("canGoBack", mCanGoBack || (mActiveSession == mPopupSession));
                obj.put("canGoForward", mCanGoForward);
                obj.put("drmSilent", mDrmSilent);
                obj.put("loading", mIsLoading);
                obj.put("hasSurface", mSurfaceBound);
                obj.put("extReady", mExtReady);
                obj.put("portReady", mPort != null);
                obj.put("volume", mVolume);
                obj.put("currentTime", mCurrentTime);
                obj.put("duration", mDuration);
                obj.put("softInputSeq", mSoftInputSeq);
                obj.put("softInputValSeq", mSoftInputValSeq);
                obj.put("softInputPass", mSoftInputIsPassword);
                obj.put("softInputVal", mSoftInputInitialText != null ? mSoftInputInitialText : "");
                return obj.toString();
            } catch (Throwable t) {
                return "{}";
            }
        }
    }

    public void shutdown() {
        mUiHandler.post(() -> {
            if (mShutdown) return;
            mShutdown = true;
            closePopupSessionLocked();
            detachDisplayFromSession(mPrimarySession);
            if (mPrimarySession != null) {
                try {
                    mPrimarySession.setActive(false);
                    mPrimarySession.close();
                } catch (Throwable ignored) {}
                mPrimarySession = null;
            }
            mActiveSession = null;
            mPort = null;
            mSurface = null;
        });
    }

    private final WebExtension.PortDelegate mPortDelegate = new WebExtension.PortDelegate() {
        @Override
        public void onPortMessage(@NonNull Object message, @NonNull WebExtension.Port port) {
            if (!(message instanceof JSONObject)) return;
            JSONObject obj = (JSONObject) message;
            String type = obj.optString("type", "");

            if ("fft".equals(type)) {
                boolean playing = obj.optBoolean("playing", false);
                boolean drmSilent = obj.optBoolean("drmSilent", false);
                double curTime = obj.optDouble("time", 0.0);
                double dur = obj.optDouble("duration", 0.0);
                int peak = obj.optInt("peak", 0);
                String b64 = obj.optString("b64", "");

                byte[] decoded = null;
                if (playing && !b64.isEmpty()) {
                    try {
                        decoded = Base64.decode(b64, Base64.DEFAULT);
                    } catch (IllegalArgumentException ignored) {
                        decoded = null;
                    }
                }

                synchronized (mStateLock) {
                    mIsPlaying = playing;
                    mDrmSilent = drmSilent;
                    mCurrentTime = curTime;
                    mDuration = dur;
                    if (playing && decoded != null && decoded.length == 512) {
                        System.arraycopy(decoded, 0, mSpectrumBytes, 0, 512);
                    } else if (!playing) {
                        java.util.Arrays.fill(mSpectrumBytes, (byte) 0);
                    }
                    mFftSeq++;
                }

                if (playing && peak > 0 && !mLoggedFftActive) {
                    mLoggedFftActive = true;
                    Log.i(TAG, "FFT active (peak=" + peak + ")");
                }
                if (drmSilent && !mLoggedDrmSilent) {
                    mLoggedDrmSilent = true;
                    Log.w(TAG, "drmSilent=true detected (EME/Widevine silenced Web Audio AnalyserNode)");
                }
            } else if ("meta".equals(type)) {
                synchronized (mStateLock) {
                    mMediaTitle = obj.optString("title", "");
                    mMediaArtist = obj.optString("artist", "");
                    mArtworkUrl = obj.optString("artworkUrl", "");
                    mIsPlaying = obj.optBoolean("playing", mIsPlaying);
                    mVolume = (float) obj.optDouble("volume", mVolume);
                }
            } else if ("inputFocus".equals(type)) {
                boolean focused = obj.optBoolean("focused", true);
                boolean open = obj.optBoolean("open", true);
                synchronized (mStateLock) {
                    if (!focused) {
                        mSoftInputInitialText = "";
                        mSoftInputIsPassword = false;
                    } else {
                        mSoftInputIsPassword = obj.optBoolean("password", false);
                        mSoftInputInitialText = obj.optString("value", "");
                        mSoftInputValSeq++;
                        if (open) {
                            mSoftInputSeq++;
                        }
                    }
                }
            } else if ("inputSync".equals(type)) {
                synchronized (mStateLock) {
                    mSoftInputIsPassword = obj.optBoolean("password", false);
                    mSoftInputInitialText = obj.optString("value", "");
                    mSoftInputValSeq++;
                }
            }
        }

        @Override
        public void onDisconnect(@NonNull WebExtension.Port port) {
            if (mPort == port) {
                mPort = null;
            }
        }
    };

    private final GeckoSession.PermissionDelegate mPermissionDelegate = new GeckoSession.PermissionDelegate() {
        @Override
        public void onAndroidPermissionsRequest(
                @NonNull GeckoSession session,
                @Nullable String[] permissions,
                @NonNull Callback callback) {
            callback.reject();
        }

        @Nullable
        @Override
        public GeckoResult<Integer> onContentPermissionRequest(
                @NonNull GeckoSession session,
                @NonNull ContentPermission perm) {
            switch (perm.permission) {
                case PERMISSION_AUTOPLAY_AUDIBLE:
                case PERMISSION_AUTOPLAY_INAUDIBLE:
                case PERMISSION_MEDIA_KEY_SYSTEM_ACCESS:
                case PERMISSION_PERSISTENT_STORAGE:
                    return GeckoResult.fromValue(ContentPermission.VALUE_ALLOW);
                default:
                    return GeckoResult.fromValue(ContentPermission.VALUE_DENY);
            }
        }

        @Override
        public void onMediaPermissionRequest(
                @NonNull GeckoSession session,
                @NonNull String uri,
                @Nullable MediaSource[] video,
                @Nullable MediaSource[] audio,
                @NonNull MediaCallback callback) {
            callback.reject();
        }
    };

    private final GeckoSession.NavigationDelegate mNavigationDelegate = new GeckoSession.NavigationDelegate() {
        @Override
        public void onLocationChange(
                @NonNull GeckoSession session,
                @Nullable String url,
                @NonNull List<GeckoSession.PermissionDelegate.ContentPermission> perms,
                @NonNull Boolean hasUserGesture) {
            if (session == mActiveSession && url != null && !url.isEmpty()) {
                synchronized (mStateLock) {
                    mCurrentUrl = url;
                }
            }
        }

        @Override
        public void onCanGoBack(@NonNull GeckoSession session, boolean canGoBack) {
            if (session == mActiveSession) {
                synchronized (mStateLock) {
                    mCanGoBack = canGoBack;
                }
            }
        }

        @Override
        public void onCanGoForward(@NonNull GeckoSession session, boolean canGoForward) {
            if (session == mActiveSession) {
                synchronized (mStateLock) {
                    mCanGoForward = canGoForward;
                }
            }
        }

        @Nullable
        @Override
        public GeckoResult<AllowOrDeny> onLoadRequest(
                @NonNull GeckoSession session,
                @NonNull LoadRequest request) {
            String uri = request.uri;
            if (uri == null) return GeckoResult.fromValue(AllowOrDeny.DENY);
            if (uri.startsWith("http://") ||
                    uri.startsWith("https://") ||
                    uri.startsWith("about:") ||
                    uri.startsWith("blob:") ||
                    uri.startsWith("data:") ||
                    uri.startsWith("javascript:")) {
                return GeckoResult.fromValue(AllowOrDeny.ALLOW);
            }
            return GeckoResult.fromValue(AllowOrDeny.DENY);
        }

        @Nullable
        @Override
        public GeckoResult<GeckoSession> onNewSession(
                @NonNull GeckoSession session,
                @NonNull String uri) {
            if (mShutdown || sRuntime == null) return null;
            if (mPopupSession != null) {
                closePopupSessionLocked();
            }
            GeckoSession popup = createConfiguredSession();
            popup.open(sRuntime);
            mPopupSession = popup;
            switchActiveSession(popup);
            return GeckoResult.fromValue(popup);
        }
    };

    private final GeckoSession.ProgressDelegate mProgressDelegate = new GeckoSession.ProgressDelegate() {
        @Override
        public void onPageStart(@NonNull GeckoSession session, @NonNull String url) {
            if (session == mActiveSession) {
                synchronized (mStateLock) {
                    mIsLoading = true;
                    mSoftInputInitialText = "";
                    mSoftInputIsPassword = false;
                }
            }
        }

        @Override
        public void onPageStop(@NonNull GeckoSession session, boolean success) {
            if (session == mActiveSession) {
                synchronized (mStateLock) {
                    mIsLoading = false;
                }
            }
        }
    };

    private final GeckoSession.ContentDelegate mContentDelegate = new GeckoSession.ContentDelegate() {
        @Override
        public void onCloseRequest(@NonNull GeckoSession session) {
            if (session == mPopupSession) {
                closePopupSessionLocked();
            }
        }

        @Override
        public void onTitleChange(@NonNull GeckoSession session, @Nullable String title) {
            if (session == mActiveSession && title != null) {
                synchronized (mStateLock) {
                    mPageTitle = title;
                }
            }
        }
    };

    private final GeckoSession.TextInputDelegate mTextInputDelegate = new GeckoSession.TextInputDelegate() {
        @Override
        public void showSoftInput(@NonNull GeckoSession session) {
            if (session == mActiveSession) {
                postCommandOnUi("requestActiveValue", null, null, false);
                synchronized (mStateLock) {
                    mSoftInputSeq++;
                }
            }
        }
    };
}
