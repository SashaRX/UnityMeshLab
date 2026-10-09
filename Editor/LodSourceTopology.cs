using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal sealed class LodSourceTopology
    {
        internal sealed class Face
        {
            internal int[] points, vertices, triangles, referenceTriangles;
            internal int submesh;
        }
        internal readonly LodMeshData data;
        internal readonly List<Face> faces;
        internal int QuadCount => faces.Count(f => f.points.Length == 4);
        internal LodSourceTopology(LodMeshData data, List<Face> faces) { this.data = data; this.faces = faces; }

        internal static bool TryLoad(MeshEntry entry, Mesh mesh, Dictionary<string, object> imports,
            out LodSourceTopology source, out string error, bool loopsOnly = true)
        {
            source = null;
            error = null;
            try
            {
                if (!mesh.isReadable || mesh.vertexCount == 0) throw new InvalidOperationException("Source mesh is not readable or is empty.");
                var data = new LodMeshData(mesh);
                if (Enumerable.Range(0, mesh.subMeshCount).All(s => mesh.GetTopology(s) == MeshTopology.Quads))
                {
                    var faces = new List<Face>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        int[] indices = mesh.GetIndices(s);
                        for (int i = 0; i < indices.Length; i += 4)
                        {
                            int[] v = indices.Skip(i).Take(4).ToArray();
                            int[] t = { v[0], v[1], v[2], v[0], v[2], v[3] };
                            faces.Add(new Face { points = v, vertices = v, triangles = t, referenceTriangles = t, submesh = s });
                        }
                    }
                    source = new LodSourceTopology(data, faces);
                }
                else
                {
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                    string path = AssetDatabase.GetAssetPath(entry.fbxMesh);
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Original polygons require an FBX asset or a mesh with quad topology.");
                    if (!imports.TryGetValue(path, out object cached))
                    {
                        cached = LoadRawTags(path);
                        imports[path] = cached;
                    }
                    var rawImport = (RawImport)cached;
                    if (rawImport.errors.TryGetValue(entry.fbxMesh.name,out string bindingError)) throw new InvalidOperationException(bindingError);
                    var tags = rawImport.tags;
                    if (!tags.TryGetValue(entry.fbxMesh.name, out var tag) || tag.ambiguous)
                        throw new InvalidOperationException("Source FBX mesh name is missing or ambiguous.");
                    using var document = FbxSourceDocument.Load(System.IO.Path.GetFullPath(path));
                    var topology = new FbxLayerChannels.Topology(document.Meshes[tag.ordinal]);
                    source = FromTagged(data, topology.polygonSizes, topology.cornerControlPoint, tag);
#else
                    throw new InvalidOperationException("Reading original FBX polygons requires the FBX Exporter package.");
#endif
                }
                if (loopsOnly)
                {
                    if (source.QuadCount == 0) throw new InvalidOperationException("Source has no quads. Export FBX without triangulation or select Triangles mode.");
                    if (!LodTopologyGraph.TryBuild(source.faces, source.data, out _, out error)) { source = null; return false; }
                }
                return true;
            }
            catch (Exception ex) { error = ex.Message; source = null; return false; }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        sealed class RawImport
        {
            internal Dictionary<string,FbxChannelWrite.Tagged> tags;
            internal readonly Dictionary<string,string> errors = new Dictionary<string,string>();
        }

        static RawImport LoadRawTags(string path)
        {
            const string folder = "Assets/__MeshLabRawLod";
            bool createdFolder = !AssetDatabase.IsValidFolder(folder);
            if (createdFolder) AssetDatabase.CreateFolder("Assets",folder.Substring(7));
            string copy = folder+"/source_"+Guid.NewGuid().ToString("N")+".fbx";
            try
            {
                Uv2AssetPostprocessor.bypassPaths.Add(copy);
                if (!AssetDatabase.CopyAsset(path,copy)) throw new InvalidOperationException("Cannot create an uncompressed FBX topology copy.");
                var importer = (ModelImporter)AssetImporter.GetAtPath(copy);
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.isReadable = true;
                importer.SaveAndReimport();
                var rawMeshes = AssetDatabase.LoadAllAssetsAtPath(copy).OfType<Mesh>().GroupBy(m => m.name).ToDictionary(g => g.Key,g => g.ToArray());
                var originals = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().GroupBy(m => m.name).ToDictionary(g => g.Key,g => g.ToArray());
                var tags = FbxChannelWrite.ImportTagged(copy,importer.swapUVChannels,out _);
                var result = new RawImport { tags = tags };
                using var document = FbxSourceDocument.Load(System.IO.Path.GetFullPath(path));
                foreach (var item in tags)
                {
                    var tag = item.Value;
                    if (tag.ambiguous || !rawMeshes.TryGetValue(item.Key,out var raw) || raw.Length != 1 ||
                        !originals.TryGetValue(item.Key,out var original) || original.Length != 1) { tag.ambiguous = true; continue; }
                    try { RebindRawTag(tag,new FbxLayerChannels.Topology(document.Meshes[tag.ordinal]),raw[0],original[0]); }
                    catch (InvalidOperationException ex) { result.errors[item.Key] = item.Key+": "+ex.Message; }
                }
                return result;
            }
            finally
            {
                Uv2AssetPostprocessor.bypassPaths.Remove(copy);
                AssetDatabase.DeleteAsset(copy);
                if (createdFolder && AssetDatabase.IsValidFolder(folder) && AssetDatabase.FindAssets(string.Empty,new[] {folder}).Length == 0)
                    AssetDatabase.DeleteAsset(folder);
            }
        }

        static void RebindRawTag(FbxChannelWrite.Tagged tag,FbxLayerChannels.Topology polygons,Mesh raw,Mesh original)
        {
            var readable = MeshAccess.Readable(original,out bool copy);
            try { RebindReadableTag(tag,polygons,raw,readable); }
            finally { if (copy && readable != null) UnityEngine.Object.DestroyImmediate(readable); }
        }

        static void RebindReadableTag(FbxChannelWrite.Tagged tag,FbxLayerChannels.Topology polygons,Mesh raw,Mesh original)
        {
            if (original == null || raw.vertexCount != original.vertexCount || raw.subMeshCount != original.subMeshCount)
                throw new InvalidOperationException("Compressed FBX vertex layout differs from its uncompressed source.");
            var triangles = new List<int>();
            for (int s = 0; s < raw.subMeshCount; s++)
            {
                var t = LodMeshData.Triangles(raw,s);
                if (!t.SequenceEqual(LodMeshData.Triangles(original,s)))
                    throw new InvalidOperationException("Compressed FBX connectivity differs from its uncompressed source.");
                triangles.AddRange(t);
            }
            var data = new LodMeshData(raw); var compressed = new LodMeshData(original);
            // Raw and compressed imports share verified index connectivity. Allow
            // bounded import quantization, never arbitrary remeshing or transforms.
            float tolerance = data.scale*.005f + 1e-7f;
            for (int v = 0; v < raw.vertexCount; v++)
                if ((data.positions[v]-compressed.positions[v]).sqrMagnitude > tolerance*tolerance)
                    throw new InvalidOperationException("Compressed FBX positions differ beyond import quantization.");
            var match = FbxCornerMatch.Match(polygons.polygonSizes,tag.cornerPositions,
                data.positions.SelectMany(p => new[] {p.x,p.y,p.z}).ToArray(),triangles.ToArray(),
                Enumerable.Repeat(3,triangles.Count/3).ToArray(),
                (c,v) => tag.uvs[0] == null || data.uvs[0] == null || ((Vector2)data.uvs[0][v]).Equals(tag.uvs[0][c]),data.SameAttributes);
            if (match.unresolved+match.conflicts+match.missing != 0)
                throw new InvalidOperationException("Raw FBX corners cannot be uniquely bound to the uncompressed import.");
            for (int c = 0; c < match.cornerToVertex.Length; c++)
            {
                int v = match.cornerToVertex[c]; var p = compressed.positions[v];
                tag.cornerPositions[c*3] = p.x; tag.cornerPositions[c*3+1] = p.y; tag.cornerPositions[c*3+2] = p.z;
                if (tag.uvs[0] != null && compressed.uvs[0] != null) tag.uvs[0][c] = compressed.uvs[0][v];
            }
        }

        internal static LodSourceTopology FromTagged(LodMeshData data, int[] sizes, int[] controlPoints, FbxChannelWrite.Tagged tag)
        {
            var positions = data.positions.SelectMany(v => new[] { v.x, v.y, v.z }).ToArray();
            var faceIndices = new List<int>();
            for (int s = 0; s < data.source.subMeshCount; s++) faceIndices.AddRange(LodMeshData.Triangles(data.source, s));
            if (tag.cornerPositions.Length != controlPoints.Length * 3)
                throw new InvalidOperationException("Degenerate or missing FBX corners cannot be used for full-loop reduction.");
            var match = FbxCornerMatch.Match(sizes, tag.cornerPositions, positions, faceIndices.ToArray(),
                Enumerable.Repeat(3, faceIndices.Count / 3).ToArray(),
                (c, v) => tag.uvs[0] == null || data.uvs[0] == null || ((Vector2)data.uvs[0][v]).Equals(tag.uvs[0][c]), data.SameAttributes);
            if (match.unresolved + match.conflicts + match.missing != 0)
                throw new InvalidOperationException($"Working mesh differs from source polygons: {match.unresolved} unresolved, {match.conflicts} conflicting, {match.missing} missing corners.");
            var faces = new List<Face>();
            var cornerFace = new int[controlPoints.Length];
            for (int f = 0, offset = 0; f < sizes.Length; offset += sizes[f], f++)
            {
                int[] v = match.cornerToVertex.Skip(offset).Take(sizes[f]).ToArray();
                int[] p = controlPoints.Skip(offset).Take(sizes[f]).ToArray();
                for (int i = 0; i < sizes[f]; i++) cornerFace[offset + i] = f;
                faces.Add(new Face { points = p, vertices = v, submesh = -1 });
            }
            var triangles = faces.Select(_ => new List<int>()).ToArray();
            var expected = new Dictionary<(int, int, int, int), int>();
            var actual = new Dictionary<(int, int, int, int), int>();
            var canonical = CanonicalVertices(data);
            for (int s = 0; s < tag.submeshCorners.Length; s++)
            {
                int[] corners = tag.submeshCorners[s];
                int size = tag.submeshFaceSizes[s];
                for (int i = 0; i < corners.Length; i += size)
                {
                    int[] t = size == 4 ? new[] { corners[i], corners[i + 1], corners[i + 2], corners[i], corners[i + 2], corners[i + 3] }
                        : new[] { corners[i], corners[i + 1], corners[i + 2] };
                    for (int j = 0; j < t.Length; j += 3)
                    {
                        int f = cornerFace[t[j]];
                        if (cornerFace[t[j + 1]] != f || cornerFace[t[j + 2]] != f)
                            throw new InvalidOperationException("Tagged import does not preserve source polygon membership.");
                        var face = faces[f];
                        if (face.submesh >= 0 && face.submesh != s) throw new InvalidOperationException("A source polygon spans material slots.");
                        face.submesh = s;
                        for (int k = 0; k < 3; k++) triangles[f].Add(match.cornerToVertex[t[j + k]]);
                    }
                }
                AddTriangleKeys(expected, triangles.Where((_, f) => faces[f].submesh == s).SelectMany(t => t).ToArray(), canonical, s);
                if (s >= data.source.subMeshCount) throw new InvalidOperationException("Material slots differ from source FBX.");
                AddTriangleKeys(actual, LodMeshData.Triangles(data.source, s), canonical, s);
            }
            if (expected.Count != actual.Count || expected.Any(k => !actual.TryGetValue(k.Key, out int n) || n != k.Value) ||
                tag.submeshCorners.Length != data.source.subMeshCount)
                throw new InvalidOperationException("Working triangles or material slots differ from source FBX. Full loops require unchanged source connectivity.");
            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                face.triangles = face.referenceTriangles = triangles[f].ToArray();
                if (face.triangles.Length == 0 || face.points.Length < 3) throw new InvalidOperationException("Source has an empty or invalid polygon.");
                Vector3 polygonNormal = Vector3.zero;
                Vector3 origin = data.positions[face.vertices[0]];
                for (int i = 0; i < face.vertices.Length; i++)
                    polygonNormal += Vector3.Cross(data.positions[face.vertices[i]] - origin, data.positions[face.vertices[(i + 1) % face.vertices.Length]] - origin);
                int[] t = face.triangles;
                Vector3 triangleNormal = Vector3.Cross(data.positions[t[1]] - data.positions[t[0]], data.positions[t[2]] - data.positions[t[0]]);
                if (Vector3.Dot(polygonNormal, triangleNormal) < 0) { Array.Reverse(face.points); Array.Reverse(face.vertices); }
            }
            return new LodSourceTopology(data, faces);
        }

        static int[] CanonicalVertices(LodMeshData data)
        {
            var atPosition = new Dictionary<FbxCornerMatch.PositionKey, List<int>>();
            var canonical = new int[data.positions.Length];
            for (int i = 0; i < canonical.Length; i++)
            {
                var p = data.positions[i];
                var key = new FbxCornerMatch.PositionKey(p.x, p.y, p.z);
                if (!atPosition.TryGetValue(key, out var candidates)) atPosition[key] = candidates = new List<int>();
                int match = candidates.FindIndex(v => data.SameAttributes(i, v));
                canonical[i] = match >= 0 ? candidates[match] : i;
                if (match < 0) candidates.Add(i);
            }
            return canonical;
        }
        static void AddTriangleKeys(Dictionary<(int, int, int, int), int> keys, int[] triangles, int[] canonical, int submesh)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = canonical[triangles[i]], b = canonical[triangles[i + 1]], c = canonical[triangles[i + 2]];
                // Cyclic rotations are equivalent; reversing winding is not.
                var key = a <= b && a <= c ? (a, b, c, submesh) : b <= c ? (b, c, a, submesh) : (c, a, b, submesh);
                keys.TryGetValue(key, out int count);
                keys[key] = count + 1;
            }
        }
#endif
    }
}
