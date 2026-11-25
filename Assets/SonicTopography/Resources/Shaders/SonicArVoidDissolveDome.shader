Shader "SonicTopography/ArVoidDissolveDome"
{
    Properties
    {
        _Color ("Void Color", Color) = (0, 0, 0, 1)
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Background+50"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "VoidDissolve"
            Tags { "LightMode" = "UniversalForward" }

            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Global transition parameters shared with SonicArDissolveCommon.hlsl
            // _SonicArDomeParams: x = normalizedSweep (0=all VR black, 1=all AR clear), y = active (0/1), z = feather, w = unused
            float4 _SonicArDomeParams;
            float4 _SonicArDomeForwardWS;
            float4 _SonicArDissolveRingColor;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dirWS      : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = posInputs.positionCS;
                output.dirWS = normalize(TransformObjectToWorldDir(input.positionOS.xyz));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                if (_SonicArDomeParams.y < 0.5)
                {
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);
                }

                float3 dir = normalize(input.dirWS);
                float3 fwd = normalize(_SonicArDomeForwardWS.xyz);

                // Sweep runs from 0 (in front of user/terrain) to 1 (directly behind user)
                float cosAngle = clamp(dot(dir, fwd), -1.0, 1.0);
                float angularCoord = (1.0 - cosAngle) * 0.5; // 0 front -> 1 back

                float sweep = _SonicArDomeParams.x;
                float feather = max(0.04, _SonicArDomeParams.z);

                // Behind wave (angularCoord < sweep): alpha 0 (passthrough visible). Ahead: alpha 1 (VR black void).
                float blackAlpha = smoothstep(sweep - feather * 0.65, sweep + feather * 0.45, angularCoord);

                float ringDelta = (angularCoord - sweep) / max(0.02, feather * 0.75);
                float ringBand = exp(-(ringDelta * ringDelta));
                float3 rgb = _SonicArDissolveRingColor.rgb * (ringBand * 1.9);
                float alpha = saturate(blackAlpha + ringBand * 0.85);

                return half4(rgb, (half)alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
