// Isolated shader validation; never copy into a consumer project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class MeshLabShaderCompileTrace : IPreprocessComputeShaders, IPreprocessShaders
{
    public int callbackOrder => 0;
    public static readonly HashSet<string> Compiled = new HashSet<string>();
    public void OnProcessComputeShader(ComputeShader shader, string kernel, IList<ShaderCompilerData> data)
    {
        foreach (var variant in data) Compiled.Add(shader.name + "/" + kernel + "/" + variant.shaderCompilerPlatform);
    }
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (!AssetDatabase.GetAssetPath(shader).StartsWith("Packages/com.sasharx.unitymeshlab/", StringComparison.Ordinal)) return;
        foreach (var variant in data) Compiled.Add(shader.name + "/" + snippet.passName + "/" + variant.shaderCompilerPlatform);
    }
}

public static class AndroidShaderBuild
{
    [Serializable] sealed class Report { public string unity, hostApi, status, error; public List<Case> cases = new List<Case>(); }
    [Serializable] sealed class Case { public string api, encoding; public string[] assets, compiled; public bool success; }
    public static void Run()
    {
        var report = new Report { unity = Application.unityVersion, hostApi = SystemInfo.graphicsDeviceType.ToString(), status = "running" };
        var project = Path.GetDirectoryName(Application.dataPath);
        var output = Path.Combine(Path.GetDirectoryName(project), "graphics-api-matrix", "android");
        Directory.CreateDirectory(output);
        int exit = 1;
        try {
            if (Path.GetFileName(project) != "preview-lighting-urp" || EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                throw new InvalidOperationException("Android shader validation is restricted to the isolated Android-target harness.");
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.sasharx.unitymeshlab/package.json");
            var assets = Directory.GetFiles(Path.Combine(package.resolvedPath, "Shaders"))
                .Where(path => path.EndsWith(".compute") || path.EndsWith(".shader"))
                .Select(path => "Packages/com.sasharx.unitymeshlab/Shaders/" + Path.GetFileName(path)).OrderBy(path => path).ToArray();
            var bundles = new[] { new AssetBundleBuild { assetBundleName = "meshlab-shaders", assetNames = assets } };
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            foreach (var api in new[] { GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.Vulkan })
            foreach (var encoding in new[] { NormalMapEncoding.XYZ, NormalMapEncoding.DXT5nm }) {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { api });
                PlayerSettings.SetNormalMapEncoding(NamedBuildTarget.Android, encoding);
                foreach (var asset in assets) {
                    var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(asset);
                    if (compute) typeof(ShaderUtil).GetMethod("ClearComputeShaderMessages", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { compute });
                }
                MeshLabShaderCompileTrace.Compiled.Clear();
                string caseOutput = Path.Combine(output, api + "-" + encoding);
                Directory.CreateDirectory(caseOutput);
                var manifest = BuildPipeline.BuildAssetBundles(caseOutput, bundles,
                    BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ChunkBasedCompression,
                    BuildTarget.Android);
                var current = new Case { api = api.ToString(), encoding = encoding.ToString(), assets = assets,
                    compiled = MeshLabShaderCompileTrace.Compiled.OrderBy(value => value).ToArray(), success = manifest != null };
                report.cases.Add(current);
                if (!manifest) throw new InvalidOperationException("Shader bundle failed: " + api + " / " + encoding);
                string platform = api == GraphicsDeviceType.OpenGLES3 ? "/GLES3x" : "/Vulkan";
                foreach (string kernel in new[] { "BvhQueries/Raycast", "BvhQueries/Nearest", "SourceAORayTrace/BakeAO", "SourceAORayTrace/FinalizeAO", "VertexAORayTrace/BakeAO", "VertexAORayTrace/FinalizeAO" })
                    if (!MeshLabShaderCompileTrace.Compiled.Contains(kernel + platform)) throw new InvalidOperationException("Missing target compilation: " + kernel + platform);
                Debug.Log("MESHLAB_ANDROID_SHADER_CASE " + api + " " + encoding + " shaders=" + assets.Length + " variants=" + current.compiled.Length);
            }
            report.status = "passed"; exit = 0;
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        finally { File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(report, true)); EditorApplication.Exit(exit); }
    }
}
