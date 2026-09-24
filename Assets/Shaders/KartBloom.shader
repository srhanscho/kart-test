// Bloom + light grading for BloomEffect (built-in pipeline).
// Pass 0 prefilter (threshold, soft knee), 1 downsample, 2 upsample (additive), 3 combine.
Shader "Hidden/KartBloom"
{
    Properties { _MainTex ("", 2D) = "black" {} }

    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _BloomTex;
    float4 _Filter;     // threshold, threshold - knee, 2 * knee, 0.25 / knee
    float _Intensity;
    float4 _Grade;      // vignette, saturation, contrast
    float4 _ViewRect;   // this camera's region in the source (xy offset, zw size) for the vignette

    half3 Box(float2 uv, float delta)
    {
        float4 o = _MainTex_TexelSize.xyxy * float2(-delta, delta).xxyy;
        half3 s = tex2D(_MainTex, uv + o.xy).rgb + tex2D(_MainTex, uv + o.zy).rgb +
                  tex2D(_MainTex, uv + o.xw).rgb + tex2D(_MainTex, uv + o.zw).rgb;
        return s * 0.25;
    }

    half3 Prefilter(half3 c)
    {
        half brightness = max(c.r, max(c.g, c.b));
        half soft = brightness - _Filter.y;
        soft = clamp(soft, 0, _Filter.z);
        soft = soft * soft * _Filter.w;
        half contribution = max(soft, brightness - _Filter.x) / max(brightness, 1e-5);
        return c * contribution;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass // 0 prefilter
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag(v2f_img i) : SV_Target { return half4(Prefilter(Box(i.uv, 1)), 1); }
            ENDCG
        }
        Pass // 1 downsample
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag(v2f_img i) : SV_Target { return half4(Box(i.uv, 1), 1); }
            ENDCG
        }
        Pass // 2 upsample, added onto the larger level
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag(v2f_img i) : SV_Target { return half4(Box(i.uv, 0.5), 1); }
            ENDCG
        }
        Pass // 3 combine + vignette + saturation/contrast
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            half4 frag(v2f_img i) : SV_Target
            {
                half4 c = tex2D(_MainTex, i.uv);
                c.rgb += tex2D(_BloomTex, i.uv).rgb * _Intensity;
                half luma = dot(c.rgb, half3(0.299, 0.587, 0.114));
                c.rgb = lerp(luma.xxx, c.rgb, _Grade.y);
                c.rgb = (c.rgb - 0.5) * _Grade.z + 0.5;
                float2 d = (i.uv - _ViewRect.xy) / max(_ViewRect.zw, 1e-4) - 0.5;
                c.rgb *= 1 - _Grade.x * saturate(dot(d, d) * 2.2);
                return max(c, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
