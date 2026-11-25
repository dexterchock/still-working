#ifndef SONIC_AR_DISSOLVE_COMMON_INCLUDED
#define SONIC_AR_DISSOLVE_COMMON_INCLUDED

// Shared VR <-> AR radial Sonic Ripple Dissolve globals set by SonicMixedRealityManager
// _SonicArDissolveParams: x = sweepRadiusMeters, y = active (0 or 1), z = featherMeters, w = reversed (0=VR->AR, 1=AR->VR)
float4 _SonicArDissolveOriginWS;
float4 _SonicArDissolveParams;
float4 _SonicArDissolveRingColor;

// Evaluates radial dissolve state at worldPos.
// Returns alpha factor (1 = keep VR object, 0 = dissolved to reveal passthrough) and outputs additive ring glow.
float EvaluateSonicArDissolve(float3 worldPos, out float3 ringGlow)
{
    ringGlow = float3(0.0, 0.0, 0.0);
    if (_SonicArDissolveParams.y < 0.5)
    {
        return 1.0;
    }

    float sweepR = _SonicArDissolveParams.x;
    float feather = max(0.05, _SonicArDissolveParams.z);
    float dist = length(worldPos - _SonicArDissolveOriginWS.xyz);

    // Behind the expanding front (dist < sweepR): dissolved (0). Ahead of front (dist > sweepR): visible (1).
    float keep = smoothstep(sweepR - feather * 0.6, sweepR + feather * 0.4, dist);

    float ringDelta = (dist - sweepR) / max(0.15, feather * 1.15);
    float ringBand = exp(-(ringDelta * ringDelta));
    ringGlow = _SonicArDissolveRingColor.rgb * (ringBand * 2.2 * keep);

    return keep;
}

void ApplySonicArDissolveOpaque(float3 worldPos, inout float3 rgb)
{
    if (_SonicArDissolveParams.y < 0.5)
    {
        return;
    }

    float3 ringGlow;
    float keep = EvaluateSonicArDissolve(worldPos, ringGlow);
    clip(keep - 0.02);
    rgb += ringGlow;
}

#endif
