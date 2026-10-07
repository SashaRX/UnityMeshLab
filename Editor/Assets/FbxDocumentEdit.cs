// FbxDocumentEdit.cs — edits an FBX file in place through the FBX SDK: load the
// document, change one layer element of one mesh, save it back in the container
// format and version it came in. Polygons, control points, smoothing, materials,
// the other layer elements, nodes and properties are never rebuilt, so they come
// through as the source file has them (n-gons stay n-gons, no vertex splitting).
//
// No UnityEngine types: the Unity side (which corner gets which value) is
// FbxCornerMatch and FbxExport.WriteChannels.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;

namespace SashaRX.UnityMeshLab
{
    /// <summary>An FBX file loaded into an SDK scene, written back as it was read.</summary>
    internal sealed class FbxSourceDocument : IDisposable
    {
        // FBX SDK writer versions by the file version they write (FbxImporter.GetFileVersion).
        static readonly (int major, int minor, string writer)[] WriterVersions =
        {
            (7, 7, "FBX202000"), (7, 5, "FBX201800"), (7, 4, "FBX201400"),
            (7, 3, "FBX201300"), (7, 2, "FBX201200"), (7, 1, "FBX201100"),
        };

        readonly string scratch;
        public readonly FbxManager Manager;
        public readonly FbxScene Scene;
        public readonly bool Binary;
        public readonly int Major, Minor;
        public readonly bool EmbedsMedia;
        /// <summary>Unique meshes in node order; the index is the mesh's ordinal.</summary>
        public readonly List<FbxMesh> Meshes = new List<FbxMesh>();

        FbxSourceDocument(string path)
        {
            // The importer extracts embedded media next to the file it reads; reading a
            // copy in a scratch folder keeps the project clean and lets the writer embed
            // the same media again.
            scratch = Path.Combine(Path.GetTempPath(), "meshlab-fbx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            string input = Path.Combine(scratch, "input.fbx");
            File.Copy(path, input);
            Binary = IsBinary(input);

            Manager = FbxManager.Create();
            var io = FbxIOSettings.Create(Manager, Globals.IOSROOT);
            Manager.SetIOSettings(io);
            Scene = FbxScene.Create(Manager, "MeshLabDocument");
            using (var importer = FbxImporter.Create(Manager, "MeshLabImport"))
            {
                if (!importer.Initialize(input, -1, io))
                    throw new IOException($"FBX SDK cannot open '{path}'.");
                importer.GetFileVersion(out Major, out Minor, out _);
                if (!importer.Import(Scene))
                    throw new IOException($"FBX SDK cannot read '{path}'.");
            }
            string media = Path.Combine(scratch, "input.fbm");
            EmbedsMedia = Directory.Exists(media) && Directory.GetFileSystemEntries(media).Length > 0;
            CollectMeshes(Scene.GetRootNode(), Meshes);
        }

        public static FbxSourceDocument Load(string path)
        {
            try { return new FbxSourceDocument(path); }
            catch (DllNotFoundException ex) { throw new InvalidOperationException("FBX SDK native library unavailable.", ex); }
        }

        /// <summary>Writes the document to <paramref name="path"/> in the source's container format and version.</summary>
        public void Save(string path)
        {
            var io = Manager.GetIOSettings();
            io.SetBoolProp(Globals.EXP_FBX_EMBEDDED, EmbedsMedia);
            int format = Manager.GetIOPluginRegistry().FindWriterIDByDescription(Binary ? "FBX binary (*.fbx)" : "FBX ascii (*.fbx)");
            if (format < 0) throw new IOException("FBX SDK has no writer for the source format.");
            using var exporter = FbxExporter.Create(Manager, "MeshLabExport");
            if (!exporter.Initialize(path, format, io))
                throw new IOException($"FBX SDK cannot write '{path}'.");
            if (!exporter.SetFileExportVersion(WriterVersion(Major, Minor)))
                throw new IOException($"FBX SDK cannot write version {Major}.{Minor}.");
            if (!exporter.Export(Scene))
                throw new IOException($"FBX SDK failed to write '{path}'.");
        }

        /// <summary>The writer for the file version, or the oldest one newer than it.</summary>
        internal static string WriterVersion(int major, int minor)
        {
            string best = WriterVersions[WriterVersions.Length - 1].writer;
            foreach (var (ma, mi, writer) in WriterVersions)
                if (ma > major || (ma == major && mi >= minor)) best = writer;
            return best;
        }

        static bool IsBinary(string path)
        {
            var head = new byte[18];
            using (var stream = File.OpenRead(path))
                if (stream.Read(head, 0, head.Length) != head.Length) return false;
            return System.Text.Encoding.ASCII.GetString(head) == "Kaydara FBX Binary";
        }

        static void CollectMeshes(FbxNode node, List<FbxMesh> meshes)
        {
            if (node == null) return;
            var mesh = node.GetMesh();
            if (mesh != null && !meshes.Contains(mesh)) meshes.Add(mesh);
            for (int i = 0; i < node.GetChildCount(); i++) CollectMeshes(node.GetChild(i), meshes);
        }

        public void Dispose()
        {
            Manager.Destroy();
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (IOException) { /* a leftover temp folder is harmless */ }
        }
    }

    /// <summary>Per-corner access to an FBX mesh's UV sets and vertex colours.</summary>
    internal static class FbxLayerChannels
    {
        internal const int MaxUnityUvChannels = 8;

        static readonly FbxLayerElement.EType[] UvTypes =
        {
            FbxLayerElement.EType.eTextureDiffuse, FbxLayerElement.EType.eTextureDiffuseFactor,
            FbxLayerElement.EType.eTextureEmissive, FbxLayerElement.EType.eTextureEmissiveFactor,
            FbxLayerElement.EType.eTextureAmbient, FbxLayerElement.EType.eTextureAmbientFactor,
            FbxLayerElement.EType.eTextureSpecular, FbxLayerElement.EType.eTextureSpecularFactor,
            FbxLayerElement.EType.eTextureShininess, FbxLayerElement.EType.eTextureNormalMap,
            FbxLayerElement.EType.eTextureBump, FbxLayerElement.EType.eTextureTransparency,
            FbxLayerElement.EType.eTextureTransparencyFactor, FbxLayerElement.EType.eTextureReflection,
            FbxLayerElement.EType.eTextureReflectionFactor, FbxLayerElement.EType.eTextureDisplacement,
            FbxLayerElement.EType.eTextureDisplacementVector,
        };

        /// <summary>The mesh's polygon structure: corner count per polygon and control point per corner.</summary>
        internal readonly struct Topology
        {
            public readonly int[] polygonSizes, cornerPolygon, cornerControlPoint;
            public readonly int controlPointCount;
            public int CornerCount => cornerControlPoint.Length;

            public Topology(FbxMesh mesh)
            {
                int polygons = mesh.GetPolygonCount();
                polygonSizes = new int[polygons];
                int corners = 0;
                for (int p = 0; p < polygons; p++) corners += polygonSizes[p] = mesh.GetPolygonSize(p);
                cornerPolygon = new int[corners];
                cornerControlPoint = new int[corners];
                for (int p = 0, c = 0; p < polygons; p++)
                    for (int k = 0; k < polygonSizes[p]; k++, c++)
                    {
                        cornerPolygon[c] = p;
                        cornerControlPoint[c] = mesh.GetPolygonVertex(p, k);
                    }
                controlPointCount = mesh.GetControlPointsCount();
            }
        }

        /// <summary>UV sets in the order Unity's importer numbers them (layer, then texture type).</summary>
        internal static List<FbxLayerElementUV> UvElements(FbxMesh mesh)
        {
            var list = new List<FbxLayerElementUV>();
            for (int l = 0; l < mesh.GetLayerCount(); l++)
            {
                var layer = mesh.GetLayer(l);
                if (layer == null) continue;
                foreach (var type in UvTypes)
                {
                    var uv = layer.GetUVs(type);
                    if (uv != null) list.Add(uv);
                }
            }
            return list;
        }

        /// <summary>
        /// The colour set Unity imports: layer 0's, or null. Unity reads vertex colours from
        /// layer 0 only (TS_UnityExport_SDK FBX_PIPELINE_CHECKLIST I4); a set on another
        /// layer is invisible to it and is left alone.
        /// </summary>
        internal static FbxLayerElementVertexColor ColorElement(FbxMesh mesh)
            => mesh.GetLayerCount() > 0 ? mesh.GetLayer(0)?.GetVertexColors() : null;

        /// <summary>
        /// Adds a UV set holding (corner index, -(ordinal + 1)) per corner so a Unity import
        /// of the file tells which FBX corner every vertex came from; returns the Unity UV
        /// channel the tag lands in. Only for the throwaway tagged copy.
        /// </summary>
        internal static int AddCornerTag(FbxMesh mesh, int ordinal)
        {
            var topology = new Topology(mesh);
            if (topology.CornerCount >= 1 << 24)
                throw new InvalidOperationException($"Mesh '{mesh.GetName()}' has too many corners to tag exactly.");
            var existing = UvElements(mesh);
            FbxLayerElementUV tag;
            int channel;
            if (existing.Count < MaxUnityUvChannels)
            {
                channel = existing.Count;
                tag = FbxLayerElementUV.Create(mesh, "MeshLabCornerTag");
                LayerAfterLastUv(mesh).SetUVs(tag, FbxLayerElement.EType.eTextureDiffuse);
            }
            else
            {
                // All eight channels are taken; the copy sacrifices the last one.
                channel = MaxUnityUvChannels - 1;
                tag = existing[channel];
            }
            tag.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            tag.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);
            var direct = tag.GetDirectArray();
            direct.SetCount(topology.CornerCount);
            for (int c = 0; c < topology.CornerCount; c++) direct.SetAt(c, new FbxVector2(c, -(ordinal + 1)));
            tag.GetIndexArray().SetCount(0);
            return channel;
        }

        /// <summary>Per-corner values (2 per corner) of a UV set.</summary>
        internal static double[] ReadUv(FbxLayerElementUV element, in Topology topology)
        {
            var direct = element.GetDirectArray();
            var slots = DirectSlots(element, element.GetIndexArray(), direct.GetCount(), topology);
            var values = new double[slots.Length * 2];
            for (int c = 0; c < slots.Length; c++)
            {
                var v = direct.GetAt(slots[c]);
                values[c * 2] = v.X; values[c * 2 + 1] = v.Y;
            }
            return values;
        }

        /// <summary>Per-corner values (4 per corner) of a colour set.</summary>
        internal static double[] ReadColor(FbxLayerElementVertexColor element, in Topology topology)
        {
            var direct = element.GetDirectArray();
            var slots = DirectSlots(element, element.GetIndexArray(), direct.GetCount(), topology);
            var values = new double[slots.Length * 4];
            for (int c = 0; c < slots.Length; c++)
            {
                var v = direct.GetAt(slots[c]);
                values[c * 4] = v.mRed; values[c * 4 + 1] = v.mGreen; values[c * 4 + 2] = v.mBlue; values[c * 4 + 3] = v.mAlpha;
            }
            return values;
        }

        /// <summary>
        /// Writes the changed corners of UV set <paramref name="channel"/> (Unity numbering).
        /// Unchanged corners keep their exact stored values. The set is created when it is
        /// the next channel after the existing ones. Returns the number of corners written.
        /// </summary>
        internal static int WriteUv(FbxMesh mesh, int channel, in Topology topology, double[] values, bool[] changed)
        {
            var sets = UvElements(mesh);
            FbxLayerElementUV element;
            if (channel < sets.Count) element = sets[channel];
            else if (channel == sets.Count && channel < MaxUnityUvChannels)
            {
                element = FbxLayerElementUV.Create(mesh, "UVChannel_" + (channel + 1));
                element.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
                element.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
                LayerAfterLastUv(mesh).SetUVs(element, FbxLayerElement.EType.eTextureDiffuse);
            }
            else throw new InvalidOperationException(
                $"Mesh '{mesh.GetName()}' has {sets.Count} UV set(s); UV{channel} cannot be added without inventing the channels before it.");
            var direct = element.GetDirectArray();
            return Write(element, direct, element.GetIndexArray(), direct.GetCount(), topology, values, changed, 2,
                i => { var v = direct.GetAt(i); return new[] { v.X, v.Y }; },
                v => direct.Add(new FbxVector2(v[0], v[1])),
                (i, v) => direct.SetAt(i, new FbxVector2(v[0], v[1])));
        }

        /// <summary>Writes the changed corners of layer 0's colour set, creating it there when layer 0 has none.</summary>
        internal static int WriteColor(FbxMesh mesh, in Topology topology, double[] values, bool[] changed)
        {
            var element = ColorElement(mesh);
            if (element == null)
            {
                element = FbxLayerElementVertexColor.Create(mesh, "VertexColors");
                element.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
                element.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
                Layer(mesh, 0).SetVertexColors(element);
            }
            var direct = element.GetDirectArray();
            return Write(element, direct, element.GetIndexArray(), direct.GetCount(), topology, values, changed, 4,
                i => { var v = direct.GetAt(i); return new[] { v.mRed, v.mGreen, v.mBlue, v.mAlpha }; },
                v => direct.Add(new FbxColor(v[0], v[1], v[2], v[3])),
                (i, v) => direct.SetAt(i, new FbxColor(v[0], v[1], v[2], v[3])));
        }

        // ── Shared element logic ──

        // Index into the direct array for every corner, by the element's mapping and reference modes.
        static int[] DirectSlots(FbxLayerElement element, FbxLayerElementArrayTemplateInt index, int directCount, in Topology topology)
        {
            var mapping = element.GetMappingMode();
            var reference = element.GetReferenceMode();
            int corners = topology.CornerCount;
            var slots = new int[corners];
            int indexCount = reference == FbxLayerElement.EReferenceMode.eDirect ? 0 : index.GetCount();
            for (int c = 0; c < corners; c++)
            {
                int slot;
                switch (mapping)
                {
                    case FbxLayerElement.EMappingMode.eByPolygonVertex: slot = c; break;
                    case FbxLayerElement.EMappingMode.eByControlPoint: slot = topology.cornerControlPoint[c]; break;
                    case FbxLayerElement.EMappingMode.eByPolygon: slot = topology.cornerPolygon[c]; break;
                    case FbxLayerElement.EMappingMode.eAllSame: slot = 0; break;
                    default: throw new InvalidDataException($"Unsupported layer element mapping {mapping}.");
                }
                if (reference != FbxLayerElement.EReferenceMode.eDirect)
                {
                    if (slot >= indexCount) throw new InvalidDataException("Layer element index array is shorter than its mapping requires.");
                    slot = index.GetAt(slot);
                }
                if (slot < 0 || slot >= directCount) throw new InvalidDataException("Layer element references a value outside its direct array.");
                slots[c] = slot;
            }
            return slots;
        }

        static int Write(FbxLayerElement element, FbxLayerElementArray direct, FbxLayerElementArrayTemplateInt index, int directCount,
            in Topology topology, double[] values, bool[] changed, int arity,
            Func<int, double[]> get, Func<double[], int> add, Action<int, double[]> set)
        {
            int corners = topology.CornerCount;
            if (values.Length != corners * arity || changed.Length != corners)
                throw new ArgumentException("Channel values do not match the mesh's corners.");
            int written = 0;
            for (int c = 0; c < corners; c++) if (changed[c]) written++;
            if (written == 0) return 0;

            bool fresh = directCount == 0;
            if (fresh && written != corners)
                throw new InvalidOperationException("A new layer element needs a value at every corner.");
            var mapping = element.GetMappingMode();
            var reference = element.GetReferenceMode();
            int[] slots = fresh ? null : DirectSlots(element, index, directCount, topology);

            // Already per corner and stored directly: overwrite the changed corners in place.
            if (!fresh && mapping == FbxLayerElement.EMappingMode.eByPolygonVertex && reference == FbxLayerElement.EReferenceMode.eDirect)
            {
                for (int c = 0; c < corners; c++) if (changed[c]) set(c, Slice(values, c, arity));
                return written;
            }

            // Per control point and every corner of each touched control point changed to one
            // value: keep the mapping and update the control point's value.
            if (!fresh && mapping == FbxLayerElement.EMappingMode.eByControlPoint && ConsistentPerControlPoint(topology, values, changed, arity, out var perPoint))
            {
                foreach (var kv in perPoint)
                {
                    if (reference == FbxLayerElement.EReferenceMode.eDirect) set(kv.Key, kv.Value);
                    else index.SetAt(kv.Key, add(kv.Value));
                }
                return written;
            }

            // Anything else becomes per corner, indexed: the existing values stay where they
            // are, unchanged corners keep pointing at them, changed corners point at new ones.
            // Corners share an entry only on the same control point with the same value — a
            // continuous UV there; equal values on different points (overlapping or mirrored
            // shells) stay separate, or DCC importers would weld those islands together.
            var shared = new Dictionary<(int point, ValueKey value), int>();
            var cornerIndex = new int[corners];
            for (int c = 0; c < corners; c++)
            {
                if (changed[c]) continue;
                cornerIndex[c] = slots[c];
                shared[(topology.cornerControlPoint[c], new ValueKey(get(slots[c])))] = slots[c];
            }
            for (int c = 0; c < corners; c++)
            {
                if (!changed[c]) continue;
                var value = Slice(values, c, arity);
                var key = (topology.cornerControlPoint[c], new ValueKey(value));
                if (!shared.TryGetValue(key, out int at)) shared[key] = at = add(value);
                cornerIndex[c] = at;
            }
            element.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            element.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            index.SetCount(corners);
            for (int c = 0; c < corners; c++) index.SetAt(c, cornerIndex[c]);
            return written;
        }

        static bool ConsistentPerControlPoint(in Topology topology, double[] values, bool[] changed, int arity, out Dictionary<int, double[]> perPoint)
        {
            perPoint = new Dictionary<int, double[]>();
            var untouched = new HashSet<int>();
            for (int c = 0; c < topology.CornerCount; c++)
            {
                int cp = topology.cornerControlPoint[c];
                if (!changed[c]) { untouched.Add(cp); continue; }
                var value = Slice(values, c, arity);
                if (perPoint.TryGetValue(cp, out var existing))
                {
                    if (!new ValueKey(existing).Equals(new ValueKey(value))) return false;
                }
                else perPoint[cp] = value;
            }
            foreach (int cp in perPoint.Keys) if (untouched.Contains(cp)) return false;
            return true;
        }

        static double[] Slice(double[] values, int corner, int arity)
        {
            var v = new double[arity];
            Array.Copy(values, corner * arity, v, 0, arity);
            return v;
        }

        static FbxLayer Layer(FbxMesh mesh, int index)
        {
            while (mesh.GetLayerCount() <= index)
                if (mesh.CreateLayer() < 0) throw new InvalidOperationException($"Cannot create layer {index} on '{mesh.GetName()}'.");
            return mesh.GetLayer(index);
        }

        // The layer after the last one holding a UV set: a new set there is numbered after all existing ones.
        static FbxLayer LayerAfterLastUv(FbxMesh mesh)
        {
            int last = -1;
            for (int l = 0; l < mesh.GetLayerCount(); l++)
            {
                var layer = mesh.GetLayer(l);
                if (layer == null) continue;
                foreach (var type in UvTypes)
                    if (layer.GetUVs(type) != null) { last = l; break; }
            }
            return Layer(mesh, last + 1);
        }

        readonly struct ValueKey : IEquatable<ValueKey>
        {
            readonly long a, b, c, d;
            public ValueKey(double[] v)
            {
                a = BitConverter.DoubleToInt64Bits(v[0]); b = BitConverter.DoubleToInt64Bits(v[1]);
                c = v.Length > 2 ? BitConverter.DoubleToInt64Bits(v[2]) : 0;
                d = v.Length > 3 ? BitConverter.DoubleToInt64Bits(v[3]) : 0;
            }
            public bool Equals(ValueKey o) => a == o.a && b == o.b && c == o.c && d == o.d;
            public override bool Equals(object o) => o is ValueKey k && Equals(k);
            public override int GetHashCode() => unchecked((a.GetHashCode() * 397) ^ (b.GetHashCode() * 31) ^ c.GetHashCode() ^ (d.GetHashCode() * 17));
        }
    }
}
#endif
