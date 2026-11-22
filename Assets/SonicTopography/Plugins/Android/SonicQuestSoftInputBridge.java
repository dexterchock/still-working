package com.dexter.audiofloor;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.method.PasswordTransformationMethod;
import android.text.method.SingleLineTransformationMethod;
import android.util.Log;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;

public class SonicQuestSoftInputBridge {
    private static final String TAG = "SonicSoftInput";

    private final Activity mActivity;
    private final Handler mUiHandler;
    private final InputMethodManager mImm;
    private final Object mStateLock = new Object();

    private SonicHiddenEditText mEditText;
    private View mPreviousFocusView;

    private int mSessionId = 0;
    private int mUiSessionId = 0;
    private int mTextSeq = 0;
    private String mText = "";
    private boolean mActive = false;
    private boolean mSubmitPressed = false;
    private int mSubmittedSessionId = 0;

    private boolean mSuppressWatcher = false;
    private boolean mGotInputConnectionForSession = false;
    private boolean mLostWindowFocusSinceOpen = false;
    private boolean mImeInsetsSeenVisible = false;
    private boolean mLastLoggedImeVisible = false;
    private long mOpenTimeMs = 0L;
    private boolean mShutdown = false;

    private Runnable mRetryRunnable150;
    private Runnable mRetryRunnable350;

    private class SonicHiddenEditText extends EditText {
        public SonicHiddenEditText(Context context) {
            super(context);
        }

        @Override
        public boolean onTouchEvent(MotionEvent event) {
            return false;
        }

        @Override
        public InputConnection onCreateInputConnection(EditorInfo outAttrs) {
            InputConnection base = super.onCreateInputConnection(outAttrs);
            if (outAttrs != null) {
                outAttrs.imeOptions &= ~EditorInfo.IME_FLAG_NO_EXTRACT_UI;
                outAttrs.imeOptions |= EditorInfo.IME_FLAG_NO_FULLSCREEN;
            }
            mGotInputConnectionForSession = (base != null);
            return base;
        }

        @Override
        public boolean onKeyPreIme(int keyCode, KeyEvent event) {
            if (event != null && keyCode == KeyEvent.KEYCODE_BACK) {
                if (event.getAction() == KeyEvent.ACTION_UP) {
                    finishSessionOnUi(mUiSessionId, false, "KeyBack");
                }
                return true;
            }
            if (event != null && (keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER)) {
                if (event.getAction() == KeyEvent.ACTION_UP) {
                    finishSessionOnUi(mUiSessionId, true, "KeyEnter");
                }
                return true;
            }
            return super.onKeyPreIme(keyCode, event);
        }

        @Override
        public void onWindowFocusChanged(boolean hasWindowFocus) {
            super.onWindowFocusChanged(hasWindowFocus);
            if (!hasWindowFocus) {
                mLostWindowFocusSinceOpen = true;
            }
        }
    }

    public SonicQuestSoftInputBridge(Activity activity) {
        mActivity = activity;
        mUiHandler = new Handler(Looper.getMainLooper());
        mImm = activity != null
                ? (InputMethodManager) activity.getSystemService(Context.INPUT_METHOD_SERVICE)
                : null;
    }

    public int open(String initialText, String hint, boolean isPassword, boolean isUrl) {
        final String safeInitial = initialText != null ? initialText : "";
        final String safeHint = hint != null ? hint : "";
        final int sid;

        synchronized (mStateLock) {
            if (mShutdown || mActivity == null) return 0;
            mSessionId++;
            sid = mSessionId;
            mText = safeInitial;
            mTextSeq++;
            mActive = true;
            mSubmitPressed = false;
            mSubmittedSessionId = 0;
        }

        Log.i(TAG, "open sid=" + sid + " len=" + safeInitial.length() + " pass=" + isPassword + " url=" + isUrl);
        mUiHandler.post(() -> openOnUi(sid, safeInitial, safeHint, isPassword, isUrl));
        return sid;
    }

    public void setText(final int sessionId, final String text) {
        final String safe = text != null ? text : "";
        synchronized (mStateLock) {
            if (mShutdown || !mActive || (sessionId > 0 && sessionId != mSessionId)) {
                return;
            }
            if (!TextUtils.equals(mText, safe)) {
                mText = safe;
                mTextSeq++;
            }
        }
        mUiHandler.post(() -> setTextOnUi(sessionId, safe));
    }

    public void close(final int sessionId) {
        final int targetSid;
        synchronized (mStateLock) {
            if (!mActive) return;
            targetSid = sessionId > 0 ? sessionId : mSessionId;
            if (targetSid != mSessionId) return;
        }
        Log.i(TAG, "close requested sid=" + targetSid);
        mUiHandler.post(() -> finishSessionOnUi(targetSid, false, "ApiClose"));
    }

    public boolean isActive() {
        synchronized (mStateLock) {
            return mActive;
        }
    }

    public boolean isSessionActive(int sessionId) {
        synchronized (mStateLock) {
            return mActive && sessionId > 0 && mSessionId == sessionId;
        }
    }

    public int getActiveSessionId() {
        synchronized (mStateLock) {
            return mActive ? mSessionId : 0;
        }
    }

    public String getText() {
        synchronized (mStateLock) {
            return mText != null ? mText : "";
        }
    }

    public int getTextSeq() {
        synchronized (mStateLock) {
            return mTextSeq;
        }
    }

    public boolean wasSubmitPressed(int sessionId) {
        synchronized (mStateLock) {
            return mSubmitPressed && (sessionId <= 0 || mSubmittedSessionId == sessionId);
        }
    }

    public void shutdown() {
        synchronized (mStateLock) {
            if (mShutdown) return;
            mShutdown = true;
            mActive = false;
        }
        mUiHandler.post(() -> {
            cancelRetriesOnUi();
            if (mImm != null && mEditText != null) {
                try {
                    mImm.hideSoftInputFromWindow(mEditText.getWindowToken(), 0);
                } catch (Throwable ignored) {}
            }
            if (mEditText != null) {
                try {
                    ViewGroup parent = (ViewGroup) mEditText.getParent();
                    if (parent != null) {
                        parent.removeView(mEditText);
                    }
                } catch (Throwable ignored) {}
                mEditText = null;
            }
            mPreviousFocusView = null;
        });
    }

    private void ensureEditTextOnUi() {
        if (mEditText != null || mActivity == null || mShutdown) return;

        View root = mActivity.findViewById(android.R.id.content);
        if (!(root instanceof ViewGroup) && mActivity.getWindow() != null) {
            root = mActivity.getWindow().getDecorView().findViewById(android.R.id.content);
        }
        if (!(root instanceof ViewGroup)) {
            Log.e(TAG, "Failed locating android.R.id.content ViewGroup");
            return;
        }

        ViewGroup content = (ViewGroup) root;
        mEditText = new SonicHiddenEditText(mActivity);

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(400, 64);
        lp.leftMargin = 0;
        lp.topMargin = 0;
        mEditText.setLayoutParams(lp);
        mEditText.setAlpha(0f);
        mEditText.setBackgroundColor(Color.TRANSPARENT);
        mEditText.setTextColor(Color.TRANSPARENT);
        mEditText.setHintTextColor(Color.TRANSPARENT);
        mEditText.setHighlightColor(Color.TRANSPARENT);
        mEditText.setCursorVisible(false);
        mEditText.setClickable(false);
        mEditText.setLongClickable(false);
        mEditText.setFocusable(false);
        mEditText.setFocusableInTouchMode(false);
        mEditText.setSingleLine(true);
        mEditText.setMaxLines(1);
        mEditText.setPadding(0, 0, 0, 0);
        mEditText.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        mEditText.setVisibility(View.GONE);

        mEditText.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {}

            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {}

            @Override
            public void afterTextChanged(Editable s) {
                if (mSuppressWatcher) return;
                String next = s != null ? s.toString() : "";
                synchronized (mStateLock) {
                    if (!mActive || mUiSessionId != mSessionId) return;
                    if (!TextUtils.equals(mText, next)) {
                        mText = next;
                        mTextSeq++;
                    }
                }
            }
        });

        mEditText.setOnEditorActionListener((v, actionId, event) -> {
            if (actionId == EditorInfo.IME_ACTION_DONE
                    || actionId == EditorInfo.IME_ACTION_GO
                    || actionId == EditorInfo.IME_ACTION_SEARCH
                    || actionId == EditorInfo.IME_ACTION_NEXT
                    || actionId == EditorInfo.IME_ACTION_SEND) {
                Log.i(TAG, "onEditorAction sid=" + mUiSessionId + " actionId=" + actionId);
                finishSessionOnUi(mUiSessionId, true, "EditorAction:" + actionId);
                return true;
            }
            if (actionId == EditorInfo.IME_NULL && event != null
                    && event.getKeyCode() == KeyEvent.KEYCODE_ENTER
                    && event.getAction() == KeyEvent.ACTION_DOWN) {
                Log.i(TAG, "onEditorAction IME_NULL enter sid=" + mUiSessionId);
                finishSessionOnUi(mUiSessionId, true, "EditorActionEnter");
                return true;
            }
            return false;
        });

        mEditText.setOnApplyWindowInsetsListener((v, insets) -> {
            boolean imeVisible = insets != null && insets.isVisible(WindowInsets.Type.ime());
            if (imeVisible != mLastLoggedImeVisible) {
                mLastLoggedImeVisible = imeVisible;
                Log.i(TAG, "onApplyWindowInsets sid=" + mUiSessionId + " imeVisible=" + imeVisible
                        + " active=" + mActive);
            }
            if (imeVisible) {
                mImeInsetsSeenVisible = true;
            } else if (mImeInsetsSeenVisible && mActive && mUiSessionId == mSessionId
                    && (SystemClock.uptimeMillis() - mOpenTimeMs > 300L)) {
                finishSessionOnUi(mUiSessionId, false, "ImeInsetsHidden");
            }
            return v.onApplyWindowInsets(insets);
        });

        content.addView(mEditText);
        Log.i(TAG, "Added SonicHiddenEditText to android.R.id.content");
    }

    private void openOnUi(final int sid, String initialText, String hint, boolean isPassword, boolean isUrl) {
        if (mShutdown || mActivity == null) return;
        synchronized (mStateLock) {
            if (!mActive || sid != mSessionId) return;
        }

        cancelRetriesOnUi();
        ensureEditTextOnUi();
        if (mEditText == null) {
            synchronized (mStateLock) {
                if (sid == mSessionId) {
                    mActive = false;
                }
            }
            return;
        }

        View curFocus = mActivity.getCurrentFocus();
        if (curFocus != null && curFocus != mEditText) {
            mPreviousFocusView = curFocus;
        }

        mUiSessionId = sid;
        mGotInputConnectionForSession = false;
        mLostWindowFocusSinceOpen = false;
        mImeInsetsSeenVisible = false;
        mLastLoggedImeVisible = false;
        mOpenTimeMs = SystemClock.uptimeMillis();

        int inputType = InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        if (isPassword) {
            inputType |= InputType.TYPE_TEXT_VARIATION_PASSWORD;
        } else if (isUrl) {
            inputType |= InputType.TYPE_TEXT_VARIATION_URI;
        }

        mSuppressWatcher = true;
        try {
            mEditText.setText(initialText);
            mEditText.setInputType(inputType);
            mEditText.setTransformationMethod(isPassword
                    ? PasswordTransformationMethod.getInstance()
                    : SingleLineTransformationMethod.getInstance());
            mEditText.setImeOptions(EditorInfo.IME_ACTION_DONE | EditorInfo.IME_FLAG_NO_FULLSCREEN);
            mEditText.setHint(hint);
            Editable ed = mEditText.getText();
            mEditText.setSelection(ed != null ? ed.length() : 0);
        } finally {
            mSuppressWatcher = false;
        }

        if (mEditText.getVisibility() != View.VISIBLE) {
            mEditText.setVisibility(View.VISIBLE);
        }
        mEditText.bringToFront();
        mEditText.setFocusable(true);
        mEditText.setFocusableInTouchMode(true);

        boolean hadFocus = mEditText.hasFocus();
        boolean reqFocus = mEditText.requestFocus();
        boolean shown = false;
        if (mImm != null) {
            if (hadFocus) {
                mImm.restartInput(mEditText);
            }
            shown = mImm.showSoftInput(mEditText, 0);
        }

        Log.i(TAG, "openOnUi sid=" + sid + " reqFocus=" + reqFocus + " hadFocus=" + hadFocus
                + " showSoftInput=" + shown);

        mRetryRunnable150 = () -> retryShowOnUi(sid, 150);
        mRetryRunnable350 = () -> retryShowOnUi(sid, 350);
        mUiHandler.postDelayed(mRetryRunnable150, 150L);
        mUiHandler.postDelayed(mRetryRunnable350, 350L);
    }

    private void retryShowOnUi(int sid, int delayMs) {
        if (mShutdown || mEditText == null || mUiSessionId != sid || !mActive) return;
        if (mGotInputConnectionForSession && (mLastLoggedImeVisible || mLostWindowFocusSinceOpen)) return;

        mImeInsetsSeenVisible = false;
        mOpenTimeMs = SystemClock.uptimeMillis();
        mEditText.setFocusable(true);
        mEditText.setFocusableInTouchMode(true);
        boolean reqFocus = mEditText.requestFocus();
        boolean shown = false;
        if (mImm != null) {
            mImm.restartInput(mEditText);
            shown = mImm.showSoftInput(mEditText, 0);
        }
        Log.i(TAG, "retryShowOnUi sid=" + sid + " delay=" + delayMs
                + " reqFocus=" + reqFocus + " showSoftInput=" + shown);
    }

    private void setTextOnUi(int sid, String safe) {
        synchronized (mStateLock) {
            if (mShutdown || !mActive || (sid > 0 && sid != mSessionId)) return;
        }
        if (mEditText == null) return;

        Editable cur = mEditText.getText();
        if (TextUtils.equals(cur, safe)) return;

        mSuppressWatcher = true;
        try {
            mEditText.setText(safe);
            Editable updated = mEditText.getText();
            mEditText.setSelection(updated != null ? updated.length() : 0);
        } finally {
            mSuppressWatcher = false;
        }

        if (mImm != null) {
            mImm.restartInput(mEditText);
        }
        Log.i(TAG, "setTextOnUi sid=" + sid + " len=" + safe.length());
    }

    private void finishSessionOnUi(int sessionId, boolean submitted, String reason) {
        synchronized (mStateLock) {
            if (!mActive) return;
            if (sessionId > 0 && sessionId != mSessionId) return;
            if (mEditText != null && mEditText.getText() != null) {
                String finalTxt = mEditText.getText().toString();
                if (!TextUtils.equals(mText, finalTxt)) {
                    mText = finalTxt;
                    mTextSeq++;
                }
            }
            if (submitted) {
                mSubmitPressed = true;
                mSubmittedSessionId = mSessionId;
            }
            mActive = false;
        }

        cancelRetriesOnUi();

        if (mImm != null && mEditText != null) {
            try {
                mImm.hideSoftInputFromWindow(mEditText.getWindowToken(), 0);
            } catch (Throwable ignored) {}
        }

        if (mEditText != null) {
            mEditText.clearFocus();
            mEditText.setFocusable(false);
            mEditText.setFocusableInTouchMode(false);
            mEditText.setVisibility(View.GONE);
        }

        if (mPreviousFocusView != null && mPreviousFocusView != mEditText && mPreviousFocusView.isAttachedToWindow()) {
            mPreviousFocusView.requestFocus();
        }
        mPreviousFocusView = null;

        Log.i(TAG, "finishSessionOnUi sid=" + sessionId + " submitted=" + submitted + " reason=" + reason);
    }

    private void cancelRetriesOnUi() {
        if (mRetryRunnable150 != null) {
            mUiHandler.removeCallbacks(mRetryRunnable150);
            mRetryRunnable150 = null;
        }
        if (mRetryRunnable350 != null) {
            mUiHandler.removeCallbacks(mRetryRunnable350);
            mRetryRunnable350 = null;
        }
    }
}
