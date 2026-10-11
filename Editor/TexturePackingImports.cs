using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Import one output per editor update; committed files finish configuration even after the window closes.</summary>
    internal static class TexturePackingImports
    {
        internal sealed class Input
        {
            public string path;
            public bool normal;
            public bool albedo;
        }

        private sealed class Batch
        {
            public Input[] inputs;
            public int index;
            public readonly List<Exception> failures = new List<Exception>();
            public readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        }

        private static readonly Queue<Batch> Pending = new Queue<Batch>();

        internal static Task Schedule(Input[] inputs)
        {
            var batch = new Batch { inputs = inputs };
            if (Pending.Count == 0) Subscribe();
            Pending.Enqueue(batch);
            return batch.completion.Task;
        }

        private static void Subscribe()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Drain;
            EditorApplication.quitting += Drain;
        }

        private static void Unsubscribe()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Drain;
            EditorApplication.quitting -= Drain;
        }

        private static void Tick()
        {
            if (Pending.Count == 0) return;
            var batch = Pending.Peek();
            if (batch.index < batch.inputs.Length)
            {
                var input = batch.inputs[batch.index++];
                try { Import(input); }
                catch (Exception exception)
                {
                    batch.failures.Add(exception);
                    UvtLog.Error("Texture packer import: " + input.path + ": " + exception.Message);
                }
            }
            if (batch.index != batch.inputs.Length) return;
            Pending.Dequeue();
            if (batch.failures.Count == 0) batch.completion.SetResult(true);
            else batch.completion.SetException(batch.failures);
            if (Pending.Count == 0) Unsubscribe();
        }

        private static void Drain()
        {
            // Domain reload is already a blocking Unity phase. Never leave committed assets with default import settings.
            while (Pending.Count > 0) Tick();
        }

        private static void Import(Input input)
        {
            using (new AssetDatabase.AssetEditingScope()) AssetDatabase.ImportAsset(input.path);
            var importer = AssetImporter.GetAtPath(input.path) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Unity did not create a texture importer: " + input.path);
            importer.textureType = input.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = input.albedo;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = input.albedo;
            importer.maxTextureSize = 8192;
            importer.npotScale = TextureImporterNPOTScale.None;
            using (new AssetDatabase.AssetEditingScope()) importer.SaveAndReimport();
        }
    }
}
