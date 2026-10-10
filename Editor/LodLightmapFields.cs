using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    // Snapshot only supported actual full-HDR, unmipped bindings. Packed RGBM,
    // mipmapped atlases and missing assignments retain the original UV2.
    internal sealed class LodLightmapFields
    {
        internal readonly Func<Vector2,Vector3,Vector4>[] fields;
        internal static LodLightmapFields TryCapture(Renderer renderer)
        {
            var maps = LightmapSettings.lightmaps;
            if (!renderer || maps == null || renderer.lightmapIndex < 0 || renderer.lightmapIndex >= maps.Length) return null;
            var map = maps[renderer.lightmapIndex];
            if (map == null) return null;
            bool directional = LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional;
            if (directional && !map.lightmapDir) return null;
            return Capture(map.lightmapColor,directional ? map.lightmapDir : null,renderer.lightmapScaleOffset,renderer.localToWorldMatrix.inverse.transpose);
        }
        internal static LodLightmapFields Capture(Texture2D color,Texture2D direction,Vector4 st,Matrix4x4 normalToWorld)
        {
            if (!color || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || color.mipmapCount != 1 ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat) || !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat) ||
                color.filterMode != FilterMode.Bilinear || direction && (direction.mipmapCount != 1 || direction.filterMode != FilterMode.Bilinear) ||
                color.format != TextureFormat.BC6H && color.format != TextureFormat.RGBAHalf && color.format != TextureFormat.RGBAFloat) return null;
            if (!WrapSupported(color) || direction && !WrapSupported(direction)) return null;
            var light = new Pixels(color); var dominant = direction ? new Pixels(direction) : null;
            Vector2 Atlas(Vector2 uv) => new Vector2(uv.x*st.x+st.z,uv.y*st.y+st.w);
            Vector4 Plain(Vector2 uv,Vector3 normal)
            { var c = light.Sample(Atlas(uv)); return new Vector4(c.r,c.g,c.b,0); }
            Vector4 Directional(Vector2 uv,Vector3 normal)
            {
                var tex = Atlas(uv); var c = light.Sample(tex); var d = dominant.Sample(tex);
                float weight = (Vector3.Dot(normalToWorld.MultiplyVector(normal).normalized,new Vector3(d.r-.5f,d.g-.5f,d.b-.5f))+.5f)/Mathf.Max(d.a,1e-4f);
                return new Vector4(c.r*weight,c.g*weight,c.b*weight,0);
            }
            return new LodLightmapFields(dominant == null ? new Func<Vector2,Vector3,Vector4>[] {Plain} : new Func<Vector2,Vector3,Vector4>[] {Plain,Directional});
        }
        static bool WrapSupported(Texture2D texture) =>
            (texture.wrapModeU == TextureWrapMode.Clamp || texture.wrapModeU == TextureWrapMode.Repeat) &&
            (texture.wrapModeV == TextureWrapMode.Clamp || texture.wrapModeV == TextureWrapMode.Repeat);
        LodLightmapFields(Func<Vector2,Vector3,Vector4>[] fields) { this.fields = fields; }
        sealed class Pixels
        {
            readonly Color[] values;
            readonly int width,height;
            readonly TextureWrapMode wrapU,wrapV;
            internal Pixels(Texture2D source)
            {
                width = source.width; height = source.height; wrapU = source.wrapModeU; wrapV = source.wrapModeV;
                var previous = RenderTexture.active;
                var target = RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                Texture2D copy = null;
                try
                {
                    copy = new Texture2D(width,height,TextureFormat.RGBAFloat,false,true);
                    Graphics.Blit(source,target); RenderTexture.active = target;
                    copy.ReadPixels(new Rect(0,0,width,height),0,0); copy.Apply(); values = copy.GetPixels();
                }
                finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); if (copy) UnityEngine.Object.DestroyImmediate(copy); }
            }
            internal Color Sample(Vector2 uv)
            {
                float x = uv.x*width-.5f,y = uv.y*height-.5f;
                int left = Mathf.FloorToInt(x),bottom = Mathf.FloorToInt(y);
                Color Pixel(int px,int py) => values[Address(py,height,wrapV)*width+Address(px,width,wrapU)];
                return Color.LerpUnclamped(Color.LerpUnclamped(Pixel(left,bottom),Pixel(left+1,bottom),x-left),
                    Color.LerpUnclamped(Pixel(left,bottom+1),Pixel(left+1,bottom+1),x-left),y-bottom);
            }
            static int Address(int index,int size,TextureWrapMode wrap) => wrap == TextureWrapMode.Repeat ? (index%size+size)%size : Mathf.Clamp(index,0,size-1);
        }
    }
}
