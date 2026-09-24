// Toon lit shader without outline (track pieces, ground, large surfaces).
Shader "Kart/Toon"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _Steps ("Light Bands", Range(1,4)) = 2
        _Softness ("Band Softness", Range(0.01,0.5)) = 0.12
        _ShadowDarkness ("Shadow Brightness", Range(0,1)) = 0.45
        _RimColor ("Rim (rgb) Strength (a)", Color) = (0.6,0.7,1,0.25)
        _RimPower ("Rim Power", Range(0.5,8)) = 3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface SurfToon Toon fullforwardshadows
        #pragma multi_compile _ KART_TOON_OFF
        #pragma target 3.0
        #include "ToonCore.cginc"
        ENDCG
    }
    FallBack "Diffuse"
}
