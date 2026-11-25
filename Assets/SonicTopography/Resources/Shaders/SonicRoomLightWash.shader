Shader "SonicTopography/RoomLightWash"
{
    Properties
    {
        _uRoomGlowIntensity ("Room Glow Intensity", Float) = 1.0
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.02
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
            // Strict alpha preservation: adds bounced light onto real room surfaces without touching eye-buffer alpha
            Blend One One, Zero One
            ColorMask RGB

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

                // Soft geometric bounce factor: walls/tables/floors facing toward the LED sculpture receive more light
                float3 toPlatter = (surfDist > 0.001) ? (-rel / surfDist) : platterN;
                float facing = saturate(dot(surfN, toPlatter) * 0.72 + 0.32);

                // Smoothly avoid harsh double-glow underneath the center of the physical sculpture itself
                float outsideRim = smoothstep(platterRadius * 0.55, platterRadius * 1.12, radialDist + abs(heightOnPlatter) * 0.65);

                // 1. Pure realistic bounced room light wash
                float reach = max(1.1, platterRadius * 3.4);
                float normDist = surfDist / reach;
                float falloff = 1.0 / (1.0 + normDist * normDist * 5.2);
                float softCutoff = exp(-normDist * 1.15);

                float activeLevel = saturate(audioEnergy * 0.95 + bassEnergy * 0.55);
                float breathLevel = 0.14 + 0.08 * idlePulse;
                float bounceStrength = (breathLevel + activeLevel * 0.85) * falloff * softCutoff * facing * (0.28 + 0.72 * outsideRim);

                float3 themeBase = lerp(_uSonicRoomCoolCore.rgb, _uSonicRoomWarmCore.rgb, saturate(warmth * 1.25 + radialDist * 0.2));
                float3 lightRgb = themeBase * (bounceStrength * 0.42);

                // 2. Spilling music waves from active terrain ripples (seamless rim transition onto real room geometry)
                float waveOutsideMask = smoothstep(platterRadius * 0.78, platterRadius * 1.04, radialDist + abs(heightOnPlatter) * 0.4);
                if (waveOutsideMask > 0.002)
                {
                    float outsideMeters = max(0.0, surfDist - platterRadius * 0.85);
                    float roomAtten = exp(-outsideMeters * 0.52) * (0.45 + 0.55 * facing);

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

                            float curSpeed = (rType > 0.5) ? 20.0 : 15.0;
                            float curWidth = (rType > 0.5) ? 22.0 : 48.0;
                            float curFadeUnits = (rType > 0.5) ? 58.0 : 95.0;

                            float waveRadius = timeSince * curSpeed;
                            // Wait until wave reaches outer edge of terrain so spills emerge organically from rim
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

                    float3 spillColor = lerp(_uSonicRoomRippleColor.rgb, _uSonicRoomWarmCore.rgb, 0.25) * (normalWaveSum * 0.65)
                                      + float3(0.92, 0.96, 1.0) * (whiteWaveSum * 0.70);
                    lightRgb += spillColor * (waveOutsideMask * roomAtten);
                }

                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                if (_uSonicRoomOcc.x > 0.5)
                {
                    float occ = CalculateEnvironmentDepthOcclusion(input.posWorld, _EnvironmentDepthBias);
                    lightRgb *= occ;
                }
                #endif

                lightRgb *= (glowIntensity * visibility);
                return half4(lightRgb, 0.0h);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
