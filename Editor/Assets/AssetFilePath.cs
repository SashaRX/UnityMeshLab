// AssetFilePath.cs — where an asset path's file is on disk: a package's assets
// (Packages/<name>/...) live in the package's own folder, wherever it is resolved.

namespace SashaRX.UnityMeshLab
{
    internal static class AssetFilePath
    {
        /// <summary>An asset path (or a path outside the project) as a full path on disk, with forward slashes.</summary>
        internal static string Physical(string path)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath) && path.StartsWith(package.assetPath + "/", System.StringComparison.Ordinal))
                path = System.IO.Path.Combine(package.resolvedPath, path.Substring(package.assetPath.Length + 1));
            return System.IO.Path.GetFullPath(path).Replace('\\', '/');
        }
    }
}
