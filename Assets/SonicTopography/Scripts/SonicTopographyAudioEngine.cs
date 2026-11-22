using System;
using System.Collections.Generic;
using UnityEngine;

namespace SonicTopography
{
    [Serializable]
    public struct SonicAudioData
    {
        // Legacy base
        public float bass;
        public float mid;
        public float treble;
        public float energy;

        // Granular bands
        public float subBass;    // 20-60Hz
        public float lowMid;     // 140-300Hz
        public float highMid;    // 800-2kHz
        public float presence;   // 2-4kHz
        public float brilliance; // 4-8kHz
        public float air;        // 8-16kHz

        // Timbral Metrics
        public float warmth;
        public float brightness;
        public float sharpness;
        public float smoothness;
        public float density;
        public float spectralCentroid;

        // Realtime kick channel for beat-led terrain motion
        public float kickLevel;
        public float kickFlux;
        public float kickThreshold;
        public float kickOnset;
        public float kickEnvelope;
        public float kickConfidence;
        public string kickWindowName;
        public int kickWindowStart;
        public int kickWindowEnd;
    }

    public enum TriggerActionType
    {
        Pulse,
        Meteor,
        Snare
    }

    public enum TriggerFireMode
    {
        Kick,
        Snare,
        Advanced
    }

    [Serializable]
    public class TriggerConfig
    {
        public TriggerActionType action;
        public bool enabled = true;
        public bool isAdvancedMode = false;
        public bool autoTrack = true;

        // Advanced parameters
        public int freqIndex = -1;
        public float threshold = 0.5f;

        // Auto Beat parameters
        public float sensitivity = 0.15f;
        public int cooldown = 60;
        public int bandStart = 0;
        public int bandEnd = 16;
        public float pulseStrength = 0.2f;

        // Internal evaluation state
        [NonSerialized] public int currentCooldown = 0;
        [NonSerialized] public int beatHold = 0;
        [NonSerialized] public float lastEvalEnergy = 0f;
        [NonSerialized] public float lastEvalThresh = 0f;
        [NonSerialized] public float[] fluxHistory = new float[40];
        [NonSerialized] public int fluxHistoryIndex = 0;
        [NonSerialized] public float smoothedFlux = 0f;
        [NonSerialized] public float prevSmoothedFlux = 0f;

        public TriggerConfig(TriggerActionType actionType)
        {
            action = actionType;
            enabled = true;
            isAdvancedMode = false;

            if (actionType == TriggerActionType.Pulse)
            {
                bandStart = 1;
                bandEnd = 2;
                sensitivity = 0.85f;
                cooldown = 15;
                pulseStrength = 0.2f;
            }
            else if (actionType == TriggerActionType.Meteor)
            {
                bandStart = 159;
                bandEnd = 174;
                sensitivity = 0.45f;
                cooldown = 241;
                pulseStrength = 0.50f;
            }
            else if (actionType == TriggerActionType.Snare)
            {
                bandStart = 47;
                bandEnd = 120;
                sensitivity = 0.6f;
                cooldown = 30;
                pulseStrength = 0.3f;
            }
        }

        public void GetTriggerRange(out int start, out int end)
        {
            if (!isAdvancedMode)
            {
                start = Mathf.Clamp(bandStart, 0, 511);
                end = Mathf.Clamp(bandEnd, start, 511);
                return;
            }
            int c = freqIndex >= 0 ? freqIndex : Mathf.FloorToInt(0.2f * 512f);
            start = Mathf.Max(0, c - 2);
            end = Mathf.Min(511, c + 2);
        }
    }

    public class SonicTopographyAudioEngine : MonoBehaviour
    {
        public const int BIN_COUNT = 512;

        [Header("Audio Source")]
        public AudioSource audioSource;
        public string defaultResourceAudioPath = string.Empty;
        public bool autoPlayOnBoot = false;
        public bool loopAudio = false;

        [Header("Spectrum Analyser Settings (Web Audio AnalyserNode Match)")]
        [Range(0.1f, 0.95f)]
        public float smoothingTimeConstant = 0.8f;
        public float minDecibels = -68f;
        public float maxDecibels = -14f;

        [Header("Beat Detector Settings")]
        [Range(0f, 100f)]
        public float beatDetectorSensitivity = 100f;

        [Header("Transient Triggers")]
        public TriggerConfig pulseTrigger = new TriggerConfig(TriggerActionType.Pulse);
        public TriggerConfig snareTrigger = new TriggerConfig(TriggerActionType.Snare);
        public TriggerConfig meteorTrigger = new TriggerConfig(TriggerActionType.Meteor);

        public event Action<float, TriggerFireMode, TriggerActionType> OnFreqTrigger;

        // Raw & smoothed spectrum buffers
        private readonly float[] _spectrumLeft = new float[BIN_COUNT];
        private readonly float[] _spectrumRight = new float[BIN_COUNT];
        private readonly float[] _smoothedLinearSpectrum = new float[BIN_COUNT];
        private readonly float[] _normalizedBins = new float[BIN_COUNT];
        private readonly float[] _prevBins = new float[BIN_COUNT];

        // External audio FFT source (built-in WebExtension / GeckoView bridge)
        private const float EXT_MIN_DB = -85f;
        private const float EXT_MAX_DB = -10f;
        private static readonly float[] EXT_BYTE_TO_DB = new float[256];
        private static readonly float[] EXT_BYTE_TO_LINEAR = new float[256];

        static SonicTopographyAudioEngine()
        {
            EXT_BYTE_TO_DB[0] = -100f;
            EXT_BYTE_TO_LINEAR[0] = 0f;
            float span = EXT_MAX_DB - EXT_MIN_DB;
            for (int b = 1; b < 256; b++)
            {
                float db = EXT_MIN_DB + (b / 255f) * span;
                EXT_BYTE_TO_DB[b] = db;
                EXT_BYTE_TO_LINEAR[b] = Mathf.Pow(10f, db * 0.05f);
            }
        }

        private readonly float[] _externalRawLinearBins = new float[BIN_COUNT];
        private float _externalPeakDb = -24f;
        private bool _externalAudioActive = false;
        private float _lastExternalUpdateTime = -10f;

        private float _prevBrightness = 0f;
        private SonicAudioData _smoothedData;
        private int _lastProcessedFrame = -1;

        // Auto-pulse 3-second sliding window tracker (pre-allocated ring buffer to avoid per-frame GC & List shifting)
        private const int PULSE_RING_CAPACITY = 512;
        private struct PulseFrameSample
        {
            public float time;
            public float[] lowBins;
        }
        private readonly PulseFrameSample[] _pulseRingBuffer = new PulseFrameSample[PULSE_RING_CAPACITY];
        private int _pulseRingHead = 0;
        private int _pulseRingCount = 0;
        private readonly float[] _maxDiffPerBin = new float[30];
        private float _lastAutoTrackTime = 0f;

        // Multi-window beat detector state (from beatDetector.ts & kickEnvelope.ts)
        private struct BeatWindow
        {
            public string name;
            public int start;
            public int end;
        }

        private static readonly BeatWindow[] BEAT_WINDOWS = new BeatWindow[]
        {
            new BeatWindow { name = "Deep", start = 0, end = 2 },
            new BeatWindow { name = "Classic", start = 1, end = 4 },
            new BeatWindow { name = "Punch", start = 2, end = 6 },
            new BeatWindow { name = "Wide", start = 0, end = 7 }
        };

        private int _activeWindowIndex = 1;
        private readonly float[] _windowScores = new float[4];
        private readonly float[] _currentWindowLevels = new float[4];
        private readonly float[] _previousWindowLevels = new float[4];
        private readonly float[] _beatFluxHistory = new float[90];
        private int _beatFluxHistoryIndex = 0;
        private float _beatSmoothedFlux = 0f;
        private float _beatPrevSmoothedFlux = 0f;
        private float _beatCooldownRemaining = 0f;

        // Kick envelope state (from kickEnvelope.ts)
        private float _kickNoiseFloor = 0f;
        private float _kickLevel = 0f;
        private float _kickOnset = 0f;
        private float _kickEnvelope = 0f;

        private void Awake()
        {
            EnsureAudioSourceReady();
        }

        private void Start()
        {
            EnsureAudioSourceReady();
            if (autoPlayOnBoot && audioSource != null && audioSource.clip != null && !audioSource.isPlaying)
            {
                audioSource.Play();
            }
        }

        public void EnsureAudioSourceReady()
        {
            if (audioSource == null)
            {
                audioSource = GetComponent<AudioSource>();
            }
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            audioSource.playOnAwake = false;
            audioSource.loop = loopAudio;
            audioSource.spatialBlend = 0f; // 2D stereo so music fills headphones evenly in VR
            audioSource.volume = 1f;
        }

        public bool IsExternalAudioActive => _externalAudioActive && (Time.unscaledTime - _lastExternalUpdateTime) < 1.0f;

        /// <summary>
        /// Feeds raw 0..255 Web Audio AnalyserNode FFT bins into the linear spectrum + adaptive loudness matcher.
        /// </summary>
        public void SetExternalAudioSpectrum(byte[] byteBins, bool isPlaying)
        {
            if (!isPlaying || byteBins == null || byteBins.Length == 0)
            {
                _externalAudioActive = false;
                return;
            }

            if (!_externalAudioActive)
            {
                _externalPeakDb = -24f;
            }

            int len = Mathf.Min(BIN_COUNT, byteBins.Length);
            float frameMaxDb = -100f;
            for (int i = 0; i < len; i++)
            {
                byte b = byteBins[i];
                _externalRawLinearBins[i] = EXT_BYTE_TO_LINEAR[b];
                if (i >= 1 && i <= 64)
                {
                    float db = EXT_BYTE_TO_DB[b];
                    if (db > frameMaxDb) frameMaxDb = db;
                }
            }
            for (int i = len; i < BIN_COUNT; i++)
            {
                _externalRawLinearBins[i] = 0f;
            }

            if (frameMaxDb > -52f)
            {
                float targetPeak = Mathf.Clamp(frameMaxDb, -30f, -12f);
                float rate = targetPeak > _externalPeakDb ? 0.18f : 0.004f;
                _externalPeakDb += (targetPeak - _externalPeakDb) * rate;
            }

            _externalAudioActive = true;
            _lastExternalUpdateTime = Time.unscaledTime;
        }

        public void ClearExternalAudio()
        {
            _externalAudioActive = false;
        }

        public SonicAudioData GetAudioData()
        {
            if (_lastProcessedFrame == Time.frameCount)
            {
                return _smoothedData;
            }
            _lastProcessedFrame = Time.frameCount;

            float deltaSeconds = Mathf.Clamp(Time.deltaTime, 1f / 240f, 0.25f);
            float now = Time.time;
            bool localPlaying = audioSource != null && audioSource.isPlaying;
            bool externalPlaying = !localPlaying && IsExternalAudioActive;
            bool isPlaying = localPlaying || externalPlaying;

            float energySum = 0f;
            float centroidNum = 0f;
            float centroidDen = 0f;

            float subBassSum = 0f, bassSum = 0f, lowMidSum = 0f, midSum = 0f;
            float highMidSum = 0f, presenceSum = 0f, brillianceSum = 0f, airSum = 0f;
            float jumpVolatilitySum = 0f;

            if (isPlaying)
            {
                // Frame-rate compensated Web Audio smoothingTimeConstant (calibrated to 60Hz reference)
                float smoothing = Mathf.Pow(smoothingTimeConstant, deltaSeconds * 60f);
                float dbRange = Mathf.Max(1f, maxDecibels - minDecibels);

                if (localPlaying)
                {
                    audioSource.GetSpectrumData(_spectrumLeft, 0, FFTWindow.BlackmanHarris);
                    audioSource.GetSpectrumData(_spectrumRight, 1, FFTWindow.BlackmanHarris);

                    for (int i = 0; i < BIN_COUNT; i++)
                    {
                        float linearMag = 0.5f * (_spectrumLeft[i] + _spectrumRight[i]);
                        _smoothedLinearSpectrum[i] = smoothing * _smoothedLinearSpectrum[i] + (1f - smoothing) * linearMag;

                        float db = 20f * Mathf.Log10(Mathf.Max(_smoothedLinearSpectrum[i], 1e-9f));
                        _normalizedBins[i] = Mathf.Clamp01((db - minDecibels) / dbRange);
                    }
                }
                else
                {
                    float makeupDb = Mathf.Clamp(maxDecibels - _externalPeakDb, 0f, 15f);
                    float linearGain = Mathf.Pow(10f, makeupDb * 0.05f);
                    for (int i = 0; i < BIN_COUNT; i++)
                    {
                        float linearMag = _externalRawLinearBins[i] * linearGain;
                        _smoothedLinearSpectrum[i] = smoothing * _smoothedLinearSpectrum[i] + (1f - smoothing) * linearMag;

                        float db = 20f * Mathf.Log10(Mathf.Max(_smoothedLinearSpectrum[i], 1e-9f));
                        _normalizedBins[i] = Mathf.Clamp01((db - minDecibels) / dbRange);
                    }
                }

                TrackAutoPulse(_normalizedBins, now);

                float fluxPulse = 0f;
                float fluxSnare = 0f;
                float fluxMeteor = 0f;

                for (int i = 0; i < BIN_COUNT; i++)
                {
                    float val = _normalizedBins[i];
                    energySum += val;

                    centroidNum += i * val;
                    centroidDen += val;

                    float prevVal = _prevBins[i];
                    jumpVolatilitySum += Mathf.Abs(val - prevVal);

                    if (i >= pulseTrigger.bandStart && i <= pulseTrigger.bandEnd)
                    {
                        float diff = val - prevVal;
                        if (diff > 0.01f) fluxPulse += diff;
                    }

                    if (i >= snareTrigger.bandStart && i <= snareTrigger.bandEnd)
                    {
                        float diff = val - prevVal;
                        if (diff > 0.01f) fluxSnare += diff;
                    }

                    if (i >= meteorTrigger.bandStart && i <= meteorTrigger.bandEnd)
                    {
                        float diff = val - prevVal;
                        if (diff > 0.01f) fluxMeteor += diff;
                    }

                    _prevBins[i] = val;

                    if (i <= 1) subBassSum += val;
                    else if (i <= 3) bassSum += val;
                    else if (i <= 7) lowMidSum += val;
                    else if (i <= 18) midSum += val;
                    else if (i <= 46) highMidSum += val;
                    else if (i <= 93) presenceSum += val;
                    else if (i <= 186) brillianceSum += val;
                    else if (i <= 372) airSum += val;
                }

                int pulseBins = Mathf.Max(1, pulseTrigger.bandEnd - pulseTrigger.bandStart + 1);
                EvaluateTrigger(pulseTrigger, fluxPulse / pulseBins, isPlaying);

                int snareBins = Mathf.Max(1, snareTrigger.bandEnd - snareTrigger.bandStart + 1);
                EvaluateTrigger(snareTrigger, fluxSnare / snareBins, isPlaying);

                int meteorBins = Mathf.Max(1, meteorTrigger.bandEnd - meteorTrigger.bandStart + 1);
                EvaluateTrigger(meteorTrigger, fluxMeteor / meteorBins, isPlaying);
            }
            else
            {
                for (int i = 0; i < BIN_COUNT; i++)
                {
                    _smoothedLinearSpectrum[i] *= 0.90f;
                    _normalizedBins[i] *= 0.94f;
                    _prevBins[i] = 0f;
                }
            }

            StepBeatDetector(deltaSeconds, out float kickFlux, out float kickThreshold, out float kickConfidence);

            float energy = energySum / BIN_COUNT;

            float subBass = subBassSum / 2f;
            float bass = bassSum / 2f;
            float lowMid = lowMidSum / 4f;
            float mid = midSum / 11f;
            float highMid = highMidSum / 28f;
            float presence = presenceSum / 47f;
            float brilliance = brillianceSum / 93f;
            float air = airSum / 186f;

            float oldBass = (subBassSum + bassSum + lowMidSum) / 8f;
            float oldMid = (midSum + highMidSum) / 39f;
            float oldTreble = (presenceSum + brillianceSum + airSum) / 326f;

            float warmth = energySum > 0f ? (subBassSum + bassSum + lowMidSum + midSum) / energySum : 0f;
            float brightness = energySum > 0f ? (presenceSum + brillianceSum + airSum) / energySum : 0f;

            float sharpness = Mathf.Max(0f, brightness - _prevBrightness) * 10f;
            _prevBrightness = brightness;

            float smoothnessVal = Mathf.Max(0f, 1f - (jumpVolatilitySum / BIN_COUNT) * 2f);

            float activeThreshold = energy * 1.5f;
            int activeBands = 0;
            if (subBass > activeThreshold) activeBands++;
            if (bass > activeThreshold) activeBands++;
            if (lowMid > activeThreshold) activeBands++;
            if (mid > activeThreshold) activeBands++;
            if (highMid > activeThreshold) activeBands++;
            if (presence > activeThreshold) activeBands++;
            if (brilliance > activeThreshold) activeBands++;
            if (air > activeThreshold) activeBands++;
            float density = activeBands / 8f;

            float spectralCentroid = centroidDen > 0f ? centroidNum / centroidDen : 0f;

            bool hasIncomingAudio = isPlaying && energySum > 0f;
            float dt = hasIncomingAudio ? 0.15f : 0.08f;

            _smoothedData.bass += (oldBass - _smoothedData.bass) * dt;
            _smoothedData.mid += (oldMid - _smoothedData.mid) * dt;
            _smoothedData.treble += (oldTreble - _smoothedData.treble) * dt;
            _smoothedData.energy += (energy - _smoothedData.energy) * dt;

            _smoothedData.subBass += (subBass - _smoothedData.subBass) * dt;
            _smoothedData.lowMid += (lowMid - _smoothedData.lowMid) * dt;
            _smoothedData.highMid += (highMid - _smoothedData.highMid) * dt;
            _smoothedData.presence += (presence - _smoothedData.presence) * dt;
            _smoothedData.brilliance += (brilliance - _smoothedData.brilliance) * dt;
            _smoothedData.air += (air - _smoothedData.air) * dt;

            _smoothedData.warmth += (warmth - _smoothedData.warmth) * dt;
            _smoothedData.brightness += (brightness - _smoothedData.brightness) * dt;
            _smoothedData.sharpness += (sharpness - _smoothedData.sharpness) * dt;
            _smoothedData.smoothness += (smoothnessVal - _smoothedData.smoothness) * dt;
            _smoothedData.density += (density - _smoothedData.density) * dt;
            _smoothedData.spectralCentroid += (spectralCentroid - _smoothedData.spectralCentroid) * dt;

            _smoothedData.kickLevel = _kickLevel;
            _smoothedData.kickFlux = kickFlux;
            _smoothedData.kickThreshold = kickThreshold;
            _smoothedData.kickOnset = _kickOnset;
            _smoothedData.kickEnvelope = _kickEnvelope;
            _smoothedData.kickConfidence = kickConfidence;
            _smoothedData.kickWindowName = BEAT_WINDOWS[_activeWindowIndex].name;
            _smoothedData.kickWindowStart = BEAT_WINDOWS[_activeWindowIndex].start;
            _smoothedData.kickWindowEnd = BEAT_WINDOWS[_activeWindowIndex].end;

            return _smoothedData;
        }

        private void TrackAutoPulse(float[] bins, float now)
        {
            if (!pulseTrigger.autoTrack) return;

            while (_pulseRingCount > 0 && now - _pulseRingBuffer[_pulseRingHead].time > 3.0f)
            {
                _pulseRingHead = (_pulseRingHead + 1) % PULSE_RING_CAPACITY;
                _pulseRingCount--;
            }

            if (_pulseRingCount == PULSE_RING_CAPACITY)
            {
                _pulseRingHead = (_pulseRingHead + 1) % PULSE_RING_CAPACITY;
                _pulseRingCount--;
            }

            int writeIdx = (_pulseRingHead + _pulseRingCount) % PULSE_RING_CAPACITY;
            float[] lowSlice = _pulseRingBuffer[writeIdx].lowBins;
            if (lowSlice == null)
            {
                lowSlice = new float[30];
                _pulseRingBuffer[writeIdx].lowBins = lowSlice;
            }
            Array.Copy(bins, 0, lowSlice, 0, 30);
            _pulseRingBuffer[writeIdx].time = now;
            _pulseRingCount++;

            if (now - _lastAutoTrackTime > 1.0f)
            {
                _lastAutoTrackTime = now;
                EvaluateAutoPulse();
            }
        }

        private void EvaluateAutoPulse()
        {
            int numFrames = _pulseRingCount;
            if (numFrames < 30) return;

            const int numBins = 30;
            Array.Clear(_maxDiffPerBin, 0, numBins);

            for (int f = 1; f < numFrames; f++)
            {
                float[] cur = _pulseRingBuffer[(_pulseRingHead + f) % PULSE_RING_CAPACITY].lowBins;
                float[] prev = _pulseRingBuffer[(_pulseRingHead + f - 1) % PULSE_RING_CAPACITY].lowBins;
                for (int b = 0; b < numBins; b++)
                {
                    float diff = cur[b] - prev[b];
                    if (diff > 0.01f && diff > _maxDiffPerBin[b])
                    {
                        _maxDiffPerBin[b] = diff;
                    }
                }
            }

            int bestBin1 = 1;
            int bestBin2 = 2;
            float bestDiff1 = -1f;
            float bestDiff2 = -1f;

            for (int b = 0; b < numBins; b++)
            {
                float d = _maxDiffPerBin[b];
                if (d > bestDiff1)
                {
                    bestDiff2 = bestDiff1;
                    bestBin2 = bestBin1;
                    bestDiff1 = d;
                    bestBin1 = b;
                }
                else if (d > bestDiff2)
                {
                    bestDiff2 = d;
                    bestBin2 = b;
                }
            }

            if (bestDiff1 < 0.15f) return;

            pulseTrigger.bandStart = Mathf.Min(bestBin1, bestBin2);
            pulseTrigger.bandEnd = Mathf.Max(bestBin1, bestBin2);
            pulseTrigger.sensitivity = 0.85f;
        }

        private void EvaluateTrigger(TriggerConfig config, float fluxScore, bool isPlaying)
        {
            if (!config.enabled || !isPlaying) return;

            bool triggered = false;
            config.GetTriggerRange(out int startBin, out int endBin);

            if (config.isAdvancedMode)
            {
                if (config.freqIndex >= 0 && config.freqIndex < BIN_COUNT)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int k = startBin; k <= endBin; k++)
                    {
                        sum += _normalizedBins[k];
                        count++;
                    }
                    float eVal = count > 0 ? sum / count : 0f;
                    config.lastEvalThresh = config.threshold;
                    if (config.currentCooldown <= 0 && eVal > config.threshold)
                    {
                        triggered = true;
                    }
                    config.lastEvalEnergy = eVal;
                    if (triggered)
                    {
                        OnFreqTrigger?.Invoke(eVal, TriggerFireMode.Advanced, config.action);
                        config.currentCooldown = 60;
                    }
                }
            }

            if (config.currentCooldown > 0) config.currentCooldown--;

            if (!config.isAdvancedMode)
            {
                config.smoothedFlux += (fluxScore - config.smoothedFlux) * 0.4f;
                config.fluxHistory[config.fluxHistoryIndex] = config.smoothedFlux;
                config.fluxHistoryIndex = (config.fluxHistoryIndex + 1) % config.fluxHistory.Length;

                float avgFlux = 0f;
                for (int i = 0; i < config.fluxHistory.Length; i++) avgFlux += config.fluxHistory[i];
                avgFlux /= config.fluxHistory.Length;

                float fluxVariance = 0f;
                for (int i = 0; i < config.fluxHistory.Length; i++)
                {
                    float d = config.fluxHistory[i] - avgFlux;
                    fluxVariance += d * d;
                }
                fluxVariance /= config.fluxHistory.Length;
                float fluxStdDev = Mathf.Sqrt(fluxVariance);

                float thresholdMultiplier = Mathf.Max(0.1f, 5.0f - config.sensitivity * 4.0f);
                float adaptiveThreshold = Mathf.Max(0.01f, avgFlux + fluxStdDev * thresholdMultiplier);

                bool isPeak = config.prevSmoothedFlux > adaptiveThreshold && config.prevSmoothedFlux >= config.smoothedFlux;

                if (config.beatHold > 0)
                {
                    config.beatHold--;
                }
                else if (isPeak && (config.prevSmoothedFlux - config.smoothedFlux) > 0.0001f)
                {
                    TriggerFireMode fireMode = config.action == TriggerActionType.Snare
                        ? TriggerFireMode.Snare
                        : TriggerFireMode.Kick;
                    OnFreqTrigger?.Invoke(config.prevSmoothedFlux * 30.0f * config.pulseStrength, fireMode, config.action);
                    config.beatHold = config.cooldown;
                }

                config.lastEvalEnergy = config.smoothedFlux * 10.0f;
                config.lastEvalThresh = adaptiveThreshold * 10.0f;
                config.prevSmoothedFlux = config.smoothedFlux;
            }
        }

        private float ReadWindowLevel(BeatWindow window)
        {
            float weighted = 0f;
            float weightTotal = 0f;
            float center = (window.start + window.end) * 0.5f;
            float halfWidth = Mathf.Max(1f, (window.end - window.start + 1) * 0.5f);

            for (int bin = window.start; bin <= window.end; bin++)
            {
                float distance = Mathf.Abs(bin - center);
                float weight = 0.35f + 0.65f * (1f - Mathf.Min(1f, distance / halfWidth));
                weighted += _normalizedBins[bin] * weight;
                weightTotal += weight;
            }

            return weightTotal > 0f ? weighted / weightTotal : 0f;
        }

        private void StepBeatDetector(
            float deltaSeconds,
            out float displayedFlux,
            out float threshold,
            out float confidence)
        {
            float sens = Mathf.Clamp(beatDetectorSensitivity, 0f, 100f);
            float lowerHalf = sens <= 50f ? sens / 50f : 1f;
            float upperHalf = sens > 50f ? (sens - 50f) / 50f : 0f;

            float stdDevGain = Mathf.Lerp(Mathf.Lerp(2.6f, 1.8f, lowerHalf), 1.1f, upperHalf);
            float threshFloor = Mathf.Lerp(Mathf.Lerp(0.05f, 0.028f, lowerHalf), 0.016f, upperHalf);
            float minTrigFlux = Mathf.Lerp(Mathf.Lerp(0.07f, 0.045f, lowerHalf), 0.025f, upperHalf);

            for (int i = 0; i < BEAT_WINDOWS.Length; i++)
            {
                _currentWindowLevels[i] = ReadWindowLevel(BEAT_WINDOWS[i]);
                float flux = Mathf.Max(0f, _currentWindowLevels[i] - _previousWindowLevels[i]);
                int width = BEAT_WINDOWS[i].end - BEAT_WINDOWS[i].start + 1;
                float focusBonus = 1f / Mathf.Sqrt(width);
                _windowScores[i] = _windowScores[i] * 0.965f + flux * focusBonus;
            }

            for (int i = 0; i < _windowScores.Length; i++)
            {
                if (_windowScores[i] > _windowScores[_activeWindowIndex] * 1.03f)
                {
                    _activeWindowIndex = i;
                }
            }

            float rawFlux = Mathf.Max(0f, _currentWindowLevels[_activeWindowIndex] - _previousWindowLevels[_activeWindowIndex]);
            for (int i = 0; i < BEAT_WINDOWS.Length; i++)
            {
                _previousWindowLevels[i] = _currentWindowLevels[i];
            }

            _beatSmoothedFlux += (rawFlux - _beatSmoothedFlux) * 0.35f;

            float avg = 0f;
            for (int i = 0; i < _beatFluxHistory.Length; i++) avg += _beatFluxHistory[i];
            avg /= _beatFluxHistory.Length;

            float variance = 0f;
            for (int i = 0; i < _beatFluxHistory.Length; i++)
            {
                float d = _beatFluxHistory[i] - avg;
                variance += d * d;
            }
            variance /= _beatFluxHistory.Length;
            float stdDev = Mathf.Sqrt(variance);

            threshold = Mathf.Max(threshFloor, avg + stdDev * stdDevGain);
            _beatCooldownRemaining = Mathf.Max(0f, _beatCooldownRemaining - deltaSeconds);

            bool isPeak = _beatPrevSmoothedFlux > threshold
                && _beatPrevSmoothedFlux >= _beatSmoothedFlux
                && _beatPrevSmoothedFlux >= minTrigFlux;

            bool onset = _beatCooldownRemaining <= 0f && isPeak;
            displayedFlux = onset ? _beatPrevSmoothedFlux : _beatSmoothedFlux;

            _beatFluxHistory[_beatFluxHistoryIndex] = _beatSmoothedFlux;
            _beatFluxHistoryIndex = (_beatFluxHistoryIndex + 1) % _beatFluxHistory.Length;
            _beatPrevSmoothedFlux = _beatSmoothedFlux;

            if (onset)
            {
                _beatCooldownRemaining = 0.12f;
            }

            StepKickEnvelope(_currentWindowLevels[_activeWindowIndex], onset, deltaSeconds);
            confidence = Mathf.Clamp01(displayedFlux / Mathf.Max(0.001f, threshold * 2.2f));
        }

        private void StepKickEnvelope(float rawKickLevel, bool onset, float deltaSeconds)
        {
            const float NOISE_FLOOR_ATTACK_RATE = 1.15f;
            const float NOISE_FLOOR_RELEASE_RATE = 0.35f;
            const float LEVEL_GATE = 0.025f;
            const float BREATH_GAIN = 0.18f;
            const float MAX_BREATH = 0.11f;
            const float ONSET_MIN_IMPULSE = 0.48f;
            const float ONSET_GAIN = 0.95f;
            const float ENVELOPE_ATTACK_RATE = 42f;
            const float ENVELOPE_RELEASE_RATE = 11.5f;

            float safeRaw = Mathf.Clamp01(rawKickLevel);
            float floorRate = safeRaw > _kickNoiseFloor ? NOISE_FLOOR_ATTACK_RATE : NOISE_FLOOR_RELEASE_RATE;
            float floorBlend = Mathf.Clamp01(1f - Mathf.Exp(-floorRate * deltaSeconds));
            _kickNoiseFloor += (safeRaw - _kickNoiseFloor) * floorBlend;

            _kickLevel = Mathf.Clamp01(safeRaw - _kickNoiseFloor - LEVEL_GATE);
            float breathTarget = Mathf.Min(MAX_BREATH, _kickLevel * BREATH_GAIN);
            float onsetTarget = onset ? Mathf.Max(ONSET_MIN_IMPULSE, _kickLevel * ONSET_GAIN) : 0f;
            float targetEnvelope = Mathf.Max(breathTarget, onsetTarget);

            float envelopeRate = targetEnvelope > _kickEnvelope ? ENVELOPE_ATTACK_RATE : ENVELOPE_RELEASE_RATE;
            float envBlend = Mathf.Clamp01(1f - Mathf.Exp(-envelopeRate * deltaSeconds));

            _kickEnvelope = Mathf.Clamp01(Mathf.Max(
                breathTarget,
                _kickEnvelope + (targetEnvelope - _kickEnvelope) * envBlend
            ));
            _kickOnset = onset ? 1f : 0f;
        }
    }
}
