Shader "SonicTopography/TerrainPillars"
{
    Properties
    {
        _BoxWidth ("Box Width", Float) = 0.9
        _uGlowIntensity ("Glow Intensity", Float) = 1.0
        _uArMode ("AR Mode Blend", Range(0, 1)) = 0.0
        _uArOcclusionEnabled ("AR Depth Occlusion", Float) = 1.0
        _uArBaseHeight ("AR Base Height", Float) = 0.045
        _uArSurfaceLiftWorld ("AR Surface Lift (m)", Float) = 0.005
        _uArIdleBoost ("AR Idle Glow Boost", Float) = 0.32
        _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.025
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
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"

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

            // Theme Uniforms (stored in sRGB 0..1 space to match original GLSL math)
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

            // Platter & Grid
            float4x4 _PlatterMatrix;
            float _BoxWidth;

            // Passthrough AR Adaptation
            float _uArMode;
            float _uArOcclusionEnabled;
            float _uArBaseHeight;
            float _uArSurfaceLiftWorld;
            float _uArIdleBoost;
            float _EnvironmentDepthBias;

            // Ripples: xy = pos, z = time, w = strength
            float4 _uRipplePosTimeStrength[10];
            // Ripples state: x = isActive, y = rippleType (0 = normal, 1 = white)
            float4 _uRippleState[10];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                float2 pillarXZ   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS       : SV_POSITION;
                float4 uvAndRelYDist    : TEXCOORD0; // xy = uv, z = relativeY, w = distance
                float4 topBaseOrCBase1  : TEXCOORD1; // rgb = topBaseColor (top) or cBase1 (side), w = normalY
                float4 topEdgeOrCBase2  : TEXCOORD2; // rgb = topEdgeGlow (top) or cBase2 (side), w = distFade
                float4 currentGlowAndN  : TEXCOORD3; // rgb = currentGlow, w = normElevation
                float4 rippleAddAndE    : TEXCOORD4; // rgb = rippleAdd, w = activeEnergy
                float  brillianceSpark  : TEXCOORD5;
                float3 worldPos         : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // GLSL-compatible smoothstep that safely handles edge0 > edge1 on mobile Vulkan/SPIR-V
            float glslSmoothstep(float edge0, float edge1, float x)
            {
                float t = saturate((x - edge0) / (edge1 - edge0));
                return t * t * (3.0 - 2.0 * t);
            }

            // 2D Simplex noise (matching CustomShaderMaterial.ts)
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
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // input.pillarXZ holds the pillar's platter-local translation (px, pz) baked into TEXCOORD1
                float2 pos2D = input.pillarXZ;
                float3 instancePos = float3(pos2D.x, 0.5, pos2D.y);

                float centerDist = length(pos2D);
                float rnd = randomHash(pos2D);

                float globalFalloff = glslSmoothstep(60.0, 30.0, centerDist);
                float elevation = 0.0;

                if (globalFalloff > 0.0001)
                {
                    // 1. Idle Background state (smooth, ocean-like)
                    float2 movingPos = pos2D * 0.05 + float2(_uTime * 0.1, _uTime * 0.05);
                    float baseNoise = (snoise(movingPos) + 1.0) * 0.5;
                    float wave = sin(pos2D.x * 0.15 + pos2D.y * 0.1 - _uTime * 0.6) * 0.5 + 0.5;
                    float idleElevation = lerp(baseNoise, wave, _uSmoothness * 0.5 + 0.2) * 0.8 * globalFalloff;

                    // 2. Frequency Regions & Displacements
                    // Sub-Bass: Center heavy, ultra slow rolling hills, massive block lifts
                    float subRegion = glslSmoothstep(25.0, 0.0, centerDist);
                    float subLift = _uSubBass * subRegion * 5.0;

                    // Bass: Chunk-based lifts, less rigid than sub, but still clustered
                    float bassLift = 0.0;
                    if (centerDist < 40.0 && _uBass > 0.0001)
                    {
                        float bassNoise = snoise(pos2D * 0.1 - float2(0.0, _uTime * 0.2));
                        float bassRegion = glslSmoothstep(35.0, 5.0, centerDist + bassNoise * 5.0);
                        bassLift = _uBass * bassRegion * glslSmoothstep(0.0, 1.0, rnd + _uDensity * 0.5) * 4.0;
                    }

                    // Low Mid: Flowing waves across the whole map slowly
                    float lowMidLift = 0.0;
                    if (_uLowMid > 0.0001)
                    {
                        float lowMidNoise = snoise(pos2D * 0.05 + float2(_uTime * 0.1, 0.0));
                        lowMidLift = _uLowMid * (lowMidNoise * 0.5 + 0.5) * 2.5;
                    }

                    // Mid: River-like current. Strong diagonal flow.
                    float midLift = 0.0;
                    if (_uMid > 0.0001)
                    {
                        float riverFlow = sin(pos2D.x * 0.2 + pos2D.y * 0.2 + snoise(pos2D * 0.1) * 2.0 - _uTime * 2.0);
                        midLift = _uMid * max(0.0, riverFlow) * 3.0;
                    }

                    // High Mid: Individual scattered spikes, highly dependent on column random
                    float highMidLift = 0.0;
                    if (frac(rnd * 13.3) > 0.8)
                    {
                        float highMidRegion = glslSmoothstep(10.0, 45.0, centerDist);
                        highMidLift = _uHighMid * highMidRegion * frac(rnd * 7.7) * 2.5;
                    }

                    // Combine
                    float audioElevation = subLift + bassLift + lowMidLift + midLift + highMidLift;

                    // Energy Spike
                    if (rnd > 0.99)
                    {
                        audioElevation += _uEnergy * 5.0;
                    }

                    audioElevation *= globalFalloff;

                    // NOISE GATE: Prevent the noise floor from lifting the entire terrain base
                    audioElevation = max(0.0, audioElevation - 0.2);

                    // Apply overall amplitude scaling
                    audioElevation *= _uAmplitude;

                    elevation = idleElevation + audioElevation;
                }

                // Ripples
                float rippleElevation = 0.0;
                float rippleIntensityNormal = 0.0;
                float rippleIntensityWhite = 0.0;
                const float baseSpeed = 15.0;
                const float baseWidth = 3.0;

                [unroll]
                for (int i = 0; i < 10; i++)
                {
                    float isActive = _uRippleState[i].x;
                    if (isActive > 0.0)
                    {
                        float2 rPos = _uRipplePosTimeStrength[i].xy;
                        float rTime = _uRipplePosTimeStrength[i].z;
                        float rStrength = _uRipplePosTimeStrength[i].w;
                        float rType = _uRippleState[i].y;

                        float dist = length(pos2D - rPos);
                        float timeSince = max(0.0, _uTime - rTime);

                        float curSpeed = baseSpeed;
                        float curWidth = baseWidth;
                        float curFadeDist = 15.0;
                        float elevationScale = 4.0;

                        if (rType > 0.5)
                        {
                            curSpeed = 20.0;
                            curWidth = 1.0;
                            curFadeDist = 8.0;
                            elevationScale = 1.0;
                        }

                        float waveRadius = timeSince * curSpeed;
                        float d = dist - waveRadius;
                        float rippleWave = exp(-d * d / curWidth);
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

                float yPos = input.positionOS.y + 0.5; // 0 to 1
                float baseHeight = lerp(1.0, _uArBaseHeight, _uArMode);
                float totalHeight = baseHeight + elevation;
                float3 localPos = float3(
                    input.positionOS.x * _BoxWidth,
                    -0.5 + yPos * totalHeight,
                    input.positionOS.z * _BoxWidth
                );

                float3 platterPos = instancePos + localPos;
                float vertexDist = length(platterPos.xz);
                float3 worldPos = TransformObjectToWorld(platterPos);
                if (_uArMode > 0.0001)
                {
                    float3 platterUpWS = normalize(TransformObjectToWorldDir(float3(0.0, 1.0, 0.0)));
                    worldPos += platterUpWS * (_uArSurfaceLiftWorld * _uArMode);
                }
                output.positionCS = TransformWorldToHClip(worldPos);
                output.worldPos = worldPos;
                output.uvAndRelYDist = float4(input.uv.x, input.uv.y, yPos, vertexDist);

                // Precompute constant per-pillar colors & glow once per vertex instead of per pixel
                float normElevation = saturate(elevation / 8.0);
                float arIdlePulse = sin(centerDist * 0.16 - _uTime * 1.45) * 0.5 + 0.5;
                float arIdleWave = _uArMode * _uArIdleBoost * (0.35 + 0.65 * arIdlePulse);
                normElevation = saturate(normElevation + arIdleWave * (1.0 - normElevation * 0.6));

                float3 backdropColor = _uFogColor.rgb;
                float vrOuterBlend = glslSmoothstep(22.0, 62.0, centerDist);
                float arOuterBlend = glslSmoothstep(66.0, 77.5, centerDist) * 0.22;
                float outerBaseBlend = lerp(vrOuterBlend, arOuterBlend, _uArMode);
                float3 cBase1 = lerp(_uBaseColor1.rgb, backdropColor, outerBaseBlend);
                float3 cBase2 = lerp(_uBaseColor2.rgb, backdropColor, outerBaseBlend);

                float3 coolCore = _uCoolCore.rgb;
                float3 coolEdge = _uCoolEdge.rgb;
                float3 warmCore = _uWarmCore.rgb;
                float3 warmEdge = _uWarmEdge.rgb;

                float warmBlend = glslSmoothstep(0.0, 1.0, _uWarmth * 1.5 + (0.5 - centerDist / 80.0));
                float3 zoneCore = lerp(coolCore, warmCore, warmBlend);
                float3 zoneEdge = lerp(coolEdge, warmEdge, warmBlend);

                float3 targetGlow = lerp(zoneCore, zoneEdge, frac(rnd * 11.0));
                float distFade = 1.0 - glslSmoothstep(lerp(38.0, 71.5, _uArMode), lerp(72.0, 77.4, _uArMode), centerDist);

                float3 brightCool = lerp(coolCore, float3(1.0, 1.0, 1.0), 0.28);
                targetGlow = lerp(targetGlow, brightCool, _uBrightness * 0.6);

                float3 currentGlow = lerp(cBase2, targetGlow, normElevation) * (_uGlowIntensity * (1.22 + 0.18 * _uArMode)) * distFade;
                currentGlow = lerp(currentGlow, _uRippleColor.rgb * 1.25, rippleAnim.x);
                currentGlow = lerp(currentGlow, float3(1.25, 1.25, 1.25), rippleAnim.y);

                output.currentGlowAndN = float4(currentGlow, normElevation);

                float3 rippleAdd = _uRippleColor.rgb * rippleAnim.x * 0.75 + float3(1.0, 1.0, 1.0) * rippleAnim.y * 1.35;
                float activeEnergy = saturate(normElevation * 1.65 + rippleAnim.x + rippleAnim.y);
                output.rippleAddAndE = float4(rippleAdd, activeEnergy);

                if (input.normalOS.y > 0.5)
                {
                    float topIntensity = glslSmoothstep(0.0, 0.36, normElevation);
                    float twinkleDistFalloff = glslSmoothstep(58.0, 26.0, centerDist);
                    float twinkleMultiplier = lerp(twinkleDistFalloff, 1.0, glslSmoothstep(0.01, 0.1, normElevation));

                    if (frac(rnd * 31.0) > 0.95 && normElevation < 0.1)
                    {
                        topIntensity += _uAir * 2.2 * twinkleMultiplier;
                    }

                    float3 topBaseColor = lerp(cBase2, currentGlow, topIntensity);

                    float flashChance = glslSmoothstep(0.3, 1.0, _uPresence);
                    if (frac(rnd * 53.0) > 0.98 - flashChance * 0.1)
                    {
                        float flashSync = sin(_uTime * 40.0 + rnd * 100.0) * 0.5 + 0.5;
                        topBaseColor += lerp(float3(1.0, 1.0, 1.0), float3(0.5, 1.0, 1.0), rnd) * flashSync * _uPresence * (1.15 + _uSharpness * 2.2) * twinkleMultiplier;
                    }

                    float3 topEdgeGlow = currentGlow * 0.92 * (topIntensity + 0.24 * distFade);
                    output.topBaseOrCBase1 = float4(topBaseColor, 1.0);
                    output.topEdgeOrCBase2 = float4(topEdgeGlow, distFade);
                    output.brillianceSpark = (frac(rnd * 89.0 + _uTime * 2.0) > 0.98) ? (_uBrilliance * 3.4 * twinkleMultiplier) : 0.0;
                }
                else
                {
                    output.topBaseOrCBase1 = float4(cBase1, 0.0);
                    output.topEdgeOrCBase2 = float4(cBase2, distFade);
                    output.brillianceSpark = 0.0;
                }

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.uvAndRelYDist.xy;
                float relativeY = input.uvAndRelYDist.z;
                float vertexDist = input.uvAndRelYDist.w;
                float3 currentGlow = input.currentGlowAndN.rgb;
                float normElevation = input.currentGlowAndN.w;
                float distFade = input.topEdgeOrCBase2.w;

                float3 finalColor;

                if (input.topBaseOrCBase1.w > 0.5)
                {
                    // Top face
                    float edgeX = glslSmoothstep(0.05, 0.01, uv.x) + glslSmoothstep(0.95, 0.99, uv.x);
                    float edgeY = glslSmoothstep(0.05, 0.01, uv.y) + glslSmoothstep(0.95, 0.99, uv.y);
                    float edge = min(edgeX + edgeY, 1.0);
                    finalColor = input.topBaseOrCBase1.rgb + input.topEdgeOrCBase2.rgb * edge;

                    // Brilliance micro-sparks strictly on edges
                    if (edge > 0.5)
                    {
                        finalColor += input.brillianceSpark;
                    }
                }
                else
                {
                    // Side faces
                    float distFromTop = 1.0 - relativeY;
                    float3 bodyColor = lerp(input.topBaseOrCBase1.rgb, input.topEdgeOrCBase2.rgb, relativeY * distFade);
                    float verticalFalloff = lerp(1.0, 3.0, _uSharpness);
                    float sideGlow = (normElevation < 0.02) ? 0.0 : (glslSmoothstep(0.5 / verticalFalloff, 0.0, distFromTop) * normElevation);

                    finalColor = lerp(bodyColor, currentGlow, sideGlow * 1.65);

                    // Top Rim
                    float rimGlow = glslSmoothstep(0.035, 0.0, distFromTop) * normElevation;
                    finalColor += currentGlow * rimGlow * 1.25;
                }

                finalColor += input.rippleAddAndE.rgb;

                // Quest 3 HDR Contrast Enhancement: keep unlit base deep, punch active glowing peaks
                float activeEnergy = input.rippleAddAndE.w;
                finalColor = lerp(finalColor, finalColor * (1.18 + 0.36 * activeEnergy), activeEnergy);

                // Smooth distance fade into backdropColor before linear conversion (bypassed cleanly in AR mode)
                float preFade = lerp(1.0 - glslSmoothstep(32.0, 74.0, vertexDist), 1.0, _uArMode);
                finalColor = lerp(_uFogColor.rgb, finalColor, preFade);

                #if !defined(UNITY_COLORSPACE_GAMMA)
                finalColor = srgbToLinearSafe(finalColor);
                #endif

                // Exact Linear-space infinite-void horizon blend (disabled in AR so physical rim stays crisp)
                float voidFade = preFade * preFade;
                finalColor = lerp(_uBackdropLinear.rgb, finalColor, voidFade);

                // Subtle Quest 3 passthrough camera blending (temporal micro-grain & gentle edge softening)
                if (_uArMode > 0.001)
                {
                    float pixelHash = frac(sin(dot(floor(input.positionCS.xy), float2(12.9898, 78.233)) + _uTime * 19.37) * 43758.5453);
                    float grain = (pixelHash - 0.5) * 0.022 * _uArMode;
                    finalColor = max(0.0, finalColor + grain * (0.25 + 0.75 * saturate(dot(finalColor, float3(0.3, 0.59, 0.11)))));

                    float2 edgeDistUV = min(uv, 1.0 - uv);
                    float edgeSoft = smoothstep(0.0, 0.035, min(edgeDistUV.x, edgeDistUV.y));
                    finalColor *= lerp(1.0, lerp(0.92, 1.0, edgeSoft), _uArMode);
                }

                half4 finalOut = half4(finalColor, 1.0h);

                #if defined(HARD_OCCLUSION) || defined(SOFT_OCCLUSION)
                if (_uArMode > 0.01 && _uArOcclusionEnabled > 0.5)
                {
                    float occ = CalculateEnvironmentDepthOcclusion(input.worldPos, _EnvironmentDepthBias);
                    finalOut *= (half)occ;
                }
                #endif

                return finalOut;
            }
            ENDHLSL
        }
    }
}
