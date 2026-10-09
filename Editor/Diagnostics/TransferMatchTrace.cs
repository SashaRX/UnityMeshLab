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
        }

        public List<Shell> shells = new List<Shell>();
        public float[] sourceTransformResiduals;
        public bool[] sourceMirrored;
        public bool truncated;
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
