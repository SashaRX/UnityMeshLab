using System;
using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    [Serializable]
    internal sealed class TransferMatchTrace
    {
        [Serializable]
        internal sealed class Candidate
        {
            public string phase;
            public int sourceShell;
            public float centroidDistanceSquared, surfaceDistanceSquared, normalDot, score;
            public float faceInteriorDistanceSquared = -1;
            public float uv0InteriorDistanceSquared = -1;
            public bool finite;
        }

        [Serializable]
        internal sealed class Shell
        {
            public int targetShell, initialSource = -1, finalSource = -1, method = -1, issues, status;
            public string initialReason;
            public string dedupDecision;
            public bool dedupReassigned, force3D, fragmentMerged, hintMatched;
            public float finalSurfaceDistanceSquared;
            public List<Candidate> candidates = new List<Candidate>();
            public int afterRescoreSource = -1, afterDedupSource = -1, normalFallbackVertices;
            public string projectionMethod;
            public List<Projection> projections = new List<Projection>();
            public TransferCandidateQuality beforeTopologyQuality, finalQuality;
        }

        [Serializable]
        internal sealed class Projection
        {
            public string method;
            public int sourceShell;
            public TransferCandidateQuality quality;
            public int[] vertices;
            public UnityEngine.Vector2[] uv;
        }

        public List<Shell> shells = new List<Shell>();
        public float[] sourceTransformResiduals;
        public bool[] sourceMirrored;
        public bool truncated;
        public bool sourceAtlasRecoveryAllowed;
        int recordedVertices;

        internal void RecordProjection(Shell shell, string method, int source,
            Dictionary<int, UnityEngine.Vector2> uv, TransferCandidateQuality quality)
        {
            if (shell == null || uv == null) return;
            var projection = new Projection { method = method, sourceShell = source, quality = quality };
            // Optional raw candidates are bounded per case; scores are always retained.
            if (uv.Count <= 4096 && recordedVertices + uv.Count <= 65536) {
                var keys = new List<int>(uv.Keys); keys.Sort();
                projection.vertices = keys.ToArray(); projection.uv = new UnityEngine.Vector2[keys.Count];
                for (int i = 0; i < keys.Count; ++i) projection.uv[i] = uv[keys[i]];
                recordedVertices += keys.Count;
            }
            else truncated = true;
            shell.projections.Add(projection);
        }
        internal Shell ForShell(int index)
        {
            if (index >= 10000) { truncated = true; return null; }
            while (shells.Count <= index) shells.Add(new Shell { targetShell = shells.Count });
            return shells[index];
        }

        internal static void RecordCandidate(Shell shell, string phase, int source, float centroid, float surface, float dot, float score,
            float faceInterior = -1, float uv0Interior = -1)
        {
            if (shell == null) return;
            bool finite = !float.IsNaN(score) && !float.IsInfinity(score);
            shell.candidates.Add(new Candidate { phase = phase, sourceShell = source,
                centroidDistanceSquared = FiniteValue(centroid), surfaceDistanceSquared = FiniteValue(surface), normalDot = FiniteValue(dot),
                faceInteriorDistanceSquared = FiniteValue(faceInterior),
                uv0InteriorDistanceSquared = FiniteValue(uv0Interior),
                score = finite ? score : float.MaxValue, finite = finite });
            // Keep the best eight evaluated candidates of each search, never rescore or change matching.
            int count = 0, worst = -1;
            for (int i = 0; i < shell.candidates.Count; ++i) {
                var candidate = shell.candidates[i];
                if (candidate.phase != phase) continue;
                ++count;
                if (worst < 0 || candidate.score > shell.candidates[worst].score) worst = i;
            }
            if (count > 8) shell.candidates.RemoveAt(worst);
        }

        static float FiniteValue(float value) => float.IsNaN(value) || float.IsInfinity(value) ? float.MaxValue : value;
    }
}
