Shader "SonicTopography/GlassStage"
{
    Properties
    {
        _TintColor ("Glass Tint", Color) = (0.008, 0.014, 0.024, 0.035)
        _EdgeColor ("Edge Glow Color", Color) = (0.24, 0.30, 0.38, 0.12)
        _LedColor ("LED Rim Color", Color) = (0.35, 0.78, 1.0, 1.0)
        _LedIntensity ("LED Intensity", Range(0, 2)) = 1.0
        _BezelColor ("Bezel Frame Color", Color) = (0.04, 0.045, 0.055, 1.0)
        _AudioPulse ("Audio Pulse", Float) = 0.0
        _AudioBeat ("Audio Beat", Float) = 0.0
        _GridCrossThickness ("Grid Cross Thickness", Float) = 0.0
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }
        LOD 100

        Pass
        {
            Name "ForwardGlass"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
                float4 _EdgeColor;
                float4 _LedColor;
                float4 _BezelColor;
                float _AudioPulse;
                float _AudioBeat;
                float _GridCrossThickness;
                float _LedIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewDirWS  : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = input.uv;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(posInputs.positionWS);
                output.positionWS = posInputs.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 N = normalize(input.normalWS);
                float3 V = normalize(input.viewDirWS);

                // Accurate Physical Glass Fresnel (Schlick approximation, Float Glass IOR = 1.52, R0 = 0.043)
                float cosTheta = saturate(dot(N, V));
                float omc = 1.0 - cosTheta;
                float omc2 = omc * omc;
                float physicalFresnel = 0.043 + 0.957 * (omc2 * omc2 * omc);

                // Render full glass, LED rim, and bezel for both top (N.y > 0.5) and bottom (N.y < -0.5) faces
                bool isTop = abs(N.y) > 0.5;
                bool isSide = abs(N.y) <= 0.5;

                // =========================================================================
                // Realistic Optical Light Diffusion & Desaturation
                // Real architectural edge-lit glass diffuses light through internal scattering:
                // raw saturated colors are softened toward natural warm-white/neutral luminance.
                // =========================================================================
                float3 rawColor = _LedColor.rgb;
                float3 neutralWhite = float3(0.94, 0.96, 0.98);
                // Balanced 38% desaturation preserves rich vibrant color tone while eliminating harsh digital glare
                float3 fadedColor = lerp(rawColor, neutralWhite, 0.38);
                float3 coreLight = lerp(fadedColor, float3(0.98, 0.99, 1.0), 0.30);

                // =========================================================================
                // 1. TOP & BOTTOM GLASS SURFACES (y = 0.0m and y = -0.12m)
                // =========================================================================
                if (isTop)
                {
                    float2 pos = input.positionWS.xz; // [-1.70, +1.70] meters
                    float halfExtent = 1.70;
                    float distFromPerimeter = min(halfExtent - abs(pos.x), halfExtent - abs(pos.y));

                    // --- A. Outer Titanium Protective Bezel Frame (outer 4cm) ---
                    float bezelMask = 1.0 - smoothstep(0.038, 0.044, distFromPerimeter);
                    float bezelHighlight = smoothstep(0.039, 0.042, distFromPerimeter) * (1.0 - smoothstep(0.042, 0.045, distFromPerimeter));

                    // --- B. Glowing LED Rim Channel (crisp, elegant architectural contrast) ---
                    float ledCenter = 0.058; // 5.8cm from perimeter
                    float dLed = abs(distFromPerimeter - ledCenter);
                    
                    // Well-defined 5.2mm core with 28mm ambient glow halo for clean contrast without razor aliasing
                    float dCore = dLed * (1.0 / 0.0052);
                    float dGlow = dLed * (1.0 / 0.028);
                    float ledCore = exp(-(dCore * dCore));
                    float ledGlow = exp(-(dGlow * dGlow)) * 0.50;
                    float ledTotal = (ledCore * 0.52 + ledGlow * 0.26) * (1.0 - bezelMask) * _LedIntensity;
                    float3 activeLedColor = lerp(fadedColor, coreLight, ledCore * 0.45);

                    // --- C. Polished Glass Chamfer Highlight (between 7.5cm and 11.5cm) ---
                    float chamferZone = smoothstep(0.072, 0.082, distFromPerimeter) * (1.0 - smoothstep(0.105, 0.120, distFromPerimeter));
                    float chamferHighlight = chamferZone * (0.05 + physicalFresnel * 0.18);

                    // --- D. Realistic Physical Glass Surface Reflections ---
                    float3 R = reflect(-V, float3(N.x, abs(N.y), N.z));

                    // 1. Celestial Sky & Starlight Specular Glints
                    float g1 = max(0.0, dot(R, float3(0.350219, 0.820513, 0.450282)));
                    float g1_2 = g1 * g1; float g1_4 = g1_2 * g1_2; float g1_8 = g1_4 * g1_4;
                    float glint1 = (g1_8 * g1_8) * 0.35;

                    float g2 = max(0.0, dot(R, float3(-0.422369, 0.754230, 0.502820)));
                    float g2_2 = g2 * g2; float g2_4 = g2_2 * g2_2; float g2_8 = g2_4 * g2_4; float g2_16 = g2_8 * g2_8;
                    float glint2 = (g2_16 * g2_16) * 0.25;

                    float hBase = 1.0 - saturate(abs(R.y));
                    float hBase2 = hBase * hBase;
                    float horizonSheen = (hBase2 * hBase * sqrt(hBase)) * 0.16;

                    // 2. Procedural Optical Float-Glass Micro-Sheen
                    float2 glassUV = pos * 12.0;
                    float microWave = sin(glassUV.x * 1.8 + cos(glassUV.y * 1.5) * 1.4) * cos(glassUV.y * 2.1 + sin(glassUV.x * 1.3) * 1.1);
                    float mBase = saturate(0.5 + 0.5 * microWave);
                    float mBase2 = mBase * mBase;
                    float microSheen = (mBase2 * mBase2 * mBase2) * 0.05;

                    // 3. Dual-Surface Parallax Reflection (32mm Tempered Glass Thickness)
                    float2 parallaxShift = (V.xz / max(0.22, abs(V.y))) * 0.032;
                    float3 subSurfacePos = float3(pos.x + parallaxShift.x, 0.0, pos.y + parallaxShift.y);
                    float subWave = sin(subSurfacePos.x * 10.0) * cos(subSurfacePos.z * 10.0);
                    float sBase = saturate(0.5 + 0.5 * subWave);
                    float sBase2 = sBase * sBase; float sBase4 = sBase2 * sBase2;
                    float subReflection = (sBase4 * sBase4) * 0.03;
                    float3 temperedGlassTint = float3(0.02, 0.07, 0.10);

                    float3 specularReflection = lerp(float3(0.90, 0.95, 1.0), fadedColor, 0.12) * (glint1 + glint2 + horizonSheen + microSheen);
                    float3 totalReflection = specularReflection + temperedGlassTint * subReflection;

                    // --- E. Composite Physical Glass Surface ---
                    float baseAlpha = _TintColor.a + physicalFresnel * 0.14 + chamferHighlight;
                    float3 baseRgb = lerp(_TintColor.rgb, _EdgeColor.rgb, physicalFresnel * 0.35 + chamferHighlight);

                    // Add physical surface reflections
                    baseRgb += totalReflection * physicalFresnel * 1.8;
                    baseAlpha = saturate(baseAlpha + physicalFresnel * 0.10);

                    // Composite layers:
                    // 1. Base Glass & Physical Reflections
                    float3 finalRgb = baseRgb;
                    float finalAlpha = baseAlpha;

                    // 2. Glowing Diffused Light Channel (crisper architectural contrast & luminous presence)
                    finalRgb = lerp(finalRgb, activeLedColor, ledTotal * 0.88);
                    finalAlpha = saturate(finalAlpha + ledTotal * 0.52);

                    // 3. Matte Titanium Protective Bezel
                    float3 bezelRgb = _BezelColor.rgb + bezelHighlight * 0.14;
                    finalRgb = lerp(finalRgb, bezelRgb, bezelMask);
                    finalAlpha = lerp(finalAlpha, 1.0, bezelMask);

                    return half4(finalRgb, finalAlpha);
                }

                // =========================================================================
                // 2. SIDE FACES (Stage Edge Chassis, y from -0.12m to 0.0m)
                // =========================================================================
                if (isSide)
                {
                    float yLocal = input.positionWS.y;
                    float distFromTop = abs(yLocal - 0.0);

                    // Crisp, elegant architectural wash along top edge of chassis
                    float sCore = distFromTop * (1.0 / 0.012);
                    float sGlow = distFromTop * (1.0 / 0.035);
                    float sideLedCore = exp(-(sCore * sCore));
                    float sideLedGlow = exp(-(sGlow * sGlow));
                    float sideLed = (sideLedCore * 0.42 + sideLedGlow * 0.22) * _LedIntensity;

                    float3 sideChassis = _BezelColor.rgb * 0.85;
                    float3 sideRgb = lerp(sideChassis, fadedColor, sideLed * 0.75);
                    float sideAlpha = lerp(0.85, 0.95, sideLed * 0.5);

                    return half4(sideRgb, sideAlpha);
                }

                // =========================================================================
                // 3. FALLBACK
                // =========================================================================
                float3 bottomRgb = _BezelColor.rgb * 0.70;
                return half4(bottomRgb, 0.65);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ForwardGlassOnly"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
                float4 _EdgeColor;
                float4 _LedColor;
                float4 _BezelColor;
                float _AudioPulse;
                float _AudioBeat;
                float _GridCrossThickness;
                float _LedIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewDirWS  : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = input.uv;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(posInputs.positionWS);
                output.positionWS = posInputs.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 N = normalize(input.normalWS);
                float3 V = normalize(input.viewDirWS);

                float cosTheta = saturate(dot(N, V));
                float omc = 1.0 - cosTheta;
                float omc2 = omc * omc;
                float physicalFresnel = 0.043 + 0.957 * (omc2 * omc2 * omc);

                // Render full glass, LED rim, and bezel for both top (N.y > 0.5) and bottom (N.y < -0.5) faces
                bool isTop = abs(N.y) > 0.5;
                bool isSide = abs(N.y) <= 0.5;

                // =========================================================================
                // Realistic Optical Light Diffusion & Desaturation
                // Real architectural edge-lit glass diffuses light through internal scattering:
                // raw saturated colors are softened toward natural warm-white/neutral luminance.
                // =========================================================================
                float3 rawColor = _LedColor.rgb;
                float3 neutralWhite = float3(0.94, 0.96, 0.98);
                // Balanced 38% desaturation preserves rich vibrant color tone while eliminating harsh digital glare
                float3 fadedColor = lerp(rawColor, neutralWhite, 0.38);
                float3 coreLight = lerp(fadedColor, float3(0.98, 0.99, 1.0), 0.30);

                // =========================================================================
                // 1. TOP & BOTTOM GLASS SURFACES (y = 0.0m and y = -0.12m)
                // =========================================================================
                if (isTop)
                {
                    float2 pos = input.positionWS.xz; // [-1.70, +1.70] meters
                    float halfExtent = 1.70;
                    float distFromPerimeter = min(halfExtent - abs(pos.x), halfExtent - abs(pos.y));

                    // --- A. Outer Titanium Protective Bezel Frame (outer 4cm) ---
                    float bezelMask = 1.0 - smoothstep(0.038, 0.044, distFromPerimeter);
                    float bezelHighlight = smoothstep(0.039, 0.042, distFromPerimeter) * (1.0 - smoothstep(0.042, 0.045, distFromPerimeter));

                    // --- B. Glowing LED Rim Channel (crisp, elegant architectural contrast) ---
                    float ledCenter = 0.058; // 5.8cm from perimeter
                    float dLed = abs(distFromPerimeter - ledCenter);
                    
                    // Well-defined 5.2mm core with 28mm ambient glow halo for clean contrast without razor aliasing
                    float dCore = dLed * (1.0 / 0.0052);
                    float dGlow = dLed * (1.0 / 0.028);
                    float ledCore = exp(-(dCore * dCore));
                    float ledGlow = exp(-(dGlow * dGlow)) * 0.50;
                    float ledTotal = (ledCore * 0.52 + ledGlow * 0.26) * (1.0 - bezelMask) * _LedIntensity;
                    float3 activeLedColor = lerp(fadedColor, coreLight, ledCore * 0.45);

                    // --- C. Polished Glass Chamfer Highlight (between 7.5cm and 11.5cm) ---
                    float chamferZone = smoothstep(0.072, 0.082, distFromPerimeter) * (1.0 - smoothstep(0.105, 0.120, distFromPerimeter));
                    float chamferHighlight = chamferZone * (0.05 + physicalFresnel * 0.18);

                    // --- D. Realistic Physical Glass Surface Reflections ---
                    float3 R = reflect(-V, float3(N.x, abs(N.y), N.z));

                    // 1. Celestial Sky & Starlight Specular Glints
                    float g1 = max(0.0, dot(R, float3(0.350219, 0.820513, 0.450282)));
                    float g1_2 = g1 * g1; float g1_4 = g1_2 * g1_2; float g1_8 = g1_4 * g1_4;
                    float glint1 = (g1_8 * g1_8) * 0.35;

                    float g2 = max(0.0, dot(R, float3(-0.422369, 0.754230, 0.502820)));
                    float g2_2 = g2 * g2; float g2_4 = g2_2 * g2_2; float g2_8 = g2_4 * g2_4; float g2_16 = g2_8 * g2_8;
                    float glint2 = (g2_16 * g2_16) * 0.25;

                    float hBase = 1.0 - saturate(abs(R.y));
                    float hBase2 = hBase * hBase;
                    float horizonSheen = (hBase2 * hBase * sqrt(hBase)) * 0.16;

                    // 2. Procedural Optical Float-Glass Micro-Sheen
                    float2 glassUV = pos * 12.0;
                    float microWave = sin(glassUV.x * 1.8 + cos(glassUV.y * 1.5) * 1.4) * cos(glassUV.y * 2.1 + sin(glassUV.x * 1.3) * 1.1);
                    float mBase = saturate(0.5 + 0.5 * microWave);
                    float mBase2 = mBase * mBase;
                    float microSheen = (mBase2 * mBase2 * mBase2) * 0.05;

                    // 3. Dual-Surface Parallax Reflection (32mm Tempered Glass Thickness)
                    float2 parallaxShift = (V.xz / max(0.22, abs(V.y))) * 0.032;
                    float3 subSurfacePos = float3(pos.x + parallaxShift.x, 0.0, pos.y + parallaxShift.y);
                    float subWave = sin(subSurfacePos.x * 10.0) * cos(subSurfacePos.z * 10.0);
                    float sBase = saturate(0.5 + 0.5 * subWave);
                    float sBase2 = sBase * sBase; float sBase4 = sBase2 * sBase2;
                    float subReflection = (sBase4 * sBase4) * 0.03;
                    float3 temperedGlassTint = float3(0.02, 0.07, 0.10);

                    float3 specularReflection = lerp(float3(0.90, 0.95, 1.0), fadedColor, 0.12) * (glint1 + glint2 + horizonSheen + microSheen);
                    float3 totalReflection = specularReflection + temperedGlassTint * subReflection;

                    // --- E. Composite Physical Glass Surface ---
                    float baseAlpha = _TintColor.a + physicalFresnel * 0.14 + chamferHighlight;
                    float3 baseRgb = lerp(_TintColor.rgb, _EdgeColor.rgb, physicalFresnel * 0.35 + chamferHighlight);

                    // Add physical surface reflections
                    baseRgb += totalReflection * physicalFresnel * 1.8;
                    baseAlpha = saturate(baseAlpha + physicalFresnel * 0.10);

                    // Composite layers:
                    // 1. Base Glass & Physical Reflections
                    float3 finalRgb = baseRgb;
                    float finalAlpha = baseAlpha;

                    // 2. Glowing Diffused Light Channel (crisper architectural contrast & luminous presence)
                    finalRgb = lerp(finalRgb, activeLedColor, ledTotal * 0.88);
                    finalAlpha = saturate(finalAlpha + ledTotal * 0.52);

                    // 3. Matte Titanium Protective Bezel
                    float3 bezelRgb = _BezelColor.rgb + bezelHighlight * 0.14;
                    finalRgb = lerp(finalRgb, bezelRgb, bezelMask);
                    finalAlpha = lerp(finalAlpha, 1.0, bezelMask);

                    return half4(finalRgb, finalAlpha);
                }

                // =========================================================================
                // 2. SIDE FACES (Stage Edge Chassis, y from -0.12m to 0.0m)
                // =========================================================================
                if (isSide)
                {
                    float yLocal = input.positionWS.y;
                    float distFromTop = abs(yLocal - 0.0);

                    // Crisp, elegant architectural wash along top edge of chassis
                    float sCore = distFromTop * (1.0 / 0.012);
                    float sGlow = distFromTop * (1.0 / 0.035);
                    float sideLedCore = exp(-(sCore * sCore));
                    float sideLedGlow = exp(-(sGlow * sGlow));
                    float sideLed = (sideLedCore * 0.42 + sideLedGlow * 0.22) * _LedIntensity;

                    float3 sideChassis = _BezelColor.rgb * 0.85;
                    float3 sideRgb = lerp(sideChassis, fadedColor, sideLed * 0.75);
                    float sideAlpha = lerp(0.85, 0.95, sideLed * 0.5);

                    return half4(sideRgb, sideAlpha);
                }

                // =========================================================================
                // 3. FALLBACK
                // =========================================================================
                float3 bottomRgb = _BezelColor.rgb * 0.70;
                return half4(bottomRgb, 0.65);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SRPDefaultUnlit"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
                float4 _EdgeColor;
                float4 _LedColor;
                float4 _BezelColor;
                float _AudioPulse;
                float _AudioBeat;
                float _GridCrossThickness;
                float _LedIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 viewDirWS  : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.uv = input.uv;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceNormalizeViewDir(posInputs.positionWS);
                output.positionWS = posInputs.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 N = normalize(input.normalWS);
                float3 V = normalize(input.viewDirWS);

                float cosTheta = saturate(dot(N, V));
                float omc = 1.0 - cosTheta;
                float omc2 = omc * omc;
                float physicalFresnel = 0.043 + 0.957 * (omc2 * omc2 * omc);

                // Render full glass, LED rim, and bezel for both top (N.y > 0.5) and bottom (N.y < -0.5) faces
                bool isTop = abs(N.y) > 0.5;
                bool isSide = abs(N.y) <= 0.5;

                // =========================================================================
                // Realistic Optical Light Diffusion & Desaturation
                // Real architectural edge-lit glass diffuses light through internal scattering:
                // raw saturated colors are softened toward natural warm-white/neutral luminance.
                // =========================================================================
                float3 rawColor = _LedColor.rgb;
                float3 neutralWhite = float3(0.94, 0.96, 0.98);
                // Balanced 38% desaturation preserves rich vibrant color tone while eliminating harsh digital glare
                float3 fadedColor = lerp(rawColor, neutralWhite, 0.38);
                float3 coreLight = lerp(fadedColor, float3(0.98, 0.99, 1.0), 0.30);

                // =========================================================================
                // 1. TOP & BOTTOM GLASS SURFACES (y = 0.0m and y = -0.12m)
                // =========================================================================
                if (isTop)
                {
                    float2 pos = input.positionWS.xz; // [-1.70, +1.70] meters
                    float halfExtent = 1.70;
                    float distFromPerimeter = min(halfExtent - abs(pos.x), halfExtent - abs(pos.y));

                    // --- A. Outer Titanium Protective Bezel Frame (outer 4cm) ---
                    float bezelMask = 1.0 - smoothstep(0.038, 0.044, distFromPerimeter);
                    float bezelHighlight = smoothstep(0.039, 0.042, distFromPerimeter) * (1.0 - smoothstep(0.042, 0.045, distFromPerimeter));

                    // --- B. Glowing LED Rim Channel (crisp, elegant architectural contrast) ---
                    float ledCenter = 0.058; // 5.8cm from perimeter
                    float dLed = abs(distFromPerimeter - ledCenter);
                    
                    // Well-defined 5.2mm core with 28mm ambient glow halo for clean contrast without razor aliasing
                    float dCore = dLed * (1.0 / 0.0052);
                    float dGlow = dLed * (1.0 / 0.028);
                    float ledCore = exp(-(dCore * dCore));
                    float ledGlow = exp(-(dGlow * dGlow)) * 0.50;
                    float ledTotal = (ledCore * 0.52 + ledGlow * 0.26) * (1.0 - bezelMask) * _LedIntensity;
                    float3 activeLedColor = lerp(fadedColor, coreLight, ledCore * 0.45);

                    // --- C. Polished Glass Chamfer Highlight (between 7.5cm and 11.5cm) ---
                    float chamferZone = smoothstep(0.072, 0.082, distFromPerimeter) * (1.0 - smoothstep(0.105, 0.120, distFromPerimeter));
                    float chamferHighlight = chamferZone * (0.05 + physicalFresnel * 0.18);

                    // --- D. Realistic Physical Glass Surface Reflections ---
                    float3 R = reflect(-V, float3(N.x, abs(N.y), N.z));

                    // 1. Celestial Sky & Starlight Specular Glints
                    float g1 = max(0.0, dot(R, float3(0.350219, 0.820513, 0.450282)));
                    float g1_2 = g1 * g1; float g1_4 = g1_2 * g1_2; float g1_8 = g1_4 * g1_4;
                    float glint1 = (g1_8 * g1_8) * 0.35;

                    float g2 = max(0.0, dot(R, float3(-0.422369, 0.754230, 0.502820)));
                    float g2_2 = g2 * g2; float g2_4 = g2_2 * g2_2; float g2_8 = g2_4 * g2_4; float g2_16 = g2_8 * g2_8;
                    float glint2 = (g2_16 * g2_16) * 0.25;

                    float hBase = 1.0 - saturate(abs(R.y));
                    float hBase2 = hBase * hBase;
                    float horizonSheen = (hBase2 * hBase * sqrt(hBase)) * 0.16;

                    // 2. Procedural Optical Float-Glass Micro-Sheen
                    float2 glassUV = pos * 12.0;
                    float microWave = sin(glassUV.x * 1.8 + cos(glassUV.y * 1.5) * 1.4) * cos(glassUV.y * 2.1 + sin(glassUV.x * 1.3) * 1.1);
                    float mBase = saturate(0.5 + 0.5 * microWave);
                    float mBase2 = mBase * mBase;
                    float microSheen = (mBase2 * mBase2 * mBase2) * 0.05;

                    // 3. Dual-Surface Parallax Reflection (32mm Tempered Glass Thickness)
                    float2 parallaxShift = (V.xz / max(0.22, abs(V.y))) * 0.032;
                    float3 subSurfacePos = float3(pos.x + parallaxShift.x, 0.0, pos.y + parallaxShift.y);
                    float subWave = sin(subSurfacePos.x * 10.0) * cos(subSurfacePos.z * 10.0);
                    float sBase = saturate(0.5 + 0.5 * subWave);
                    float sBase2 = sBase * sBase; float sBase4 = sBase2 * sBase2;
                    float subReflection = (sBase4 * sBase4) * 0.03;
                    float3 temperedGlassTint = float3(0.02, 0.07, 0.10);

                    float3 specularReflection = lerp(float3(0.90, 0.95, 1.0), fadedColor, 0.12) * (glint1 + glint2 + horizonSheen + microSheen);
                    float3 totalReflection = specularReflection + temperedGlassTint * subReflection;

                    // --- E. Composite Physical Glass Surface ---
                    float baseAlpha = _TintColor.a + physicalFresnel * 0.14 + chamferHighlight;
                    float3 baseRgb = lerp(_TintColor.rgb, _EdgeColor.rgb, physicalFresnel * 0.35 + chamferHighlight);

                    // Add physical surface reflections
                    baseRgb += totalReflection * physicalFresnel * 1.8;
                    baseAlpha = saturate(baseAlpha + physicalFresnel * 0.10);

                    // Composite layers:
                    // 1. Base Glass & Physical Reflections
                    float3 finalRgb = baseRgb;
                    float finalAlpha = baseAlpha;

                    // 2. Glowing Diffused Light Channel (crisper architectural contrast & luminous presence)
                    finalRgb = lerp(finalRgb, activeLedColor, ledTotal * 0.88);
                    finalAlpha = saturate(finalAlpha + ledTotal * 0.52);

                    // 3. Matte Titanium Protective Bezel
                    float3 bezelRgb = _BezelColor.rgb + bezelHighlight * 0.14;
                    finalRgb = lerp(finalRgb, bezelRgb, bezelMask);
                    finalAlpha = lerp(finalAlpha, 1.0, bezelMask);

                    return half4(finalRgb, finalAlpha);
                }

                // =========================================================================
                // 2. SIDE FACES (Stage Edge Chassis, y from -0.12m to 0.0m)
                // =========================================================================
                if (isSide)
                {
                    float yLocal = input.positionWS.y;
                    float distFromTop = abs(yLocal - 0.0);

                    // Crisp, elegant architectural wash along top edge of chassis
                    float sCore = distFromTop * (1.0 / 0.012);
                    float sGlow = distFromTop * (1.0 / 0.035);
                    float sideLedCore = exp(-(sCore * sCore));
                    float sideLedGlow = exp(-(sGlow * sGlow));
                    float sideLed = (sideLedCore * 0.42 + sideLedGlow * 0.22) * _LedIntensity;

                    float3 sideChassis = _BezelColor.rgb * 0.85;
                    float3 sideRgb = lerp(sideChassis, fadedColor, sideLed * 0.75);
                    float sideAlpha = lerp(0.85, 0.95, sideLed * 0.5);

                    return half4(sideRgb, sideAlpha);
                }

                // =========================================================================
                // 3. FALLBACK
                // =========================================================================
                float3 bottomRgb = _BezelColor.rgb * 0.70;
                return half4(bottomRgb, 0.65);
            }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Unlit"
}