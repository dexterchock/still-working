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

                // Strictly local proximity range: far walls, ceiling, and distant furniture receive exact 0.0
                float closestDist = length(input.posWorld - closestOnDisc);
                float maxRange = clamp(platterRadius * 2.4 + 0.32, 0.72, 1.45);
                if (closestDist >= maxRange)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // C2-continuous zero-edge smooth window: fades to exact 0.0 with zero visible boundary
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
                //    Samples live 64x64 mipmapped terrain emission map at distance-proportional LOD:
                //    bright rim peaks cast vivid local pools; dark valleys stay dark!
                // =========================================================================
                float contactRange = clamp(platterRadius * 0.68, 0.16, 0.30);

                // Pull emitter UV inward from rim; on coplanar grounded surfaces pull gradually inward with distance
                // and raise mip LOD + 3-tap tangential arc blend to eliminate radial spoke streaks
                float groundedEmitterFrac = lerp(0.88, 0.50, saturate(closestDist / contactRange));
                float emitterFrac = lerp(0.90, groundedEmitterFrac, coplanarSurface);
                float emitterRadial = min(radialDist, platterRadius * emitterFrac);
                float3 emitterPlanarWS = inPlaneDir * emitterRadial;
                float2 emitterUV = float2(dot(emitterPlanarWS, platterRight), dot(emitterPlanarWS, platterFwd))
                                 / (2.0 * platterRadius) + 0.5;

                // Tight contact zone (0..0.20m): mip 1.45..2.45 (floating/wall) or 2.65..4.75 (grounded coplanar)
                float contactBlend = saturate(closestDist / max(0.20, platterRadius * 0.65));
                float wideBlend = saturate((closestDist - 0.18) / max(0.35, maxRange - 0.18));
                float baseBounceLod = lerp(1.45, 2.45, contactBlend) + wideBlend * 2.95;
                float groundedLodT = saturate(closestDist / max(0.14, platterRadius * 0.55));
                float groundedBounceLod = min(5.40, lerp(2.65, 4.75, groundedLodT) + wideBlend * 0.65);
                float bounceLod = lerp(baseBounceLod, groundedBounceLod, coplanarSurface);

                float3 bounceEmission = sampleTerrainEmission(emitterUV, bounceLod, fallbackCol);
                if (coplanarSurface > 0.001)
                {
                    float3 rimTangent = cross(platterN, inPlaneDir);
                    float arcSpread = clamp(closestDist * 0.55, 0.03, platterRadius * 0.45);
                    float3 tapPlusWS = emitterPlanarWS + rimTangent * arcSpread;
                    float3 tapMinusWS = emitterPlanarWS - rimTangent * arcSpread;
                    float2 uvPlus = float2(dot(tapPlusWS, platterRight), dot(tapPlusWS, platterFwd)) / (2.0 * platterRadius) + 0.5;
                    float2 uvMinus = float2(dot(tapMinusWS, platterRight), dot(tapMinusWS, platterFwd)) / (2.0 * platterRadius) + 0.5;
                    float3 tapPlus = sampleTerrainEmission(uvPlus, bounceLod, fallbackCol);
                    float3 tapMinus = sampleTerrainEmission(uvMinus, bounceLod, fallbackCol);
                    float3 arcBlended = bounceEmission * 0.50 + (tapPlus + tapMinus) * 0.25;
                    bounceEmission = lerp(bounceEmission, arcBlended, coplanarSurface);
                }

                // Tight, smooth cubic perimeter contact spill on coplanar floors/tables
                float contactT = saturate(closestDist / contactRange);
                float oneMinusContact = 1.0 - contactT;
                float contactFalloff = oneMinusContact * oneMinusContact * oneMinusContact;
                float contactSpill = coplanarSurface * contactFalloff * (1.0 - smoothstep(0.0, platterRadius * 0.35, max(0.0, platterRadius - radialDist)));

                float diffuseWeight = lerp(0.42, 0.32, coplanarSurface);
                float3 diffuseRgb = bounceEmission * (distAtten * diffuseFacing * diffuseWeight + contactSpill * 0.34);

                // Under-platter spatially-varying projection when floating above a floor/table (heightOnPlatter < -0.04)
                float belowMask = smoothstep(-0.04, -0.22, heightOnPlatter) * saturate(dot(surfN, platterN));
                if (belowMask > 0.002)
                {
                    float absH = abs(heightOnPlatter);
                    float2 underUV = float2(dot(inPlaneVec, platterRight), dot(inPlaneVec, platterFwd))
                                   / (2.0 * platterRadius) + 0.5;
                    float underConeR = platterRadius * 1.15 + absH * 0.65;
                    float underRadialFade = 1.0 - smoothstep(platterRadius * 0.72, underConeR, radialDist);
                    float underLod = lerp(1.4, 5.1, saturate(absH / max(0.35, maxRange * 0.85)));
                    float3 underEmission = sampleTerrainEmission(underUV, underLod, fallbackCol);
                    diffuseRgb = lerp(diffuseRgb, underEmission * (distAtten * lambert * underRadialFade * 0.62), belowMask * underRadialFade);
                }

                // =========================================================================
                // 2. TRUE DISTANCE-BLURRED GLOSSY REFLECTION OF THE LIVE TERRAIN
                //    (Reddit Mario tabletop / glossy floor tile effect!)
                // =========================================================================
                float3 reflRgb = float3(0.0, 0.0, 0.0);
                float3 R = reflect(-viewDir, surfN);
                float canopyLift = clamp(platterRadius * 0.10, 0.015, 0.06);
                float3 canopyCenter = platterCenter + platterN * canopyLift;

                float3 toCanopy = canopyCenter - input.posWorld;
                float rNumer = dot(toCanopy, platterN);
                float rDenom = dot(R, platterN);

                // Intersect reflected ray with elevated glowing terrain canopy plane (forward hits only)
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
                    // Fallback for grazing rays: closest point on reflected ray to canopy center
                    float tClosest = max(0.0, dot(toCanopy, R));
                    tRefl = tClosest;
                    reflHitWS = input.posWorld + R * tClosest;
                }

                float3 hitRel = reflHitWS - canopyCenter;
                float3 hitInPlane = hitRel - platterN * dot(hitRel, platterN);
                float hitRadial = length(hitInPlane);

                // Representative Point clamp onto the glowing disc (preserves sharp mirror hits inside disc
                // AND realistic glossy roughness tail right outside the rim on tabletops & shiny floor tiles)
                float reflClampFrac = lerp(0.94, 0.84, coplanarSurface);
                float clampedHitR = min(hitRadial, platterRadius * reflClampFrac);
                float3 clampedInPlane = (hitRadial > 0.0005) ? (hitInPlane * (clampedHitR / hitRadial)) : float3(0.0, 0.0, 0.0);
                float3 repHitWS = canopyCenter + clampedInPlane;

                float3 toRep = repHitWS - input.posWorld;
                float repDist = max(0.015, length(toRep));
                float3 Lrefl = toRep / repDist;

                // Strictly zero on back faces (dot(surfN, Lrefl) <= 0)
                float reflFacing = smoothstep(0.0, 0.14, dot(surfN, Lrefl));

                if (reflFacing > 0.001)
                {
                    // Angular glossy lobe: 1.0 when ray hits inside the glowing disc, smooth roughness falloff outside rim
                    float rDotL = saturate(dot(R, Lrefl));
                    float glossExp = lerp(18.0, 7.5, saturate(repDist / max(0.35, platterRadius * 2.4)));
                    float glossLobe = (rDotL > 0.0001) ? pow(rDotL, glossExp) : 0.0;

                    if (glossLobe > 0.002)
                    {
                        float2 reflUV = float2(dot(clampedInPlane, platterRight), dot(clampedInPlane, platterFwd))
                                      / (2.0 * platterRadius) + 0.5;

                        // Distance-proportional mip LOD: crisp high-contrast hotspots near rim (0.80..1.8 floating/wall),
                        // softened on coplanar grounded floor (min 2.35) to prevent radial rim spokes
                        float distFactor = saturate(repDist / max(0.32, platterRadius * 2.6));
                        float offAxisBlur = (1.0 - rDotL) * 4.5;
                        float minReflLod = lerp(0.80, 2.35, coplanarSurface);
                        float reflLod = clamp(lerp(minReflLod, 4.85, sqrt(distFactor)) + offAxisBlur, minReflLod, 5.25);

                        float3 reflEmission = sampleTerrainEmission(reflUV, reflLod, fallbackCol);

                        // Schlick Fresnel: F = 0.06 + 0.94 * (1 - NdotV)^3.5 (NaN-free)
                        float oneMinusV = max(0.0, 1.0 - nDotV);
                        float fresnel = 0.06 + 0.94 * ((oneMinusV > 0.0001) ? pow(oneMinusV, 3.5) : 0.0);

                        float reflDistAtten = 1.0 / (1.0 + dNorm * dNorm * 1.45);
                        float reflCoplanarWeight = lerp(1.0, 0.55, coplanarSurface);
                        reflRgb = reflEmission * (fresnel * glossLobe * reflFacing * reflDistAtten * smoothWindow * 0.95 * reflCoplanarWeight);
                    }
                }

                // Tighter, softer distance envelope strictly for coplanar grounded floors/tables
                // (leaves floating under-glow and ceiling/wall bounce 100% unchanged)
                float coplanarEnvelope = 1.0;
                if (coplanarSurface > 0.001)
                {
                    float coplanarScale = clamp(platterRadius * 0.62, 0.14, 0.32);
                    float coplanarDecay = exp(-(closestDist / coplanarScale) * 2.15)
                                        * (1.0 - smoothstep(platterRadius * 0.55, platterRadius * 1.18, closestDist));
                    coplanarEnvelope = lerp(1.0, coplanarDecay, coplanarSurface);
                }

                float3 lightRgb = (diffuseRgb + reflRgb) * coplanarEnvelope;

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
                if (peakLum <= 0.0004)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Ultra-low premultiplied alpha preserves 92-100% of real Quest 3 passthrough textures & shadows
                float outAlpha = min(saturate(peakLum * 0.045), 0.08);

                return half4((half3)tonemapped, (half)outAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
