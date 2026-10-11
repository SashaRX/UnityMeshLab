#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System.IO;
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab
{
    /// <summary>One project export policy shared by document edits, rebuilds and Remesh.</summary>
    internal readonly struct FbxWriteSettings
    {
        internal readonly bool Binary, EmbedTextures;
        internal string FormatName => Binary ? "binary" : "ASCII";

        internal FbxWriteSettings(bool binary, bool embedTextures)
        {
            Binary = binary;
            EmbedTextures = binary && embedTextures;
        }

        internal static FbxWriteSettings FromProject(bool? embedTextures = null)
        {
            var settings = MeshLabProjectSettings.Instance;
            return new FbxWriteSettings(settings.fbxFormat != MeshLabFbxFormat.ASCII,
                embedTextures ?? settings.embedFbxTextures);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        internal int WriterFormat(FbxManager manager)
        {
            int format = manager.GetIOPluginRegistry().FindWriterIDByDescription(
                Binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            if (format < 0) throw new IOException("FBX SDK has no writer for the selected format.");
            return format;
        }
#endif
    }
}
