Shader "SonicTopography/SplashCube"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.2, 0.9, 1.0, 1.0)
        _EmissionColor ("Emission Color", Color) = (0.2, 0.9, 1.0, 1.0)
        _ColorFill ("Color Fill", Range(0, 1)) = 1.0
        _GlowIntensity ("Glow Intensity", Float) = 1.6
        _Alpha ("Alpha", Float) = 1.0
        _FresnelPower ("Fresnel Power", Float) = 2.5
        _FresnelScale ("Fresnel Scale", Float) = 0.6
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent+510"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Back
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EmissionColor;
                float _FresnelPower;
                float _FresnelScale;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _ColorFill)
                UNITY_DEFINE_INSTANCED_PROP(float, _Alpha)
                UNITY_DEFINE_INSTANCED_PROP(float, _GlowIntensity)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldNormal: TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.worldPos = vertexInput.positionWS;
                output.worldNormal = normalInput.normalWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 viewDir = normalize(GetCameraPositionWS() - input.worldPos);
                float NdotV = saturate(dot(normalize(input.worldNormal), viewDir));
                float fresnel = pow(1.0 - NdotV, _FresnelPower);

                float fill = saturate(UNITY_ACCESS_INSTANCED_PROP(Props, _ColorFill));
                float activeGlow = UNITY_ACCESS_INSTANCED_PROP(Props, _GlowIntensity);
                float activeAlpha = UNITY_ACCESS_INSTANCED_PROP(Props, _Alpha);

                // Sleek silver-white monochrome B&W baseline for un-filled cubes
                float lum = dot(_BaseColor.rgb, float3(0.299, 0.587, 0.114));
                half3 bwBase = half3(lum, lum, lum) * 0.85 + half3(0.15, 0.15, 0.15);
                half3 finalBase = lerp(bwBase, _BaseColor.rgb, fill);

                float emLum = dot(_EmissionColor.rgb, float3(0.299, 0.587, 0.114));
                half3 bwEmission = half3(emLum, emLum, emLum) * 0.85 + half3(0.15, 0.15, 0.15);
                half3 finalEmission = lerp(bwEmission, _EmissionColor.rgb, fill);

                half3 col = finalBase * activeGlow + finalEmission * (fresnel * _FresnelScale);
                half alpha = _BaseColor.a * activeAlpha;

                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
