Shader "Hidden/MeshLab/RemeshNormalPreviewPack"
{
    Properties
    {
        _MainTex ("Canonical normal", 2D) = "bump" {}
        _NormalConvention ("Normal convention", Float) = 0
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _NormalConvention;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_NormalConvention > 0.5)
                {
                    // Core/Packing.hlsl: URP gives ASTC precedence over RGB.
                    #if defined(UNITY_ASTC_NORMALMAP_ENCODING)
                    return float4(1, c.g, c.b, c.r);
                    #else
                    // Both plain RGB and RG/AG (R*A with A=1) accept canonical RGB.
                    return float4(c.rgb, 1);
                    #endif
                }
                else
                {
                    // Standard's UnpackScaleNormal gives plain RGB precedence.
                    #if defined(UNITY_NO_DXT5nm)
                    return float4(c.rgb, 1);
                    #elif defined(UNITY_ASTC_NORMALMAP_ENCODING)
                    return float4(1, c.g, c.b, c.r);
                    #else
                    return float4(c.rgb, 1);
                    #endif
                }
            }
            ENDCG
        }
    }
}
