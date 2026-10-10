Shader "Hidden/MeshLab/PreviewPoints"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _PointSize ("Diameter in target pixels", Float) = 5
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest [_ZTest]
        Offset -2, -2
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color, _ViewportSize;
            float _PointSize;
            struct appdata { float4 vertex : POSITION; float2 corner : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 corner : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.pos.xy += v.corner * (_PointSize + 2) * 2 / _ViewportSize.xy * o.pos.w;
                o.corner = v.corner * (_PointSize + 2);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float alpha = saturate(_PointSize * .5 + .5 - length(i.corner)) * _Color.a;
                return float4(_Color.rgb, alpha);
            }
            ENDCG
        }
    }
}
