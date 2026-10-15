Shader "Hidden/MeshLab/ViewportOverlay"
{
    Properties
    {
        _MainTex ("Surface", 2D) = "white" {}
        _UvScaleOffset ("Surface UV scale / offset", Vector) = (1,1,0,0)
        _Color ("Tint", Color) = (1,1,1,1)
        _UseVertexColor ("Vertex colours", Float) = 0
        _UseTexture ("Texture", Float) = 0
        _UseTextureAlpha ("Transparent material alpha", Float) = 0
        _Lit ("Headlight", Float) = 0
        _ShellFill ("Shell fill", Float) = 0
        _ShellOpacity ("Shell opacity", Float) = 1
        _ShellAA ("Shell boundary coverage", Float) = 0
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
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Color;
            float4 _UvScaleOffset;
            float _UseVertexColor, _UseTexture, _UseTextureAlpha, _Lit, _ShellFill, _ShellOpacity, _ShellAA;
            float4 _ShellPalette[16], _IssuePalette[7];
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 validation : TEXCOORD1; float3 neighbors : TEXCOORD2; float3 barycentric : TEXCOORD3; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 normal : TEXCOORD1; float3 view : TEXCOORD2; float4 neighbor0 : TEXCOORD3; float4 neighbor1 : TEXCOORD4; float4 neighbor2 : TEXCOORD5; float4 barycentric : TEXCOORD6; };
            float4 ShellComposite(float4 color, float4 issue)
            {
                float shellAlpha = saturate(color.a * _ShellOpacity);
                float issueAlpha = saturate(issue.a);
                return float4(issue.rgb * issueAlpha + color.rgb * shellAlpha * (1-issueAlpha),
                    issueAlpha + shellAlpha * (1-issueAlpha));
            }
            float4 NeighborComposite(float packed)
            {
                if (packed < 0) return float4(0,0,0,-1);
                int code = (int)round(packed);
                float4 color = _ShellPalette[code % 16];
                if ((code / 16) % 2 != 0) color.rgb = lerp(color.rgb,1,.45);
                color.a = (code / 32) % 2 != 0 ? 1.85 : 1;
                return ShellComposite(color,_IssuePalette[code / 64]);
            }
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _UvScaleOffset.xy + _UvScaleOffset.zw;
                o.color = _Color;
                if (_UseVertexColor > .5) o.color *= v.color;
                o.normal = UnityObjectToWorldNormal(v.normal); o.view = WorldSpaceViewDir(v.vertex);
                o.barycentric = float4(v.barycentric,0);
                o.neighbor0 = o.neighbor1 = o.neighbor2 = float4(0,0,0,-1);
                if (_ShellFill > .5) {
                    o.color.a = v.color.a;
                    o.color = ShellComposite(o.color,v.validation);
                    // Styles are constant per face: compose them per vertex rather than
                    // fetching/decomposing palette entries for every covered pixel.
                    [branch] if (_ShellAA > .5 && any(v.neighbors >= 0)) {
                        o.neighbor0 = NeighborComposite(v.neighbors.x);
                        o.neighbor1 = NeighborComposite(v.neighbors.y);
                        o.neighbor2 = NeighborComposite(v.neighbors.z);
                        o.barycentric.w = abs(dot(normalize(o.normal),normalize(o.view)));
                    }
                }
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 color = i.color;
                if (_ShellFill > .5) {
                    float4 composed = color;
                    // Only mix the two surface colours at a seam. Keep sharp creases,
                    // silhouettes, underlying materials and analytic wire coverage intact.
                    [branch] if (_ShellAA > .5 && i.barycentric.w > .35) {
                        float3 distance = i.barycentric.xyz / max(fwidth(i.barycentric.xyz),0.00001);
                        float3 weight = saturate(.5-distance) * step(0,float3(i.neighbor0.a,i.neighbor1.a,i.neighbor2.a));
                        float total = weight.x+weight.y+weight.z;
                        if (total > 0) {
                            weight /= max(1,total);
                            composed *= 1-min(1,total);
                            composed += weight.x * float4(i.neighbor0.rgb,max(0,i.neighbor0.a));
                            composed += weight.y * float4(i.neighbor1.rgb,max(0,i.neighbor1.a));
                            composed += weight.z * float4(i.neighbor2.rgb,max(0,i.neighbor2.a));
                        }
                    }
                    return float4(composed.rgb / max(composed.a,0.00001),composed.a*_Color.a);
                }
                if (_UseTexture > .5) {
                    float4 sampled = tex2D(_MainTex, i.uv);
                    color.rgb *= sampled.rgb;
                    if (_UseTextureAlpha > .5) color.a *= sampled.a;
                }
                if (_Lit > .5) color.rgb *= .35 + .65 * abs(dot(normalize(i.normal), normalize(i.view)));
                return color;
            }
            ENDCG
        }
    }
}
