Shader "Hidden/MeshLab/RemeshReadback"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
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
            float _DecodeNormal, _ColorMap, _HDR, _NormalConvention;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_DecodeNormal > 0.5)
                {
                    // Preserve unscaled channels for CPU interpolation. Applying
                    // strength/reconstructing Z per texel would change bilinear
                    // normal-map sampling. Alpha records the shader recipe:
                    // 0 = Z after strength, .5 = Z before strength, 1 = stored RGB Z.
                    if (_NormalConvention > 0.5)
                    {
                        // Match Core/Packing.hlsl's branch order even when both
                        // ASTC and plain-RGB macros are defined on the target.
                        #if defined(UNITY_ASTC_NORMALMAP_ENCODING)
                        return float4(c.a, c.g, 1, 0.5);
                        #elif defined(UNITY_NO_DXT5nm)
                        return float4(c.rgb, 1);
                        #else
                        return float4(c.r * c.a, c.g, 1, 0.5);
                        #endif
                    }
                    else
                    {
                        // Match Standard's UnpackScaleNormal branch order.
                        #if defined(UNITY_NO_DXT5nm)
                        return float4(c.rgb, 1);
                        #elif defined(UNITY_ASTC_NORMALMAP_ENCODING)
                        return float4(c.a, c.g, 1, 0.5);
                        #else
                        return float4(c.r * c.a, c.g, 1, 0);
                        #endif
                    }
                }
                #ifdef UNITY_COLORSPACE_GAMMA
                if (_ColorMap > 0.5) c.rgb = GammaToLinearSpace(c.rgb);
                #endif
                // Store color in sRGB bytes; the CPU converts each tap before interpolation.
                if (_ColorMap > 0.5 && _HDR < 0.5) c.rgb = LinearToGammaSpace(c.rgb);
                return c;
            }
            ENDCG
        }
    }
}
