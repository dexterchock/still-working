Shader "SonicTopography/StarRiver"
{
    Properties
    {
        _StarTime ("Star Time", Float) = 0
        _Speed ("Flow Speed", Float) = 1.0
        _Bass ("Bass", Float) = 0
        _Mid ("Mid", Float) = 0
        _Treble ("Treble", Float) = 0
        _Beat ("Beat", Float) = 0
        _TintColor ("Tint Color", Color) = (0.85, 0.92, 1.0, 1.0)
        _AuroraCool ("Aurora Cool", Color) = (0.38, 0.74, 1.0, 1.0)
        _AuroraWarm ("Aurora Warm", Color) = (0.68, 0.48, 1.0, 1.0)
        _OverallAlpha ("Overall Alpha", Float) = 1.0
        _StarScale ("Star Scale", Float) = 1.0
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent-50"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "StarRiverWaves"
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

            #ifndef PI
            #define PI 3.14159265359
            #endif

            float _StarTime;
            float _Speed;
            float _Bass;
            float _Mid;
            float _Treble;
            float _Beat;
            float4 _TintColor;
            float4 _AuroraCool;
            float4 _AuroraWarm;
            float _OverallAlpha;
            float _StarScale;

            // ----------------------------------------------------
            // 3D Simplex Noise (Ashima Arts / Ian McEwan)
            // Ported directly from Mineradio 00-pointer-cover-particles.js
            // ----------------------------------------------------
            float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 mod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float4 perm(float4 x) { return mod289(((x * 34.0) + 1.0) * x); }

            float snoise(float3 v)
            {
                const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
                const float4 D = float4(0.0, 0.5, 1.0, 2.0);

                float3 i = floor(v + dot(v, C.yyy));
                float3 x0 = v - i + dot(i, C.xxx);

                float3 g = step(x0.yzx, x0.xyz);
                float3 l = 1.0 - g;
                float3 i1 = min(g.xyz, l.zxy);
                float3 i2 = max(g.xyz, l.zxy);

                float3 x1 = x0 - i1 + C.xxx;
                float3 x2 = x0 - i2 + C.yyy;
                float3 x3 = x0 - D.yyy;

                i = mod289(i);
                float4 p = perm(perm(perm(
                            i.z + float4(0.0, i1.z, i2.z, 1.0))
                        + i.y + float4(0.0, i1.y, i2.y, 1.0))
                        + i.x + float4(0.0, i1.x, i2.x, 1.0));

                float n_ = 0.142857142857;
                float3 ns = n_ * D.wyz - D.xzx;

                float4 j = p - 49.0 * floor(p * ns.z * ns.z);

                float4 x_ = floor(j * ns.z);
                float4 y_ = floor(j - 7.0 * x_);

                float4 x = x_ * ns.x + ns.yyyy;
                float4 y = y_ * ns.x + ns.yyyy;
                float4 h = 1.0 - abs(x) - abs(y);

                float4 b0 = float4(x.xy, y.xy);
                float4 b1 = float4(x.zw, y.zw);

                float4 s0 = floor(b0) * 2.0 + 1.0;
                float4 s1 = floor(b1) * 2.0 + 1.0;
                float4 sh = -step(h, float4(0.0, 0.0, 0.0, 0.0));

                float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
                float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

                float3 p0 = float3(a0.xy, h.x);
                float3 p1 = float3(a0.zw, h.y);
                float3 p2 = float3(a1.xy, h.z);
                float3 p3 = float3(a1.zw, h.w);

                float4 norm = rsqrt(float4(dot(p0, p0), dot(p1, p1), dot(p2, p2), dot(p3, p3)));
                p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;

                float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
                m = m * m;
                return 42.0 * dot(m * m, float4(dot(p0, x0), dot(p1, x1), dot(p2, x2), dot(p3, x3)));
            }

            float hash11(float p)
            {
                return frac(sin(p * 127.1) * 43758.5453123);
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;         // (grid u, v in [0, 1])
                float4 uv2 : TEXCOORD1;        // (quadOffset.x, quadOffset.y, aRand, laneSeed)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;         // quad local coordinates [-1, 1]
                float4 color : COLOR;          // rgb = star color, a = alpha
                float bloom : TEXCOORD1;       // bloom halo strength
                float3 worldPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float t = _StarTime * _Speed;
                float bassGlow = smoothstep(0.07, 0.78, _Bass) * 0.34 + _Beat * 0.014;
                float midGlow = smoothstep(0.07, 0.62, _Mid) * 0.42;
                float highGlow = smoothstep(0.04, 0.46, _Treble) * 0.46;

                float2 aUv = input.uv;
                float aRand = input.uv2.z;
                float lane = aUv.y;

                float3 localPos = float3(0.0, 0.0, 0.0);
                float vAlpha = 0.0;
                float3 vColor = float3(1.0, 1.0, 1.0);
                float vBloomStrength = 0.4;
                float starSize = 0.06;

                if (lane < 0.80)
                {
                    // ----------------------------------------------------
                    // 1. Undulating 3D Ribbon Waves (80% of star river)
                    // ----------------------------------------------------
                    float laneWarp = snoise(float3(aUv.x * 0.42, lane * 1.7, t * 0.026)) * 0.11 + (hash11(aRand * 73.1) - 0.5) * 0.045;
                    float warpedLane = clamp(lane + laneWarp, 0.0, 0.80);
                    float bandCoord = warpedLane / 0.80 * 5.65 + snoise(float3(aUv.x * 0.82, lane * 2.25, t * 0.032)) * 0.62;
                    float band = floor(bandCoord);
                    float local = frac(bandCoord + hash11(band * 9.13 + aRand * 2.4) * 0.18);
                    float bandN = clamp((band + 0.5) / 5.65, 0.0, 1.0);
                    float seed = hash11(band * 19.17 + aRand * 31.0);
                    float flow = frac(aUv.x + t * (0.0034 + bandN * 0.0038 + seed * 0.0022) + seed * 0.53);
                    float arc = (flow - 0.5) * PI * (1.35 + bandN * 0.72 + seed * 0.24);
                    float armCurve = sin(arc + bandN * 2.2 + seed * 5.3);
                    float spiralRadius = 9.2 + bandN * 11.8 + seed * 6.0 + local * 2.9;
                    float x = cos(arc * 0.68 + bandN * 0.92 + seed * 1.3) * spiralRadius + (flow - 0.5) * (18.5 + bandN * 12.0);
                    float ribbonPhase = flow * PI * 2.0 * (0.55 + bandN * 0.24 + seed * 0.10) + t * (0.010 + bandN * 0.007) + seed * 5.7;
                    float broadWave = sin(ribbonPhase) * 1.8;
                    float fineWave = sin(ribbonPhase * (1.36 + seed * 0.62) - t * 0.044 + seed * 5.0) * 0.12;
                    // Vertically span between mid-space (above terrain platter) and upper sky
                    float yBase = (bandN - 0.5) * 26.0 + armCurve * (3.5 + bandN * 2.5) + (seed - 0.5) * 2.8 + snoise(float3(bandN * 2.0, flow * 0.62, seed)) * 1.5;
                    float ridgeCenter = 0.43 + (seed - 0.5) * 0.18;
                    float ridgeDiff = (local - ridgeCenter) / (0.25 + seed * 0.04);
                    float ridge = exp(-(ridgeDiff * ridgeDiff));
                    float softMask = smoothstep(0.010, 0.12, lane) * (1.0 - smoothstep(0.72, 0.81, lane));
                    float ribbonNoise = snoise(float3(flow * 1.18 + seed, bandN * 2.0, t * 0.018)) * 0.74;
                    float zLayer = lerp(-28.5, 18.5, bandN) + (seed - 0.5) * 8.0;

                    localPos.x = x + ribbonNoise * 1.40 + sin(t * 0.012 + seed * 8.0) * 0.22;
                    localPos.y = yBase + broadWave + fineWave + (local - 0.5) * (1.2 + ridge * 0.40);
                    localPos.z = zLayer + broadWave * 1.35 + ribbonNoise * 1.85;

                    float pulseLine = 0.5 + 0.5 * sin(ribbonPhase * (1.7 + seed * 0.9) - t * 0.32 + seed * 6.0);
                    float3 aurora = lerp(_AuroraCool.rgb, _AuroraWarm.rgb, bandN);
                    aurora = lerp(aurora, float3(0.96, 0.98, 0.92), bassGlow * 0.08);

                    vAlpha = (0.24 + ridge * 0.58 + pulseLine * highGlow * 0.06 + bassGlow * 0.04) * softMask;
                    vColor = lerp(_TintColor.rgb, aurora, 0.45 + ridge * 0.25) * (0.80 + ridge * 0.60 + pulseLine * highGlow * 0.06 + bassGlow * 0.04);
                    vBloomStrength = 0.25 + ridge * 0.40 + bassGlow * 0.18;
                    // Balanced size: clearly visible, sharp starlight motes
                    starSize = (0.016 + ridge * 0.024 + pulseLine * highGlow * 0.008) * (0.85 + _Bass * 0.18 + _Beat * 0.14);
                }
                else
                {
                    // ----------------------------------------------------
                    // 2. Deep Celestial Dust & Sparkles (20% of star river)
                    // ----------------------------------------------------
                    float q = (lane - 0.80) / 0.20;
                    float seed = hash11(aRand * 917.0 + floor(q * 130.0));
                    float depth = lerp(-32.0, 18.0, seed);
                    float drift = frac(aUv.x + t * (0.0014 + seed * 0.0048) + seed * 0.63);
                    float cluster = snoise(float3(seed * 2.0, q * 3.2, t * 0.007));
                    float x = (drift - 0.5) * (45.0 + seed * 22.0) + cluster * 3.4;
                    // Span from mid-level horizon up into deep sky
                    float y = (hash11(aRand * 331.0 + seed * 5.0) - 0.5) * 36.0 + sin(t * (0.018 + seed * 0.028) + seed * 7.0) * 1.2;
                    float z = depth + sin(t * (0.020 + seed * 0.032) + aRand * 8.0) * 1.05;
                    float twBase = 0.5 + 0.5 * sin(t * (0.24 + seed * 0.42) + aRand * 17.0);
                    float tw2 = twBase * twBase;
                    float twinkle = tw2 * tw2 * twBase;
                    float dust = smoothstep(0.22, 0.98, hash11(aRand * 661.0 + floor(q * 160.0)));

                    localPos = float3(x, y, z);
                    vAlpha = dust * (0.18 + twinkle * 0.46 + highGlow * 0.035 + bassGlow * 0.025) * (1.0 - q * 0.06);
                    vColor = lerp(_TintColor.rgb, float3(0.92, 0.97, 1.0), 0.55 + twinkle * 0.14) * (0.80 + twinkle * 0.50 + bassGlow * 0.030);
                    vBloomStrength = 0.22 + twinkle * 0.42;
                    starSize = (0.012 + twinkle * 0.018) * (0.85 + _Beat * 0.12);
                }

                // Transform particle center to world space
                float3 worldCenter = TransformObjectToWorld(localPos);

                // Billboard expansion in camera plane
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float finalSize = starSize * _StarScale;
                float3 worldPos = worldCenter + (camRight * input.uv2.x + camUp * input.uv2.y) * finalSize;

                output.positionCS = TransformWorldToHClip(worldPos);
                output.uv = input.uv2.xy;
                output.color = float4(vColor, vAlpha * _OverallAlpha);
                output.bloom = vBloomStrength;
                output.worldPos = worldPos;

                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float r2 = dot(input.uv, input.uv);
                if (r2 > 1.0) return float4(0.0, 0.0, 0.0, 0.0);

                // Dual-gaussian profile:
                // Core: crisp pinpoint starlight mote
                float core = exp(-r2 * 10.5);
                // Bloom: soft, luminous ambient halo
                float bloom = exp(-r2 * 2.8) * input.bloom;

                float intensity = core * 1.6 + bloom * 0.42;
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
