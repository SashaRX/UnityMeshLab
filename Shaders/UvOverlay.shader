// Mesh Lab 3D canvas: the UV viewer's layer (shell fills, borders, checker or
// lightmap background) rendered into a UV-space texture and laid over the model
// through the preview UV channel. Transparent, depth-offset so it wins against the
// surface it covers; with a white texture it tints (shell highlights).
Shader "Hidden/MeshLab/UvOverlay"
{
    Properties
    {
        _MainTex ("Overlay", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _UVChannel ("UV Channel", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        ZWrite Off
        Cull Off
        Offset -1, -1
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color;
            float _UVChannel;
            struct appdata { float4 vertex : POSITION; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2; float2 uv3 : TEXCOORD3; float2 uv4 : TEXCOORD4; float2 uv5 : TEXCOORD5; float2 uv6 : TEXCOORD6; float2 uv7 : TEXCOORD7; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                int ch = (int)_UVChannel;
                o.uv = ch == 1 ? v.uv1 : ch == 2 ? v.uv2 : ch == 3 ? v.uv3 : ch == 4 ? v.uv4 : ch == 5 ? v.uv5 : ch == 6 ? v.uv6 : ch == 7 ? v.uv7 : v.uv0;
                o.color = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv) * _Color;
                return t;
            }
            ENDCG
        }
    }
}
