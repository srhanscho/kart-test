// Toon lit shader with an inverted-hull outline (karts, drivers, items, props).
// The outline pushes vertices along a smoothed normal stored in the mesh tangents
// (baked by the KenneyOutlineNormals asset postprocessor), in clip space, so the width
// stays constant on screen and hard-edged meshes do not crack.
Shader "Kart/ToonOutline"
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
        _OutlineColor ("Outline Color", Color) = (0.04,0.04,0.08,1)
        _OutlineWidth ("Outline Width (pixels)", Range(0,6)) = 2.2
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

        Pass
        {
            Name "OUTLINE"
            Tags { "LightMode"="Always" }
            Cull Front
            ZWrite On
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ KART_TOON_OFF
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            fixed4 _OutlineColor;
            half _OutlineWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_FOG_COORDS(0)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Smoothed normal from the tangents when baked (w == 1), otherwise the vertex normal.
                float3 n = v.tangent.w > 0.5 && dot(v.tangent.xyz, v.tangent.xyz) > 0.01 ? v.tangent.xyz : v.normal;
                float3 viewNormal = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, n));
                float2 clipNormal = normalize(TransformViewToProjection(viewNormal.xy));
            #ifdef KART_TOON_OFF
                float width = 0;
            #else
                float width = _OutlineWidth;
            #endif
                o.pos.xy += clipNormal * width * o.pos.w * 2.0 / _ScreenParams.xy;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = _OutlineColor;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
