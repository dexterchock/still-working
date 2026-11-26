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

                // Strictly local proximity range: far walls, ceiling, and distant furniture receive exact 0.0
                float closestDist = length(input.posWorld - closestOnDisc);
                float maxRange = clamp(platterRadius * 2.5 + 0.35, 0.75, 1.55);
                if (closestDist >= maxRange)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // C2-continuous zero-edge smooth window: fades to exact 0.0 with zero visible boundary
                float tRange = saturate(closestDist / maxRange);
                float wRange = 1.0 - tRange * tRange;
                float smoothWindow = wRange * wRange * wRange;

                // Steep physical inverse-square falloff (90% of visible glow within ~0.0-0.50m of slab edge)
                float attenScale = clamp(platterRadius * 0.95, 0.22, 0.65);
                float dNorm = closestDist / attenScale;
                float invSq = 1.0 / (1.0 + dNorm * dNorm * 2.4);
                float distAtten = invSq * smoothWindow;

                // View vector & silhouette softening so MRUK plane edges never show hard glowing outlines
                float3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.posWorld);
                float nDotV = saturate(dot(surfN, viewDir));
                float silhouetteFade = smoothstep(0.03, 0.20, nDotV);
                if (silhouetteFade <= 0.001)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Slightly elevated effective LED pillar height for coplanar surfaces (desk/floor right around slab)
                float emitLift = clamp(platterRadius * 0.16, 0.030, 0.12);
                float nearPlaneWeight = saturate(1.0 - abs(heightOnPlatter) / max(0.18, platterRadius * 0.80));
                float3 effectiveEmitter = closestOnDisc + platterN * (emitLift * nearPlaneWeight);
                float3 toEmitterVec = effectiveEmitter - input.posWorld;
                float toEmitterDist = max(0.001, length(toEmitterVec));
                float3 toEmitter = toEmitterVec / toEmitterDist;

                // Strict directional Lambertian facing (zero back-face ambient leak)
                // plus gentle grazing wrap ONLY for nearly coplanar base surfaces right next to the platter
                float nDotL = dot(surfN, toEmitter);
                float lambert = saturate(nDotL);
                float coplanarSurface = saturate((dot(surfN, platterN) - 0.78) / 0.22)
                                      * (1.0 - smoothstep(0.02, 0.20, abs(heightOnPlatter)));
                float coplanarWrap = coplanarSurface * saturate(nDotL * 0.62 + 0.38);
                float diffuseFacing = max(lambert, coplanarWrap * 0.72);

                // Representative Point disc area-light specular reflection (head-tracked glossy table/floor sheen)
                float specLift = clamp(platterRadius * 0.24, 0.055, 0.16);
                float3 specDiscCenter = platterCenter + platterN * specLift;
                float3 R = reflect(-viewDir, surfN);
                float3 toDiscCenter = specDiscCenter - input.posWorld;
                float planeNumer = dot(toDiscCenter, platterN);
                float planeDenom = dot(R, platterN);

                float3 repPt;
                if (abs(planeDenom) > 0.001 && (planeNumer / planeDenom) > 0.0)
                {
                    float tPlane = min(planeNumer / planeDenom, maxRange * 2.5);
                    float3 hitPlane = input.posWorld + R * tPlane;
                    float3 hitOffset = hitPlane - specDiscCenter;
                    float hitRadial = length(hitOffset);
                    float clampedHitR = min(hitRadial, platterRadius);
                    repPt = specDiscCenter + ((hitRadial > 0.0005) ? (hitOffset * (clampedHitR / hitRadial)) : float3(0.0, 0.0, 0.0));
                }
                else
                {
                    float tRay = max(0.0, dot(toDiscCenter, R));
                    float3 rayPt = input.posWorld + R * tRay;
                    float3 relRay = rayPt - specDiscCenter;
                    float3 inDiscRay = relRay - platterN * dot(relRay, platterN);
                    float rayRadial = length(inDiscRay);
                    float clampedRayR = min(rayRadial, platterRadius);
                    repPt = specDiscCenter + ((rayRadial > 0.0005) ? (inDiscRay * (clampedRayR / rayRadial)) : float3(0.0, 0.0, 0.0));
                }

                float3 toRep = repPt - input.posWorld;
                float repDist = max(0.02, length(toRep));
                float3 Lspec = toRep / repDist;
                float rawSpecNdotL = dot(surfN, Lspec);
                // Strictly zero on back faces (rawSpecNdotL <= 0), smooth ramp at grazing angles
                float specFacing = smoothstep(0.0, 0.18, rawSpecNdotL);

                if (diffuseFacing <= 0.001 && specFacing <= 0.001)
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

                // Smooth wide organic ripple/beat energy swell (ZERO sharp concentric rings)
                float colorRippleAcc = 0.0;
                float whiteRippleAcc = 0.0;
                float sideScale = max(0.50, platterRadius * 2.2);
                [unroll]
                for (int i = 0; i < 10; i++)
                {
                    if (_uSonicRoomRippleState[i].x > 0.5)
                    {
                        float2 rPos = _uSonicRoomRipplePosTimeStrength[i].xy;
                        float rTime = _uSonicRoomRipplePosTimeStrength[i].z;
                        float rStrength = min(_uSonicRoomRipplePosTimeStrength[i].w, 1.8);
                        float age = max(0.0, uTime - rTime);

                        if (age < 2.4)
                        {
                            float env = smoothstep(0.0, 0.16, age) * exp(-age * 1.45) * rStrength;
                            float3 rOriginWS = platterCenter + (platterRight * rPos.x + platterFwd * rPos.y) * platterScale;
                            float sideDist = length(input.posWorld - rOriginWS);
                            float sideRatio = sideDist / sideScale;
                            float sideBias = 0.55 + 0.45 * exp(-(sideRatio * sideRatio));
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
                float colorRipple = 1.0 - exp(-colorRippleAcc * 0.70);
                float whiteRipple = 1.0 - exp(-whiteRippleAcc * 0.60);
                float totalRipple = saturate(colorRipple * 0.60 + whiteRipple * 0.50);

                // Refined luminous drive: calm breathing at idle, organic swell on beats & ripples
                float idleDrive = 0.34 + 0.08 * idlePulse;
                float musicDrive = saturate(audioEnergy * 0.55 + bassEnergy * 0.45);
                float totalDrive = idleDrive + musicDrive * 0.46 + totalRipple * 0.36;

                // Soft diffuse tint with subtle organic variation
                float2 planarCoord = float2(dot(inPlaneVec, platterRight), dot(inPlaneVec, platterFwd));
                float softShimmer = valueNoise2D(planarCoord * (1.35 / max(0.30, platterRadius * 2.0)) + float2(uTime * 0.16, -uTime * 0.12));
                float colorMix = saturate(warmth * 0.72 + bassEnergy * 0.22 + (softShimmer - 0.5) * 0.12);
                float3 diffuseTint = lerp(coolCol, warmCol, colorMix);
                diffuseTint = lerp(diffuseTint, rippleCol, saturate(colorRipple * 0.40));
                diffuseTint = lerp(diffuseTint, lerp(rippleCol, float3(0.78, 0.86, 0.94), 0.45), saturate(whiteRipple * 0.30));

                // 1. Tight local diffuse perimeter bounce pool
                float diffuseShimmerMod = lerp(0.93, 1.07, softShimmer);
                float diffuseScalar = totalDrive * distAtten * diffuseFacing * diffuseShimmerMod * 0.44;
                float3 diffuseRgb = diffuseTint * diffuseScalar;

                // 2. View-dependent soft glossy specular reflection on nearby tables/floors (roughness ~0.38 + Schlick Fresnel)
                float roughness = 0.38;
                float alphaR = roughness * roughness;
                float alphaPrime = saturate(alphaR + platterRadius / (2.5 * repDist + platterRadius));
                float specExp = clamp(2.0 / max(0.02, alphaPrime * alphaPrime) - 2.0, 4.0, 26.0);
                float rDotL = saturate(dot(R, Lspec));
                float specLobe = (rDotL > 0.0001) ? pow(rDotL, specExp) : 0.0;
                float normFactor = sqrt(alphaR / max(0.05, alphaPrime));

                // Schlick Fresnel: F = 0.04 + 0.96 * (1 - NdotV)^4 (NaN-free)
                float oneMinusV = 1.0 - nDotV;
                float oneMinusV2 = oneMinusV * oneMinusV;
                float fresnel = 0.04 + 0.96 * (oneMinusV2 * oneMinusV2);

                // Subtle reflected terrain texture modulation sampled at the representative point on the glowing disc
                float3 repOffset = repPt - specDiscCenter;
                float2 repDiscUv = float2(dot(repOffset, platterRight), dot(repOffset, platterFwd)) / max(0.08, platterRadius);
                float repNoise = valueNoise2D(repDiscUv * 2.2 + float2(uTime * 0.22, -uTime * 0.18));
                float3 specTint = lerp(diffuseTint, lerp(coolCol, warmCol, saturate(0.35 + repNoise * 0.55)), 0.45);
                specTint = lerp(specTint, float3(0.85, 0.92, 1.0), 0.14 + 0.16 * whiteRipple);

                float specDistAtten = 1.0 / (1.0 + dNorm * dNorm * 1.65);
                float specScalar = totalDrive * specDistAtten * smoothWindow * specFacing * fresnel * specLobe * normFactor
                                 * lerp(0.84, 1.16, repNoise) * 1.38;
                float3 specRgb = specTint * specScalar;

                float3 lightRgb = diffuseRgb + specRgb;

                // Soft local disc boundary fade (only active on Depth-raycast snapped fallback catcher)
                if (_uLocalDiscFadeRadius > 0.01)
                {
                    float discFade = 1.0 - smoothstep(_uLocalDiscFadeRadius * 0.50, _uLocalDiscFadeRadius * 0.95, radialDist);
                    discFade *= discFade;
                    lightRgb *= discFade;
                }

                // Apply silhouette softening, Room Glow slider, and visibility
                lightRgb *= (silhouetteFade * glowIntensity * visibility);

                // Soft photographic knee compression so high RoomGlow or loud drops stay refined & realistic
                lightRgb = lightRgb / (1.0 + lightRgb * 1.25);

                float peakLum = max(lightRgb.r, max(lightRgb.g, lightRgb.b));
                if (peakLum <= 0.0004)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                // Near-pure additive passthrough compositing: keeps >=94.5% of real Quest 3 camera feed untouched
                float outAlpha = min(saturate(peakLum * 0.055), 0.055);

                return half4((half3)lightRgb, (half)outAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
