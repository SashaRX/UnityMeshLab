using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Coalesces preview requests. Unity snapshots and uploads run on editor
    /// update; the expensive array-only preparation runs on one worker at a time.</summary>
    internal sealed class PreviewWork<T> : IDisposable
    {
        readonly string label;
        Func<Func<CancellationToken, T>> pending;
        Action<T> pendingApply, activeApply;
        Task<T> active;
        CancellationTokenSource cancellation;
        bool subscribed;

        public PreviewWork(string label) { this.label = label; }
        internal bool IsPending => pending != null || active != null;

        public void Enqueue(Func<Func<CancellationToken, T>> prepare, Action<T> apply)
        {
            pending = prepare; pendingApply = apply;
            activeApply = null;
            cancellation?.Cancel();
            if (subscribed) return;
            EditorApplication.update += Tick;
            subscribed = true;
        }

        void Tick()
        {
            if (active != null) {
                if (!active.IsCompleted) return;
                var finished = active; var apply = activeApply;
                active = null; activeApply = null;
                cancellation.Dispose(); cancellation = null;
                try { var result = finished.GetAwaiter().GetResult(); apply?.Invoke(result); }
                catch (OperationCanceledException) { /* Superseded preview requests intentionally publish nothing. */ }
                catch (Exception exception) { if (apply != null) UvtLog.Error(label + ": " + exception.Message); }
            }
            if (pending == null) { Unsubscribe(); return; }
            var prepare = pending; var ready = pendingApply;
            pending = null; pendingApply = null;
            try {
                var build = prepare();
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                activeApply = ready;
                active = Task.Run(() => build(token), token);
            }
            catch (Exception exception) {
                cancellation?.Dispose(); cancellation = null;
                activeApply = null;
                UvtLog.Error(label + ": " + exception.Message);
                if (pending == null) Unsubscribe();
            }
        }

        void Unsubscribe()
        {
            if (!subscribed) return;
            EditorApplication.update -= Tick;
            subscribed = false;
        }

        public void Dispose()
        {
            Unsubscribe();
            pending = null; pendingApply = activeApply = null;
            if (active != null) {
                var source = cancellation;
                source.Cancel();
                // Observe faults and release the token after the worker stops. No
                // Unity calls or references back to a closed window in this continuation.
                _ = active.ContinueWith(task => { _ = task.Exception; source.Dispose(); }, TaskScheduler.Default);
            }
            else cancellation?.Dispose();
            active = null; cancellation = null;
        }
    }
}
