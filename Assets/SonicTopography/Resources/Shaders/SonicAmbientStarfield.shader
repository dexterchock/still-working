Shader "SonicTopography/AmbientStarfield"
{
    Properties
    {
        _StarTime ("Star Time", Float) = 0
        _Speed ("Drift Speed", Float) = 1.0
        _Bass ("Bass", Float) = 0
        _Beat ("Beat", Float) = 0
        _TintColor ("Tint Color", Color) = (0.88, 0.94, 1.0, 1.0)
        _OverallAlpha ("Overall Alpha", Float) = 1.0
        _StarScale ("Star Scale", Float) = 1.0
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-60"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "AmbientStars"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SonicArDissolveCommon.hlsl"

            float _StarTime;
            float _Speed;
            float _Bass;
            float _Beat;
            float4 _TintColor;
            float _OverallAlpha;
            float _StarScale;

            struct Attributes
            {
                float4 positionOS : POSITION;  // Base position (bx, by, bz)
                float2 uv : TEXCOORD0;         // (phaseX, phaseY)
                float4 uv2 : TEXCOORD1;        // (quadOffset.x, quadOffset.y, aRand, aAmp)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;         // quad local coordinates [-1, 1]
                float4 color : COLOR;          // rgb = star color, a = alpha
                float3 worldPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float t = _StarTime * _Speed;
                float3 pos = input.positionOS.xyz;
                float aRand = input.uv2.z;
                float aAmp = input.uv2.w;
                float2 aPhase = input.uv.xy;

                // 1. Slow cosmic horizontal orbit around world Y axis (in XZ plane)
                float orbit = t * (0.012 + aRand * 0.016);
                float cs = cos(orbit), sn = sin(orbit);
                pos.xz = float2(cs * pos.x - sn * pos.z, sn * pos.x + cs * pos.z);

                // 2. Harmonic breathing
                float breathe = 1.0 + sin(t * 0.28 + aPhase.x) * 0.045;
                pos.xz *= breathe;

                // 3. 3-axis harmonic drifting (from Mineradio floatGroup)
                pos.x += sin(t * (0.14 + aRand * 0.04) + aPhase.x) * aAmp * 0.45;
                pos.y += cos(t * (0.12 + aRand * 0.05) + aPhase.y) * aAmp * 0.38;
                pos.z += sin(t * (0.09 + aRand * 0.03) + aPhase.x * 1.5) * aAmp * 0.75 + _Bass * 0.18 * sin(aRand * 12.0);

                // Transform to world space
                float3 worldCenter = TransformObjectToWorld(pos);

                // 4. Twinkle and distance-based atmospheric alpha
                float twinkle = 0.50 + 0.50 * sin(t * (0.35 + aRand * 0.28) + aPhase.y);
                float distToCam = length(worldCenter - _WorldSpaceCameraPos);
                float alpha = clamp(0.18 + (38.0 - distToCam) * 0.014, 0.06, 0.55) * twinkle;

                // 5. Star color (diamond white with subtle celestial tint)
                float3 diamondWhite = float3(0.92, 0.95, 1.0);
                float3 tinted = lerp(diamondWhite, _TintColor.rgb, 0.25);
                float3 starCol = lerp(diamondWhite, tinted, aRand);

                // Star scale: clearly visible, sharp starlight dust motes
                float sz = clamp(0.013 + (aRand * 0.016), 0.009, 0.028) * (1.0 + _Bass * 0.16 + _Beat * 0.12) * _StarScale;

                // 6. Camera-aligned billboard expansion
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float3 worldPos = worldCenter + (camRight * input.uv2.x + camUp * input.uv2.y) * sz;

                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv2.xy;
                output.color = float4(starCol, alpha * _OverallAlpha);
                output.worldPos = worldPos;

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float r2 = dot(input.uv, input.uv);
                if (r2 > 1.0) return float4(0.0, 0.0, 0.0, 0.0);

                // Pinpoint starlight core + luminous halo
                float core = exp(-r2 * 10.5);
                float bloom = exp(-r2 * 3.0) * 0.44;

                float intensity = core * 1.6 + bloom * 0.44;
                float alpha = intensity * input.color.a;
                float3 finalRgb = input.color.rgb * alpha;

                float3 ringGlow;
                float keep = EvaluateSonicArDissolve(input.worldPos, ringGlow);
                finalRgb = finalRgb * keep + ringGlow * alpha;
                alpha *= keep;

                return float4(finalRgb, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
