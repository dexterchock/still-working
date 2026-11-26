Shader "SonicTopography/BrowserUnderlayPunchout"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BorderInset ("Glass Border Inset (px)", Float) = 0.0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        _ClipRect ("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
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
        Blend Zero SrcAlpha, One Zero
        ColorMask RGBA

        Pass
        {
            Name "BrowserUnderlayPunchout"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
                float4 uv3        : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 positionOS : TEXCOORD0;
                float2 rectUV     : TEXCOORD1;
                float2 rectSize   : TEXCOORD2;
                float4 shapeParam : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _ClipRect;
            float _BorderInset;

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

                bool hasCustomSize = (IN.uv2.x > 0.5 && IN.uv2.y > 0.5);
                OUT.rectUV = hasCustomSize ? IN.uv1 : IN.uv0;
                OUT.rectSize = hasCustomSize ? IN.uv2 : float2(932.0, 536.0);
                OUT.shapeParam = hasCustomSize ? IN.uv3 : float4(24.0, 10.0, 0.0, 0.0);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                #if defined(UNITY_UI_CLIP_RECT)
                if (IN.positionOS.x < _ClipRect.x || IN.positionOS.x > _ClipRect.z ||
                    IN.positionOS.y < _ClipRect.y || IN.positionOS.y > _ClipRect.w)
                {
                    discard;
                }
                #endif

                float2 size = max(IN.rectSize, float2(2.0, 2.0));
                float2 halfSize = size * 0.5;
                float2 localPx = (IN.rectUV - 0.5) * size;

                float maxRadius = min(halfSize.x, halfSize.y);
                float radius = clamp(IN.shapeParam.x, 0.0, maxRadius);

                float dist = sdRoundedBox(localPx, halfSize - 0.5, radius);
                float aa = clamp(fwidth(dist), 0.35, 1.6);
                float innerDist = dist + _BorderInset;
                half keep = (half)smoothstep(-aa * 0.55, aa * 0.55, innerDist);
                return half4(0.0h, 0.0h, 0.0h, keep);
            }
            ENDHLSL
        }
    }
}
