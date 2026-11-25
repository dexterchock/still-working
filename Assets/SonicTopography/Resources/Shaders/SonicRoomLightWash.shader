Shader "SonicTopography/RoomLightWash"
{
    Properties
    {
        _uRoomGlowIntensity ("Room Glow Intensity", Float) = 1.0
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.075
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

            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

            // Global uniforms driven by SonicMixedRealityManager / SonicTopographyVisualizer
            float4 _uSonicRoomPlatterCenterWS;
            float4 _uSonicRoomPlatterNormalWS;
            float4 _uSonicRoomPlatterRightWS;
            float4 _uSonicRoomPlatterForwardWS;
            float4 _uSonicRoomParams; // x = worldRadius, y = platterScale, z = audioEnergy, w = bassEnergy
            float4 _uSonicRoomFx;     // x = warmth, y = roomGlowIntensity, z = arVisibility, w = time
            float4 _uSonicRoomOcc;    // x = occlusionEnabled (0/1), y = rippleSpeedScale, z = idlePulse, w = unused

            float4 _uSonicRoomCoolCore;
            float4 _uSonicRoomWarmCore;
            float4 _uSonicRoomRippleColor;

            float4 _uSonicRoomRipplePosTimeStrength[10];
            float4 _uSonicRoomRippleState[10];

            float _uRoomGlowIntensity;
            float _EnvironmentDepthBias;
            float _uLocalDiscFadeRadius;

            float3 srgbToLinearSafe(float3 c)
            {
                float3 clamped = saturate(c);
                float3 excess = max(0.0, c - 1.0);
                float3 lin = pow(clamped, 2.2);
                return lin + excess;
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
                float audioEnergy = _uSonicRoomParams.z;
                float bassEnergy = _uSonicRoomParams.w;
                float warmth = _uSonicRoomFx.x;
                float uTime = _uSonicRoomFx.w;
                float idlePulse = _uSonicRoomOcc.z;

                float3 surfN = normalize(input.normalWS);
                float3 rel = input.posWorld - platterCenter;
                float heightOnPlatter = dot(rel, platterN);
                float3 inPlaneVec = rel - platterN * heightOnPlatter;
                float radialDist = length(inPlaneVec);
                float surfDist = length(rel);

                // Project onto platter XZ basis for wave origin continuity
                float2 surfLocalXZ = float2(dot(inPlaneVec, platterRight), dot(inPlaneVec, platterFwd)) / platterScale;

                // Two-sided geometric bounce factor: flat desks/floors/walls under the platter glow vividly
                float3 toPlatter = (surfDist > 0.001) ? (-rel / surfDist) : platterN;
                float facing = saturate(abs(dot(surfN, toPlatter)) * 0.55 + 0.55);

                float3 coolCol = _uSonicRoomCoolCore.rgb;
                float3 warmCol = _uSonicRoomWarmCore.rgb;
                float3 rippleCol = _uSonicRoomRippleColor.rgb;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                coolCol = srgbToLinearSafe(coolCol);
                warmCol = srgbToLinearSafe(warmCol);
                rippleCol = srgbToLinearSafe(rippleCol);
                #endif

                // Smoothly avoid harsh double-glow underneath the center of the physical sculpture itself
                float outsideRim = smoothstep(platterRadius * 0.45, platterRadius * 1.05, radialDist + abs(heightOnPlatter) * 0.5);

                // 1. Pure realistic bounced room light wash
                float reach = max(2.4, platterRadius * 5.2);
                float normDist = surfDist / reach;
                float falloff = 1.0 / (1.0 + normDist * normDist * 2.8);
                float softCutoff = exp(-normDist * 0.85);

                float activeLevel = saturate(audioEnergy * 1.15 + bassEnergy * 0.75);
                float breathLevel = 0.36 + 0.16 * idlePulse;
                float bounceStrength = (breathLevel + activeLevel * 1.10) * falloff * softCutoff * facing * (0.35 + 0.65 * outsideRim);

                float3 themeBase = lerp(coolCol, warmCol, saturate(warmth * 1.25 + radialDist * 0.15));
                float3 lightRgb = themeBase * (bounceStrength * 1.15);

                // 2. Spilling music waves from active terrain ripples (seamless rim transition onto real room geometry)
                float waveOutsideMask = smoothstep(platterRadius * 0.72, platterRadius * 1.02, radialDist + abs(heightOnPlatter) * 0.35);
                if (waveOutsideMask > 0.002)
                {
                    float outsideMeters = max(0.0, surfDist - platterRadius * 0.82);
                    float roomAtten = exp(-outsideMeters * 0.34) * (0.55 + 0.45 * facing);

                    float normalWaveSum = 0.0;
                    float whiteWaveSum = 0.0;

                    [unroll]
                    for (int i = 0; i < 10; i++)
                    {
                        float isActive = _uSonicRoomRippleState[i].x;
                        if (isActive > 0.0)
                        {
                            float2 rPos = _uSonicRoomRipplePosTimeStrength[i].xy;
                            float rTime = _uSonicRoomRipplePosTimeStrength[i].z;
                            float rStrength = _uSonicRoomRipplePosTimeStrength[i].w;
                            float rType = _uSonicRoomRippleState[i].y;

                            float distUnits = length(surfLocalXZ - rPos);
                            float timeSince = max(0.0, uTime - rTime);

                            float curSpeed = (rType > 0.5) ? 26.0 : 20.0;
                            float curWidth = (rType > 0.5) ? 140.0 : 220.0;
                            float curFadeUnits = (rType > 0.5) ? 175.0 : 260.0;

                            float waveRadius = timeSince * curSpeed;
                            float d = distUnits - waveRadius;
                            float ring = exp(-(d * d) / curWidth);
                            float fade = exp(-waveRadius / curFadeUnits);
                            float pulse = ring * fade * rStrength;

                            if (rType > 0.5)
                            {
                                whiteWaveSum += pulse;
                            }
                            else
                            {
                                normalWaveSum += pulse;
                            }
                        }
                    }

                    float3 spillColor = lerp(rippleCol, warmCol, 0.25) * (normalWaveSum * 1.65)
                                      + float3(0.95, 0.98, 1.0) * (whiteWaveSum * 1.55);
                    lightRgb += spillColor * (waveOutsideMask * roomAtten);
                }

                if (_uLocalDiscFadeRadius > 0.01)
                {
                    float discFade = 1.0 - smoothstep(_uLocalDiscFadeRadius * 0.42, _uLocalDiscFadeRadius, radialDist);
                    lightRgb *= discFade;
                }

                lightRgb *= (glowIntensity * visibility);
                float peakLum = max(lightRgb.r, max(lightRgb.g, lightRgb.b));
                float outAlpha = saturate(peakLum * 0.42);

                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                if (_uSonicRoomOcc.x > 0.5)
                {
                    float occ = CalculateEnvironmentDepthOcclusion(input.posWorld, _EnvironmentDepthBias);
                    lightRgb *= occ;
                    outAlpha *= occ;
                }
                #endif

                return half4(lightRgb, (half)outAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
