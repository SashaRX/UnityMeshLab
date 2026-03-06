Shader "Hidden/LightmapUvTool/SpotProjection"
{
    Properties
    {
        _SpotUv ("Spot UV", Vector) = (0,0,0,0)
        _SpotRadius ("Spot Radius", Range(0.001, 0.25)) = 0.008
        _SpotColor ("Spot Color", Color) = (1,0.75,0.2,0.95)
        _UseUv2 ("Use UV2", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" }
        ZWrite Off
        ZTest LEqual
        Cull Back
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _SpotUv;
            float _SpotRadius;
            fixed4 _SpotColor;
            float _UseUv2;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv2 : TEXCOORD1;
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
                o.uv = lerp(v.uv0, v.uv2, saturate(_UseUv2));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d = i.uv - _SpotUv.xy;
                float dist = length(d);

                // Единый стиль с UV-канвой: мягкий spot больше перекрестья
                float crossLen = _SpotRadius;
                float lineW = _SpotRadius * 0.08;
                float spotInnerR = _SpotRadius * 0.55;
                float spotOuterR = _SpotRadius * 1.35;

                float dx = abs(d.x);
                float dy = abs(d.y);

                float hArm = step(dy, lineW) * step(dx, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dy));
                float vArm = step(dx, lineW) * step(dy, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dx));
                float cross = saturate(max(hArm, vArm));

                // Soft spot: плотный центр + мягкое затухание к внешнему радиусу
                float core = 1.0 - smoothstep(0.0, spotInnerR, dist);
                float feather = 1.0 - smoothstep(spotInnerR, spotOuterR, dist);
                float spot = saturate(max(core, feather * 0.7));

                float spotA = spot * 0.42;
                float crossA = cross;
                float a = saturate(max(spotA, crossA)) * _SpotColor.a;
                if (a <= 0.001) discard;

                fixed3 crossCol = _SpotColor.rgb;
                fixed3 spotCol = fixed3(1.0, 1.0, 1.0);

                // Перекрестье должно оставаться читаемым поверх пятна
                fixed3 rgb = lerp(spotCol, crossCol, cross);
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
