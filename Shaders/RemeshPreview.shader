// Remesh & Bake preview: headlight shading, optional base map, tangent-space
// normal map, vertex colour or flat colour; also draws wireframe line meshes.
// No LightMode tag, so Built-in, URP and PreviewRenderUtility all render it.
Shader "Hidden/MeshLab/RemeshPreview"
{
    Properties
    {
        _MainTex ("Base", 2D) = "white" {}
        _BumpMap ("Normal map", 2D) = "bump" {}
        _Color ("Color", Color) = (1,1,1,1)
        _UseTexture ("Use texture", Float) = 0
        _UseBumpMap ("Use normal map", Float) = 0
        _UseVertexColor ("Use vertex colour", Float) = 0
        _Lit ("Lit", Float) = 1
        _DepthOffset ("Depth offset", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" }
        Cull Off
        Offset [_DepthOffset], [_DepthOffset]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _BumpMap;
            float4 _Color;
            float _UseTexture, _UseBumpMap, _UseVertexColor, _Lit;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 tangent : TANGENT; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; float2 uv : TEXCOORD1; float4 color : COLOR; float3 view : TEXCOORD2; float4 tangent : TEXCOORD3; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.view = normalize(WorldSpaceViewDir(v.vertex));
                o.uv = v.uv;
                o.color = v.color;
                o.tangent = float4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = _Color;
                if (_UseTexture > 0.5) c *= tex2D(_MainTex, i.uv);
                if (_UseVertexColor > 0.5) c *= i.color;
                if (_Lit > 0.5)
                {
                    float3 n = normalize(i.normal);
                    // The bake encodes raw linear RGBA, not Unity's packed normal-map
                    // format, so decode with a plain *2-1 instead of UnpackNormal.
                    if (_UseBumpMap > 0.5)
                    {
                        float3 tn = tex2D(_BumpMap, i.uv).xyz * 2.0 - 1.0;
                        float3 t = i.tangent.xyz - n * dot(n, i.tangent.xyz);
                        t = normalize(t);
                        float3 b = cross(n, t) * i.tangent.w;
                        n = normalize(tn.x * t + tn.y * b + tn.z * n);
                    }
                    c.rgb *= 0.25 + 0.75 * abs(dot(n, i.view));
                }
                return c;
            }
            ENDCG
        }
    }
}
