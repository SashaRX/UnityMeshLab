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

                // Cross + мягкое spot-пятно поверх перекрестья (референс: мягкий круг)
                float crossLen = _SpotRadius;
                float lineW = _SpotRadius * 0.08;
                float outlineMul = 1.35;

                float spotInnerR = _SpotRadius * 0.55;
                float spotOuterR = _SpotRadius * 1.25;

                float dx = abs(d.x);
                float dy = abs(d.y);

                float hArm = step(dy, lineW) * step(dx, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dy));
                float vArm = step(dx, lineW) * step(dy, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dx));
                float cross = saturate(max(hArm, vArm));

                float hArmOut = step(dy, lineW * outlineMul) * step(dx, crossLen + lineW) * (1.0 - smoothstep(lineW * 0.5, lineW * outlineMul, dy));
                float vArmOut = step(dx, lineW * outlineMul) * step(dy, crossLen + lineW) * (1.0 - smoothstep(lineW * 0.5, lineW * outlineMul, dx));
                float crossOut = saturate(max(hArmOut, vArmOut));
                float outline = saturate(crossOut - cross);

                // Soft spot: плотный центр + плавное затухание к краю
                float core = 1.0 - smoothstep(0.0, spotInnerR, dist);
                float feather = 1.0 - smoothstep(spotInnerR, spotOuterR, dist);
                float spot = saturate(max(core, feather * 0.45));

                float fill = saturate(max(cross, spot));
                float a = saturate(max(fill, outline * 0.8)) * _SpotColor.a;
                if (a <= 0.001) discard;

                fixed3 spotCol = fixed3(1.0, 1.0, 1.0);
                fixed3 rgb = lerp(fixed3(0, 0, 0), _SpotColor.rgb, cross);
                rgb = lerp(rgb, spotCol, saturate(spot * 0.6));
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
