Shader "SonicTopography/TerrainEmissionBake"
{
    Properties
    {
        _uGlowIntensity ("Glow Intensity", Float) = 1.0
        _uArMode ("AR Mode Blend", Range(0, 1)) = 1.0
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 100

        Pass
        {
            Name "TerrainEmissionBake"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _uTime;

            // Frequency envelopes
            float _uSubBass;
            float _uBass;
            float _uLowMid;
            float _uMid;
            float _uHighMid;
            float _uPresence;
            float _uBrilliance;
            float _uAir;

            // Timbral & Energy
            float _uWarmth;
            float _uBrightness;
            float _uSharpness;
            float _uSmoothness;
            float _uDensity;
            float _uSpectralCentroid;
            float _uEnergy;
            float _uAmplitude;
            float _uArMode;

            // Theme Uniforms (stored in sRGB 0..1 space matching SonicTerrainPillars.shader)
            float4 _uBaseColor1;
            float4 _uBaseColor2;
            float4 _uFogColor;
            float4 _uBackdropLinear;
            float4 _uCoolCore;
            float4 _uCoolEdge;
            float4 _uWarmCore;
            float4 _uWarmEdge;
            float4 _uRippleColor;
            float _uGlowIntensity;

            // Ripples: xy = pos, z = time, w = strength
            float4 _uRipplePosTimeStrength[10];
            // Ripples state: x = isActive, y = rippleType (0 = normal, 1 = white)
            float4 _uRippleState[10];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            // GLSL-compatible smoothstep safe for edge0 > edge1 on mobile Vulkan/SPIR-V
            float glslSmoothstep(float edge0, float edge1, float x)
            {
                float t = saturate((x - edge0) / (edge1 - edge0));
                return t * t * (3.0 - 2.0 * t);
            }

            // 2D Simplex noise (exact match to SonicTerrainPillars.shader)
            float3 mod289_3(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float2 mod289_2(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float3 permute(float3 x) { return mod289_3(((x * 34.0) + 1.0) * x); }

            float snoise(float2 v)
            {
                const float4 C = float4(
                    0.211324865405187,
                    0.366025403784439,
                    -0.577350269189626,
                    0.024390243902439
                );
                float2 i  = floor(v + dot(v, C.yy));
                float2 x0 = v - i + dot(i, C.xx);
                float2 i1 = (x0.x > x0.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
                float4 x12 = x0.xyxy + C.xxzz;
                x12.xy -= i1;
                i = mod289_2(i);
                float3 p = permute(permute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));
                float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
                m = m * m;
                m = m * m;
                float3 x = 2.0 * frac(p * C.www) - 1.0;
                float3 h = abs(x) - 0.5;
                float3 ox = floor(x + 0.5);
                float3 a0 = x - ox;
                m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
                float3 g;
                g.x  = a0.x  * x0.x  + h.x  * x0.y;
                g.yz = a0.yz * x12.xz + h.yz * x12.yw;
                return 130.0 * dot(m, g);
            }

            float randomHash(float2 st)
            {
                return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
            }

            float3 srgbToLinearSafe(float3 c)
            {
                float3 clamped = saturate(c);
                float3 excess = max(0.0, c - 1.0);
                float3 lin = pow(clamped, 2.2);
                return lin + excess;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Map uv (0..1) -> platter local XZ (-77.5..+77.5), matching platterRight (+X) and platterFwd (+Z)
                float2 pos2D = (input.uv * 2.0 - 1.0) * 77.5;
                float centerDist = length(pos2D);
                if (centerDist > 77.5)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                float circleMask = 1.0 - glslSmoothstep(75.5, 77.5, centerDist);

                // Quantized cluster hash so 64x64 texels stay temporally stable without sub-pixel noise flicker
                float2 cellId = floor(pos2D * 0.45);
                float rnd = randomHash(cellId);

                // Match SonicTerrainPillars.shader AR elevation math
                float arGlobalFalloff = lerp(0.62, 1.0, glslSmoothstep(77.5, 44.0, centerDist));

                // 1. AR organic crystal-mosaic idle topography
                float2 arDrift1 = pos2D * 0.048 + float2(_uTime * 0.075, -_uTime * 0.045);
                float2 arDrift2 = pos2D * 0.135 + float2(-_uTime * 0.09, _uTime * 0.065);
                float arHillsVal = snoise(arDrift1) * 0.5 + 0.5;
                float arRidges = snoise(arDrift2) * 0.5 + 0.5;
                float arGentleWave = sin(pos2D.x * 0.11 - pos2D.y * 0.08 + _uTime * 0.45) * 0.5 + 0.5;
                float arTileStep = frac(rnd * 7.13);
                float arRimWeight = lerp(0.58, 1.0, glslSmoothstep(77.5, 52.0, centerDist));
                float arIdleElevation = (arHillsVal * 1.15 + arRidges * 0.72 + arGentleWave * 0.38 + arTileStep * 0.45) * arRimWeight;

                // 2. Frequency regions & displacements (matching SonicTerrainPillars.shader)
                float subRegion = glslSmoothstep(25.0, 0.0, centerDist);
                float subLift = _uSubBass * subRegion * 5.0;

                float bassLift = 0.0;
                if (centerDist < 40.0 && _uBass > 0.0001)
                {
                    float bassNoise = snoise(pos2D * 0.1 - float2(0.0, _uTime * 0.2));
                    float bassRegion = glslSmoothstep(35.0, 5.0, centerDist + bassNoise * 5.0);
                    bassLift = _uBass * bassRegion * glslSmoothstep(0.0, 1.0, 0.45 + rnd * 0.55 + _uDensity * 0.5) * 4.0;
                }

                float lowMidLift = 0.0;
                if (_uLowMid > 0.0001)
                {
                    float lowMidNoise = snoise(pos2D * 0.05 + float2(_uTime * 0.1, 0.0));
                    lowMidLift = _uLowMid * (lowMidNoise * 0.5 + 0.5) * 2.5;
                }

                float midLift = 0.0;
                if (_uMid > 0.0001)
                {
                    float riverFlow = sin(pos2D.x * 0.2 + pos2D.y * 0.2 + snoise(pos2D * 0.1) * 2.0 - _uTime * 2.0);
                    midLift = _uMid * max(0.0, riverFlow) * 3.0;
                }

                float highMidLift = 0.0;
                if (frac(rnd * 13.3) > 0.75)
                {
                    float highMidRegion = glslSmoothstep(10.0, 45.0, centerDist);
                    highMidLift = _uHighMid * highMidRegion * frac(rnd * 7.7) * 2.5;
                }

                float audioElevation = (subLift + bassLift + lowMidLift + midLift + highMidLift) * arGlobalFalloff;
                audioElevation = max(0.0, audioElevation - 0.2) * _uAmplitude;

                float elevation = arIdleElevation + audioElevation;

                // 3. Active ripples (exact match to SonicTerrainPillars.shader)
                float rippleElevation = 0.0;
                float rippleIntensityNormal = 0.0;
                float rippleIntensityWhite = 0.0;
                const float baseSpeed = 15.0;
                const float baseWidth = 3.0;

                [unroll]
                for (int i = 0; i < 10; i++)
                {
                    if (_uRippleState[i].x > 0.0)
                    {
                        float2 rPos = _uRipplePosTimeStrength[i].xy;
                        float rTime = _uRipplePosTimeStrength[i].z;
                        float rStrength = _uRipplePosTimeStrength[i].w;
                        float rType = _uRippleState[i].y;

                        float dist = length(pos2D - rPos);
                        float timeSince = max(0.0, _uTime - rTime);

                        float curSpeed = (rType > 0.5) ? 20.0 : baseSpeed;
                        float curWidth = (rType > 0.5) ? 1.0 : baseWidth;
                        float curFadeDist = (rType > 0.5) ? 8.0 : 15.0;
                        float elevationScale = (rType > 0.5) ? 1.0 : 4.0;

                        float waveRadius = timeSince * curSpeed;
                        float d = dist - waveRadius;
                        float rippleWave = exp(-(d * d) / curWidth);
                        float fade = exp(-waveRadius / curFadeDist);
                        float rPulse = rippleWave * fade * rStrength;

                        rippleElevation += rPulse * elevationScale;
                        if (rType > 0.5)
                        {
                            rippleIntensityWhite += rPulse;
                        }
                        else
                        {
                            rippleIntensityNormal += rPulse;
                        }
                    }
                }

                elevation += rippleElevation;
                float2 rippleAnim = float2(saturate(rippleIntensityNormal), saturate(rippleIntensityWhite));

                // 4. Theme palette matching SonicTerrainPillars.shader AR crystal colors
                float3 coolCore = _uCoolCore.rgb;
                float3 coolEdge = _uCoolEdge.rgb;
                float3 warmCore = _uWarmCore.rgb;
                float3 warmEdge = _uWarmEdge.rgb;

                float warmBlend = glslSmoothstep(0.0, 1.0, _uWarmth * 1.5 + (0.5 - centerDist / 80.0));
                float arMusicLift = saturate(audioElevation / 5.5 + rippleAnim.x * 0.65 + rippleAnim.y * 0.85);
                float arWarmFactor = saturate(warmBlend * 0.68 + arMusicLift * 0.32);

                float3 arCoreMix = lerp(coolCore, warmCore, arWarmFactor);
                float3 arEdgeMix = lerp(coolEdge, warmEdge, arWarmFactor);
                float3 brightCool = lerp(coolCore, float3(1.0, 1.0, 1.0), 0.28);

                float3 srgbCol = lerp(arCoreMix, arEdgeMix, 0.35 + 0.50 * arRidges);
                float accentMix = glslSmoothstep(0.45, 0.95, arHillsVal * 0.65 + frac(rnd * 23.7) * 0.35) * 0.35;
                srgbCol = lerp(srgbCol, _uRippleColor.rgb, accentMix);
                srgbCol = lerp(srgbCol, brightCool, _uBrightness * 0.40);

                // Ripple tint override on bright ripple crests
                srgbCol = lerp(srgbCol, _uRippleColor.rgb, saturate(rippleAnim.x * 0.85));

                float3 linearCol = srgbToLinearSafe(srgbCol);

                // 5. High-Contrast Physical HDR Emission Curve:
                //    Dark valleys -> ~0.02-0.05 (no muddy fog)
                //    Mid-glow crystal -> ~0.20-0.45
                //    Bright ridges, music peaks & ripples -> HOT HDR linear 1.4-3.6 with subtle incandescent core
                float ridgeNorm = saturate((elevation - 0.62) / 3.05);
                float emissiveFactor = saturate(ridgeNorm * 0.72 + arMusicLift * 0.62 + _uPresence * 0.10 * arRidges);
                float contrastCurve = emissiveFactor * emissiveFactor; // pow(emissiveFactor, 2.0)

                float glowScale = clamp(_uGlowIntensity, 0.75, 1.65);
                float hdrPower = (0.025 + contrastCurve * (1.95 + arMusicLift * 1.50)) * glowScale;

                // Subtle white-hot core on extreme peaks & active ripples
                float hotCore = smoothstep(0.58, 1.0, emissiveFactor) * 0.32;
                float3 emittedRgb = lerp(linearCol, lerp(linearCol, float3(1.0, 0.98, 1.0), 0.65), hotCore) * hdrPower;

                // Explicit HDR ripple bursts so expanding waves reflect & bounce vividly across the room
                float3 rippleLinear = srgbToLinearSafe(_uRippleColor.rgb);
                emittedRgb += rippleLinear * (rippleAnim.x * 1.75 * glowScale);
                emittedRgb += lerp(rippleLinear, float3(1.0, 1.0, 1.0), 0.75) * (rippleAnim.y * 2.35 * glowScale);

                emittedRgb *= circleMask;
                return half4((half3)emittedRgb, (half)circleMask);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
