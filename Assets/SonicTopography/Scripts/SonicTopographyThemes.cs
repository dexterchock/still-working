using System;
using UnityEngine;

namespace SonicTopography
{
    /// <summary>
    /// Preset color themes for Sonic Topography.
    /// Index 0 (Nocturnal / Cosmos) serves as the default theme on first boot-up,
    /// when no theme is selected, and as the fallback when album art is unavailable.
    /// </summary>
    public enum SonicThemePreset
    {
        Nocturnal = 0,        // Cosmos (System Default)
        BlueHour = 1,         // Solaris
        NeonTokyo = 2,        // Cyberpunk
        CyberForest = 3,      // Emerald Abyss
        MinimalMonochrome = 4,// Platinum
        KoiPond = 5,          // Sakura Dusk
        CoralReef = 6,        // Aurora
        MossGlass = 7,        // Verdant Glow
        WineSignal = 8,       // Velvet Twilight
        GlacierDay = 9,       // Glacial Ice
        PorcelainTeal = 10,   // Ocean Trench
        DaybreakLime = 11,    // Volcanic Magma
        InkWash = 12          // Obsidian Ink
    }

    [Serializable]
    public struct SonicThemeColors
    {
        public string id;
        public string name;
        public Color uBaseColor1;
        public Color uBaseColor2;
        public Color uFogColor;
        public Color uCoolCore;
        public Color uCoolEdge;
        public Color uWarmCore;
        public Color uWarmEdge;
        public Color uRippleColor;
        public float uGlowIntensity;

        public static SonicThemeColors Lerp(SonicThemeColors a, SonicThemeColors b, float t)
        {
            t = Mathf.Clamp01(t);
            return new SonicThemeColors
            {
                id = b.id,
                name = b.name,
                uBaseColor1 = Color.Lerp(a.uBaseColor1, b.uBaseColor1, t),
                uBaseColor2 = Color.Lerp(a.uBaseColor2, b.uBaseColor2, t),
                uFogColor = Color.Lerp(a.uFogColor, b.uFogColor, t),
                uCoolCore = Color.Lerp(a.uCoolCore, b.uCoolCore, t),
                uCoolEdge = Color.Lerp(a.uCoolEdge, b.uCoolEdge, t),
                uWarmCore = Color.Lerp(a.uWarmCore, b.uWarmCore, t),
                uWarmEdge = Color.Lerp(a.uWarmEdge, b.uWarmEdge, t),
                uRippleColor = Color.Lerp(a.uRippleColor, b.uRippleColor, t),
                uGlowIntensity = Mathf.Lerp(a.uGlowIntensity, b.uGlowIntensity, t)
            };
        }
    }

    [Serializable]
    public class GroundEqSettings
    {
        [Tooltip("8 bands (0..100): SubBass, Bass, LowMid, Mid, HighMid, Presence, Brilliance, Air")]
        public float[] bands = new float[8] { 90f, 92f, 50f, 50f, 50f, 50f, 50f, 48f };

        public bool[] enabledBands = new bool[8] { true, true, true, true, true, true, true, true };

        [Range(0f, 100f)]
        public float motionSpeed = 50f;

        [Range(0f, 100f)]
        public float amplitude = 50f;

        [Range(0f, 100f)]
        [Tooltip("Controls pillar grid resolution (96x96 to 224x224; default 46 -> 155x155 = 24,025 pillars)")]
        public float terrainDensity = 46f;

        public bool floatingBlocksEnabled = true;

        [Range(0f, 100f)]
        public float floatingBlockIntensity = 65f;

        [Range(0f, 100f)]
        public float floatingBlockMinSize = 12f;

        [Range(0f, 100f)]
        public float floatingBlockMaxSize = 30f;

        [Range(0f, 100f)]
        public float floatingBlockSpeed = 77f;

        [Range(10, 160)]
        public int floatingBlockCount = 96;
    }

    public struct TerrainGridSettings
    {
        public float density;
        public int gridSize;
        public float spacing;
        public float boxWidth;
        public int instanceCount;
        public float terrainSize;
    }

    public static class SonicTopographyConfig
    {
        public const float DEFAULT_GROUND_EQ_VALUE = 50f;
        public const float TERRAIN_BASE_SIZE = 168f;
        public const int TERRAIN_MIN_GRID_SIZE = 96;
        public const int TERRAIN_MAX_GRID_SIZE = 224;

        private const float MAX_KICK_DEFORM = 0.75f;
        private const float MAX_SHADER_SUB_BASS = 1.2f;
        private const float MAX_SHADER_BASS = 1.15f;
        private const float KICK_SUB_BASS_GAIN = 1.28f;
        private const float KICK_BASS_GAIN = 1.15f;
        private const float BASE_SUB_BASS_GAIN = 0.22f;
        private const float BASE_BASS_GAIN = 0.20f;

        public static float ClampAnimationBlend(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp01(value);
        }

        public static float ApplyGroundEqBandValue(float value, float[] bands, int bandIndex, float max = 1f)
        {
            float eq = (bands != null && bandIndex >= 0 && bandIndex < bands.Length)
                ? Mathf.Clamp(bands[bandIndex], 0f, 100f)
                : DEFAULT_GROUND_EQ_VALUE;

            float delta = (eq - DEFAULT_GROUND_EQ_VALUE) / DEFAULT_GROUND_EQ_VALUE;
            if (delta >= 0f)
            {
                return Mathf.Clamp(value * (1f + delta * 1.8f), 0f, max);
            }

            float dullness = Mathf.Abs(delta);
            return Mathf.Clamp(Mathf.Max(0f, value - dullness * 0.35f) * (1f - dullness * 0.35f), 0f, max);
        }

        public static void DeriveKickFollowLowBands(
            float kickEnvelope,
            float subBassEnergy,
            float bassEnergy,
            float[] bands,
            bool[] enabledBands,
            out float outSubBass,
            out float outBass)
        {
            float safeKick = Mathf.Clamp(kickEnvelope, 0f, MAX_KICK_DEFORM);
            float normalizedKick = safeKick / MAX_KICK_DEFORM;
            float safeSubBass = Mathf.Clamp01(subBassEnergy);
            float safeBass = Mathf.Clamp01(bassEnergy);

            float subBassInput = safeSubBass * BASE_SUB_BASS_GAIN + normalizedKick * KICK_SUB_BASS_GAIN;
            float bassInput = safeBass * BASE_BASS_GAIN + normalizedKick * KICK_BASS_GAIN;

            bool subEnabled = enabledBands == null || enabledBands.Length == 0 || enabledBands[0];
            bool bassEnabled = enabledBands == null || enabledBands.Length <= 1 || enabledBands[1];

            outSubBass = subEnabled
                ? ApplyGroundEqBandValue(subBassInput, bands, 0, MAX_SHADER_SUB_BASS)
                : 0f;
            outBass = bassEnabled
                ? ApplyGroundEqBandValue(bassInput, bands, 1, MAX_SHADER_BASS)
                : 0f;
        }

        public static TerrainGridSettings DeriveTerrainGridSettings(float terrainDensity)
        {
            float density = Mathf.Clamp(terrainDensity, 0f, 100f);
            int gridSize = Mathf.RoundToInt(TERRAIN_MIN_GRID_SIZE + ((TERRAIN_MAX_GRID_SIZE - TERRAIN_MIN_GRID_SIZE) * density) / 100f);
            float spacing = TERRAIN_BASE_SIZE / gridSize;
            return new TerrainGridSettings
            {
                density = density,
                gridSize = gridSize,
                spacing = spacing,
                boxWidth = spacing * (0.9f / 1.05f),
                instanceCount = gridSize * gridSize,
                terrainSize = TERRAIN_BASE_SIZE
            };
        }

        public static SonicThemeColors GetTheme(SonicThemePreset preset)
        {
            switch (preset)
            {
                case SonicThemePreset.Nocturnal:
                    return new SonicThemeColors
                    {
                        id = "cosmos",
                        name = "Cosmos",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.012f, 0.015f, 0.040f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.10f, 0.40f, 1.0f, 1f),
                        uCoolEdge = new Color(0.40f, 0.65f, 1.0f, 1f),
                        uWarmCore = new Color(0.68f, 0.18f, 1.0f, 1f),
                        uWarmEdge = new Color(0.85f, 0.55f, 1.0f, 1f),
                        uRippleColor = new Color(0.20f, 0.90f, 1.0f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.BlueHour:
                    return new SonicThemeColors
                    {
                        id = "solaris",
                        name = "Solaris",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.035f, 0.010f, 0.012f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.85f, 0.15f, 0.25f, 1f),
                        uCoolEdge = new Color(0.98f, 0.35f, 0.45f, 1f),
                        uWarmCore = new Color(1.0f, 0.62f, 0.05f, 1f),
                        uWarmEdge = new Color(1.0f, 0.85f, 0.40f, 1f),
                        uRippleColor = new Color(1.0f, 0.92f, 0.65f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.NeonTokyo:
                    return new SonicThemeColors
                    {
                        id = "cyberpunk",
                        name = "Cyberpunk",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.015f, 0.008f, 0.035f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.00f, 0.68f, 1.0f, 1f),
                        uCoolEdge = new Color(0.35f, 0.85f, 1.0f, 1f),
                        uWarmCore = new Color(1.0f, 0.15f, 0.65f, 1f),
                        uWarmEdge = new Color(1.0f, 0.55f, 0.85f, 1f),
                        uRippleColor = new Color(0.15f, 1.0f, 0.92f, 1f),
                        uGlowIntensity = 1.45f
                    };

                case SonicThemePreset.CyberForest:
                    return new SonicThemeColors
                    {
                        id = "emerald-abyss",
                        name = "Emerald Abyss",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.006f, 0.025f, 0.018f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.00f, 0.55f, 0.62f, 1f),
                        uCoolEdge = new Color(0.18f, 0.80f, 0.78f, 1f),
                        uWarmCore = new Color(0.08f, 0.95f, 0.55f, 1f),
                        uWarmEdge = new Color(0.55f, 1.0f, 0.85f, 1f),
                        uRippleColor = new Color(0.28f, 1.0f, 0.82f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.MinimalMonochrome:
                    return new SonicThemeColors
                    {
                        id = "platinum",
                        name = "Platinum",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.020f, 0.022f, 0.026f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.48f, 0.55f, 0.65f, 1f),
                        uCoolEdge = new Color(0.72f, 0.78f, 0.86f, 1f),
                        uWarmCore = new Color(0.94f, 0.96f, 1.0f, 1f),
                        uWarmEdge = new Color(1.0f, 1.0f, 1.0f, 1f),
                        uRippleColor = new Color(1.0f, 1.0f, 1.0f, 1f),
                        uGlowIntensity = 1.20f
                    };

                case SonicThemePreset.KoiPond:
                    return new SonicThemeColors
                    {
                        id = "sakura-dusk",
                        name = "Sakura Dusk",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.020f, 0.010f, 0.035f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.26f, 0.22f, 0.72f, 1f),
                        uCoolEdge = new Color(0.52f, 0.45f, 0.90f, 1f),
                        uWarmCore = new Color(1.0f, 0.42f, 0.55f, 1f),
                        uWarmEdge = new Color(1.0f, 0.70f, 0.80f, 1f),
                        uRippleColor = new Color(1.0f, 0.82f, 0.70f, 1f),
                        uGlowIntensity = 1.30f
                    };

                case SonicThemePreset.CoralReef:
                    return new SonicThemeColors
                    {
                        id = "aurora",
                        name = "Aurora",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.006f, 0.018f, 0.028f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.00f, 0.65f, 0.88f, 1f),
                        uCoolEdge = new Color(0.28f, 0.85f, 0.95f, 1f),
                        uWarmCore = new Color(0.12f, 0.92f, 0.48f, 1f),
                        uWarmEdge = new Color(0.60f, 1.0f, 0.78f, 1f),
                        uRippleColor = new Color(0.72f, 0.35f, 1.0f, 1f),
                        uGlowIntensity = 1.40f
                    };

                case SonicThemePreset.MossGlass:
                    return new SonicThemeColors
                    {
                        id = "verdant",
                        name = "Verdant Glow",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.008f, 0.025f, 0.015f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.06f, 0.60f, 0.38f, 1f),
                        uCoolEdge = new Color(0.25f, 0.82f, 0.52f, 1f),
                        uWarmCore = new Color(0.75f, 0.95f, 0.10f, 1f),
                        uWarmEdge = new Color(0.88f, 1.0f, 0.45f, 1f),
                        uRippleColor = new Color(1.0f, 0.85f, 0.22f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.WineSignal:
                    return new SonicThemeColors
                    {
                        id = "velvet-twilight",
                        name = "Velvet Twilight",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.028f, 0.008f, 0.035f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.50f, 0.08f, 0.78f, 1f),
                        uCoolEdge = new Color(0.75f, 0.30f, 0.95f, 1f),
                        uWarmCore = new Color(1.0f, 0.48f, 0.06f, 1f),
                        uWarmEdge = new Color(1.0f, 0.75f, 0.38f, 1f),
                        uRippleColor = new Color(1.0f, 0.85f, 0.48f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.GlacierDay:
                    return new SonicThemeColors
                    {
                        id = "glacial-ice",
                        name = "Glacial Ice",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.008f, 0.016f, 0.035f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.06f, 0.32f, 0.68f, 1f),
                        uCoolEdge = new Color(0.28f, 0.62f, 0.92f, 1f),
                        uWarmCore = new Color(0.25f, 0.85f, 1.0f, 1f),
                        uWarmEdge = new Color(0.75f, 0.95f, 1.0f, 1f),
                        uRippleColor = new Color(1.0f, 1.0f, 1.0f, 1f),
                        uGlowIntensity = 1.30f
                    };

                case SonicThemePreset.PorcelainTeal:
                    return new SonicThemeColors
                    {
                        id = "ocean-trench",
                        name = "Ocean Trench",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.005f, 0.018f, 0.032f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.02f, 0.42f, 0.88f, 1f),
                        uCoolEdge = new Color(0.22f, 0.68f, 0.98f, 1f),
                        uWarmCore = new Color(0.00f, 0.92f, 0.80f, 1f),
                        uWarmEdge = new Color(0.50f, 1.0f, 0.92f, 1f),
                        uRippleColor = new Color(1.0f, 0.38f, 0.58f, 1f),
                        uGlowIntensity = 1.35f
                    };

                case SonicThemePreset.DaybreakLime:
                    return new SonicThemeColors
                    {
                        id = "volcanic-magma",
                        name = "Volcanic Magma",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.035f, 0.008f, 0.010f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.72f, 0.08f, 0.22f, 1f),
                        uCoolEdge = new Color(0.92f, 0.25f, 0.38f, 1f),
                        uWarmCore = new Color(1.0f, 0.38f, 0.00f, 1f),
                        uWarmEdge = new Color(1.0f, 0.72f, 0.22f, 1f),
                        uRippleColor = new Color(1.0f, 0.90f, 0.32f, 1f),
                        uGlowIntensity = 1.40f
                    };

                case SonicThemePreset.InkWash:
                    return new SonicThemeColors
                    {
                        id = "obsidian-ink",
                        name = "Obsidian Ink",
                        uBaseColor1 = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uBaseColor2 = new Color(0.015f, 0.015f, 0.018f, 1f),
                        uFogColor = new Color(0.0f, 0.0f, 0.0f, 1f),
                        uCoolCore = new Color(0.35f, 0.38f, 0.44f, 1f),
                        uCoolEdge = new Color(0.58f, 0.62f, 0.68f, 1f),
                        uWarmCore = new Color(0.92f, 0.94f, 0.98f, 1f),
                        uWarmEdge = new Color(1.0f, 1.0f, 1.0f, 1f),
                        uRippleColor = new Color(1.0f, 1.0f, 1.0f, 1f),
                        uGlowIntensity = 1.25f
                    };

                default:
                    return GetTheme(SonicThemePreset.Nocturnal);
            }
        }
    }
}
