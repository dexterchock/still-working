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

            // Global uniforms driven by SonicMixedRealityManager
            TEXTURE2D(_uSonicTerrainEmissionTex);
            SAMPLER(sampler_uSonicTerrainEmissionTex);
            float _uSonicRoomHasEmissionTex;

            // Stereo Passthrough Camera Access (PCA) real-surface albedo relighting uniforms
            TEXTURE2D(_uSonicPcaTexL);
            SAMPLER(sampler_uSonicPcaTexL);
            TEXTURE2D(_uSonicPcaTexR);
            SAMPLER(sampler_uSonicPcaTexR);
            float4x4 _uSonicPcaWorldToCamL;
            float4x4 _uSonicPcaWorldToCamR;
            float4 _uSonicPcaProjL;
            float4 _uSonicPcaProjR;
            float4 _uSonicPcaFlags; // x = hasL, y = hasR, z = 1/resX, w = 1/resY

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

            float _uRoomGlowIntensity;
            float _uLocalDiscFadeRadius;

            float3 srgbToLinearSafe(float3 c)
            {
                float3 clamped = saturate(c);
                float3 excess = max(0.0, c - 1.0);
                float3 lin = pow(clamped, 2.2);
                return lin + excess;
            }

            float3 sampleTerrainEmission(float2 uv, float lod, float3 fallbackCol)
            {
                if (_uSonicRoomHasEmissionTex > 0.5)
                {
                    float2 clampedUv = clamp(uv, float2(0.015, 0.015), float2(0.985, 0.985));
                    return SAMPLE_TEXTURE2D_LOD(_uSonicTerrainEmissionTex, sampler_uSonicTerrainEmissionTex, clampedUv, lod).rgb;
                }
                return fallbackCol;
            }

            float3 boostContrastSubtle(float3 e)
            {
                float eLum = max(e.r, max(e.g, e.b));
                return e * lerp(0.82, 1.16, smoothstep(0.08, 1.35, eLum));
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
                float audioEnergy = saturate(_uSonicRoomParams.z);
                float bassEnergy = saturate(_uSonicRoomParams.w);
                float warmth = saturate(_uSonicRoomFx.x);

                float3 surfN = normalize(input.normalWS);
                float3 rel = input.posWorld - platterCenter;
                float heightOnPlatter = dot(rel, platterN);
                float3 inPlaneVec = rel - platterN * heightOnPlatter;
                float radialDist = length(inPlaneVec);

                // Closest point on glowing circular LED platter disc
                float clampedRadial = min(radialDist, platterRadius);
                float3 inPlaneDir = (radialDist > 0.0005) ? (inPlaneVec / radialDist) : platterRight;
                float3 closestOnDisc = platterCenter + inPlaneDir * clampedRadial;

                // Strictly local horizontal/ceiling range, with extended range for floors directly below a floating platter
                float closestDist = length(input.posWorld - closestOnDisc);
                float maxRange = clamp(platterRadius * 2.2 + 0.28, 0.70, 1.35);
                bool isUnderFloating = (heightOnPlatter < -0.04 && dot(surfN, platterN) > 0.55);
                float floatUnderMaxRange = clamp(platterRadius * 2.8 + 0.95, 1.45, 2.25);
                float activeMaxRange = isUnderFloating ? max(maxRange, floatUnderMaxRange) : maxRange;
                if (closestDist >= activeMaxRange)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // C2-continuous zero-edge smooth window for local horizontal/ceiling bounce
                float tRange = saturate(closestDist / maxRange);
                float wRange = 1.0 - tRange * tRange;
                float smoothWindow = wRange * wRange * wRange;

                // View vector & silhouette softening so MRUK plane edges never show hard glowing outlines
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.posWorld);
                float nDotV = saturate(dot(surfN, viewDir));
                float silhouetteFade = smoothstep(0.03, 0.20, nDotV);
                if (silhouetteFade <= 0.001)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Fallback theme tint if emission RT isn't bound yet
                float3 fallbackCol = lerp(_uSonicRoomCoolCore.rgb, _uSonicRoomWarmCore.rgb, warmth * 0.7);
                #if !defined(UNITY_COLORSPACE_GAMMA)
                fallbackCol = srgbToLinearSafe(fallbackCol);
                #endif
                fallbackCol *= (0.35 + audioEnergy * 0.55 + bassEnergy * 0.45);

                // Steep physical inverse-square falloff from glowing slab edge
                float attenScale = clamp(platterRadius * 0.90, 0.20, 0.60);
                float dNorm = closestDist / attenScale;
                float invSq = 1.0 / (1.0 + dNorm * dNorm * 2.35);
                float distAtten = invSq * smoothWindow;

                // Slightly elevated LED pillar wall for coplanar floors/tables touching the platter base
                float emitLift = clamp(platterRadius * 0.16, 0.030, 0.12);
                float nearPlaneWeight = saturate(1.0 - abs(heightOnPlatter) / max(0.18, platterRadius * 0.80));
                float3 effectiveEmitter = closestOnDisc + platterN * (emitLift * nearPlaneWeight);
                float3 toEmitterVec = effectiveEmitter - input.posWorld;
                float toEmitterDist = max(0.001, length(toEmitterVec));
                float3 toEmitter = toEmitterVec / toEmitterDist;

                // Strict directional Lambertian facing (zero back-face leak)
                float nDotL = dot(surfN, toEmitter);
                float lambert = saturate(nDotL);
                float coplanarSurface = saturate((dot(surfN, platterN) - 0.78) / 0.22)
                                      * (1.0 - smoothstep(0.02, 0.18, abs(heightOnPlatter)));
                float coplanarWrap = coplanarSurface * saturate(nDotL * 0.55 + 0.45);
                float diffuseFacing = max(lambert, coplanarWrap * 0.78);

                // =========================================================================
                // 1. SPATIALLY-VARYING HIGH-CONTRAST PROXIMITY BOUNCE LIGHT
                //    Crisp 64x64 mipmapped emission sampling + tight grounded reach +
                //    extended floating under-platter floor projection
                // =========================================================================
                float emitterRadial = min(radialDist, platterRadius * 0.90);
                float3 emitterPlanarWS = inPlaneDir * emitterRadial;
                float2 emitterUV = float2(dot(emitterPlanarWS, platterRight), dot(emitterPlanarWS, platterFwd))
                                 / (2.0 * platterRadius) + 0.5;

                float contactBlend = saturate(closestDist / max(0.20, platterRadius * 0.65));
                float wideBlend = saturate((closestDist - 0.18) / max(0.35, maxRange - 0.18));
                float bounceLod = lerp(1.45, 2.45, contactBlend) + wideBlend * 2.95;
                float3 bounceEmission = boostContrastSubtle(sampleTerrainEmission(emitterUV, bounceLod, fallbackCol));

                // Much tighter grounded perimeter contact spill on coplanar floors/tables
                float contactRange = clamp(platterRadius * 0.42, 0.09, 0.19);
                float contactT = saturate(closestDist / contactRange);
                float oneMinusContact = max(0.0, 1.0 - contactT);
                float contactFalloff = (oneMinusContact > 0.0001) ? pow(oneMinusContact, 2.5) : 0.0;
                float contactSpill = coplanarSurface * contactFalloff * (1.0 - smoothstep(0.0, platterRadius * 0.35, max(0.0, platterRadius - radialDist)));

                float3 diffuseRgb = bounceEmission * (distAtten * diffuseFacing * 0.42 + contactSpill * 0.46);

                // Extended under-platter spatially-varying projection when floating above a floor/table
                float belowMask = (1.0 - smoothstep(-0.22, -0.04, heightOnPlatter)) * saturate((dot(surfN, platterN) - 0.55) / 0.45);
                if (belowMask > 0.002)
                {
                    float absH = abs(heightOnPlatter);
                    float2 underUV = float2(dot(inPlaneVec, platterRight), dot(inPlaneVec, platterFwd))
                                   / (2.0 * platterRadius) + 0.5;
                    float underConeR = platterRadius * 1.15 + absH * 0.72;
                    float underRadialFade = 1.0 - smoothstep(platterRadius * 0.72, underConeR, radialDist);
                    float underLod = lerp(1.4, 5.1, saturate(absH / max(0.35, floatUnderMaxRange * 0.85)));
                    float3 underEmission = boostContrastSubtle(sampleTerrainEmission(underUV, underLod, fallbackCol));

                    float tUnder = saturate(closestDist / floatUnderMaxRange);
                    float wUnder = 1.0 - tUnder * tUnder;
                    float smoothWindowUnder = wUnder * wUnder * wUnder;
                    float attenScaleUnder = clamp(platterRadius * 1.35, 0.35, 0.85);
                    float dNormUnder = closestDist / attenScaleUnder;
                    float distAttenUnder = (1.0 / (1.0 + dNormUnder * dNormUnder * 1.45)) * smoothWindowUnder;

                    diffuseRgb = lerp(diffuseRgb, underEmission * (distAttenUnder * lambert * underRadialFade * 0.58), belowMask * underRadialFade);
                }

                // =========================================================================
                // 2. TRUE DISTANCE-BLURRED GLOSSY REFLECTION OF THE LIVE TERRAIN
                // =========================================================================
                float3 reflRgb = float3(0.0, 0.0, 0.0);
                if (smoothWindow > 0.001)
                {
                    float3 R = reflect(-viewDir, surfN);
                    float canopyLift = clamp(platterRadius * 0.10, 0.015, 0.06);
                    float3 canopyCenter = platterCenter + platterN * canopyLift;

                    float3 toCanopy = canopyCenter - input.posWorld;
                    float rNumer = dot(toCanopy, platterN);
                    float rDenom = dot(R, platterN);

                    float tRefl = -1.0;
                    if (abs(rDenom) > 0.001)
                    {
                        tRefl = rNumer / rDenom;
                    }

                    float3 reflHitWS;
                    bool hasPlaneHit = (tRefl > 0.0 && tRefl < maxRange * 2.5);
                    if (hasPlaneHit)
                    {
                        reflHitWS = input.posWorld + R * tRefl;
                    }
                    else
                    {
                        float tClosest = max(0.0, dot(toCanopy, R));
                        tRefl = tClosest;
                        reflHitWS = input.posWorld + R * tClosest;
                    }

                    float3 hitRel = reflHitWS - canopyCenter;
                    float3 hitInPlane = hitRel - platterN * dot(hitRel, platterN);
                    float hitRadial = length(hitInPlane);

                    float clampedHitR = min(hitRadial, platterRadius * 0.94);
                    float3 clampedInPlane = (hitRadial > 0.0005) ? (hitInPlane * (clampedHitR / hitRadial)) : float3(0.0, 0.0, 0.0);
                    float3 repHitWS = canopyCenter + clampedInPlane;

                    float3 toRep = repHitWS - input.posWorld;
                    float repDist = max(0.015, length(toRep));
                    float3 Lrefl = toRep / repDist;

                    float reflFacing = smoothstep(0.0, 0.14, dot(surfN, Lrefl));

                    if (reflFacing > 0.001)
                    {
                        float rDotL = saturate(dot(R, Lrefl));
                        float glossExp = lerp(18.0, 7.5, saturate(repDist / max(0.35, platterRadius * 2.4)));
                        float glossLobe = (rDotL > 0.0001) ? pow(rDotL, glossExp) : 0.0;

                        if (glossLobe > 0.002)
                        {
                            float2 reflUV = float2(dot(clampedInPlane, platterRight), dot(clampedInPlane, platterFwd))
                                          / (2.0 * platterRadius) + 0.5;

                            float distFactor = saturate(repDist / max(0.32, platterRadius * 2.6));
                            float offAxisBlur = (1.0 - rDotL) * 4.5;
                            float reflLod = clamp(lerp(0.80, 4.85, sqrt(distFactor)) + offAxisBlur, 0.75, 5.25);

                            float3 reflEmission = boostContrastSubtle(sampleTerrainEmission(reflUV, reflLod, fallbackCol));

                            float oneMinusV = max(0.0, 1.0 - nDotV);
                            float fresnel = 0.06 + 0.94 * ((oneMinusV > 0.0001) ? pow(oneMinusV, 3.5) : 0.0);

                            float reflDistAtten = 1.0 / (1.0 + dNorm * dNorm * 1.45);
                            reflRgb = reflEmission * (fresnel * glossLobe * reflFacing * reflDistAtten * smoothWindow * 0.95);
                        }
                    }
                }

                // =========================================================================
                // 3. TRUE PHYSICAL REAL-SURFACE ALBEDO RELIGHTING VIA STEREO PCA
                //    RealSurfaceAlbedo * VirtualLight: dark mats/grout stay dark, light tiles
                //    tint vividly by real surface color, tile seams break up glossy reflection
                // =========================================================================
                bool hasL = _uSonicPcaFlags.x > 0.5;
                bool hasR = _uSonicPcaFlags.y > 0.5;
                if (hasL || hasR)
                {
                    bool useRight = (unity_StereoEyeIndex > 0) ? hasR : (!hasL && hasR);
                    float4x4 worldToCam = useRight ? _uSonicPcaWorldToCamR : _uSonicPcaWorldToCamL;
                    float4 proj = useRight ? _uSonicPcaProjR : _uSonicPcaProjL;

                    float3 pCam = mul(worldToCam, float4(input.posWorld, 1.0)).xyz;
                    float2 pcaUV = (pCam.z > 0.08) ? ((pCam.xy / pCam.z) * proj.xy + proj.zw) : float2(-1.0, -1.0);
                    float pcaEdge = step(0.08, pCam.z)
                                  * smoothstep(0.01, 0.07, pcaUV.x) * (1.0 - smoothstep(0.93, 0.99, pcaUV.x))
                                  * smoothstep(0.01, 0.07, pcaUV.y) * (1.0 - smoothstep(0.93, 0.99, pcaUV.y));

                    if (pcaEdge > 0.001)
                    {
                        float2 stepUV = _uSonicPcaFlags.zw * 1.5;
                        float3 camRgb;
                        float3 camN, camS, camE, camW;
                        if (useRight)
                        {
                            camRgb = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexR, sampler_uSonicPcaTexR, pcaUV, 0.0).rgb;
                            camN = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexR, sampler_uSonicPcaTexR, pcaUV + float2(0.0, stepUV.y), 0.0).rgb;
                            camS = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexR, sampler_uSonicPcaTexR, pcaUV - float2(0.0, stepUV.y), 0.0).rgb;
                            camE = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexR, sampler_uSonicPcaTexR, pcaUV + float2(stepUV.x, 0.0), 0.0).rgb;
                            camW = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexR, sampler_uSonicPcaTexR, pcaUV - float2(stepUV.x, 0.0), 0.0).rgb;
                        }
                        else
                        {
                            camRgb = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexL, sampler_uSonicPcaTexL, pcaUV, 0.0).rgb;
                            camN = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexL, sampler_uSonicPcaTexL, pcaUV + float2(0.0, stepUV.y), 0.0).rgb;
                            camS = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexL, sampler_uSonicPcaTexL, pcaUV - float2(0.0, stepUV.y), 0.0).rgb;
                            camE = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexL, sampler_uSonicPcaTexL, pcaUV + float2(stepUV.x, 0.0), 0.0).rgb;
                            camW = SAMPLE_TEXTURE2D_LOD(_uSonicPcaTexL, sampler_uSonicPcaTexL, pcaUV - float2(stepUV.x, 0.0), 0.0).rgb;
                        }

                        const float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
                        float camLuma = dot(camRgb, lumaWeights);
                        float lumaN = dot(camN, lumaWeights);
                        float lumaS = dot(camS, lumaWeights);
                        float lumaE = dot(camE, lumaWeights);
                        float lumaW = dot(camW, lumaWeights);

                        // Diffuse albedo modulation (NaN-safe pow) + 1.22x compensation so bright tiles stay vivid
                        float3 estAlbedo = lerp(camLuma.xxx, camRgb, 0.65);
                        float3 scaledAlbedo = max(float3(1e-5, 1e-5, 1e-5), saturate(estAlbedo * 2.55));
                        float3 diffuseSurfaceMod = pow(scaledAlbedo, 0.92);

                        // Glossy specular modulation: keeps Fresnel on dark glossy tiles, breaks on grout seams & ultra-dark matte fabric
                        float neighborAvg = 0.25 * (lumaN + lumaS + lumaE + lumaW);
                        float seamBreak = 1.0 - saturate(abs(camLuma - neighborAvg) * 6.0) * 0.45;
                        float specSurfaceMod = (0.28 + 0.72 * smoothstep(0.008, 0.065, camLuma)) * seamBreak;

                        diffuseRgb *= lerp(float3(1.0, 1.0, 1.0), diffuseSurfaceMod * 1.22, pcaEdge);
                        reflRgb *= lerp(1.0, specSurfaceMod, pcaEdge);
                    }
                }

                // Tight grounded distance envelope strictly when snapped coplanar to floor/table (floating/ceiling = 1.0)
                float coplanarReach = clamp(platterRadius * 0.44, 0.10, 0.21);
                float coplanarFade = exp(-(closestDist / coplanarReach) * 2.35)
                                   * (1.0 - smoothstep(coplanarReach * 0.60, coplanarReach * 1.30, closestDist));
                float groundedEnvelope = lerp(1.0, coplanarFade, coplanarSurface);

                float3 lightRgb = (diffuseRgb + reflRgb) * groundedEnvelope;

                // Soft local disc boundary fade (only active on Depth-raycast snapped fallback catcher)
                if (_uLocalDiscFadeRadius > 0.01)
                {
                    float discFade = 1.0 - smoothstep(_uLocalDiscFadeRadius * 0.50, _uLocalDiscFadeRadius * 0.95, radialDist);
                    discFade *= discFade;
                    lightRgb *= discFade;
                }

                // Apply silhouette softening, Room Glow slider, and visibility
                lightRgb *= (silhouetteFade * glowIntensity * visibility);

                // Gentle HDR shoulder + subtle hot-core lift so bright peaks genuinely pop while dark areas stay dark
                float3 tonemapped = lightRgb / (1.0 + lightRgb * 0.28);
                float hotPeak = max(tonemapped.r, max(tonemapped.g, tonemapped.b));
                tonemapped += float3(0.05, 0.05, 0.06) * smoothstep(0.50, 1.40, hotPeak);

                float peakLum = max(tonemapped.r, max(tonemapped.g, tonemapped.b));

                // Near-zero glow premultiplied alpha preserves 97.5-100% of real floor texture & grain
                float lightAlpha = min(saturate(peakLum * 0.020), 0.025);

                // Subtle multiplicative contact shadow right at grounded slab rim where rim emission is low
                float contactAo = coplanarSurface
                                * (1.0 - smoothstep(0.0, 0.028, closestDist))
                                * 0.24
                                * (1.0 - saturate(peakLum * 1.6))
                                * (silhouetteFade * visibility);
                float outAlpha = max(lightAlpha, contactAo);

                if (peakLum <= 0.0004 && outAlpha <= 0.0004)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                return half4((half3)tonemapped, (half)outAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
