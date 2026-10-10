Shader "Hidden/MeshLab/ViewportOverlay"
{
    Properties
    {
        _MainTex ("Surface", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _UseVertexColor ("Vertex colours", Float) = 0
        _UseTexture ("Texture", Float) = 0
        _Lit ("Headlight", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+5" "RenderType" = "Transparent" }
        Cull Off
        ZWrite Off
        ZTest [_ZTest]
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
            float _UseVertexColor, _UseTexture, _Lit;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 normal : TEXCOORD1; float3 view : TEXCOORD2; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = _Color;
                if (_UseVertexColor > .5) o.color *= v.color;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = WorldSpaceViewDir(v.vertex);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 color = i.color;
                if (_UseTexture > .5) color *= tex2D(_MainTex, i.uv);
                if (_Lit > .5) color.rgb *= .35 + .65 * abs(dot(normalize(i.normal), normalize(i.view)));
                return color;
            }
            ENDCG
        }
    }
}
