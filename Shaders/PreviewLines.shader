// Screen-space capsules with analytic coverage; no geometry shader or MSAA required.
Shader "Hidden/MeshLab/PreviewLines"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _LineWidth ("Width in target pixels", Float) = 1
        _DepthOffset ("Depth offset", Float) = -2
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Offset [_DepthOffset], [_DepthOffset]
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float4 _ViewportSize;
            float _LineWidth;
            struct appdata { float4 vertex : POSITION; float3 end : TEXCOORD1; float2 corner : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float4 segment : TEXCOORD0; float4 color : COLOR; };

            float2 Pixel(float4 clip)
            {
                float2 uv = clip.xy / clip.w * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    uv.y = 1.0 - uv.y;
                #endif
                return uv * _ViewportSize.xy;
            }

            v2f vert(appdata v)
            {
                v2f o;
                float3 a = mul(UNITY_MATRIX_MV, v.vertex).xyz;
                float3 b = mul(UNITY_MATRIX_MV, float4(v.end, 1)).xyz;
                float nearZ = -_ProjectionParams.y;
                o.color = v.color * _Color;
                o.segment = 0;
                o.pos = float4(0, 0, 0, 1);
                // Clip in view space before division by w. An edge passing behind the
                // eye must not expand into a giant ribbon across the viewport.
                if (a.z > nearZ && b.z > nearZ) return o;
                if (a.z > nearZ) a = lerp(a, b, (nearZ - a.z) / (b.z - a.z));
                if (b.z > nearZ) b = lerp(b, a, (nearZ - b.z) / (a.z - b.z));
                float4 ca = mul(UNITY_MATRIX_P, float4(a, 1));
                float4 cb = mul(UNITY_MATRIX_P, float4(b, 1));
                float2 delta = (cb.xy / cb.w - ca.xy / ca.w) * _ViewportSize.xy;
                float len = length(delta);
                float2 along = len > 1e-5 ? delta / len : float2(1, 0);
                float2 across = float2(-along.y, along.x);
                float radius = max(_LineWidth, 1.0) * 0.5 + 0.5;
                o.pos = lerp(ca, cb, v.corner.x);
                float2 expansion = (along * (v.corner.x * 2 - 1) + across * v.corner.y) * radius;
                o.pos.xy += expansion * (2.0 / _ViewportSize.xy) * o.pos.w;
                o.segment = float4(Pixel(ca), Pixel(cb));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 ab = i.segment.zw - i.segment.xy;
                float t = saturate(dot(i.pos.xy - i.segment.xy, ab) / max(dot(ab, ab), 1e-8));
                float distance = length(i.pos.xy - (i.segment.xy + ab * t));
                float coverage = saturate(max(_LineWidth, 1.0) * 0.5 + 0.5 - distance) * saturate(_LineWidth);
                float alpha = i.color.a * coverage;
                return float4(i.color.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
}
