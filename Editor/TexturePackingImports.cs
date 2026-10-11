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
            public Task installation;
            public bool refreshBlocked;
            public readonly List<Exception> failures = new List<Exception>();
            public readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        }

        private static readonly Queue<Batch> Pending = new Queue<Batch>();

        internal static Task Schedule(Input[] inputs)
            => Schedule(inputs, null);

        internal static Task Schedule(Input[] inputs, Action install)
        {
            var batch = new Batch { inputs = inputs };
            try
            {
                if (install != null)
                {
                    // The static queue owns the refresh counter and worker even after its window closes.
                    AssetDatabase.DisallowAutoRefresh();
                    batch.refreshBlocked = true;
                    batch.installation = Task.Run(install);
                }
                if (Pending.Count == 0) Subscribe();
                Pending.Enqueue(batch);
                return batch.completion.Task;
            }
            catch
            {
                ReleaseRefresh(batch);
                if (Pending.Count == 0) Unsubscribe();
                if (batch.installation != null)
                    _ = batch.installation.ContinueWith(finished => { _ = finished.Exception; }, TaskScheduler.Default);
                throw;
            }
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
            if (batch.installation != null)
            {
                if (!batch.installation.IsCompleted) return;
                try { batch.installation.GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    batch.failures.Add(exception);
                    batch.index = batch.inputs.Length; // A failed transaction must never be imported.
                    UvtLog.Error("Texture packer installation: " + exception.Message);
                }
                finally
                {
                    batch.installation = null;
                    ReleaseRefresh(batch);
                }
            }
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
            while (Pending.Count > 0)
            {
                var installation = Pending.Peek().installation;
                if (installation != null && !installation.IsCompleted)
                {
                    try { installation.GetAwaiter().GetResult(); }
                    catch (Exception) { _ = installation.Exception; } // Tick publishes transaction failures.
                }
                Tick();
            }
        }

        private static void ReleaseRefresh(Batch batch)
        {
            if (!batch.refreshBlocked) return;
            batch.refreshBlocked = false;
            AssetDatabase.AllowAutoRefresh();
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
