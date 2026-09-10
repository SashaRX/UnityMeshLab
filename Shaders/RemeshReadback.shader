Shader "Hidden/MeshLab/RemeshReadback"
{
    Properties { _MainTex ("Source", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _DecodeNormal, _ColorMap, _HDR;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_DecodeNormal > 0.5) return float4(UnpackNormal(c) * 0.5 + 0.5, 1);
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
