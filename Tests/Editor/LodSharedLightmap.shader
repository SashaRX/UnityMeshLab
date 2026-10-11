// Irradiance diagnostic: actual shared atlas, no headlight or invented bake.
Shader "Hidden/MeshLab/Tests/SharedLightmap"
{
    Properties
    {
        _BakedLight ("Baked color", 2D) = "white" {}
        _BakedDirection ("Baked direction", 2D) = "gray" {}
        _Directional ("Directional", Float) = 0
    }
    SubShader
    {
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _BakedLight, _BakedDirection;
            float4 _BakedST;
            float4x4 _BakedNormalToWorld;
            float _Directional;
            struct Input { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD1; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 normal : TEXCOORD1; };
            Output vert(Input input)
            {
                Output output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv*_BakedST.xy+_BakedST.zw;
                output.normal = mul((float3x3)_BakedNormalToWorld,input.normal);
                return output;
            }
            float4 frag(Output input) : SV_Target
            {
                float3 light = tex2D(_BakedLight,input.uv).rgb;
                if (_Directional > .5)
                {
                    float4 direction = tex2D(_BakedDirection,input.uv);
                    // Unity's combined dominant-direction lightmap equation.
                    float halfLambert = dot(normalize(input.normal),direction.xyz-.5)+.5;
                    light *= halfLambert/max(direction.w,1e-4);
                }
                return float4(light,1);
            }
            ENDCG
        }
    }
}
