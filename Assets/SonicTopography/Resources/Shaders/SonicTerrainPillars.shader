Shader "SonicTopography/TerrainPillars"
{
    Properties
    {
        _BoxWidth ("Box Width", Float) = 0.9
        _uGlowIntensity ("Glow Intensity", Float) = 1.0
        _uArMode ("AR Mode Blend", Range(0, 1)) = 0.0
        _uArOcclusionEnabled ("AR Depth Occlusion", Float) = 0.0
        _uArBaseHeight ("AR Base Height", Float) = 1.55
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

            // Passthrough AR Adaptation (Real Occlusion bypassed for now so crystal pillars stay crisply opaque)
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
                float4 sparkAndArData   : TEXCOORD5; // x = brillianceSpark, y = arFaceShade, z = arMusicLift, w = unused
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

                // VR preserves exact original circular falloff; AR extends organic height & waves cleanly out to the rim
                float vrGlobalFalloff = glslSmoothstep(60.0, 30.0, centerDist);
                float arGlobalFalloff = lerp(0.62, 1.0, glslSmoothstep(77.5, 44.0, centerDist));
                float globalFalloff = lerp(vrGlobalFalloff, arGlobalFalloff, _uArMode);

                float elevation = 0.0;
                float audioElevation = 0.0;
                float arIdleElevation = 0.0;
                float arHillsVal = 0.5;

                if (globalFalloff > 0.0001)
                {
                    // 1. Idle Background state (VR exact original + AR organic crystal-mosaic topography)
                    float2 movingPos = pos2D * 0.05 + float2(_uTime * 0.1, _uTime * 0.05);
                    float baseNoise = (snoise(movingPos) + 1.0) * 0.5;
                    float wave = sin(pos2D.x * 0.15 + pos2D.y * 0.1 - _uTime * 0.6) * 0.5 + 0.5;
                    float vrIdleElevation = lerp(baseNoise, wave, _uSmoothness * 0.5 + 0.2) * 0.8 * vrGlobalFalloff;

                    if (_uArMode > 0.0001)
                    {
                        float2 arDrift1 = pos2D * 0.048 + float2(_uTime * 0.075, -_uTime * 0.045);
                        float2 arDrift2 = pos2D * 0.135 + float2(-_uTime * 0.09, _uTime * 0.065);
                        arHillsVal = snoise(arDrift1) * 0.5 + 0.5;
                        float arRidges = snoise(arDrift2) * 0.5 + 0.5;
                        float arGentleWave = sin(pos2D.x * 0.11 - pos2D.y * 0.08 + _uTime * 0.45) * 0.5 + 0.5;
                        float arTileStep = frac(rnd * 7.13);
                        float arRimWeight = lerp(0.58, 1.0, glslSmoothstep(77.5, 52.0, centerDist));
                        arIdleElevation = (arHillsVal * 1.15 + arRidges * 0.72 + arGentleWave * 0.38 + arTileStep * 0.65) * arRimWeight;
                    }

                    float idleElevation = lerp(vrIdleElevation, arIdleElevation, _uArMode);

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
                    audioElevation = subLift + bassLift + lowMidLift + midLift + highMidLift;

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

                // --- Shared theme palette inputs ---
                float3 backdropColor = _uFogColor.rgb;
                float vrOuterBlend = glslSmoothstep(22.0, 62.0, centerDist);
                float3 vrBase1 = lerp(_uBaseColor1.rgb, backdropColor, vrOuterBlend);
                float3 vrBase2 = lerp(_uBaseColor2.rgb, backdropColor, vrOuterBlend);

                float3 coolCore = _uCoolCore.rgb;
                float3 coolEdge = _uCoolEdge.rgb;
                float3 warmCore = _uWarmCore.rgb;
                float3 warmEdge = _uWarmEdge.rgb;

                float warmBlend = glslSmoothstep(0.0, 1.0, _uWarmth * 1.5 + (0.5 - centerDist / 80.0));
                float3 zoneCore = lerp(coolCore, warmCore, warmBlend);
                float3 zoneEdge = lerp(coolEdge, warmEdge, warmBlend);

                float3 targetGlow = lerp(zoneCore, zoneEdge, frac(rnd * 11.0));
                float3 brightCool = lerp(coolCore, float3(1.0, 1.0, 1.0), 0.28);
                targetGlow = lerp(targetGlow, brightCool, _uBrightness * 0.6);

                // --- Exact VR path (untouched when _uArMode == 0) ---
                float vrNormElevation = saturate(elevation / 8.0);
                float vrDistFade = 1.0 - glslSmoothstep(38.0, 72.0, centerDist);
                float3 vrCurrentGlow = lerp(vrBase2, targetGlow, vrNormElevation) * (_uGlowIntensity * 1.22) * vrDistFade;
                vrCurrentGlow = lerp(vrCurrentGlow, _uRippleColor.rgb * 1.25, rippleAnim.x);
                vrCurrentGlow = lerp(vrCurrentGlow, float3(1.25, 1.25, 1.25), rippleAnim.y);
                float vrActiveEnergy = saturate(vrNormElevation * 1.65 + rippleAnim.x + rippleAnim.y);

                // --- AR Luminous Crystal/LED Tile path (only computed when _uArMode > 0) ---
                float arNormElevation = vrNormElevation;
                float arDistFade = vrDistFade;
                float3 arCurrentGlow = vrCurrentGlow;
                float3 arSideBottom = vrBase1;
                float3 arSideMid = vrBase2;
                float3 arTopBevelColor = vrCurrentGlow;
                float arActiveEnergy = vrActiveEnergy;
                float arMusicLift = 0.0;
                float arFaceShade = 1.0;

                if (_uArMode > 0.0001)
                {
                    // Keep the entire disc glowing crisply right to the circular physical edge
                    arDistFade = lerp(0.84, 1.0, glslSmoothstep(77.5, 58.0, centerDist));

                    arMusicLift = saturate(audioElevation / 6.0 + rippleAnim.x * 0.55 + rippleAnim.y * 0.75);
                    float arTopoNorm = saturate(elevation / 4.2);
                    float arIdleGlowFactor = 0.85 + _uArIdleBoost * 0.45;
                    arNormElevation = saturate(0.28 + arTopoNorm * 0.54 + arMusicLift * 0.42);

                    float arWarmFactor = saturate(warmBlend * 0.68 + arMusicLift * 0.32);
                    float3 arCoreMix = lerp(coolCore, warmCore, arWarmFactor);
                    float3 arEdgeMix = lerp(coolEdge, warmEdge, arWarmFactor);

                    float tileRnd = frac(rnd * 17.31);
                    float tileSpark = lerp(0.80, 1.18, tileRnd);

                    // Luminous translucent mosaic body color with scattered theme ripple-accent crystal tiles
                    float3 arCrystalMid = lerp(arCoreMix, arEdgeMix, 0.30 + 0.55 * frac(rnd * 11.0));
                    float arAccentMix = glslSmoothstep(0.42, 0.96, frac(rnd * 23.7 + arHillsVal * 0.35)) * 0.38;
                    arCrystalMid = lerp(arCrystalMid, _uRippleColor.rgb, arAccentMix);
                    arCrystalMid = lerp(arCrystalMid, brightCool, _uBrightness * 0.45);

                    // Rich saturated backlit crystal troughs (never black) + slim dark grounded chassis bottom
                    float3 arCoolBase = lerp(coolCore, coolEdge, 0.25);
                    float3 arDeepCrystal = arCoolBase * 0.44 + arCrystalMid * 0.14;
                    arSideBottom = arDeepCrystal * 0.20;

                    // Incandescent crest highlights for raised idle ridges & active audio peaks without blowing out flat white
                    float arGlowScale = clamp(_uGlowIntensity, 0.75, 1.65) * 0.76 * arIdleGlowFactor;
                    float arCrestMix = saturate(arTopoNorm * 0.80 + arMusicLift * 0.48);
                    float3 arIncandescent = lerp(arCrystalMid, float3(1.0, 1.0, 1.0), 0.34 + 0.24 * arMusicLift);

                    arCurrentGlow = lerp(arDeepCrystal, arCrystalMid, glslSmoothstep(0.04, 0.52, arCrestMix));
                    arCurrentGlow = lerp(arCurrentGlow, arIncandescent, glslSmoothstep(0.46, 1.0, arCrestMix));
                    arCurrentGlow *= tileSpark * arGlowScale * arDistFade;

                    arCurrentGlow = lerp(arCurrentGlow, _uRippleColor.rgb * 1.12, rippleAnim.x);
                    arCurrentGlow = lerp(arCurrentGlow, float3(1.16, 1.16, 1.16), rippleAnim.y);

                    arSideMid = arCurrentGlow * 0.84;
                    arTopBevelColor = lerp(arCurrentGlow, lerp(arEdgeMix, float3(1.0, 1.0, 1.0), 0.52), 0.44) * arDistFade;
                    arActiveEnergy = saturate(arMusicLift * 1.35 + rippleAnim.x + rippleAnim.y);

                    // Subtle directional X/Z facet contrast so adjacent 3D crystal cubes separate crisply
                    if (abs(input.normalOS.x) > 0.5)
                    {
                        arFaceShade = (input.normalOS.x > 0.0) ? 1.06 : 0.80;
                    }
                    else if (abs(input.normalOS.z) > 0.5)
                    {
                        arFaceShade = (input.normalOS.z > 0.0) ? 0.94 : 0.72;
                    }
                }

                float normElevation = lerp(vrNormElevation, arNormElevation, _uArMode);
                float distFade = lerp(vrDistFade, arDistFade, _uArMode);
                float3 currentGlow = lerp(vrCurrentGlow, arCurrentGlow, _uArMode);
                float activeEnergy = lerp(vrActiveEnergy, arActiveEnergy, _uArMode);

                output.currentGlowAndN = float4(currentGlow, normElevation);

                float3 rippleAdd = _uRippleColor.rgb * rippleAnim.x * 0.75 + float3(1.0, 1.0, 1.0) * rippleAnim.y * 1.35;
                output.rippleAddAndE = float4(rippleAdd, activeEnergy);

                if (input.normalOS.y > 0.5)
                {
                    float topIntensity = glslSmoothstep(0.0, 0.36, vrNormElevation);
                    float twinkleDistFalloff = glslSmoothstep(58.0, 26.0, centerDist);
                    float twinkleMultiplier = lerp(twinkleDistFalloff, 1.0, glslSmoothstep(0.01, 0.1, vrNormElevation));
                    float arTwinkleMultiplier = 1.0;

                    if (frac(rnd * 31.0) > 0.95 && vrNormElevation < 0.1)
                    {
                        topIntensity += _uAir * 2.2 * twinkleMultiplier;
                    }

                    float3 vrTopBaseColor = lerp(vrBase2, vrCurrentGlow, topIntensity);
                    float3 arTopBaseColor = arCurrentGlow * 0.94;
                    if (_uArMode > 0.0001 && frac(rnd * 31.0) > 0.94)
                    {
                        arTopBaseColor += arTopBevelColor * (_uAir * 0.42);
                    }

                    float flashChance = glslSmoothstep(0.3, 1.0, _uPresence);
                    if (frac(rnd * 53.0) > 0.98 - flashChance * 0.1)
                    {
                        float flashSync = sin(_uTime * 40.0 + rnd * 100.0) * 0.5 + 0.5;
                        float3 flashColor = lerp(float3(1.0, 1.0, 1.0), float3(0.5, 1.0, 1.0), rnd) * flashSync * _uPresence * (1.15 + _uSharpness * 2.2);
                        vrTopBaseColor += flashColor * twinkleMultiplier;
                        arTopBaseColor += flashColor * (0.45 * arTwinkleMultiplier);
                    }

                    float3 vrTopEdgeGlow = vrCurrentGlow * 0.92 * (topIntensity + 0.24 * vrDistFade);
                    float3 topBaseColor = lerp(vrTopBaseColor, arTopBaseColor, _uArMode);
                    float3 topEdgeGlow = lerp(vrTopEdgeGlow, arTopBevelColor, _uArMode);

                    output.topBaseOrCBase1 = float4(topBaseColor, 1.0);
                    output.topEdgeOrCBase2 = float4(topEdgeGlow, distFade);

                    float sparkMul = lerp(twinkleMultiplier, 1.0, _uArMode);
                    float spark = (frac(rnd * 89.0 + _uTime * 2.0) > 0.98) ? (_uBrilliance * 3.4 * sparkMul) : 0.0;
                    output.sparkAndArData = float4(spark, 1.0, arMusicLift, 0.0);
                }
                else
                {
                    float3 sideBase1 = lerp(vrBase1, arSideBottom, _uArMode);
                    float3 sideBase2 = lerp(vrBase2, arSideMid, _uArMode);
                    output.topBaseOrCBase1 = float4(sideBase1, 0.0);
                    output.topEdgeOrCBase2 = float4(sideBase2, distFade);
                    output.sparkAndArData = float4(0.0, arFaceShade, arMusicLift, 0.0);
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
                    // Top face (exact VR math preserved when _uArMode == 0)
                    float edgeX = glslSmoothstep(0.05, 0.01, uv.x) + glslSmoothstep(0.95, 0.99, uv.x);
                    float edgeY = glslSmoothstep(0.05, 0.01, uv.y) + glslSmoothstep(0.95, 0.99, uv.y);
                    float edge = min(edgeX + edgeY, 1.0);
                    float3 vrTopFinal = input.topBaseOrCBase1.rgb + input.topEdgeOrCBase2.rgb * edge;

                    // Brilliance micro-sparks strictly on edges
                    if (edge > 0.5)
                    {
                        vrTopFinal += input.sparkAndArData.x;
                    }

                    if (_uArMode > 0.001)
                    {
                        // Frosted translucent crystal-LED tile with luminous inner chamfer & crisp cube seam
                        float2 edgeDist2 = min(uv, 1.0 - uv);
                        float minEdge = min(edgeDist2.x, edgeDist2.y);
                        float arBevelCrisp = 1.0 - glslSmoothstep(0.018, 0.11, minEdge);
                        float arBevelSoft = 1.0 - glslSmoothstep(0.05, 0.26, minEdge);
                        float2 centeredUV = (uv - 0.5) * 2.0;
                        float arCoreGlow = 1.0 - saturate(dot(centeredUV, centeredUV)) * 0.18;

                        float3 arTopFinal = input.topBaseOrCBase1.rgb * (0.88 + 0.16 * arCoreGlow)
                                          + input.topEdgeOrCBase2.rgb * (arBevelSoft * 0.22 + arBevelCrisp * 0.44);

                        float arOuterSeam = glslSmoothstep(0.0, 0.025, minEdge);
                        arTopFinal *= lerp(0.82, 1.0, arOuterSeam);

                        if (arBevelCrisp > 0.35)
                        {
                            arTopFinal += input.sparkAndArData.x * 0.65;
                        }

                        finalColor = lerp(vrTopFinal, arTopFinal, _uArMode);
                    }
                    else
                    {
                        finalColor = vrTopFinal;
                    }
                }
                else
                {
                    // Side faces (exact VR math preserved when _uArMode == 0)
                    float distFromTop = 1.0 - relativeY;
                    float3 bodyColor = lerp(input.topBaseOrCBase1.rgb, input.topEdgeOrCBase2.rgb, relativeY * distFade);
                    float verticalFalloff = lerp(1.0, 3.0, _uSharpness);
                    float sideGlow = (normElevation < 0.02) ? 0.0 : (glslSmoothstep(0.5 / verticalFalloff, 0.0, distFromTop) * normElevation);

                    float3 vrSideFinal = lerp(bodyColor, currentGlow, sideGlow * 1.65);

                    // Top Rim
                    float rimGlow = glslSmoothstep(0.035, 0.0, distFromTop) * normElevation;
                    vrSideFinal += currentGlow * rimGlow * 1.25;

                    if (_uArMode > 0.001)
                    {
                        // Backlit translucent crystal walls with grounded dark bottom chassis & directional X/Z facet contrast
                        float baseGroundT = glslSmoothstep(0.06, 0.24, relativeY);
                        float upperLiftT = glslSmoothstep(0.20, 1.0, relativeY);
                        float3 arWallColor = lerp(input.topBaseOrCBase1.rgb, input.topEdgeOrCBase2.rgb, baseGroundT);
                        arWallColor = lerp(arWallColor, currentGlow * 1.02, upperLiftT * (0.48 + 0.42 * normElevation));

                        // Directional X vs Z face contrast so 3D blocks read as distinct physical cubes
                        arWallColor *= input.sparkAndArData.y;

                        // Subtle translucent corner edge refraction along vertical seams
                        float sideEdgeX = 1.0 - glslSmoothstep(0.02, 0.14, min(uv.x, 1.0 - uv.x));
                        arWallColor += currentGlow * sideEdgeX * baseGroundT * 0.18;

                        // Luminous upper rim catching internal LED backlight
                        float arRimGlow = glslSmoothstep(0.11, 0.0, distFromTop);
                        arWallColor += currentGlow * arRimGlow * (0.28 + 0.34 * normElevation);

                        finalColor = lerp(vrSideFinal, arWallColor, _uArMode);
                    }
                    else
                    {
                        finalColor = vrSideFinal;
                    }
                }

                finalColor += input.rippleAddAndE.rgb * lerp(1.0, 0.55, _uArMode);

                // Quest 3 HDR Contrast Enhancement: exact original in VR; gentler in AR so luminous crystal colors never clip flat white
                float activeEnergy = input.rippleAddAndE.w;
                float contrastGain = lerp(1.18 + 0.36 * activeEnergy, 1.04 + 0.20 * activeEnergy, _uArMode);
                finalColor = lerp(finalColor, finalColor * contrastGain, activeEnergy);

                // Smooth distance fade into backdropColor before linear conversion (bypassed cleanly in AR mode)
                float preFade = lerp(1.0 - glslSmoothstep(32.0, 74.0, vertexDist), 1.0, _uArMode);
                finalColor = lerp(_uFogColor.rgb, finalColor, preFade);

                #if !defined(UNITY_COLORSPACE_GAMMA)
                finalColor = srgbToLinearSafe(finalColor);
                #endif

                // Exact Linear-space infinite-void horizon blend (disabled in AR so physical rim stays crisp)
                float voidFade = preFade * preFade;
                finalColor = lerp(_uBackdropLinear.rgb, finalColor, voidFade);

                // Very subtle Quest 3 passthrough camera micro-grain (keeps crystal blocks jewel-like and crisp)
                if (_uArMode > 0.001)
                {
                    float pixelHash = frac(sin(dot(floor(input.positionCS.xy), float2(12.9898, 78.233)) + _uTime * 19.37) * 43758.5453);
                    float grain = (pixelHash - 0.5) * 0.012 * _uArMode;
                    finalColor = max(0.0, finalColor + grain * (0.20 + 0.65 * saturate(dot(finalColor, float3(0.3, 0.59, 0.11)))));
                }

                // Real Occlusion intentionally bypassed in AR for now so crystal pillars render crisply opaque with zero depth artifacts
                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }
    }
}
