// Matches the camera-directed vertex offset used by Environment_Master.
Shader "Hidden/MeshLab/Tests/ViewerVertexOffset"
{
    Properties
    {
        _Color ("Color", Color) = (0,0,1,1)
        _VPOOffsetNear ("Near offset", Float) = 0
        _VPOOffsetFar ("Far offset", Float) = 0.02
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float _VPOOffsetNear, _VPOOffsetFar;
            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                float3 world = mul(unity_ObjectToWorld, vertex).xyz;
                float distance = -mul(UNITY_MATRIX_V, float4(world, 1)).z;
                float offset = lerp(_VPOOffsetNear, _VPOOffsetFar, saturate((distance - _ProjectionParams.y) / 10));
                world += normalize(_WorldSpaceCameraPos - world) * offset;
                return UnityWorldToClipPos(world);
            }
            float4 frag() : SV_Target { return _Color; }
            ENDCG
        }
    }
}
