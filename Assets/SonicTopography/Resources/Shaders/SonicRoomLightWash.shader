Shader "SonicTopography/RoomLightWash"
{
    Properties
    {
        _uRoomGlowIntensity ("Room Glow Intensity", Float) = 1.0
        _uLocalDiscFadeRadius ("Local Disc Fade Radius (0=off)", Float) = 0.0
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-5"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "RoomLightWash"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            ColorMask RGBA

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Global uniforms driven by SonicMixedRealityManager / SonicTopographyVisualizer
            float4 _uSonicRoomPlatterCenterWS;
            float4 _uSonicRoomPlatterNormalWS;
            float4 _uSonicRoomPlatterRightWS;
            float4 _uSonicRoomPlatterForwardWS;
            float4 _uSonicRoomParams; // x = worldRadius, y = platterScale, z = audioEnergy, w = bassEnergy
            float4 _uSonicRoomFx;     // x = warmth, y = roomGlowIntensity, z = arVisibility, w = time
            float4 _uSonicRoomOcc;    // x = occlusionEnabled (0), y = rippleSpeedScale, z = idlePulse, w = unused

            float4 _uSonicRoomCoolCore;
            float4 _uSonicRoomWarmCore;
            float4 _uSonicRoomRippleColor;

            float4 _uSonicRoomRipplePosTimeStrength[10];
            float4 _uSonicRoomRippleState[10];

            float _uRoomGlowIntensity;
            float _uLocalDiscFadeRadius;

            float3 srgbToLinearSafe(float3 c)
            {
                float3 clamped = saturate(c);
                float3 excess = max(0.0, c - 1.0);
                float3 lin = pow(clamped, 2.2);
                return lin + excess;
            }

            // Perceptually balanced tint so warm amber/gold themes match cool cyan/blue brightness without flooding
            float3 balanceThemeTint(float3 c)
            {
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                float maxCh = max(c.r, max(c.g, c.b));
                float denom = max(0.55, luma * 1.65 + maxCh * 0.35);
                return c / denom;
            }

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise2D(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1.0, 0.0));
                float c = hash21(i + float2(0.0, 1.0));
                float d = hash21(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // Soft cloudy organic caustic pattern for ceiling bounce (never sharp rings)
            float organicCaustic2D(float2 uv, float t)
            {
                float2 driftA = float2(t * 0.13, -t * 0.09);
                float2 driftB = float2(-t * 0.11, t * 0.15);
                float n1 = valueNoise2D(uv * 1.35 + driftA);
                float n2 = valueNoise2D(uv * 2.45 + driftB + float2(n1 * 0.85, -n1 * 0.65));
                float n3 = valueNoise2D(uv * 0.72 - driftA * 0.60);
                float combined = n1 * 0.42 + n2 * 0.38 + n3 * 0.20;
                return smoothstep(0.20, 0.84, combined);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 posWorld   : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.posWorld = posInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float visibility = _uSonicRoomFx.z;
                float glowIntensity = _uSonicRoomFx.y * _uRoomGlowIntensity;
                if (visibility <= 0.001 || glowIntensity <= 0.001)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                float3 platterCenter = _uSonicRoomPlatterCenterWS.xyz;
                float3 platterN = normalize(_uSonicRoomPlatterNormalWS.xyz);
                float3 platterRight = normalize(_uSonicRoomPlatterRightWS.xyz);
                float3 platterFwd = normalize(_uSonicRoomPlatterForwardWS.xyz);

                float platterRadius = max(0.08, _uSonicRoomParams.x);
                float platterScale = max(0.0005, _uSonicRoomParams.y);
                float audioEnergy = saturate(_uSonicRoomParams.z);
                float bassEnergy = saturate(_uSonicRoomParams.w);
                float warmth = saturate(_uSonicRoomFx.x);
                float uTime = _uSonicRoomFx.w;
                float idlePulse = _uSonicRoomOcc.z;

                float3 surfN = normalize(input.normalWS);
                float3 rel = input.posWorld - platterCenter;
                float heightOnPlatter = dot(rel, platterN);
                float3 inPlaneVec = rel - platterN * heightOnPlatter;
                float radialDist = length(inPlaneVec);

                // Closest emitting point on the glowing circular LED slab
                float clampedRadial = min(radialDist, platterRadius);
                float3 inPlaneDir = (radialDist > 0.0005) ? (inPlaneVec / radialDist) : platterRight;
                float3 closestOnDisc = platterCenter + inPlaneDir * clampedRadial;

                // True distance from room surface to closest edge/face of glowing slab
                float3 fromDisc = input.posWorld - closestOnDisc;
                float closestDist = length(fromDisc);

                // Quick range cull for far room geometry so distant walls stay completely natural
                float maxRange = clamp(2.20 + platterRadius * 1.80, 2.30, 4.00);
                float maxCeilingHeight = 3.40;
                if (closestDist >= maxRange && heightOnPlatter <= 0.15)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }
                if (closestDist >= maxCeilingHeight)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Slightly elevated effective LED pillar height for coplanar surfaces (desk/floor right around slab)
                float emitLift = clamp(platterRadius * 0.18, 0.035, 0.14);
                float nearPlaneWeight = saturate(1.0 - abs(heightOnPlatter) / max(0.20, platterRadius * 0.85));
                float3 effectiveEmitter = closestOnDisc + platterN * (emitLift * nearPlaneWeight);
                float3 toEmitterVec = effectiveEmitter - input.posWorld;
                float toEmitterDist = max(0.001, length(toEmitterVec));
                float3 toEmitter = toEmitterVec / toEmitterDist;

                // True directional Lambertian facing (zero back-face ambient leak)
                // plus gentle grazing wrap ONLY for nearly coplanar surfaces at the platter base (floor/table it sits on)
                float nDotL = dot(surfN, toEmitter);
                float lambert = saturate(nDotL);
                float coplanarSurface = saturate((dot(surfN, platterN) - 0.72) / 0.28)
                                      * (1.0 - smoothstep(0.03, 0.26, abs(heightOnPlatter)));
                float coplanarWrap = coplanarSurface * saturate(nDotL * 0.65 + 0.35);
                float facing = max(lambert, coplanarWrap * 0.78);

                // View-grazing silhouette softening so MRUK plane edges never show hard glowing outlines
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.posWorld);
                float nDotV = abs(dot(surfN, viewDir));
                float silhouetteFade = smoothstep(0.04, 0.24, nDotV);

                if (facing <= 0.001 || silhouetteFade <= 0.001)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Perceptually balanced theme colors
                float3 coolCol = _uSonicRoomCoolCore.rgb;
                float3 warmCol = _uSonicRoomWarmCore.rgb;
                float3 rippleCol = _uSonicRoomRippleColor.rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                coolCol = srgbToLinearSafe(coolCol);
                warmCol = srgbToLinearSafe(warmCol);
                rippleCol = srgbToLinearSafe(rippleCol);
                #endif
                coolCol = balanceThemeTint(coolCol);
                warmCol = balanceThemeTint(warmCol);
                rippleCol = balanceThemeTint(rippleCol);

                // Smooth organic ripple/beat energy swell (NO sharp concentric rings!)
                float colorRippleAcc = 0.0;
                float whiteRippleAcc = 0.0;
                float sideScale = max(0.65, platterRadius * 2.8);
                [unroll]
                for (int i = 0; i < 10; i++)
                {
                    if (_uSonicRoomRippleState[i].x > 0.5)
                    {
                        float2 rPos = _uSonicRoomRipplePosTimeStrength[i].xy;
                        float rTime = _uSonicRoomRipplePosTimeStrength[i].z;
                        float rStrength = min(_uSonicRoomRipplePosTimeStrength[i].w, 1.8);
                        float age = max(0.0, uTime - rTime);

                        if (age < 2.6)
                        {
                            float env = smoothstep(0.0, 0.16, age) * exp(-age * 1.35) * rStrength;
                            float3 rOriginWS = platterCenter + (platterRight * rPos.x + platterFwd * rPos.y) * platterScale;
                            float sideDist = length(input.posWorld - rOriginWS);
                            // Wide directional proximity bias toward the ripple's side of the slab
                            float sideBias = 0.55 + 0.45 * exp(-(sideDist * sideDist) / (sideScale * sideScale));
                            float swell = env * sideBias;

                            if (_uSonicRoomRippleState[i].y > 0.5)
                            {
                                whiteRippleAcc += swell;
                            }
                            else
                            {
                                colorRippleAcc += swell;
                            }
                        }
                    }
                }
                float colorRipple = 1.0 - exp(-colorRippleAcc * 0.72);
                float whiteRipple = 1.0 - exp(-whiteRippleAcc * 0.62);
                float totalRipple = saturate(colorRipple * 0.60 + whiteRipple * 0.50);

                // Overall luminous drive: calm breathing at idle, rich swell on beats & ripples
                float idleDrive = 0.36 + 0.10 * idlePulse;
                float musicDrive = saturate(audioEnergy * 0.55 + bassEnergy * 0.45);
                float totalDrive = idleDrive + musicDrive * 0.48 + totalRipple * 0.38;

                // Blend base theme tint with warm beat shift and ripple accent tint
                float2 planarCoord = float2(dot(inPlaneVec, platterRight), dot(inPlaneVec, platterFwd));
                float softShimmer = valueNoise2D(planarCoord * (1.25 / max(0.35, platterRadius * 2.0)) + float2(uTime * 0.18, -uTime * 0.14));
                float colorMix = saturate(warmth * 0.75 + bassEnergy * 0.20 + (softShimmer - 0.5) * 0.14);
                float3 baseTint = lerp(coolCol, warmCol, colorMix);
                baseTint = lerp(baseTint, rippleCol, saturate(colorRipple * 0.42));
                baseTint = lerp(baseTint, lerp(rippleCol, float3(0.75, 0.84, 0.92), 0.45), saturate(whiteRipple * 0.32));

                // 1. Physically-based inverse-square area-light bounce (tight luminous pool near slab, dark far walls)
                float attenScale = clamp(platterRadius * 2.10, 0.50, 1.45);
                float dNorm = closestDist / attenScale;
                float invSq = 1.0 / (1.0 + dNorm * dNorm * 1.28);
                float rangeRem = saturate(1.0 - closestDist / maxRange);
                float rangeWindow = (rangeRem > 0.0001) ? pow(rangeRem, 1.75) : 0.0;
                float distAtten = invSq * rangeWindow;

                // Gentle downward pool lift directly beneath a floating slab (carpet glow like real suspended LED panel)
                float belowFloating = smoothstep(-0.14, -0.45, heightOnPlatter) * saturate(dot(surfN, platterN));
                float underRatio = radialDist / max(0.55, platterRadius * 1.9 + abs(heightOnPlatter) * 0.55);
                float underCone = exp(-(underRatio * underRatio));
                float underPoolBoost = 1.0 + belowFloating * underCone * 1.05;

                // Very subtle wide shimmer so floor/desk bounce feels softly alive without any rings
                float floorShimmerMod = lerp(0.91, 1.09, softShimmer);
                float bounceScalar = totalDrive * distAtten * facing * underPoolBoost * floorShimmerMod * 0.50;
                float3 lightRgb = baseTint * bounceScalar;

                // 2. Organic cloudy ceiling caustic bounce directly above the platter (surfaces facing down toward slab)
                float aboveHeight = max(0.0, heightOnPlatter);
                float ceilingAlign = saturate((dot(surfN, -platterN) - 0.25) / 0.55);
                float aboveMask = smoothstep(0.18, 0.55, aboveHeight) * ceilingAlign;
                if (aboveMask > 0.002 && aboveHeight < maxCeilingHeight)
                {
                    float ceilHNorm = aboveHeight / 1.45;
                    float ceilRem = saturate(1.0 - aboveHeight / maxCeilingHeight);
                    float ceilHeightAtten = (1.0 / (1.0 + ceilHNorm * ceilHNorm))
                                          * ((ceilRem > 0.0001) ? pow(ceilRem, 1.35) : 0.0);
                    float coneRadius = platterRadius * 1.65 + aboveHeight * 0.72;
                    float ceilRatio = radialDist / max(0.40, coneRadius);
                    float ceilRadialFade = exp(-(ceilRatio * ceilRatio) * (0.95 + 0.15 * ceilRatio));

                    float2 ceilUv = planarCoord / max(0.55, coneRadius * 0.62);
                    float causticRaw = organicCaustic2D(ceilUv, uTime * (1.0 + bassEnergy * 0.20));
                    // Cloudy light patches with soft dark troughs, pulsing gently with bass/ripples
                    float causticPattern = lerp(0.24, 1.0, causticRaw);
                    float causticDrive = (0.34 + 0.08 * idlePulse + musicDrive * 0.52 + totalRipple * 0.42);

                    float3 ceilTint = lerp(coolCol, baseTint, 0.55 + 0.30 * causticRaw);
                    ceilTint = lerp(ceilTint, rippleCol, saturate(totalRipple * 0.35 * causticRaw));

                    // Replace harsh flat ceiling fill with organic caustic bounce (avoid double-stacking with main bounce)
                    float ceilStrength = aboveMask * ceilHeightAtten * ceilRadialFade * causticPattern * causticDrive * 0.46;
                    lightRgb = lerp(lightRgb, lightRgb * 0.30 + ceilTint * ceilStrength, aboveMask);
                }

                // Soft local disc boundary fade (only active on Depth-raycast snapped fallback catcher)
                if (_uLocalDiscFadeRadius > 0.01)
                {
                    float discFade = 1.0 - smoothstep(_uLocalDiscFadeRadius * 0.35, _uLocalDiscFadeRadius * 0.92, radialDist);
                    discFade *= discFade;
                    lightRgb *= discFade;
                }

                // Apply silhouette softening, Room Glow slider, and visibility
                lightRgb *= (silhouetteFade * glowIntensity * visibility);

                // Soft photographic knee compression so high RoomGlow or loud drops NEVER paint opaque solid fog
                lightRgb = lightRgb / (1.0 + lightRgb * 1.35);

                float peakLum = max(lightRgb.r, max(lightRgb.g, lightRgb.b));
                if (peakLum <= 0.0005)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Low soft-capped premultiplied alpha preserves real Quest 3 passthrough room details, textures & shadows
                float outAlpha = min(saturate(peakLum * 0.26), 0.22);

                return half4((half3)lightRgb, (half)outAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
