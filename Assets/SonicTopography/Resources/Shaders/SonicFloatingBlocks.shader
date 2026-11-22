Shader "SonicTopography/FloatingBlocks"
{
    Properties
    {
        _uGlowIntensity ("Glow Intensity", Float) = 1.0
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+10"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _uTime;
            float _uPulse;
            float _uPulseScale;
            float _uLiftOffset;

            float _uPresence;
            float _uBrilliance;
            float _uAir;
            float _uWarmth;
            float _uBrightness;
            float _uSharpness;

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

            float4x4 _PlatterMatrix;

            struct Attributes
            {
                float4 positionOS     : POSITION;
                float3 normalOS       : NORMAL;
                float2 uv             : TEXCOORD0;
                float4 blockPosScale  : TEXCOORD1;
                float2 blockPhaseSpd  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS      : SV_POSITION;
                float2 uv              : TEXCOORD0;
                float  distance        : TEXCOORD1;
                float  activeEnergy    : TEXCOORD2;
                float  brillianceSpark : TEXCOORD3;
                float3 baseFaceColor   : TEXCOORD4;
                float3 edgeGlowColor   : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float glslSmoothstep(float edge0, float edge1, float x)
            {
                float t = saturate((x - edge0) / (edge1 - edge0));
                return t * t * (3.0 - 2.0 * t);
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

            float3 RotateVectorEuler(float3 v, float3 eulerRad)
            {
                float cx = cos(eulerRad.x), sx = sin(eulerRad.x);
                float cy = cos(eulerRad.y), sy = sin(eulerRad.y);
                float cz = cos(eulerRad.z), sz = sin(eulerRad.z);

                // Rotate Z, then X, then Y (matching Unity's Euler order)
                float3 vZ = float3(v.x * cz - v.y * sz, v.x * sz + v.y * cz, v.z);
                float3 vX = float3(vZ.x, vZ.y * cx - vZ.z * sx, vZ.y * sx + vZ.z * cx);
                float3 vY = float3(vX.x * cy + vX.z * sy, vX.y, -vX.x * sy + vX.z * cy);
                return vY;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.uv = input.uv;

                float3 blockBasePos = input.blockPosScale.xyz;
                float baseScale = max(0.01, input.blockPosScale.w);
                float phase = input.blockPhaseSpd.x;
                float rotSpeed = input.blockPhaseSpd.y;

                float2 pos2D = blockBasePos.xz;
                float centerDist = length(pos2D);
                float rnd = randomHash(pos2D);

                float2 rippleAnim = float2(_uPulse * 0.8, _uPulse * 0.3);
                float elevation = _uPulse * 20.0;

                // Vertically disperse floating blocks across lower, mid, and upper sky space:
                // Platter is at world Y = -26.0m, camera is at world Y = 0 to 1.6m.
                // Platter-space Y from 4.0 to 50.0 maps to world Y = -22.0m to +24.0m:
                // - Lower tier (near terrain): platter Y ~ 4m to 16m (world -22m to -10m)
                // - Mid tier (around user eye level): platter Y ~ 20m to 34m (world -6m to +8m)
                // - High tier (upper sky/star zone): platter Y ~ 36m to 50m (world +10m to +24m)
                float normH = saturate((blockBasePos.y - 6.0) / 18.0);
                float randTier = frac(sin(phase * 19.17 + rotSpeed * 37.41) * 43758.5453);
                float dispersedY = lerp(4.0, 48.0, randTier) + (normH - 0.5) * 4.0;

                // Gentle continuous floating drift and dual-harmonic bobbing so blocks are never static
                float bob = sin(_uTime * (0.45 + rotSpeed * 0.8) + phase) * 0.85 + cos(_uTime * 0.28 + phase * 1.5) * 0.35;
                float driftX = sin(_uTime * 0.22 + phase) * 0.45;
                float driftZ = cos(_uTime * 0.18 + phase * 1.3) * 0.45;
                float3 centerPos = float3(blockBasePos.x + driftX, dispersedY + bob + _uLiftOffset, blockBasePos.z + driftZ);

                float3 eulerRad = float3(
                    _uTime * rotSpeed + phase,
                    _uTime * rotSpeed * 0.7 + phase,
                    _uTime * rotSpeed * 0.45
                );
                float scaleVal = baseScale * max(0.05, _uPulseScale);
                float3 localOffset = RotateVectorEuler(input.positionOS.xyz * scaleVal, eulerRad);

                float3 platterPos = centerPos + localOffset;
                output.distance = length(platterPos.xz);
                VertexPositionInputs posInputs = GetVertexPositionInputs(platterPos);
                output.positionCS = posInputs.positionCS;

                // Precompute per-block lighting & color constants in vertex shader
                float normElevation = saturate(elevation / 8.0);
                float3 backdropColor = _uFogColor.rgb;
                float outerBaseBlend = glslSmoothstep(22.0, 62.0, centerDist);
                float3 cBase2 = lerp(_uBaseColor2.rgb, backdropColor, outerBaseBlend);

                float3 coolCore = _uCoolCore.rgb;
                float3 coolEdge = _uCoolEdge.rgb;
                float3 warmCore = _uWarmCore.rgb;
                float3 warmEdge = _uWarmEdge.rgb;

                float warmBlend = glslSmoothstep(0.0, 1.0, _uWarmth * 1.5 + (0.5 - centerDist / 80.0));
                float3 zoneCore = lerp(coolCore, warmCore, warmBlend);
                float3 zoneEdge = lerp(coolEdge, warmEdge, warmBlend);

                float3 targetGlow = lerp(zoneCore, zoneEdge, frac(rnd * 11.0));
                float distFade = 1.0 - glslSmoothstep(38.0, 72.0, centerDist);
                float3 brightCool = lerp(coolCore, float3(1.0, 1.0, 1.0), 0.28);
                targetGlow = lerp(targetGlow, brightCool, _uBrightness * 0.6);

                float3 currentGlow = lerp(cBase2, targetGlow, normElevation) * (_uGlowIntensity * 1.25) * distFade;
                currentGlow = lerp(currentGlow, _uRippleColor.rgb * 1.25, rippleAnim.x);
                currentGlow = lerp(currentGlow, float3(1.25, 1.25, 1.25), rippleAnim.y);

                float topIntensity = glslSmoothstep(0.0, 0.36, normElevation);
                float twinkleDistFalloff = glslSmoothstep(58.0, 26.0, centerDist);
                float twinkleMultiplier = lerp(twinkleDistFalloff, 1.0, glslSmoothstep(0.01, 0.1, normElevation));

                float3 baseFace = lerp(cBase2, currentGlow, topIntensity);

                float flashChance = glslSmoothstep(0.3, 1.0, _uPresence);
                if (frac(rnd * 53.0) > 0.98 - flashChance * 0.1)
                {
                    float flashSync = sin(_uTime * 40.0 + rnd * 100.0) * 0.5 + 0.5;
                    baseFace += lerp(float3(1.0, 1.0, 1.0), float3(0.5, 1.0, 1.0), rnd) * flashSync * _uPresence * (1.15 + _uSharpness * 2.2) * twinkleMultiplier;
                }

                baseFace += _uRippleColor.rgb * rippleAnim.x * 0.75;
                baseFace += float3(1.0, 1.0, 1.0) * rippleAnim.y * 1.35;

                output.baseFaceColor = baseFace;
                output.edgeGlowColor = currentGlow * (0.95 * (topIntensity + 0.3));
                output.brillianceSpark = (frac(rnd * 89.0 + _uTime * 2.0) > 0.98) ? (_uBrilliance * 3.4 * twinkleMultiplier) : 0.0;
                output.activeEnergy = saturate(normElevation * 1.65 + rippleAnim.x + rippleAnim.y);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Edges glow on all faces
                float edgeX = glslSmoothstep(0.05, 0.01, input.uv.x) + glslSmoothstep(0.95, 0.99, input.uv.x);
                float edgeY = glslSmoothstep(0.05, 0.01, input.uv.y) + glslSmoothstep(0.95, 0.99, input.uv.y);
                float edge = min(edgeX + edgeY, 1.0);

                float3 finalColor = input.baseFaceColor + input.edgeGlowColor * edge;

                if (edge > 0.5)
                {
                    finalColor += input.brillianceSpark;
                }

                // Quest 3 HDR Contrast Enhancement
                float activeEnergy = input.activeEnergy;
                finalColor = lerp(finalColor, finalColor * (1.18 + 0.36 * activeEnergy), activeEnergy);

                float preFade = 1.0 - glslSmoothstep(32.0, 74.0, input.distance);
                finalColor = lerp(_uFogColor.rgb, finalColor, preFade);

                #if !defined(UNITY_COLORSPACE_GAMMA)
                finalColor = srgbToLinearSafe(finalColor);
                #endif

                float voidFade = preFade * preFade;
                finalColor = lerp(_uBackdropLinear.rgb, finalColor, voidFade);

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
}
