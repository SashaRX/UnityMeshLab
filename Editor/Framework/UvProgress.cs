// UvProgress.cs — Lightweight, non-modal progress reporting.
//
// Replaces EditorUtility.DisplayProgressBar / DisplayCancelableProgressBar
// (which render a modal dialog that blocks input and forces full editor
// repaints on every Report call) with:
//
//   * UnityEditor.Progress — visible in the Background Tasks panel in the
//     bottom-right of the editor; non-modal; supports cancellation hooks.
//   * An in-memory Snapshot that UvToolHub reads each OnGUI to draw an
//     inline status strip directly in the tool window.
//
// Designed for main-thread callers. Reporting is cheap (no GUI redraw
// triggered per Report); the snapshot is throttled so tight loops do not
// flood the OnChanged listeners.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public static class UvProgress
    {
        /// <summary>
        /// Read-only snapshot of the current top-level operation.
        /// The hub samples this every OnGUI; tools call Begin/Report/End
        /// to drive it.
        /// </summary>
        public struct Snapshot
        {
            public bool   active;
            public string title;
            public string phase;
            public string detail;
            public float  fraction;       // 0..1, or -1 for indeterminate.
            public double startTime;
            public bool   cancelable;
            public bool   cancelRequested;

            public double Elapsed => EditorApplication.timeSinceStartup - startTime;
        }

        // Stack so nested Begin/End scopes work; the bottom of the stack is the
        // top-level operation and drives the inline snapshot.
        internal static readonly Stack<int> _stack = new Stack<int>();
        static Snapshot _snapshot;
        static double _lastNotify;
        const double kNotifyIntervalSec = 1.0 / 30.0;

        /// <summary>Fires when the snapshot changes (throttled to ~30 Hz).</summary>
        public static event Action OnChanged;

        public static Snapshot Current => _snapshot;
        public static bool IsActive => _snapshot.active;

        /// <summary>
        /// Outcome of the most recently finished operation. Drawn in the
        /// inline strip while idle so the user has a passive record of what
        /// just happened ("✓ Run Full Pipeline · 12.3s" or "✘ Repack cancelled").
        /// Cleared on the next Begin.
        /// </summary>
        public struct LastResult
        {
            public bool   valid;
            public string title;
            public double duration;
            public Progress.Status status;
        }
        public static LastResult Last { get; private set; }

        /// <summary>
        /// True if the user clicked Cancel in the Background Tasks panel or
        /// in the inline status strip. Long loops should poll this between
        /// safe abort points.
        /// </summary>
        public static bool CancelRequested => _snapshot.cancelRequested;

        /// <summary>
        /// Start a new operation. Returns a progress id (or -1 if the API is
        /// unavailable). Multiple Begin calls nest; only the outermost drives
        /// the inline snapshot.
        /// </summary>
        public static int Begin(string title, bool cancelable = false)
        {
            int parent = _stack.Count > 0 ? _stack.Peek() : -1;
            int id;
            try
            {
                id = Progress.Start(title, string.Empty, Progress.Options.None, parent);
                if (cancelable)
                {
                    int capturedId = id;
                    Progress.RegisterCancelCallback(capturedId, () =>
                    {
                        _snapshot.cancelRequested = true;
                        Notify(force: true);
                        return true;
                    });
                }
            }
            catch
            {
                id = -1;
            }

            _stack.Push(id);
            if (_stack.Count == 1)
            {
                // Clear the idle "last operation" line — a fresh run replaces
                // the previous outcome.
                Last = default;
                _snapshot = new Snapshot
                {
                    active          = true,
                    title           = title,
                    phase           = null,
                    detail          = null,
                    fraction        = -1f,
                    startTime       = EditorApplication.timeSinceStartup,
                    cancelable      = cancelable,
                    cancelRequested = false,
                };
                Notify(force: true);
            }
            return id;
        }

        /// <summary>
        /// Update progress for the current (top-of-stack) operation. Pass
        /// fraction &lt; 0 to indicate indeterminate progress (spinner).
        /// </summary>
        public static void Report(float fraction, string detail = null)
        {
            int top = _stack.Count > 0 ? _stack.Peek() : -1;
            if (top >= 0)
            {
                try
                {
                    if (fraction < 0f) Progress.Report(top, 0f, detail ?? string.Empty);
                    else               Progress.Report(top, Mathf.Clamp01(fraction), detail ?? string.Empty);
                }
                catch { /* progress API may not be available in headless contexts */ }
            }
            // Only the outermost scope drives the inline snapshot — nested
            // sub-tasks update their own Progress entry but not the strip.
            if (_stack.Count <= 1)
            {
                _snapshot.fraction = fraction;
                if (detail != null) _snapshot.detail = detail;
            }
            Notify(force: false);
        }

        /// <summary>
        /// Switch to a new logical phase under the current operation.
        /// Resets the inline fraction to 0 and clears the detail line.
        /// </summary>
        public static void SetPhase(string phase, float fraction = 0f, string detail = null)
        {
            if (_stack.Count <= 1)
            {
                _snapshot.phase    = phase;
                _snapshot.detail   = detail;
                _snapshot.fraction = fraction;
            }
            int top = _stack.Count > 0 ? _stack.Peek() : -1;
            if (top >= 0)
            {
                try { Progress.SetDescription(top, phase ?? string.Empty); }
                catch { /* ignore */ }
                if (fraction >= 0f)
                {
                    try { Progress.Report(top, Mathf.Clamp01(fraction), detail ?? string.Empty); }
                    catch { /* ignore */ }
                }
            }
            Notify(force: true);
        }

        /// <summary>
        /// Mark the current operation as cancel-requested. Same effect as the
        /// user clicking the cancel button in the Background Tasks panel; long
        /// loops poll <see cref="CancelRequested"/> to abort cleanly.
        /// </summary>
        public static void RequestCancel()
        {
            if (!_snapshot.active) return;
            _snapshot.cancelRequested = true;
            Notify(force: true);
        }

        /// <summary>End the current operation with success status.</summary>
        public static void End() => Finish(Progress.Status.Succeeded);

        /// <summary>End the current operation with failure status and a message.</summary>
        public static void Fail(string error)
        {
            if (_stack.Count > 0 && !string.IsNullOrEmpty(error))
                _snapshot.detail = error;
            Finish(Progress.Status.Failed);
        }

        /// <summary>End the current operation with cancelled status.</summary>
        public static void Cancel() => Finish(Progress.Status.Canceled);

        static void Finish(Progress.Status status)
        {
            if (_stack.Count == 0) return;
            int id = _stack.Pop();
            if (id >= 0)
            {
                try { Progress.Finish(id, status); }
                catch { /* ignore */ }
            }
            if (_stack.Count == 0)
            {
                // Record outcome for the idle-state "last operation" line in
                // the inline strip before clearing the active flag.
                Last = new LastResult
                {
                    valid    = true,
                    title    = _snapshot.title,
                    duration = _snapshot.Elapsed,
                    status   = status,
                };
                _snapshot.active = false;
                Notify(force: true);
            }
        }

        static void Notify(bool force)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now - _lastNotify < kNotifyIntervalSec) return;
            _lastNotify = now;
            try { OnChanged?.Invoke(); }
            catch { /* listeners must not crash the pipeline */ }
        }

        /// <summary>
        /// Force a notify regardless of throttle. Useful between phases or
        /// before yielding to give the inline UI a chance to repaint.
        /// </summary>
        public static void Flush() => Notify(force: true);

        // ────────────────────────────────────────────────────────────
        //  Background-thread progress drain
        // ────────────────────────────────────────────────────────────
        // Direct Report() calls from a non-main thread (e.g. inside
        // GroupedShellTransfer.TransferCore running under Task.Run) silently
        // no-op: Progress.Report and EditorWindow.Repaint both require the
        // main thread, so they get swallowed by the try/catch wrapper.
        //
        // The drain pattern: background threads write to a volatile slot via
        // ReportFromBackground; a main-thread EditorApplication.update tick
        // reads the slot and replays it through the regular Report path so
        // the strip + Background Tasks panel both update correctly.
        static volatile string _bgPhase;
        static volatile string _bgDetail;
        static int _bgPumpHooked; // 0=no, 1=yes (Interlocked guard)

        /// <summary>
        /// Thread-safe version of Report for background callers. Stores the
        /// payload in a volatile slot drained by an EditorApplication.update
        /// pump on the main thread. Safe to call from Task.Run / ThreadPool.
        /// </summary>
        public static void ReportFromBackground(string phase, string detail = null)
        {
            _bgPhase = phase;
            _bgDetail = detail;
            EnsureBgPump();
        }

        static void EnsureBgPump()
        {
            // Idempotent: hook the pump once per editor session. Interlocked
            // so two background callers can't race the registration.
            if (System.Threading.Interlocked.Exchange(ref _bgPumpHooked, 1) != 0) return;
            EditorApplication.update += BgPump;
        }

        static void BgPump()
        {
            // Snapshot then clear so subsequent ticks don't re-replay the
            // same payload (Report is cheap but the throttled OnChanged
            // event would still fire each tick).
            string phase = _bgPhase;
            string detail = _bgDetail;
            if (phase == null && detail == null) return;
            _bgPhase = null;
            _bgDetail = null;

            // Update the snapshot via the normal SetPhase / Report path so
            // both the inline strip and the Background Tasks panel reflect
            // the same state.
            if (phase != null) SetPhase(phase, fraction: -1f, detail: detail);
            else               Report(-1f, detail);
        }
    }
}
