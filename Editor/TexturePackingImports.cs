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

        internal sealed class Transaction
        {
            public Action install;
            public Action verify;
            public Action prepareRollback;
            public Action rollback;
            public Action restoreImports;
            public Action<bool> complete;
        }

        private sealed class Batch
        {
            public Input[] inputs;
            public int index;
            public Transaction transaction;
            public Task verification;
            public Exception failure;
            public bool refreshBlocked;
            public readonly List<Exception> failures = new List<Exception>();
            public readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        }

        private static readonly Queue<Batch> Pending = new Queue<Batch>();

        internal static Task Schedule(Input[] inputs) => Schedule(inputs, (Transaction)null);
        internal static Task Schedule(Input[] inputs, Action install)
            => Schedule(inputs, new Transaction { install = install });

        internal static Task Schedule(Input[] inputs, Transaction transaction)
        {
            var batch = new Batch { inputs = inputs, transaction = transaction };
            try
            {
                if (transaction != null)
                {
                    AssetDatabase.DisallowAutoRefresh();
                    batch.refreshBlocked = true;
                    try { using (new AssetDatabase.AssetEditingScope()) transaction.install?.Invoke(); }
                    catch (Exception exception) { batch.failure = exception; }
                    batch.verification = Task.Run(() => Verify(batch));
                }
                if (Pending.Count == 0) Subscribe();
                Pending.Enqueue(batch);
                return batch.completion.Task;
            }
            catch
            {
                ReleaseRefresh(batch);
                if (Pending.Count == 0) Unsubscribe();
                if (batch.verification != null)
                    _ = batch.verification.ContinueWith(finished => { _ = finished.Exception; }, TaskScheduler.Default);
                throw;
            }
        }

        private static void Verify(Batch batch)
        {
            if (batch.failure == null)
            {
                try { batch.transaction.verify?.Invoke(); }
                catch (Exception exception) { batch.failure = exception; }
            }
            if (batch.failure != null) batch.transaction.prepareRollback?.Invoke();
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
            if (batch.verification != null)
            {
                if (!batch.verification.IsCompleted) return;
                FinishTransaction(batch);
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

        private static void FinishTransaction(Batch batch)
        {
            try
            {
                try { batch.verification.GetAwaiter().GetResult(); }
                catch (Exception exception) { AddFailure(batch, exception); }
                if (batch.failure != null)
                {
                    try { using (new AssetDatabase.AssetEditingScope()) batch.transaction.rollback?.Invoke(); }
                    catch (Exception exception) { AddFailure(batch, exception); }
                    try { batch.transaction.restoreImports?.Invoke(); }
                    catch (Exception exception) { AddFailure(batch, exception); }
                }
                try { batch.transaction.complete?.Invoke(batch.failure == null); }
                catch (Exception exception) { AddFailure(batch, exception); }
                if (batch.failure != null)
                {
                    batch.failures.Add(batch.failure);
                    batch.index = batch.inputs.Length;
                    UvtLog.Error("Texture packer installation: " + batch.failure.Message);
                }
            }
            finally
            {
                batch.verification = null;
                batch.transaction = null;
                ReleaseRefresh(batch);
            }
        }

        private static void AddFailure(Batch batch, Exception exception)
        {
            batch.failure = batch.failure == null ? exception : new AggregateException(batch.failure, exception);
        }

        private static void Drain()
        {
            // Unity reload/quit is already blocking; finish verification before unloading its callbacks.
            while (Pending.Count > 0)
            {
                var verification = Pending.Peek().verification;
                if (verification != null && !verification.IsCompleted)
                {
                    try { verification.GetAwaiter().GetResult(); }
                    catch (Exception) { _ = verification.Exception; }
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
            if (input.normal) importer.convertToNormalmap = false;
            importer.sRGBTexture = input.albedo;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = input.albedo;
            importer.maxTextureSize = 8192;
            importer.npotScale = TextureImporterNPOTScale.None;
            using (new AssetDatabase.AssetEditingScope()) importer.SaveAndReimport();
        }
    }
}
