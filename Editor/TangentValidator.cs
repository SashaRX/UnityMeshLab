// TangentValidator.cs — Save-time and weld-time TBN (tangent) validation helpers.
// Goals:
//  • If the original mesh had no tangents, do not save tangents on derived/result meshes.
//  • If tangents are present, validate the W component (handedness) and detect
//    NaN / zero-length / non-unit-W issues that indicate broken TBN data.
//  • After UV weld operations, verify tangents survived the merge cleanly.

using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TangentValidator
    {
        // Allowed deviation of |w| from 1.0 before we flag the tangent as invalid.
        // Unity stores tangent.w as ±1 sign of bitangent handedness; FBX importers
        // sometimes round-trip slightly off, so we only complain about real drift.
        const float W_TOLERANCE = 0.01f;

        // A tangent xyz with squared magnitude below this is treated as effectively zero.
        const float MIN_SQR_LEN = 1e-10f;

        /// <summary>True if the mesh carries a non-empty tangent stream matching its vertex count.</summary>
        internal static bool HasTangents(Mesh mesh)
        {
            if (mesh == null) return false;
            var t = mesh.tangents;
            return t != null && t.Length > 0 && t.Length == mesh.vertexCount;
        }

        /// <summary>
        /// Validate handedness (w == ±1) and basic sanity (non-NaN, non-zero) on a tangent array.
        /// Logs at Warn level when issues are found. Returns true when all tangents look valid.
        /// </summary>
        internal static bool ValidateTangentsW(Vector4[] tangents, string meshName, string operation)
        {
            if (tangents == null || tangents.Length == 0) return true;

            int nanCount = 0;
            int zeroCount = 0;
            int badWCount = 0;
            int firstBadIdx = -1;
            float worstWDev = 0f;

            for (int i = 0; i < tangents.Length; i++)
            {
                var t = tangents[i];

                if (float.IsNaN(t.x) || float.IsNaN(t.y) || float.IsNaN(t.z) || float.IsNaN(t.w))
                {
                    nanCount++;
                    if (firstBadIdx < 0) firstBadIdx = i;
                    continue;
                }

                float sqr = t.x * t.x + t.y * t.y + t.z * t.z;
                if (sqr < MIN_SQR_LEN)
                {
                    zeroCount++;
                    if (firstBadIdx < 0) firstBadIdx = i;
                }

                float wDev = Mathf.Abs(Mathf.Abs(t.w) - 1f);
                if (wDev > W_TOLERANCE)
                {
                    badWCount++;
                    if (wDev > worstWDev) worstWDev = wDev;
                    if (firstBadIdx < 0) firstBadIdx = i;
                }
            }

            int total = nanCount + zeroCount + badWCount;
            if (total == 0) return true;

            UvtLog.Warn($"[TBN] {operation} '{meshName}': tangent issues — " +
                        $"NaN={nanCount}, zero-length={zeroCount}, bad-W={badWCount} (worst |w|-1={worstWDev:F4}), " +
                        $"first bad index={firstBadIdx}");
            return false;
        }

        /// <summary>
        /// Mirror tangent presence from <paramref name="original"/> onto <paramref name="result"/>.
        /// If original had no tangents, drops them on result; if it had them, validates W on result.
        /// Warns when the original carried tangents but the result has none — that indicates a
        /// pipeline step dropped TBN data and normal-map shading would regress on the saved mesh.
        /// Safe to call with null arguments — no-op when either is null.
        /// </summary>
        internal static void EnforceTangentsMatchOriginal(Mesh result, Mesh original, string operation)
        {
            if (result == null || original == null) return;

            bool originalHasTbn = HasTangents(original);
            bool resultHasTbn = HasTangents(result);

            if (!originalHasTbn && resultHasTbn)
            {
                UvtLog.Info($"[TBN] {operation} '{result.name}': source had no tangents — stripping {result.tangents.Length} tangents from result");
                result.tangents = null;
                return;
            }

            if (originalHasTbn && !resultHasTbn)
            {
                UvtLog.Warn($"[TBN] {operation} '{result.name}': source carried tangents but result has none — TBN dropped upstream, saved mesh will be missing tangent-space data");
                return;
            }

            if (originalHasTbn && resultHasTbn)
                ValidateTangentsW(result.tangents, result.name, operation);
        }

        /// <summary>
        /// After a weld/merge operation, verify the welded tangent stream is still consistent.
        /// Flags if the original had tangents but the welded result lost them, and validates W
        /// when both are present. Does not mutate the welded mesh.
        /// </summary>
        internal static void ValidateAfterWeld(Mesh original, Mesh welded, string operation)
        {
            if (original == null || welded == null) return;

            bool origHas = HasTangents(original);
            bool weldedHas = HasTangents(welded);

            if (origHas && !weldedHas)
            {
                UvtLog.Warn($"[TBN] {operation} '{welded.name}': source had tangents but welded result has none — TBN dropped during weld");
                return;
            }

            if (!weldedHas) return;
            ValidateTangentsW(welded.tangents, welded.name, operation);
        }
    }
}
