Shader "SonicTopography/SimpleInstanced"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent-10"
        }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SonicArDissolveCommon.hlsl"

            float4 _BaseColor;
            float4x4 _PlatterMatrix;
            float4 _uItemPosActive[200];
            float4 _uItemScale[200];

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 itemIndex  : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 worldPos   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                int idx = clamp((int)(input.itemIndex.x + 0.5), 0, 199);
                float4 posActive = _uItemPosActive[idx];
                if (posActive.w < 0.5)
                {
                    output.positionCS = float4(0.0, 0.0, 0.0, 0.0);
                    output.worldPos = float3(0.0, 0.0, 0.0);
                    return output;
                }

                float3 scaleXYZ = _uItemScale[idx].xyz;
                float3 platterPos = posActive.xyz + input.positionOS.xyz * scaleXYZ;
                VertexPositionInputs posInputs = GetVertexPositionInputs(platterPos);
                output.positionCS = posInputs.positionCS;
                output.worldPos = posInputs.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 ringGlow;
                float keep = EvaluateSonicArDissolve(input.worldPos, ringGlow);
                return half4(_BaseColor.rgb * keep + ringGlow, _BaseColor.a * keep);
            }
            ENDHLSL
        }
    }
}
