package com.dexter.audiofloor;

import android.app.Activity;
import android.graphics.SurfaceTexture;
import android.opengl.EGL14;
import android.opengl.EGLConfig;
import android.opengl.EGLContext;
import android.opengl.EGLDisplay;
import android.opengl.EGLSurface;
import android.opengl.GLES11Ext;
import android.opengl.GLES20;
import android.opengl.Matrix;
import android.os.Handler;
import android.os.HandlerThread;
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

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

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
    private RoundedSurfaceBlitter mBlitter;
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

    private void releaseBlitter() {
        if (mBlitter != null) {
            try {
                mBlitter.release();
            } catch (Throwable t) {
                Log.w(TAG, "RoundedSurfaceBlitter release warning: " + t.getMessage());
            }
            mBlitter = null;
        }
    }

    private void bindSurfaceToActiveSessionIfReady() {
        if (mShutdown || mActiveSession == null || mSurface == null || !mSurface.isValid()) {
            return;
        }
        try {
            if (mBlitter == null || !mBlitter.matches(mSurface, mSurfaceWidth, mSurfaceHeight)) {
                releaseBlitter();
                float radiusPx = 38.0f * (mSurfaceWidth / 960.0f);
                RoundedSurfaceBlitter candidate = new RoundedSurfaceBlitter(mSurface, mSurfaceWidth, mSurfaceHeight, radiusPx);
                if (candidate.isReady()) {
                    mBlitter = candidate;
                    Log.i(TAG, "RoundedSurfaceBlitter active (" + mSurfaceWidth + "x" + mSurfaceHeight + ", r=" + radiusPx + "px)");
                } else {
                    candidate.release();
                    Log.w(TAG, "RoundedSurfaceBlitter failed to initialize; falling back to direct Surface");
                }
            }

            Surface targetSurface = (mBlitter != null && mBlitter.isReady())
                    ? mBlitter.getInputSurface()
                    : mSurface;
            if (targetSurface == null || !targetSurface.isValid()) {
                targetSurface = mSurface;
            }

            if (mDisplay == null) {
                mDisplay = mActiveSession.acquireDisplay();
            }
            mDisplay.surfaceChanged(
                    new GeckoDisplay.SurfaceInfo.Builder(targetSurface)
                            .size(mSurfaceWidth, mSurfaceHeight)
                            .build()
            );
            mSurfaceBound = true;
            mActiveSession.setActive(true);
            Log.i(TAG, "attachSurface bound to GeckoDisplay (" + mSurfaceWidth + "x" + mSurfaceHeight + ", rounded=" + (mBlitter != null) + ")");
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
            int nextW = width > 0 ? width : mSurfaceWidth;
            int nextH = height > 0 ? height : mSurfaceHeight;
            boolean needsRecreate = (surface != mSurface) || (nextW != mSurfaceWidth) || (nextH != mSurfaceHeight);

            if (surface == null) {
                detachDisplayFromSession(mActiveSession);
                releaseBlitter();
                mSurface = null;
                return;
            }

            if (needsRecreate && mBlitter != null) {
                detachDisplayFromSession(mActiveSession);
                releaseBlitter();
            }

            mSurface = surface;
            mSurfaceWidth = nextW;
            mSurfaceHeight = nextH;
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

    public void seekTo(float seconds) {
        float clamped = Math.max(0f, seconds);
        synchronized (mStateLock) {
            if (mDuration > 0.0 && clamped > mDuration) {
                clamped = (float) mDuration;
            }
            mCurrentTime = clamped;
        }
        postCommandToExtension("seek", null, clamped, false);
    }

    public void setSpatialAudio(boolean active, float lx, float ly, float lz, float rx, float ry, float rz, float refDist) {
        mUiHandler.post(() -> {
            if (mPort == null || mShutdown) return;
            try {
                JSONObject obj = new JSONObject();
                obj.put("cmd", "spatialPose");
                obj.put("active", active);
                obj.put("lx", lx);
                obj.put("ly", ly);
                obj.put("lz", lz);
                obj.put("rx", rx);
                obj.put("ry", ry);
                obj.put("rz", rz);
                obj.put("refDist", refDist);
                mPort.postMessage(obj);
            } catch (Throwable t) {
                Log.w(TAG, "setSpatialAudio post failed: " + t.getMessage());
            }
        });
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
            releaseBlitter();
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
                    if (obj.has("duration")) {
                        double dur = obj.optDouble("duration", -1.0);
                        if (dur >= 0.0) {
                            mDuration = dur;
                        }
                    }
                    if (obj.has("time")) {
                        double cur = obj.optDouble("time", -1.0);
                        if (cur >= 0.0) {
                            mCurrentTime = cur;
                        }
                    }
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

    /**
     * Lightweight on-demand GLES 2.0 SurfaceTexture -> EGL14 rounded-corner blitter.
     * Applies per-pixel premultiplied rounded-rect alpha masking to GeckoView's output frames
     * before presenting them to OVROverlay's external Surface, so frameless 960x564 (r=38px)
     * browser corners blend cleanly in both AR passthrough and VR.
     */
    private static final class RoundedSurfaceBlitter {
        private static final String VERT_SHADER =
                "attribute vec2 aPos;\n" +
                "attribute vec2 aUv;\n" +
                "uniform mat4 uStMatrix;\n" +
                "varying vec2 vQuadUv;\n" +
                "varying vec2 vTexUv;\n" +
                "void main() {\n" +
                "    gl_Position = vec4(aPos, 0.0, 1.0);\n" +
                "    vQuadUv = aUv;\n" +
                "    vTexUv = (uStMatrix * vec4(aUv, 0.0, 1.0)).xy;\n" +
                "}\n";

        private static final String FRAG_SHADER =
                "#extension GL_OES_EGL_image_external : require\n" +
                "precision mediump float;\n" +
                "varying vec2 vQuadUv;\n" +
                "varying vec2 vTexUv;\n" +
                "uniform samplerExternalOES uTex;\n" +
                "uniform vec2 uSize;\n" +
                "uniform float uRadius;\n" +
                "void main() {\n" +
                "    vec2 halfSize = uSize * 0.5;\n" +
                "    vec2 localPx = (vQuadUv - 0.5) * uSize;\n" +
                "    vec2 q = abs(localPx) - (halfSize - 0.5) + uRadius;\n" +
                "    float dist = min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - uRadius;\n" +
                "    float alpha = 1.0 - smoothstep(-0.75, 0.75, dist);\n" +
                "    vec4 c = texture2D(uTex, vTexUv);\n" +
                "    gl_FragColor = vec4(c.rgb * alpha, alpha);\n" +
                "}\n";

        private static final float[] QUAD_VERTS = {
                -1.0f, -1.0f, 0.0f, 0.0f,
                 1.0f, -1.0f, 1.0f, 0.0f,
                -1.0f,  1.0f, 0.0f, 1.0f,
                 1.0f,  1.0f, 1.0f, 1.0f
        };

        private final Surface mOutputSurface;
        private final int mWidth;
        private final int mHeight;
        private final float mRadiusPx;
        private final HandlerThread mThread;
        private final Handler mHandler;
        private final FloatBuffer mQuadBuffer;
        private final float[] mStMatrix = new float[16];

        private EGLDisplay mEglDisplay = EGL14.EGL_NO_DISPLAY;
        private EGLContext mEglContext = EGL14.EGL_NO_CONTEXT;
        private EGLSurface mEglSurface = EGL14.EGL_NO_SURFACE;
        private int mProgram = 0;
        private int mOesTexId = 0;
        private int mAttrPos = -1;
        private int mAttrUv = -1;
        private int mUniStMatrix = -1;
        private int mUniTex = -1;
        private int mUniSize = -1;
        private int mUniRadius = -1;

        private SurfaceTexture mSurfaceTexture;
        private Surface mInputSurface;
        private volatile boolean mReady = false;
        private volatile boolean mReleased = false;

        RoundedSurfaceBlitter(@NonNull Surface outputSurface, int width, int height, float radiusPx) {
            mOutputSurface = outputSurface;
            mWidth = width;
            mHeight = height;
            mRadiusPx = radiusPx;

            ByteBuffer bb = ByteBuffer.allocateDirect(QUAD_VERTS.length * 4).order(ByteOrder.nativeOrder());
            mQuadBuffer = bb.asFloatBuffer();
            mQuadBuffer.put(QUAD_VERTS);
            mQuadBuffer.position(0);
            Matrix.setIdentityM(mStMatrix, 0);

            mThread = new HandlerThread("SonicBrowserBlit");
            mThread.start();
            mHandler = new Handler(mThread.getLooper());

            CountDownLatch initLatch = new CountDownLatch(1);
            mHandler.post(() -> {
                try {
                    initOnBlitThread();
                } catch (Throwable t) {
                    Log.e(TAG, "RoundedSurfaceBlitter init failed", t);
                    cleanupOnBlitThread();
                } finally {
                    initLatch.countDown();
                }
            });

            try {
                if (!initLatch.await(2000L, TimeUnit.MILLISECONDS)) {
                    Log.e(TAG, "RoundedSurfaceBlitter init timed out");
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            }
        }

        boolean isReady() {
            return mReady && !mReleased && mInputSurface != null && mInputSurface.isValid();
        }

        boolean matches(@Nullable Surface outputSurface, int width, int height) {
            return isReady() && mOutputSurface == outputSurface && mWidth == width && mHeight == height;
        }

        @Nullable
        Surface getInputSurface() {
            return mInputSurface;
        }

        private void initOnBlitThread() {
            mEglDisplay = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY);
            if (mEglDisplay == EGL14.EGL_NO_DISPLAY) {
                throw new RuntimeException("eglGetDisplay failed");
            }
            int[] version = new int[2];
            if (!EGL14.eglInitialize(mEglDisplay, version, 0, version, 1)) {
                throw new RuntimeException("eglInitialize failed");
            }

            int[] attribList = {
                    EGL14.EGL_RED_SIZE, 8,
                    EGL14.EGL_GREEN_SIZE, 8,
                    EGL14.EGL_BLUE_SIZE, 8,
                    EGL14.EGL_ALPHA_SIZE, 8,
                    EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
                    EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT,
                    EGL14.EGL_NONE
            };
            EGLConfig[] configs = new EGLConfig[1];
            int[] numConfigs = new int[1];
            if (!EGL14.eglChooseConfig(mEglDisplay, attribList, 0, configs, 0, 1, numConfigs, 0) || numConfigs[0] <= 0) {
                throw new RuntimeException("eglChooseConfig RGBA8888 GLES2 failed");
            }

            int[] ctxAttribs = {
                    EGL14.EGL_CONTEXT_CLIENT_VERSION, 2,
                    EGL14.EGL_NONE
            };
            mEglContext = EGL14.eglCreateContext(mEglDisplay, configs[0], EGL14.EGL_NO_CONTEXT, ctxAttribs, 0);
            if (mEglContext == null || mEglContext == EGL14.EGL_NO_CONTEXT) {
                throw new RuntimeException("eglCreateContext failed");
            }

            int[] surfAttribs = { EGL14.EGL_NONE };
            mEglSurface = EGL14.eglCreateWindowSurface(mEglDisplay, configs[0], mOutputSurface, surfAttribs, 0);
            if (mEglSurface == null || mEglSurface == EGL14.EGL_NO_SURFACE) {
                throw new RuntimeException("eglCreateWindowSurface failed");
            }

            if (!EGL14.eglMakeCurrent(mEglDisplay, mEglSurface, mEglSurface, mEglContext)) {
                throw new RuntimeException("eglMakeCurrent failed");
            }

            mProgram = buildProgram(VERT_SHADER, FRAG_SHADER);
            mAttrPos = GLES20.glGetAttribLocation(mProgram, "aPos");
            mAttrUv = GLES20.glGetAttribLocation(mProgram, "aUv");
            mUniStMatrix = GLES20.glGetUniformLocation(mProgram, "uStMatrix");
            mUniTex = GLES20.glGetUniformLocation(mProgram, "uTex");
            mUniSize = GLES20.glGetUniformLocation(mProgram, "uSize");
            mUniRadius = GLES20.glGetUniformLocation(mProgram, "uRadius");

            int[] texIds = new int[1];
            GLES20.glGenTextures(1, texIds, 0);
            mOesTexId = texIds[0];
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, mOesTexId);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE);
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);

            // Clear output Surface to transparent black before first GeckoView frame arrives
            GLES20.glViewport(0, 0, mWidth, mHeight);
            GLES20.glDisable(GLES20.GL_BLEND);
            GLES20.glDisable(GLES20.GL_DEPTH_TEST);
            GLES20.glDisable(GLES20.GL_CULL_FACE);
            GLES20.glClearColor(0.0f, 0.0f, 0.0f, 0.0f);
            GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);
            EGL14.eglSwapBuffers(mEglDisplay, mEglSurface);

            mSurfaceTexture = new SurfaceTexture(mOesTexId);
            mSurfaceTexture.setDefaultBufferSize(mWidth, mHeight);
            mSurfaceTexture.setOnFrameAvailableListener(st -> drawLatestFrameOnBlitThread(), mHandler);
            mInputSurface = new Surface(mSurfaceTexture);
            mReady = true;
        }

        private void drawLatestFrameOnBlitThread() {
            if (mReleased || !mReady || mSurfaceTexture == null || mEglDisplay == EGL14.EGL_NO_DISPLAY) {
                return;
            }
            try {
                mSurfaceTexture.updateTexImage();
                mSurfaceTexture.getTransformMatrix(mStMatrix);

                GLES20.glViewport(0, 0, mWidth, mHeight);
                GLES20.glClearColor(0.0f, 0.0f, 0.0f, 0.0f);
                GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);

                GLES20.glUseProgram(mProgram);
                GLES20.glActiveTexture(GLES20.GL_TEXTURE0);
                GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, mOesTexId);
                GLES20.glUniform1i(mUniTex, 0);
                GLES20.glUniformMatrix4fv(mUniStMatrix, 1, false, mStMatrix, 0);
                GLES20.glUniform2f(mUniSize, (float) mWidth, (float) mHeight);
                GLES20.glUniform1f(mUniRadius, mRadiusPx);

                mQuadBuffer.position(0);
                GLES20.glEnableVertexAttribArray(mAttrPos);
                GLES20.glVertexAttribPointer(mAttrPos, 2, GLES20.GL_FLOAT, false, 16, mQuadBuffer);

                mQuadBuffer.position(2);
                GLES20.glEnableVertexAttribArray(mAttrUv);
                GLES20.glVertexAttribPointer(mAttrUv, 2, GLES20.GL_FLOAT, false, 16, mQuadBuffer);

                GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4);

                GLES20.glDisableVertexAttribArray(mAttrPos);
                GLES20.glDisableVertexAttribArray(mAttrUv);

                EGL14.eglSwapBuffers(mEglDisplay, mEglSurface);
            } catch (Throwable t) {
                Log.w(TAG, "RoundedSurfaceBlitter frame blit error: " + t.getMessage());
            }
        }

        void release() {
            if (mReleased) return;
            mReleased = true;
            mReady = false;

            CountDownLatch releaseLatch = new CountDownLatch(1);
            boolean posted = mHandler.post(() -> {
                try {
                    cleanupOnBlitThread();
                } finally {
                    releaseLatch.countDown();
                }
            });
            if (posted) {
                try {
                    releaseLatch.await(1000L, TimeUnit.MILLISECONDS);
                } catch (InterruptedException ignored) {
                    Thread.currentThread().interrupt();
                }
            }
            mThread.quitSafely();
        }

        private void cleanupOnBlitThread() {
            if (mSurfaceTexture != null) {
                try { mSurfaceTexture.setOnFrameAvailableListener(null); } catch (Throwable ignored) {}
            }
            if (mInputSurface != null) {
                try { mInputSurface.release(); } catch (Throwable ignored) {}
                mInputSurface = null;
            }
            if (mSurfaceTexture != null) {
                try { mSurfaceTexture.release(); } catch (Throwable ignored) {}
                mSurfaceTexture = null;
            }
            if (mOesTexId != 0 && mEglContext != EGL14.EGL_NO_CONTEXT) {
                try { GLES20.glDeleteTextures(1, new int[]{ mOesTexId }, 0); } catch (Throwable ignored) {}
                mOesTexId = 0;
            }
            if (mProgram != 0 && mEglContext != EGL14.EGL_NO_CONTEXT) {
                try { GLES20.glDeleteProgram(mProgram); } catch (Throwable ignored) {}
                mProgram = 0;
            }
            if (mEglDisplay != EGL14.EGL_NO_DISPLAY) {
                try {
                    EGL14.eglMakeCurrent(mEglDisplay, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT);
                } catch (Throwable ignored) {}
                if (mEglSurface != EGL14.EGL_NO_SURFACE) {
                    try { EGL14.eglDestroySurface(mEglDisplay, mEglSurface); } catch (Throwable ignored) {}
                    mEglSurface = EGL14.EGL_NO_SURFACE;
                }
                if (mEglContext != EGL14.EGL_NO_CONTEXT) {
                    try { EGL14.eglDestroyContext(mEglDisplay, mEglContext); } catch (Throwable ignored) {}
                    mEglContext = EGL14.EGL_NO_CONTEXT;
                }
                try { EGL14.eglTerminate(mEglDisplay); } catch (Throwable ignored) {}
                mEglDisplay = EGL14.EGL_NO_DISPLAY;
            }
        }

        private static int compileShader(int type, String source) {
            int shader = GLES20.glCreateShader(type);
            GLES20.glShaderSource(shader, source);
            GLES20.glCompileShader(shader);
            int[] status = new int[1];
            GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, status, 0);
            if (status[0] == 0) {
                String info = GLES20.glGetShaderInfoLog(shader);
                GLES20.glDeleteShader(shader);
                throw new RuntimeException("Shader compile failed (" + type + "): " + info);
            }
            return shader;
        }

        private static int buildProgram(String vertSrc, String fragSrc) {
            int vs = compileShader(GLES20.GL_VERTEX_SHADER, vertSrc);
            int fs = compileShader(GLES20.GL_FRAGMENT_SHADER, fragSrc);
            int prog = GLES20.glCreateProgram();
            GLES20.glAttachShader(prog, vs);
            GLES20.glAttachShader(prog, fs);
            GLES20.glLinkProgram(prog);
            int[] status = new int[1];
            GLES20.glGetProgramiv(prog, GLES20.GL_LINK_STATUS, status, 0);
            GLES20.glDeleteShader(vs);
            GLES20.glDeleteShader(fs);
            if (status[0] == 0) {
                String info = GLES20.glGetProgramInfoLog(prog);
                GLES20.glDeleteProgram(prog);
                throw new RuntimeException("Program link failed: " + info);
            }
            return prog;
        }
    }
}
