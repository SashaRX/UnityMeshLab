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

                // Unified style: crosshair + spot ring (ring чуть больше перекрестья)
                float crossLen = _SpotRadius;
                float lineW = _SpotRadius * 0.08;
                float ringR = _SpotRadius * 1.18;
                float ringW = _SpotRadius * 0.09;
                float outlineMul = 1.35;

                float dx = abs(d.x);
                float dy = abs(d.y);

                // Cross
                float hArm = step(dy, lineW) * step(dx, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dy));
                float vArm = step(dx, lineW) * step(dy, crossLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dx));
                float cross = saturate(max(hArm, vArm));

                // Ring
                float ringOuter = 1.0 - smoothstep(ringR, ringR + ringW, dist);
                float ringInner = 1.0 - smoothstep(max(0.0001, ringR - ringW), ringR, dist);
                float ring = saturate(ringOuter * (1.0 - ringInner));

                float fill = saturate(max(cross, ring));

                // Dark outline to match UV canvas style
                float hArmOut = step(dy, lineW * outlineMul) * step(dx, crossLen + lineW) * (1.0 - smoothstep(lineW * 0.5, lineW * outlineMul, dy));
                float vArmOut = step(dx, lineW * outlineMul) * step(dy, crossLen + lineW) * (1.0 - smoothstep(lineW * 0.5, lineW * outlineMul, dx));
                float crossOut = saturate(max(hArmOut, vArmOut));

                float ringOuterOut = 1.0 - smoothstep(ringR, ringR + ringW * outlineMul, dist);
                float ringInnerOut = 1.0 - smoothstep(max(0.0001, ringR - ringW * outlineMul), ringR, dist);
                float ringOut = saturate(ringOuterOut * (1.0 - ringInnerOut));

                float outline = saturate(max(crossOut, ringOut) - fill);

                float a = saturate(max(fill, outline * 0.8)) * _SpotColor.a;
                if (a <= 0.001) discard;

                fixed3 rgb = lerp(fixed3(0, 0, 0), _SpotColor.rgb, fill);
                return fixed4(rgb, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
