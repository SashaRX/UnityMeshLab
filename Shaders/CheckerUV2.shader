Shader "Hidden/LightmapUvTool/CheckerUV2"
{
    Properties
    {
        _MainTex ("Checker", 2D) = "white" {}
        _FineGrid ("Fine Grid Size", Float) = 32
        _FineAlpha ("Fine Grid Alpha", Range(0, 0.5)) = 0.15
        _CellLineWidth ("Cell Line Width", Range(0.001, 0.05)) = 0.015
        _CellLineAlpha ("Cell Line Alpha", Range(0, 1)) = 0.7
        _BorderWidth ("UV Border Width", Range(0.001, 0.02)) = 0.005
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
            float _FineGrid;
            float _FineAlpha;
            float _CellLineWidth;
            float _CellLineAlpha;
            float _BorderWidth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv2 : TEXCOORD2;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv2;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // ── Base: colored cell texture with labels ──
                fixed4 base = tex2D(_MainTex, uv);

                // ── Fine checkerboard overlay ──
                float2 fineCell = floor(uv * _FineGrid);
                float fineParity = fmod(fineCell.x + fineCell.y, 2.0);
                float fineVal = lerp(0.2, 0.8, fineParity);
                base.rgb = lerp(base.rgb, float3(fineVal, fineVal, fineVal), _FineAlpha);

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

                // ── Simple directional lighting for depth readability ──
                float3 lightDir = normalize(float3(0.3, 1.0, 0.5));
                float ndl = saturate(dot(normalize(i.worldNormal), lightDir));
                float lighting = lerp(0.45, 1.0, ndl);
                base.rgb *= lighting;

                return base;
            }
            ENDCG
        }
    }
    FallBack "Unlit/Texture"
}
