using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Barycentric subdivision of a detached mesh. Original corners keep
    /// their attribute bytes; new corners interpolate every channel in its format.
    /// Positions use float32 so imported position quantization cannot bend a cut.</summary>
    internal static class ReverseUvRefinedMesh
    {
        readonly struct Sample
        {
            internal readonly int a, b, c;
            internal readonly Vector3 weights;
            internal Sample(int a, int b, int c, Vector3 weights) { this.a = a; this.b = b; this.c = c; this.weights = weights; }
            // Only a complete canonical corner identifies an original byte copy;
            // a nearly-one weight must still interpolate all source attributes.
            internal int Exact => weights.Equals(Vector3.right) ? a : weights.Equals(Vector3.up) ? b : weights.Equals(Vector3.forward) ? c : -1;
        }

        internal static Mesh Copy(Mesh source, int[] sourceFaces, Vector3[] barycentrics, Vector2[] uv)
        {
            if (barycentrics.Length != sourceFaces.Length * 3 || uv.Length != barycentrics.Length)
                throw new ArgumentException("Refined reverse corners do not match their source face map.");
            var triangles = source.triangles;
            var samples = new List<Sample>(); var pixels = new List<Vector2>(); var indices = new int[uv.Length];
            var remap = new Dictionary<(int, int, int, Vector3, Vector2), int>();
            for (int i = 0; i < indices.Length; ++i)
            {
                int t = sourceFaces[i / 3] * 3;
                var sample = new Sample(triangles[t], triangles[t + 1], triangles[t + 2], barycentrics[i]);
                var key = sample.Exact >= 0 ? (sample.Exact, -1, -1, Vector3.right, uv[i])
                    : (sample.a, sample.b, sample.c, sample.weights, uv[i]);
                if (!remap.TryGetValue(key, out int vertex))
                { vertex = samples.Count; remap.Add(key, vertex); samples.Add(sample); pixels.Add(uv[i]); }
                indices[i] = vertex;
            }
            var output = new Mesh { name = source.name + "_reverseSplit", hideFlags = HideFlags.HideAndDontSave,
                indexFormat = samples.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                CopyStreams(source, output, samples);
                var faceSubmeshes = new List<int>();
                for (int sub = 0; sub < source.subMeshCount; ++sub)
                    faceSubmeshes.AddRange(Enumerable.Repeat(sub, source.GetTriangles(sub).Length / 3));
                var parts = Enumerable.Range(0, source.subMeshCount).Select(_ => new List<int>()).ToArray();
                for (int f = 0; f < sourceFaces.Length; ++f)
                    for (int k = 0; k < 3; ++k) parts[faceSubmeshes[sourceFaces[f]]].Add(indices[f * 3 + k]);
                output.subMeshCount = parts.Length;
                for (int sub = 0; sub < parts.Length; ++sub) output.SetTriangles(parts[sub], sub, false);
                output.bindposes = source.bindposes;
                CopyBlendShapes(source, output, samples);
                CopyWeights(source, output, samples);
                output.SetUVs(1, pixels); output.bounds = source.bounds;
                MeshUvState.SetDraft(output, MeshUvState.IsDraft(source));
                return output;
            }
            catch { UnityEngine.Object.DestroyImmediate(output); throw; }
        }

        static void CopyStreams(Mesh source, Mesh output, List<Sample> samples)
        {
            var layout = source.GetVertexAttributes(); var targetLayout = layout.ToArray();
            for (int i = 0; i < targetLayout.Length; ++i)
                if (targetLayout[i].attribute == VertexAttribute.Position)
                    targetLayout[i] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, targetLayout[i].dimension, targetLayout[i].stream);
            output.SetVertexBufferParams(samples.Count, targetLayout);
            using var data = Mesh.AcquireReadOnlyMeshData(source);
            for (int stream = 0; stream < source.vertexBufferCount; ++stream)
            {
                int fromStride = source.GetVertexBufferStride(stream), toStride = output.GetVertexBufferStride(stream);
                var from = data[0].GetVertexData<byte>(stream).ToArray(); var bytes = new byte[samples.Count * toStride];
                foreach (var attribute in layout.Where(a => a.stream == stream))
                {
                    int offset = source.GetVertexAttributeOffset(attribute.attribute), target = output.GetVertexAttributeOffset(attribute.attribute);
                    var format = attribute.attribute == VertexAttribute.Position ? VertexAttributeFormat.Float32 : attribute.format;
                    int size = Size(attribute.format), outputSize = Size(format);
                    for (int v = 0; v < samples.Count; ++v)
                    {
                        var sample = samples[v];
                        if (sample.Exact >= 0 && format == attribute.format)
                        { Buffer.BlockCopy(from, sample.Exact * fromStride + offset, bytes, v * toStride + target, attribute.dimension * size); continue; }
                        for (int c = 0; c < attribute.dimension; ++c)
                        {
                            double a = Read(from, sample.a * fromStride + offset + c * size, attribute.format);
                            double b = Read(from, sample.b * fromStride + offset + c * size, attribute.format);
                            double d = Read(from, sample.c * fromStride + offset + c * size, attribute.format);
                            double value = a * sample.weights.x + b * sample.weights.y + d * sample.weights.z;
                            if (IsInteger(attribute.format) || (attribute.attribute == VertexAttribute.Tangent && c == 3))
                                value = sample.weights.x >= sample.weights.y && sample.weights.x >= sample.weights.z ? a : sample.weights.y >= sample.weights.z ? b : d;
                            Write(bytes, v * toStride + target + c * outputSize, format, value);
                        }
                    }
                }
                output.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream);
            }
        }

        static bool IsInteger(VertexAttributeFormat format) => format >= VertexAttributeFormat.UInt8;
        static int Size(VertexAttributeFormat format)
            => format == VertexAttributeFormat.Float32 || format == VertexAttributeFormat.UInt32 || format == VertexAttributeFormat.SInt32 ? 4
                : format == VertexAttributeFormat.Float16 || format == VertexAttributeFormat.UNorm16 || format == VertexAttributeFormat.SNorm16
                    || format == VertexAttributeFormat.UInt16 || format == VertexAttributeFormat.SInt16 ? 2 : 1;

        static double Read(byte[] bytes, int offset, VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(bytes, offset);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, offset));
                case VertexAttributeFormat.UNorm8: return bytes[offset] / 255d;
                case VertexAttributeFormat.SNorm8: return Math.Max(-1, (sbyte)bytes[offset] / 127d);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(bytes, offset) / 65535d;
                case VertexAttributeFormat.SNorm16: return Math.Max(-1, BitConverter.ToInt16(bytes, offset) / 32767d);
                case VertexAttributeFormat.UInt8: return bytes[offset];
                case VertexAttributeFormat.SInt8: return (sbyte)bytes[offset];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(bytes, offset);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(bytes, offset);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(bytes, offset);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(bytes, offset);
                default: throw new InvalidOperationException("Unsupported reverse vertex format.");
            }
        }

        static void Write(byte[] bytes, int offset, VertexAttributeFormat format, double value)
        {
            byte[] encoded;
            switch (format)
            {
                case VertexAttributeFormat.Float32: encoded = BitConverter.GetBytes((float)value); break;
                case VertexAttributeFormat.Float16: encoded = BitConverter.GetBytes(Mathf.FloatToHalf((float)value)); break;
                case VertexAttributeFormat.UNorm8: bytes[offset] = (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255); return;
                case VertexAttributeFormat.SNorm8: bytes[offset] = unchecked((byte)(sbyte)Math.Round(Math.Max(-1, Math.Min(1, value)) * 127)); return;
                case VertexAttributeFormat.UNorm16: encoded = BitConverter.GetBytes((ushort)Math.Round(Math.Max(0, Math.Min(1, value)) * 65535)); break;
                case VertexAttributeFormat.SNorm16: encoded = BitConverter.GetBytes((short)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767)); break;
                case VertexAttributeFormat.UInt8: bytes[offset] = (byte)value; return;
                case VertexAttributeFormat.SInt8: bytes[offset] = unchecked((byte)(sbyte)value); return;
                case VertexAttributeFormat.UInt16: encoded = BitConverter.GetBytes((ushort)value); break;
                case VertexAttributeFormat.SInt16: encoded = BitConverter.GetBytes((short)value); break;
                case VertexAttributeFormat.UInt32: encoded = BitConverter.GetBytes((uint)value); break;
                case VertexAttributeFormat.SInt32: encoded = BitConverter.GetBytes((int)value); break;
                default: throw new InvalidOperationException("Unsupported reverse vertex format.");
            }
            Buffer.BlockCopy(encoded, 0, bytes, offset, encoded.Length);
        }

        static Vector3 Interpolate(Vector3[] values, Sample sample)
            => values[sample.a] * sample.weights.x + values[sample.b] * sample.weights.y + values[sample.c] * sample.weights.z;

        static void CopyBlendShapes(Mesh source, Mesh output, List<Sample> samples)
        {
            for (int shape = 0; shape < source.blendShapeCount; ++shape)
                for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); ++frame)
                {
                    var p = new Vector3[source.vertexCount]; var n = new Vector3[p.Length]; var t = new Vector3[p.Length];
                    source.GetBlendShapeFrameVertices(shape, frame, p, n, t);
                    output.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                        samples.Select(s => Interpolate(p, s)).ToArray(), samples.Select(s => Interpolate(n, s)).ToArray(), samples.Select(s => Interpolate(t, s)).ToArray());
                }
        }

        static void CopyWeights(Mesh source, Mesh output, List<Sample> samples)
        {
            using var counts = source.GetBonesPerVertex(); using var weights = source.GetAllBoneWeights();
            if (counts.Length == 0 || weights.Length == 0) return;
            var offsets = new int[counts.Length]; int sum = 0;
            for (int i = 0; i < counts.Length; ++i) { offsets[i] = sum; sum += counts[i]; }
            var newCounts = new byte[samples.Count]; var newWeights = new List<BoneWeight1>();
            for (int v = 0; v < samples.Count; ++v)
            {
                var sample = samples[v];
                if (sample.Exact >= 0)
                {
                    newCounts[v] = counts[sample.Exact];
                    for (int k = 0; k < counts[sample.Exact]; ++k) newWeights.Add(weights[offsets[sample.Exact] + k]);
                    continue;
                }
                var blended = new Dictionary<int, float>();
                var vertices = new[] { sample.a, sample.b, sample.c };
                for (int k = 0; k < 3; ++k)
                    for (int w = 0; w < counts[vertices[k]]; ++w)
                    {
                        var weight = weights[offsets[vertices[k]] + w];
                        blended.TryGetValue(weight.boneIndex, out float previous);
                        blended[weight.boneIndex] = previous + weight.weight * sample.weights[k];
                    }
                var ordered = blended.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key).ToArray();
                if (ordered.Length > 255) throw new InvalidOperationException("Reverse UV subdivision requires more than 255 bone influences at one vertex.");
                newCounts[v] = (byte)ordered.Length; float total = ordered.Sum(p => p.Value);
                foreach (var pair in ordered) newWeights.Add(new BoneWeight1 { boneIndex = pair.Key, weight = pair.Value / total });
            }
            using var mappedCounts = new NativeArray<byte>(newCounts, Allocator.Temp);
            using var mappedWeights = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp);
            output.SetBoneWeights(mappedCounts, mappedWeights);
        }
    }
}
