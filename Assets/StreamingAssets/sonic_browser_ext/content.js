'use strict';

(function () {
  if (window.__sonicFloorBridgeInstalled) return;
  window.__sonicFloorBridgeInstalled = true;

  const FFT_SIZE = 1024;
  const BIN_COUNT = 512;
  const FFT_INTERVAL_MS = 12; // ~60-72 Hz feed matching Unity XR frame cadence

  let audioCtx = null;
  let analyser = null;
  let dryGain = null;
  let wetGain = null;
  let pannerL = null;
  let pannerR = null;
  let splitter = null;
  const hookedElements = new WeakSet();
  const knownElements = new Set();
  const freqBuf = new Uint8Array(BIN_COUNT);

  let primaryMedia = null;
  let rafId = 0;
  let lastFftSendMs = 0;
  let zeroAudioStartMs = 0;
  let wasPlayingSent = false;
  let desiredVolume = 1.0;

  let lastMetaSig = '';
  let isCommittingFromNative = false;

  function sendToNative(payload) {
    try {
      browser.runtime.sendMessage(payload).catch(() => {});
    } catch (_) {}
  }

  function canHookMediaElement(el) {
    if (!el || hookedElements.has(el)) return false;
    try {
      const src = el.currentSrc || el.getAttribute('src') || '';
      if (!src) return true; // MSE or source attached later
      if (src.startsWith('blob:') || src.startsWith('data:') || src.startsWith('/')) {
        return true;
      }
      if (el.crossOrigin) {
        return true;
      }
      const parsed = new URL(src, window.location.href);
      return parsed.origin === window.location.origin;
    } catch (_) {
      return false;
    }
  }

  function ensureAudioGraph() {
    if (!audioCtx) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return false;
      audioCtx = new AudioCtx();
      analyser = audioCtx.createAnalyser();
      analyser.fftSize = FFT_SIZE;
      analyser.minDecibels = -85;
      analyser.maxDecibels = -10;
      analyser.channelCountMode = 'explicit';
      analyser.channelCount = 2;
      // Unsmoothed per-frame FFT so Unity runs one identical temporal pass for local & browser audio
      analyser.smoothingTimeConstant = 0.0;

      // Dual-path output: Analyser stays pre-spatialization so FFT is immune to AR distance attenuation.
      dryGain = audioCtx.createGain();
      dryGain.gain.value = 1.0;
      analyser.connect(dryGain);
      dryGain.connect(audioCtx.destination);

      try {
        splitter = audioCtx.createChannelSplitter(2);
        pannerL = audioCtx.createPanner();
        pannerR = audioCtx.createPanner();
        for (const p of [pannerL, pannerR]) {
          p.panningModel = 'HRTF';
          p.distanceModel = 'inverse';
          p.refDistance = 1.2;
          p.maxDistance = 12.0;
          p.rolloffFactor = 0.62;
          p.coneInnerAngle = 360;
          p.coneOuterAngle = 360;
        }
        wetGain = audioCtx.createGain();
        wetGain.gain.value = 0.0;

        analyser.connect(splitter);
        splitter.connect(pannerL, 0);
        splitter.connect(pannerR, 1);
        pannerL.connect(wetGain);
        pannerR.connect(wetGain);
        wetGain.connect(audioCtx.destination);
      } catch (_) {
        // Fallback to pure dry path if PannerNode is restricted on a given page
        pannerL = null;
        pannerR = null;
        wetGain = null;
      }
    }
    if (audioCtx.state === 'suspended') {
      audioCtx.resume().catch(() => {});
    }
    return true;
  }

  function attachMediaElement(el) {
    if (!el || knownElements.has(el)) {
      if (el && !hookedElements.has(el) && canHookMediaElement(el)) {
        tryHookMedia(el);
      }
      return;
    }
    knownElements.add(el);

    const onPlayEvent = () => {
      primaryMedia = el;
      if (desiredVolume >= 0 && desiredVolume <= 1 && Math.abs(el.volume - desiredVolume) > 0.01) {
        try { el.volume = desiredVolume; } catch (_) {}
      }
      tryHookMedia(el);
      startFftLoop();
      pushMetadata(true);
    };

    const onPauseOrEnd = () => {
      pushMetadata(true);
      if (el === primaryMedia && (el.paused || el.ended)) {
        sendPausedFft(el);
      }
    };

    el.addEventListener('play', onPlayEvent, true);
    el.addEventListener('playing', onPlayEvent, true);
    el.addEventListener('pause', onPauseOrEnd, true);
    el.addEventListener('ended', onPauseOrEnd, true);
    el.addEventListener('loadedmetadata', () => pushMetadata(true), true);
    el.addEventListener('seeked', () => pushMetadata(true), true);
    el.addEventListener('durationchange', () => pushMetadata(true), true);

    if (!el.paused && !el.ended) {
      onPlayEvent();
    } else if (!hookedElements.has(el) && canHookMediaElement(el)) {
      tryHookMedia(el);
    }
  }

  function tryHookMedia(el) {
    if (!el || hookedElements.has(el) || !canHookMediaElement(el)) return;
    if (!ensureAudioGraph()) return;
    try {
      const srcNode = audioCtx.createMediaElementSource(el);
      srcNode.connect(analyser);
      hookedElements.add(el);
    } catch (_) {
      // Element may already be captured or blocked; avoid retrying endlessly
      hookedElements.add(el);
    }
  }

  function pickPlayingMedia() {
    if (primaryMedia && document.contains(primaryMedia) && !primaryMedia.paused && !primaryMedia.ended) {
      return primaryMedia;
    }
    const list = document.querySelectorAll('video, audio');
    for (let i = 0; i < list.length; i++) {
      const el = list[i];
      if (!el.paused && !el.ended) {
        primaryMedia = el;
        return el;
      }
    }
    if (primaryMedia && document.contains(primaryMedia) && Number.isFinite(primaryMedia.duration) && primaryMedia.duration > 0 && primaryMedia.readyState >= 1) {
      return primaryMedia;
    }
    for (let i = 0; i < list.length; i++) {
      const el = list[i];
      if (Number.isFinite(el.duration) && el.duration > 0 && el.readyState >= 1) {
        primaryMedia = el;
        return el;
      }
    }
    return primaryMedia && document.contains(primaryMedia) ? primaryMedia : (list[0] || null);
  }

  function getMediaTimeAndDuration(el) {
    let time = (el && Number.isFinite(el.currentTime) && el.currentTime >= 0) ? el.currentTime : 0;
    let duration = (el && Number.isFinite(el.duration) && el.duration > 0) ? el.duration : 0;
    if (duration <= 0) {
      try {
        const bar = document.querySelector('ytmusic-player-bar #progress-bar');
        if (bar) {
          const maxRaw = (bar.max !== undefined && bar.max !== null && bar.max !== '') ? bar.max : bar.getAttribute('aria-valuemax');
          const valRaw = (bar.value !== undefined && bar.value !== null && bar.value !== '') ? bar.value : bar.getAttribute('aria-valuenow');
          const maxNum = Number(maxRaw);
          const valNum = Number(valRaw);
          if (Number.isFinite(maxNum) && maxNum > 0) {
            duration = maxNum;
            if (Number.isFinite(valNum) && valNum >= 0) {
              time = Math.min(valNum, duration);
            }
          }
        }
      } catch (_) {}
    }
    return { time, duration };
  }

  function encodeFreqBase64(buf) {
    let raw = '';
    for (let i = 0; i < buf.length; i += 64) {
      raw += String.fromCharCode.apply(null, buf.subarray(i, i + 64));
    }
    return btoa(raw);
  }

  function sendPausedFft(el) {
    if (!wasPlayingSent) return;
    wasPlayingSent = false;
    zeroAudioStartMs = 0;
    const { time, duration } = getMediaTimeAndDuration(el);
    sendToNative({
      type: 'fft',
      playing: false,
      b64: '',
      drmSilent: false,
      peak: 0,
      time: time,
      duration: duration
    });
  }

  function sampleAndSendFft(el, now) {
    lastFftSendMs = now;
    let peak = 0;
    let drmSilent = false;
    let b64 = '';

    if (analyser) {
      if (audioCtx && audioCtx.state === 'suspended') {
        audioCtx.resume().catch(() => {});
      }
      analyser.getByteFrequencyData(freqBuf);
      for (let i = 0; i < BIN_COUNT; i++) {
        if (freqBuf[i] > peak) peak = freqBuf[i];
      }
      if (peak === 0 && !el.muted && el.volume > 0.01 && el.readyState >= 2) {
        if (zeroAudioStartMs === 0) zeroAudioStartMs = now;
        if (now - zeroAudioStartMs > 2500) drmSilent = true;
      } else {
        zeroAudioStartMs = 0;
      }
      b64 = encodeFreqBase64(freqBuf);
    }

    wasPlayingSent = true;
    const { time, duration } = getMediaTimeAndDuration(el);
    sendToNative({
      type: 'fft',
      playing: true,
      b64: b64,
      drmSilent: drmSilent,
      peak: peak,
      time: time,
      duration: duration
    });
  }

  function startFftLoop() {
    if (rafId) return;
    const step = (now) => {
      rafId = 0;
      const el = pickPlayingMedia();
      if (!el || el.paused || el.ended) {
        sendPausedFft(el);
        return;
      }

      if (!hookedElements.has(el) && canHookMediaElement(el)) {
        tryHookMedia(el);
      }

      if (now - lastFftSendMs >= FFT_INTERVAL_MS) {
        sampleAndSendFft(el, now);
      }

      rafId = requestAnimationFrame(step);
    };
    rafId = requestAnimationFrame(step);
  }

  function pushMetadata(force) {
    const el = pickPlayingMedia();
    const ms = navigator.mediaSession && navigator.mediaSession.metadata;
    let title = (ms && ms.title) ? ms.title : (document.title || '');
    let artist = (ms && ms.artist) ? ms.artist : '';
    let artworkUrl = '';
    if (ms && Array.isArray(ms.artwork) && ms.artwork.length > 0) {
      for (let i = 0; i < ms.artwork.length; i++) {
        const entry = ms.artwork[i];
        if (!entry || !entry.src) continue;
        if (/(192|226|256|300|384|512)/.test(String(entry.sizes || ''))) {
          artworkUrl = entry.src;
          break;
        }
      }
      if (!artworkUrl) {
        artworkUrl = ms.artwork[0].src || ms.artwork[ms.artwork.length - 1].src || '';
      }
    }
    if (!artworkUrl) {
      const thumbEl = document.querySelector('ytmusic-player-bar img.image, .ytp-cued-thumbnail-overlay-image');
      if (thumbEl) {
        const imgSrc = thumbEl.currentSrc || thumbEl.getAttribute('src') || '';
        if (imgSrc && !imgSrc.startsWith('data:image/gif')) {
          artworkUrl = imgSrc;
        } else if (thumbEl.style && thumbEl.style.backgroundImage) {
          const m = thumbEl.style.backgroundImage.match(/url\(["']?([^"')]+)["']?\)/);
          if (m && m[1]) artworkUrl = m[1];
        }
      }
    }

    if (!artist && window.location.hostname) {
      artist = window.location.hostname.replace(/^www\./, '');
    }

    const playing = !!(el && !el.paused && !el.ended);
    const vol = el ? (el.muted ? 0 : el.volume) : desiredVolume;
    const { time, duration } = getMediaTimeAndDuration(el);
    const sig = `${title}|${artist}|${artworkUrl}|${playing ? 1 : 0}|${vol.toFixed(2)}|${Math.floor(time)}|${Math.floor(duration)}`;
    if (!force && sig === lastMetaSig) return;
    lastMetaSig = sig;

    sendToNative({
      type: 'meta',
      title: title,
      artist: artist,
      artworkUrl: artworkUrl,
      playing: playing,
      volume: vol,
      time: time,
      duration: duration
    });
  }

  function scanMedia() {
    const els = document.querySelectorAll('video, audio');
    for (let i = 0; i < els.length; i++) {
      attachMediaElement(els[i]);
    }
  }

  function getDeepActiveElement() {
    let active = document.activeElement;
    while (active && active.shadowRoot && active.shadowRoot.activeElement) {
      active = active.shadowRoot.activeElement;
    }
    return active;
  }

  function isTextEditableElement(el) {
    if (!el) return false;
    const tag = (el.tagName || '').toUpperCase();
    if (tag === 'TEXTAREA') return !el.disabled && !el.readOnly;
    if (tag === 'INPUT') {
      const t = (el.type || 'text').toLowerCase();
      return !el.disabled && !el.readOnly &&
        (t === 'text' || t === 'search' || t === 'email' || t === 'password' || t === 'url' || t === 'tel' || t === 'number');
    }
    return !!el.isContentEditable;
  }

  function setNativeInputValue(el, value) {
    try {
      const tag = (el.tagName || '').toUpperCase();
      const proto = tag === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
      const desc = Object.getOwnPropertyDescriptor(proto, 'value');
      if (desc && typeof desc.set === 'function') {
        desc.set.call(el, value);
      } else {
        el.value = value;
      }
    } catch (_) {
      el.value = value;
    }
  }

  function readEditableState(el) {
    const tag = (el && el.tagName ? el.tagName : '').toUpperCase();
    const isPass = (tag === 'INPUT' && (el.type || '').toLowerCase() === 'password');
    const val = (tag === 'INPUT' || tag === 'TEXTAREA') ? (el.value || '') : (el.textContent || '');
    return { password: isPass, value: val };
  }

  function findEditableInEvent(e) {
    if (!e) return null;
    if (typeof e.composedPath === 'function') {
      const path = e.composedPath();
      if (Array.isArray(path)) {
        for (let i = 0; i < path.length; i++) {
          if (isTextEditableElement(path[i])) {
            return path[i];
          }
        }
      }
    }
    let cur = e.target;
    while (cur) {
      if (isTextEditableElement(cur)) return cur;
      cur = cur.parentNode || cur.host || null;
    }
    return null;
  }

  function reportEditable(el, msgType, openKeyboard) {
    if (!isTextEditableElement(el)) return;
    const state = readEditableState(el);
    sendToNative({
      type: msgType,
      focused: true,
      open: !!openKeyboard,
      password: state.password,
      value: state.value
    });
  }

  function reportInputBlurred() {
    sendToNative({
      type: 'inputFocus',
      focused: false,
      open: false,
      password: false,
      value: ''
    });
  }

  function commitTextToActiveElement(text, pressEnter) {
    const active = getDeepActiveElement();
    if (!active) return;

    isCommittingFromNative = true;
    try {
      if (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA') {
        const nextVal = text || '';
        setNativeInputValue(active, nextVal);
        try { active.setSelectionRange(nextVal.length, nextVal.length); } catch (_) {}
        active.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: nextVal }));
        active.dispatchEvent(new Event('change', { bubbles: true }));
      } else if (active.isContentEditable) {
        active.textContent = text || '';
        active.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: text || '' }));
      }
    } finally {
      isCommittingFromNative = false;
    }

    if (pressEnter) {
      dispatchEnterKey(active);
    }
  }

  function dispatchEnterKey(target) {
    const el = target || getDeepActiveElement();
    if (!el) return;
    const opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true, composed: true };
    el.dispatchEvent(new KeyboardEvent('keydown', opts));
    el.dispatchEvent(new KeyboardEvent('keypress', opts));
    el.dispatchEvent(new KeyboardEvent('keyup', opts));
    if (el.form && typeof el.form.requestSubmit === 'function') {
      try { el.form.requestSubmit(); } catch (_) {}
    } else if (el.form && typeof el.form.submit === 'function') {
      try { el.form.submit(); } catch (_) {}
    }
  }

  function clickFirstMatch(selectors) {
    for (let i = 0; i < selectors.length; i++) {
      const btn = document.querySelector(selectors[i]);
      if (btn && typeof btn.click === 'function') {
        btn.click();
        return true;
      }
    }
    return false;
  }

  // Capture play/playing events anywhere in document/shadow trees
  document.addEventListener('play', (e) => {
    if (e && e.target && (e.target.tagName === 'VIDEO' || e.target.tagName === 'AUDIO')) {
      attachMediaElement(e.target);
    }
  }, true);

  document.addEventListener('focusin', () => {
    const active = getDeepActiveElement();
    if (isTextEditableElement(active)) {
      reportEditable(active, 'inputFocus', true);
    }
  }, true);

  document.addEventListener('click', (e) => {
    const hit = findEditableInEvent(e);
    if (hit) {
      reportEditable(hit, 'inputFocus', true);
    }
  }, true);

  document.addEventListener('focusout', () => {
    setTimeout(() => {
      if (!isTextEditableElement(getDeepActiveElement())) {
        reportInputBlurred();
      }
    }, 0);
  }, true);

  const onPageInputSync = () => {
    if (isCommittingFromNative) return;
    const active = getDeepActiveElement();
    if (isTextEditableElement(active)) {
      reportEditable(active, 'inputSync', false);
    }
  };
  document.addEventListener('input', onPageInputSync, true);
  document.addEventListener('change', onPageInputSync, true);

  browser.runtime.onMessage.addListener((msg) => {
    if (!msg || typeof msg !== 'object') return;
    const el = pickPlayingMedia();

    switch (msg.cmd) {
      case 'playPause': {
        if (el) {
          if (el.paused || el.ended) {
            el.play().catch(() => {
              clickFirstMatch(['tp-yt-paper-icon-button#play-pause-button', '.play-pause-button', '.ytp-play-button', 'button[aria-label*="Play"]']);
            });
          } else {
            el.pause();
          }
        } else {
          clickFirstMatch(['tp-yt-paper-icon-button#play-pause-button', '.play-pause-button', '.ytp-play-button']);
        }
        setTimeout(() => pushMetadata(true), 80);
        break;
      }
      case 'pause': {
        if (el && !el.paused) {
          el.pause();
          setTimeout(() => pushMetadata(true), 50);
        }
        break;
      }
      case 'next': {
        clickFirstMatch(['tp-yt-paper-icon-button.next-button', '.next-button', '.ytp-next-button', 'button[aria-label*="Next"]', '[data-testid="next-button"]']);
        break;
      }
      case 'prev': {
        if (el && el.currentTime > 3) {
          try { el.currentTime = 0; } catch (_) {}
        } else {
          clickFirstMatch(['tp-yt-paper-icon-button.previous-button', '.previous-button', '.ytp-prev-button', 'button[aria-label*="Previous"]', '[data-testid="previous-button"]']);
        }
        break;
      }
      case 'setVolume': {
        const v = Math.max(0, Math.min(1, Number(msg.value) || 0));
        desiredVolume = v;
        const list = document.querySelectorAll('video, audio');
        for (let i = 0; i < list.length; i++) {
          try {
            list[i].volume = v;
            list[i].muted = (v <= 0.001);
          } catch (_) {}
        }
        pushMetadata(true);
        break;
      }
      case 'seek': {
        if (!el) break;
        const { duration: maxDur } = getMediaTimeAndDuration(el);
        const rawTarget = Number(msg.value);
        if (!Number.isFinite(rawTarget)) break;
        const clamped = maxDur > 0 ? Math.max(0, Math.min(maxDur, rawTarget)) : Math.max(0, rawTarget);
        try {
          el.currentTime = clamped;
        } catch (_) {}
        pushMetadata(true);
        if (!el.paused && !el.ended) {
          sampleAndSendFft(el, performance.now());
        }
        break;
      }
      case 'commitText': {
        commitTextToActiveElement(msg.text || '', !!msg.enter);
        break;
      }
      case 'keyEnter': {
        dispatchEnterKey(null);
        break;
      }
      case 'requestActiveValue': {
        const active = getDeepActiveElement();
        if (isTextEditableElement(active)) {
          reportEditable(active, 'inputSync', false);
        } else {
          reportInputBlurred();
        }
        break;
      }
      case 'spatialPose': {
        if (!audioCtx || !dryGain || !wetGain || !pannerL || !pannerR) break;
        const now = audioCtx.currentTime;
        const active = !!msg.active;
        const targetDry = active ? 0.18 : 1.0;
        const targetWet = active ? 0.82 : 0.0;
        dryGain.gain.setTargetAtTime(targetDry, now, 0.035);
        wetGain.gain.setTargetAtTime(targetWet, now, 0.035);
        if (active) {
          const refD = Math.max(0.6, Number(msg.refDist) || 1.2);
          pannerL.refDistance = refD;
          pannerR.refDistance = refD;
          if (pannerL.positionX) {
            pannerL.positionX.setTargetAtTime(Number(msg.lx) || 0, now, 0.035);
            pannerL.positionY.setTargetAtTime(Number(msg.ly) || 0, now, 0.035);
            pannerL.positionZ.setTargetAtTime(Number(msg.lz) || -1, now, 0.035);
            pannerR.positionX.setTargetAtTime(Number(msg.rx) || 0, now, 0.035);
            pannerR.positionY.setTargetAtTime(Number(msg.ry) || 0, now, 0.035);
            pannerR.positionZ.setTargetAtTime(Number(msg.rz) || -1, now, 0.035);
          } else if (typeof pannerL.setPosition === 'function') {
            pannerL.setPosition(Number(msg.lx) || 0, Number(msg.ly) || 0, Number(msg.lz) || -1);
            pannerR.setPosition(Number(msg.rx) || 0, Number(msg.ry) || 0, Number(msg.rz) || -1);
          }
        }
        break;
      }
    }
  });

  const observer = new MutationObserver(() => scanMedia());
  if (document.documentElement) {
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  scanMedia();
  setInterval(() => {
    scanMedia();
    pushMetadata(false);
  }, 1000);

  // Fallback ticker if rAF is throttled while browser panel is minimized/hidden
  setInterval(() => {
    const el = pickPlayingMedia();
    if (!el || el.paused || el.ended) {
      sendPausedFft(el);
      return;
    }
    const now = performance.now();
    if (now - lastFftSendMs >= 45) {
      if (!hookedElements.has(el) && canHookMediaElement(el)) {
        tryHookMedia(el);
      }
      sampleAndSendFft(el, now);
    }
  }, 30);
})();
