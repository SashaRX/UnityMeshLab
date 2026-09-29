Shader "Hidden/MeshLab/RemeshBeautyEquirect"
{
    // Cubemap → equirectangular blit so reflection probes can be sampled by
    // direction on the CPU: u = atan2(z, x) / 2π, v = acos(y) / π (see
    // RemeshBeauty.SampleEquirect — the mapping below must stay its mirror).
    Properties { _MainTex ("Probe", Cube) = "" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            samplerCUBE _MainTex;
            float _Lod;
            float4 frag(v2f_img i) : SV_Target
            {
                float phi = i.uv.x * 2.0 * UNITY_PI;
                float theta = i.uv.y * UNITY_PI;
                float3 dir = float3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
                // _Lod samples the probe's own prefiltered mip — the roughness blur the
                // game sees. The CPU side pairs two levels for the fractional part.
                return float4(texCUBElod(_MainTex, float4(dir, _Lod)).rgb, 1);
            }
            ENDCG
        }
    }
}
