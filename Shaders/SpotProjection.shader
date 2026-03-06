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

                // Crosshair style: thin lines along U and V axes + small center dot
                float lineW = _SpotRadius * 0.08;
                float armLen = _SpotRadius;
                float dotR = _SpotRadius * 0.18;

                float dx = abs(d.x);
                float dy = abs(d.y);

                // Horizontal arm: thin in Y, within armLen in X
                float hArm = step(dy, lineW) * step(dx, armLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dy));
                // Vertical arm: thin in X, within armLen in Y
                float vArm = step(dx, lineW) * step(dy, armLen) * (1.0 - smoothstep(lineW * 0.5, lineW, dx));
                // Center dot
                float dot = 1.0 - smoothstep(dotR * 0.5, dotR, dist);

                float a = saturate(max(max(hArm, vArm), dot)) * _SpotColor.a;
                if (a <= 0.001) discard;
                return fixed4(_SpotColor.rgb, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
