// Shared toon lighting for Kart/Toon and Kart/ToonOutline (built-in render pipeline surface shaders).
#ifndef KART_TOON_CORE
#define KART_TOON_CORE

sampler2D _MainTex;
sampler2D _EmissionMap;
fixed4 _Color;
half4 _EmissionColor;
half _Steps;
half _Softness;
half _ShadowDarkness;
half4 _RimColor;
half _RimPower;

struct Input
{
    float2 uv_MainTex;
    float3 viewDir;
};

struct SurfaceOutputToon
{
    fixed3 Albedo;
    fixed3 Normal;
    half3 Emission;
    fixed Alpha;
    half Rim;
};

// Banded diffuse: N.L and the shadow attenuation are quantised into a few soft-edged steps.
half4 LightingToon(SurfaceOutputToon s, half3 lightDir, half atten)
{
    half ndl = dot(s.Normal, lightDir) * 0.5 + 0.5;         // half-lambert keeps the dark side readable
    half lit = ndl * saturate(atten * 1.5);
#ifdef KART_TOON_OFF
    half band = lit;                                         // plain (smooth) shading for A/B comparison
#else
    half steps = max(_Steps, 1.0);
    half scaled = lit * steps;
    half base = floor(scaled);
    half band = (base + smoothstep(0.5 - _Softness, 0.5 + _Softness, scaled - base)) / steps;
#endif
#ifdef UNITY_PASS_FORWARDADD
    // Extra lights (track lamps, headlights) only add light inside their falloff: no floor.
    band *= saturate(atten * 2.0);
#else
    band = lerp(_ShadowDarkness, 1.0, band);
#endif
    half4 c;
    c.rgb = s.Albedo * _LightColor0.rgb * band;
    c.a = s.Alpha;
    return c;
}

void SurfToon(Input IN, inout SurfaceOutputToon o)
{
    fixed4 tex = tex2D(_MainTex, IN.uv_MainTex) * _Color;
    o.Albedo = tex.rgb;
    o.Alpha = tex.a;
    half rim = 1.0 - saturate(dot(normalize(IN.viewDir), o.Normal));
#ifdef KART_TOON_OFF
    half rimTerm = 0;
#else
    half rimTerm = pow(rim, _RimPower) * _RimColor.a;
#endif
    o.Emission = tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor.rgb + _RimColor.rgb * rimTerm * tex.rgb;
}

#endif
