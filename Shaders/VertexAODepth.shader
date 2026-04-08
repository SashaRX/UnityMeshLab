Shader "Hidden/LightmapUvTool/VertexAODepth"
{
    // Renders mesh geometry outputting linear depth (0=near, 1=far) to an RFloat color target.
    // Depth is computed from view-space Z for platform independence (no reversed-Z dependency).
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
            ZTest GEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float4x4 _AO_ViewMatrix;
            float    _AO_InvDepthRange;

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

                // Compute linear depth from view-space Z (platform-independent).
                // View matrix is the raw (non-GPU-adjusted) world-to-camera transform.
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float viewZ = mul(_AO_ViewMatrix, float4(worldPos, 1.0)).z;
                o.depth = -viewZ * _AO_InvDepthRange;
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
