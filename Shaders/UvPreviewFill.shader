Shader "Hidden/MeshLab/UvPreviewFill"
{
    Properties
    {
        _Color ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Float) = 1
        _SrcBlend ("Source blend", Int) = 5
        _DstBlend ("Destination blend", Int) = 10
        _Cull ("Cull", Int) = 0
        _ZWrite ("Depth write", Int) = 0
    }
    SubShader
    {
        Cull [_Cull] ZWrite [_ZWrite] ZTest Always
        Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float _Opacity;
            struct appdata { float4 vertex : POSITION; float4 color : COLOR; };
            struct v2f { float4 vertex : SV_POSITION; float4 color : COLOR; };
            v2f vert(appdata v)
            {
                v2f o; o.vertex = UnityObjectToClipPos(v.vertex); o.color = v.color * _Color; return o;
            }
            float4 frag(v2f i) : SV_Target { return float4(i.color.rgb, saturate(i.color.a * _Opacity)); }
            ENDCG
        }
    }
}
