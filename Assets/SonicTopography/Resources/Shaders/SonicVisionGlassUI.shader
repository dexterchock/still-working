Shader "SonicTopography/SonicVisionGlassUI"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _GlassColor ("Glass Flat Tint", Color) = (0.022, 0.022, 0.022, 0.96)
        _GlassTopColor ("Glass Top Tint", Color) = (0.022, 0.022, 0.022, 0.96)
        _GlassBottomColor ("Glass Bottom Tint", Color) = (0.022, 0.022, 0.022, 0.96)
        _BlurRadius ("Backdrop Blur Radius", Float) = 0.058
        _BackdropWeight ("Backdrop Blur Mix", Float) = 0.30

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
        _TopClipFade ("Top Clip Fade Distance", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
            "RenderPipeline"="UniversalPipeline"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "VisionGlassUI"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1; // Normalized Rect UV (0..1)
                float2 uv2        : TEXCOORD2; // Rect Size (width, height) in Canvas px
                float4 uv3        : TEXCOORD3; // (cornerRadius, mode * 10 + borderIntensity, glowMargin, 0)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color      : COLOR;
                float2 uv0        : TEXCOORD0;
                float2 rectUV     : TEXCOORD1;
                float2 rectSize   : TEXCOORD2;
                float4 shapeParam : TEXCOORD3;
                float4 positionOS : TEXCOORD4;
                float4 screenPos  : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _Color;
            float4 _GlassColor;
            float4 _GlassTopColor;
            float4 _GlassBottomColor;
            float _BlurRadius;
            float _BackdropWeight;
            float4 _ClipRect;
            float _TopClipFade;

            float sdRoundedBox(float2 p, float2 b, float r)
            {
                float2 q = abs(p) - b + r;
                return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionOS = IN.positionOS;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv0 = IN.uv0;

                // Fallback if SonicGlassSurface wasn't attached
                bool hasCustomSize = (IN.uv2.x > 0.5 && IN.uv2.y > 0.5);
                OUT.rectUV = hasCustomSize ? IN.uv1 : IN.uv0;
                OUT.rectSize = hasCustomSize ? IN.uv2 : float2(100.0, 100.0);
                OUT.shapeParam = hasCustomSize ? IN.uv3 : float4(16.0, 10.25, 16.0, 0.0);

                OUT.color = IN.color * _Color;
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // 1. 2D ScrollView RectMask2D clipping in Canvas space
                float2 dMin = IN.positionOS.xy - _ClipRect.xy;
                float2 dMax = _ClipRect.zw - IN.positionOS.xy;
                float clipAlpha = saturate(min(min(dMin.x, dMin.y), min(dMax.x, dMax.y)));
                if (clipAlpha <= 0.001) discard;

                #if defined(UNITY_UI_CLIP_RECT)
                // Smooth top-and-bottom vertical feathering for scroll view (dynamic top fade based on scroll offset)
                float fadeBottom = saturate(dMin.y / 36.0);
                float fadeTop = saturate(dMax.y / max(_TopClipFade, 1.0));
                clipAlpha *= min(fadeBottom, fadeTop);
                #endif

                // 2. Analytical Signed Distance Field (SDF) Rounded Box / Pill / Circle
                float2 size = max(IN.rectSize, float2(2.0, 2.0));
                float2 halfSize = size * 0.5;
                float2 localPx = (IN.rectUV - 0.5) * size;

                float maxRadius = min(halfSize.x, halfSize.y);
                float radius = clamp(IN.shapeParam.x, 0.0, maxRadius);

                float packed = IN.shapeParam.y;
                int mode = (int)floor(packed * 0.1 + 0.01); // 0 = Frosted Blur Window, 1 = SDF Shape, 2 = Rounded Texture, 3 = Soft Outer Glow
                float borderStrength = saturate(packed - mode * 10.0);

                if (mode == 3)
                {
                    // MODE 3: Analytical SDF Soft Outer Glow (Album cover razor-thin halo)
                    float glowMargin = max(IN.shapeParam.z, 1.0);
                    float2 innerHalf = max(halfSize - glowMargin, float2(1.0, 1.0));
                    float dist = sdRoundedBox(localPx, innerHalf, radius);
                    float glowDist = max(dist, 0.0);
                    float normDist = saturate(glowDist / glowMargin);
                    float glow = 1.0 - normDist;
                    glow = glow * glow * (3.0 - 2.0 * glow);
                    glow = pow(glow, 1.4);
                    half3 glowRgb = IN.color.rgb;
                    half glowAlpha = glow * IN.color.a * clipAlpha;
                    if (glowAlpha <= 0.001) discard;
                    return half4(glowRgb, glowAlpha);
                }

                // Evaluate SDF in Canvas pixels and anti-alias across 1 screen pixel using fwidth()
                float dist = sdRoundedBox(localPx, halfSize - 0.5, radius);
                float aa = clamp(fwidth(dist), 0.35, 1.6);
                float shapeAlpha = smoothstep(aa * 0.55, -aa * 0.55, dist);
                if (shapeAlpha <= 0.001) discard;

                // 3. Crisp uniform 1.0px glass border (razor-sharp, zero blur)
                float borderWidth = 1.0;
                float borderDist = abs(dist + borderWidth * 0.5) - borderWidth * 0.5;
                float borderMask = smoothstep(aa * 0.55, -aa * 0.55, borderDist);
                float borderAlpha = borderMask * borderStrength;

                half3 finalRgb;
                half finalAlpha;

                if (mode == 0)
                {
                    // MODE 0: Frosted Dark Smoked Glass Panel (Main Window, Left Nav Pill, Bottom Player Bar)
                    // Analytical single-pass tile-memory (GMEM) smoked glass + hardware alpha blend
                    half4 frostTint = _GlassColor;
                    half2 centeredUV = half2(IN.rectUV - 0.5);
                    half radialGlow = saturate(1.0h - dot(centeredUV, centeredUV) * 1.5h);
                    half verticalGlow = lerp(0.012h, 0.024h, half(1.0 - IN.rectUV.y));
                    half3 vibrantBackdrop = half3(verticalGlow, verticalGlow * 1.03h, verticalGlow * 1.08h) + radialGlow * 0.008h;

                    // Combine smooth ambient backdrop with dark tinted window tint (high contrast for text)
                    finalRgb = lerp(frostTint.rgb, vibrantBackdrop * 0.35h + frostTint.rgb * 0.75h, (half)_BackdropWeight);
                    finalRgb *= IN.color.rgb;

                    // Crisp uniform 1px white border (15-20% opacity for main panels)
                    finalRgb = lerp(finalRgb, half3(1.0, 1.0, 1.0), borderAlpha);
                    finalAlpha = saturate(frostTint.a * IN.color.a + borderAlpha) * shapeAlpha * clipAlpha;
                }
                else if (mode == 2)
                {
                    // MODE 2: Analytical SDF Rounded Texture (Album Artwork)
                    half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv0) * IN.color;
                    finalRgb = lerp(texCol.rgb, half3(1.0, 1.0, 1.0), borderAlpha);
                    finalAlpha = saturate(texCol.a + borderAlpha) * shapeAlpha * clipAlpha;
                }
                else
                {
                    // MODE 1: Analytical SDF UI Shape (Cards, Capsules, Buttons, Sliders, Toggles)
                    // Flat uniform color - no gradient
                    finalRgb = IN.color.rgb;
                    // Crisp uniform 1px white border (8-12% opacity for inner cards/rows, brightened on hover)
                    finalRgb = lerp(finalRgb, half3(1.0, 1.0, 1.0), borderAlpha);
                    finalAlpha = saturate(IN.color.a + borderAlpha) * shapeAlpha * clipAlpha;
                }

                return half4(finalRgb, finalAlpha);
            }
            ENDHLSL
        }
    }
}
