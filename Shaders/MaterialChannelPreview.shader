Shader "Hidden/MeshLab/MaterialChannelPreview"
{
    Properties
    {
        _MainTex ("Map", 2D) = "white" {}
        _UvScaleOffset ("UV scale / offset", Vector) = (1,1,0,0)
        _Color ("Albedo tint", Color) = (1,1,1,1)
        _ViewerMode ("Channel", Float) = 0
        _UseTexture ("Map assigned", Float) = 0
        _UseVertexColor ("Vertex colours", Float) = 0
        _Channel ("Component", Float) = 0
        _Scalar ("Multiplier", Float) = 1
        _Bias ("Offset", Float) = 0
        _NormalScale ("Normal strength", Float) = 1
        _NormalConvention ("Normal encoding", Float) = 0
        _Opacity ("Opacity", Float) = 1
        _ZWrite ("Depth write", Float) = 1
        _ZTest ("Depth test", Float) = 4
        _SrcBlend ("Source blend", Float) = 1
        _DstBlend ("Destination blend", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" }
        Cull Off
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Blend [_SrcBlend] [_DstBlend]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityStandardUtils.cginc"
            sampler2D _MainTex;
            float4 _UvScaleOffset, _Color;
            float _ViewerMode, _UseTexture, _UseVertexColor, _Channel, _Scalar, _Bias;
            float _NormalScale, _NormalConvention, _Opacity;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _UvScaleOffset.xy + _UvScaleOffset.zw;
                o.color = v.color;
                return o;
            }
            float3 Normal(float4 packed)
            {
                if (_NormalConvention > 1.5) {
                    float3 n = packed.rgb * 2 - 1;
                    n.xy *= _NormalScale;
                    return normalize(n);
                }
                if (_NormalConvention < .5) return normalize(UnpackScaleNormal(packed, _NormalScale));
                // URP's Packing.hlsl gives ASTC precedence over the plain RGB branch
                // and reconstructs Z before strength; Standard does it after strength.
                #if defined(UNITY_ASTC_NORMALMAP_ENCODING)
                    float2 xy = packed.ag * 2 - 1;
                #elif defined(UNITY_NO_DXT5nm)
                    float3 rgb = packed.rgb * 2 - 1;
                    rgb.xy *= _NormalScale;
                    return normalize(rgb);
                #else
                    float2 xy = float2(packed.r * packed.a, packed.g) * 2 - 1;
                #endif
                #if !defined(UNITY_NO_DXT5nm) || defined(UNITY_ASTC_NORMALMAP_ENCODING)
                    return normalize(float3(xy * _NormalScale, sqrt(1 - saturate(dot(xy, xy)))));
                #endif
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 sample = _UseTexture > .5 ? tex2D(_MainTex, i.uv) : 1;
                if (_ViewerMode < .5) {
                    float3 albedo = sample.rgb * _Color.rgb;
                    if (_UseVertexColor > .5) albedo *= i.color.rgb;
                    return float4(albedo, _Opacity);
                }
                float3 value;
                if (_ViewerMode < 1.5) value = _UseTexture > .5 ? Normal(sample) * .5 + .5 : float3(.5, .5, 1);
                else {
                    float component = _Channel > 2.5 ? sample.a : _Channel > .5 ? sample.g : sample.r;
                    value = saturate(component * _Scalar + _Bias).xxx;
                }
                // Data values are displayed numerically (0.5 is middle grey) in both
                // colour spaces, rather than receiving an extra display gamma curve.
                #ifndef UNITY_COLORSPACE_GAMMA
                    value = GammaToLinearSpace(value);
                #endif
                return float4(value, _Opacity);
            }
            ENDCG
        }
    }
}
