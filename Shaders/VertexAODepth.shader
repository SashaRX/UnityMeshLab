Shader "Hidden/LightmapUvTool/VertexAODepth"
{
    // Renders mesh geometry outputting linear depth (0=near, 1=far) to an RFloat color target.
    // Used by VertexAOBaker: orthographic camera renders from hemisphere directions,
    // compute shader samples this depth to determine per-vertex occlusion.
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float  depth : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);

                // Output normalized depth: 0 = near plane, 1 = far plane
                float d = o.pos.z / o.pos.w;
                #if defined(UNITY_REVERSED_Z)
                d = 1.0 - d;
                #endif
                o.depth = d;
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                return i.depth;
            }
            ENDCG
        }
    }
}
