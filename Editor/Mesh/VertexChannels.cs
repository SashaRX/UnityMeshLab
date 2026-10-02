// VertexChannels.cs — a scalar per-vertex channel (a vertex colour component or a UV
// component, as AOTargetChannel names them) read from and written to a mesh, plus the
// whole-colour-array operations the variant export and the AO preview need. Vertex AO
// baking, the transfer tool's export, the sidecar and the variant painter each decoded
// the channel enum and poked colors32 / GetUVs themselves; this is the one place.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class VertexChannels
    {
        /// <summary>Raised after this class writes a mesh's colours or UVs, so cached
        /// views of that mesh (the 3D viewport's encodings) can drop it.</summary>
        internal static event Action<Mesh> Changed;
        internal static AOTargetChannel? LastAppliedTargetChannel { get; set; }

        static void NotifyChanged(Mesh mesh) => Changed?.Invoke(mesh);

        // ── channel decoding ──

        /// <summary>True for the four vertex colour components.</summary>
        internal static bool IsColor(AOTargetChannel channel) => (int)channel <= (int)AOTargetChannel.VertexColorA;

        /// <summary>0 = R, 1 = G, 2 = B, 3 = A; -1 for a UV channel.</summary>
        internal static int ColorComponent(AOTargetChannel channel)
            => IsColor(channel) ? (int)channel - (int)AOTargetChannel.VertexColorR : -1;

        /// <summary>The UV set (0–4) a UV channel addresses; -1 for a colour channel.</summary>
        internal static int UvChannel(AOTargetChannel channel)
            => IsColor(channel) ? -1 : ((int)channel - (int)AOTargetChannel.UV0_X) / 2;

        /// <summary>0 = X, 1 = Y; -1 for a colour channel.</summary>
        internal static int UvComponent(AOTargetChannel channel)
            => IsColor(channel) ? -1 : ((int)channel - (int)AOTargetChannel.UV0_X) % 2;

        /// <summary>"Vertex Color R" … "UV4 Y", as the Vertex Colors tab labels them.</summary>
        internal static string Name(AOTargetChannel channel)
        {
            if (IsColor(channel)) return "Vertex Color " + "RGBA"[ColorComponent(channel)];
            return "UV" + UvChannel(channel) + " " + "XY"[UvComponent(channel)];
        }

        // ── scalar read / write ──

        /// <summary>
        /// The channel as one float per vertex (colour bytes / 255, UV components clamped
        /// to [0,1]); null when the mesh does not carry that stream at the vertex count.
        /// </summary>
        internal static float[] Read(Mesh mesh, AOTargetChannel channel)
        {
            if (mesh == null) return null;
            int vertCount = mesh.vertexCount;
            if (IsColor(channel))
            {
                var colors = mesh.colors32;
                if (colors == null || colors.Length != vertCount) return null;
                int comp = ColorComponent(channel);
                var values = new float[vertCount];
                for (int i = 0; i < vertCount; i++)
                {
                    var c = colors[i];
                    values[i] = (comp == 0 ? c.r : comp == 1 ? c.g : comp == 2 ? c.b : c.a) / 255f;
                }
                return values;
            }
            else
            {
                int comp = UvComponent(channel);
                var uvs = new List<Vector2>();
                mesh.GetUVs(UvChannel(channel), uvs);
                if (uvs.Count != vertCount) return null;
                var values = new float[vertCount];
                for (int i = 0; i < vertCount; i++)
                    values[i] = Mathf.Clamp01(comp == 0 ? uvs[i].x : uvs[i].y);
                return values;
            }
        }

        /// <summary>
        /// Stores <paramref name="values"/> (clamped to [0,1]) in the channel, keeping the
        /// other components: the colour write remaps to bytes over a white default, the UV
        /// write keeps the float over a zero default. Values are linear — no gamma. A
        /// length mismatch writes nothing. Records Undo under <paramref name="undoLabel"/>
        /// when given.
        /// </summary>
        internal static void Write(Mesh mesh, float[] values, AOTargetChannel channel, string undoLabel = null)
            => WriteMasked(mesh, values, channel, null, undoLabel);

        // The write behind Write and WriteSubmesh: with a mask, only the vertices it marks
        // are touched and every other vertex keeps its stored value byte- or float-exact.
        static void WriteMasked(Mesh mesh, float[] values, AOTargetChannel channel, bool[] mask, string undoLabel)
        {
            if (mesh == null || values == null || values.Length != mesh.vertexCount) return;
            if (undoLabel != null) Undo.RecordObject(mesh, undoLabel);

            if (IsColor(channel))
            {
                var colors = mesh.colors32;
                if (colors == null || colors.Length != mesh.vertexCount)
                {
                    colors = new Color32[mesh.vertexCount];
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 255, 255, 255);
                }
                int comp = ColorComponent(channel);
                for (int i = 0; i < values.Length; i++)
                {
                    if (mask != null && !mask[i]) continue;
                    byte v = (byte)(Mathf.Clamp01(values[i]) * 255f);
                    var c = colors[i];
                    if (comp == 0) c.r = v; else if (comp == 1) c.g = v; else if (comp == 2) c.b = v; else c.a = v;
                    colors[i] = c;
                }
                mesh.colors32 = colors;
            }
            else
            {
                int uvIdx = UvChannel(channel), comp = UvComponent(channel);
                var uvs = new List<Vector2>();
                mesh.GetUVs(uvIdx, uvs);
                if (uvs.Count != mesh.vertexCount)
                {
                    uvs.Clear();
                    for (int i = 0; i < mesh.vertexCount; i++) uvs.Add(Vector2.zero);
                }
                for (int i = 0; i < values.Length; i++)
                {
                    if (mask != null && !mask[i]) continue;
                    // Clamp to [0,1] like the colour path; blur and area correction can
                    // drift slightly out of range.
                    float v = Mathf.Clamp01(values[i]);
                    var uv = uvs[i];
                    if (comp == 0) uv.x = v; else uv.y = v;
                    uvs[i] = uv;
                }
                mesh.SetUVs(uvIdx, uvs);
            }
            NotifyChanged(mesh);
        }

        /// <summary>
        /// Writes <paramref name="values"/> only at the vertices submesh
        /// <paramref name="submesh"/> uses; every other vertex keeps its stored value
        /// exactly (no re-read, no clamp). False when the submesh index is out of range.
        /// </summary>
        internal static bool WriteSubmesh(Mesh mesh, float[] values, AOTargetChannel channel, int submesh, string undoLabel = null)
        {
            if (mesh == null || values == null || values.Length != mesh.vertexCount) return false;
            if (submesh < 0 || submesh >= mesh.subMeshCount) return false;
            var mask = new bool[mesh.vertexCount];
            foreach (int vi in mesh.GetIndices(submesh))
                if (vi >= 0 && vi < mask.Length) mask[vi] = true;
            WriteMasked(mesh, values, channel, mask, undoLabel);
            return true;
        }

        // ── scalar maths ──

        /// <summary>`(v - 0.5) * contrast + 0.5 + brightness`, clamped to [0,1], in place.</summary>
        internal static void Levels(float[] values, float brightness, float contrast)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++)
                values[i] = Mathf.Clamp01((values[i] - 0.5f) * contrast + 0.5f + brightness);
        }

        /// <summary>A new array `lerp(a, b, t)` per vertex; a copy of <paramref name="a"/> when <paramref name="b"/> is missing or mismatched.</summary>
        internal static float[] Blend(float[] a, float[] b, float t)
        {
            if (a == null) return null;
            var result = (float[])a.Clone();
            if (b == null || b.Length != a.Length || t <= 0f) return result;
            for (int i = 0; i < result.Length; i++) result[i] = Mathf.Lerp(a[i], b[i], t);
            return result;
        }

        // ── colour arrays ──

        /// <summary>The values as opaque greys (v, v, v, 255), for a preview.</summary>
        internal static Color32[] Greyscale(float[] values)
        {
            if (values == null) return null;
            var colors = new Color32[values.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                byte v = (byte)(Mathf.Clamp01(values[i]) * 255f);
                colors[i] = new Color32(v, v, v, 255);
            }
            return colors;
        }

        /// <summary>Sets every vertex colour to <paramref name="color"/>, with Undo and SetDirty.</summary>
        internal static void FillColors(Mesh mesh, Color color, string undoLabel)
        {
            if (mesh == null || mesh.vertexCount == 0) return;
            var color32 = (Color32)color;
            if (undoLabel != null) Undo.RecordObject(mesh, undoLabel);
            var arr = new Color32[mesh.vertexCount];
            for (int i = 0; i < arr.Length; i++) arr[i] = color32;
            mesh.colors32 = arr;
            EditorUtility.SetDirty(mesh);
            NotifyChanged(mesh);
        }

        /// <summary>Replaces the mesh's whole colour array (null removes the stream) and raises
        /// <see cref="Changed"/>. No Undo or SetDirty: a caller that needs them records them itself.</summary>
        internal static void SetColors(Mesh mesh, Color[] colors)
        {
            if (mesh == null) return;
            mesh.colors = colors;
            NotifyChanged(mesh);
        }

        /// <summary>Replaces one UV channel (null removes it) and raises <see cref="Changed"/>. No Undo or SetDirty.</summary>
        internal static void SetUvs(Mesh mesh, int channel, Vector2[] uvs)
        {
            if (mesh == null) return;
            if (uvs == null) mesh.SetUVs(channel, (List<Vector2>)null); else mesh.SetUVs(channel, uvs);
            NotifyChanged(mesh);
        }

        /// <summary>The mesh's colours at its vertex count, or null when it has none (for <see cref="RestoreColors"/>).</summary>
        internal static Color32[] SnapshotColors(Mesh mesh)
        {
            if (mesh == null) return null;
            var c = mesh.colors32;
            return c != null && c.Length == mesh.vertexCount ? c : null;
        }

        /// <summary>Puts a <see cref="SnapshotColors"/> result back; a null snapshot (the mesh
        /// had no colours) removes the colour stream again rather than leaving one behind.
        /// With Undo and SetDirty.</summary>
        internal static void RestoreColors(Mesh mesh, Color32[] snapshot, string undoLabel)
        {
            if (mesh == null) return;
            if (undoLabel != null) Undo.RecordObject(mesh, undoLabel);
            mesh.colors32 = snapshot ?? Array.Empty<Color32>();
            EditorUtility.SetDirty(mesh);
            NotifyChanged(mesh);
        }
    }
}
