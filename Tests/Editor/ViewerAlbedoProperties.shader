Shader "Hidden/MeshLab/Tests/ViewerAlbedoProperties"
{
    Properties
    {
        _BaseColorMap ("HDRP base", 2D) = "white" {}
        _AlbedoMap ("Custom base", 2D) = "white" {}
        _Albedo ("Alternative base", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
        _Surface ("Surface type", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
}
