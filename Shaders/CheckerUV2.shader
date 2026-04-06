Shader "Hidden/LightmapUvTool/CheckerUV2"
{
    Properties
    {
        _MainTex ("Checker", 2D) = "white" {}
        _CellLineWidth ("Cell Line Width", Range(0.001, 0.05)) = 0.015
        _CellLineAlpha ("Cell Line Alpha", Range(0, 1)) = 0.7
        _BorderWidth ("UV Border Width", Range(0.001, 0.02)) = 0.005
        _UVChannel ("UV Channel", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _CellLineWidth;
            float _CellLineAlpha;
            float _BorderWidth;
            float _UVChannel;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2;
                float2 uv3 : TEXCOORD3;
                float2 uv4 : TEXCOORD4;
                float2 uv5 : TEXCOORD5;
                float2 uv6 : TEXCOORD6;
                float2 uv7 : TEXCOORD7;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                int ch = (int)_UVChannel;
                if      (ch == 0) o.uv = v.uv0;
                else if (ch == 1) o.uv = v.uv1;
                else if (ch == 2) o.uv = v.uv2;
                else if (ch == 3) o.uv = v.uv3;
                else if (ch == 4) o.uv = v.uv4;
                else if (ch == 5) o.uv = v.uv5;
                else if (ch == 6) o.uv = v.uv6;
                else              o.uv = v.uv7;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // ── Base: colored cell texture with labels ──
                fixed4 base = tex2D(_MainTex, uv);

                // ── Cell grid lines (8x8) ──
                float2 cellPos = frac(uv * 8.0);
                float cellLineX = step(cellPos.x, _CellLineWidth) + step(1.0 - _CellLineWidth, cellPos.x);
                float cellLineY = step(cellPos.y, _CellLineWidth) + step(1.0 - _CellLineWidth, cellPos.y);
                float cellLine = saturate(cellLineX + cellLineY);
                base.rgb = lerp(base.rgb, float3(0.05, 0.05, 0.05), cellLine * _CellLineAlpha);

                // ── UV 0-1 boundary (red lines) ──
                float brdX = step(uv.x, _BorderWidth) + step(1.0 - _BorderWidth, uv.x);
                float brdY = step(uv.y, _BorderWidth) + step(1.0 - _BorderWidth, uv.y);
                float border = saturate(brdX + brdY);
                base.rgb = lerp(base.rgb, float3(1.0, 0.15, 0.1), border * 0.85);

                return base;
            }
            ENDCG
        }
    }
    FallBack "Unlit/Texture"
}
