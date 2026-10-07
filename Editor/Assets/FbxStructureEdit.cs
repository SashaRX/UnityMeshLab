// FbxStructureEdit.cs — adds and replaces mesh nodes in a loaded FBX document through
// the FBX SDK. A new node is a sibling of the node its geometry was made from, with the
// same transform (local TRS, rotation order, pivots, pre/post rotation, geometric
// transform, inherit type) and the same materials, so it lands where that node is and
// is grouped with it by name. Nothing else in the document is renamed, moved or removed;
// a node is removed only when a new one takes its name.
//
// No UnityEngine types: the Unity side is FbxStructureWrite.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using Autodesk.Fbx;

namespace SashaRX.UnityMeshLab
{
    /// <summary>A structure change the FBX document cannot take as asked; the file is left as it was.</summary>
    internal sealed class FbxStructureRefusalException : InvalidOperationException
    {
        public FbxStructureRefusalException(string message) : base(message) { }
    }

    internal static class FbxStructureEdit
    {
        // ── Reading ──

        /// <summary>xyz per control point.</summary>
        internal static double[] ControlPoints(FbxMesh mesh)
        {
            int count = mesh.GetControlPointsCount();
            var points = new double[count * 3];
            for (int i = 0; i < count; i++)
            {
                var p = mesh.GetControlPointAt(i);
                points[i * 3] = p.X; points[i * 3 + 1] = p.Y; points[i * 3 + 2] = p.Z;
            }
            return points;
        }

        /// <summary>The node material index of each polygon (layer 0's material element); -1 where the mesh has none.</summary>
        internal static int[] PolygonMaterials(FbxMesh mesh)
        {
            int polygons = mesh.GetPolygonCount();
            var result = new int[polygons];
            var element = mesh.GetLayerCount() > 0 ? mesh.GetLayer(0)?.GetMaterials() : null;
            var index = element?.GetIndexArray();
            int count = index?.GetCount() ?? 0;
            for (int p = 0; p < polygons; p++)
            {
                if (element == null || count == 0) { result[p] = -1; continue; }
                int slot = element.GetMappingMode() == FbxLayerElement.EMappingMode.eAllSame ? 0 : p;
                result[p] = slot < count ? index.GetAt(slot) : -1;
            }
            return result;
        }

        internal static bool HasDeformers(FbxMesh mesh) => mesh.GetDeformerCount() > 0;

        /// <summary>The node showing <paramref name="mesh"/>: the one named <paramref name="name"/> when it is instanced, else its only node; null when that is ambiguous.</summary>
        internal static FbxNode NodeOf(FbxMesh mesh, string name)
        {
            FbxNode only = null;
            int count = 0;
            for (int i = 0; i < mesh.GetNodeCount(); i++)
            {
                var node = mesh.GetNode(i);
                if (node == null) continue;
                if (node.GetName() == name) return node;
                only = node;
                count++;
            }
            return count == 1 ? only : null;
        }

        /// <summary>Every node named <paramref name="name"/> below the scene root.</summary>
        internal static List<FbxNode> FindNodes(FbxScene scene, string name)
        {
            var found = new List<FbxNode>();
            Collect(scene.GetRootNode(), name, found);
            return found;
        }

        /// <summary>The direct children of <paramref name="parent"/> named <paramref name="name"/>.</summary>
        internal static List<FbxNode> ChildrenNamed(FbxNode parent, string name)
        {
            var found = new List<FbxNode>();
            if (parent == null) return found;
            for (int i = 0; i < parent.GetChildCount(); i++)
            {
                var child = parent.GetChild(i);
                if (child.GetName() == name) found.Add(child);
            }
            return found;
        }

        static void Collect(FbxNode node, string name, List<FbxNode> found)
        {
            for (int i = 0; i < node.GetChildCount(); i++)
            {
                var child = node.GetChild(i);
                if (child.GetName() == name) found.Add(child);
                Collect(child, name, found);
            }
        }

        /// <summary>
        /// True when <paramref name="node"/> is the scene's only top-level node: Unity imports
        /// such a node as the model's root object itself, so a sibling next to it would change
        /// the imported hierarchy.
        /// </summary>
        internal static bool IsSoleTopNode(FbxScene scene, FbxNode node)
        {
            var root = scene.GetRootNode();
            return node.GetParent() == root && root.GetChildCount() == 1;
        }

        // ── Writing ──

        /// <summary>A new mesh in <paramref name="scene"/> holding <paramref name="data"/>; normals, materials and colours on layer 0, UV set i on layer i.</summary>
        internal static FbxMesh CreateMesh(FbxScene scene, string name, FbxMeshData data)
        {
            var mesh = FbxMesh.Create(scene, name);
            int points = data.controlPoints.Length / 3;
            mesh.InitControlPoints(points);
            for (int i = 0; i < points; i++)
                mesh.SetControlPointAt(new FbxVector4(data.controlPoints[i * 3], data.controlPoints[i * 3 + 1], data.controlPoints[i * 3 + 2]), i);
            for (int p = 0, c = 0; p < data.polygonSizes.Length; p++)
            {
                mesh.BeginPolygon(-1, -1, -1, false);
                for (int k = 0; k < data.polygonSizes[p]; k++) mesh.AddPolygon(data.polygonVertices[c++]);
                mesh.EndPolygon();
            }
            var cornerPoint = data.polygonVertices;

            if (data.normals != null)
            {
                var normals = FbxLayerElementNormal.Create(mesh, "Normals");
                FbxLayerChannels.Layer(mesh, 0).SetNormals(normals);
                var direct = normals.GetDirectArray();
                FillIndexed(normals, normals.GetIndexArray(), cornerPoint, data.normals, 3, v => direct.Add(new FbxVector4(v[0], v[1], v[2], 0)));
            }
            if (data.tangents != null && data.binormals != null)
            {
                var tangents = FbxLayerElementTangent.Create(mesh, "Tangents");
                FbxLayerChannels.Layer(mesh, 0).SetTangents(tangents);
                var tangentArray = tangents.GetDirectArray();
                FillIndexed(tangents, tangents.GetIndexArray(), cornerPoint, data.tangents, 3, v => tangentArray.Add(new FbxVector4(v[0], v[1], v[2], 0)));
                var binormals = FbxLayerElementBinormal.Create(mesh, "Binormals");
                FbxLayerChannels.Layer(mesh, 0).SetBinormals(binormals);
                var binormalArray = binormals.GetDirectArray();
                FillIndexed(binormals, binormals.GetIndexArray(), cornerPoint, data.binormals, 3, v => binormalArray.Add(new FbxVector4(v[0], v[1], v[2], 0)));
            }
            if (data.polygonMaterials != null && data.polygonMaterials.Length > 0)
                WriteMaterials(mesh, data.polygonMaterials);
            for (int set = 0; set < data.uvs.Count; set++)
            {
                var uv = FbxLayerElementUV.Create(mesh, data.uvNames[set]);
                FbxLayerChannels.Layer(mesh, set).SetUVs(uv, FbxLayerElement.EType.eTextureDiffuse);
                var direct = uv.GetDirectArray();
                FillIndexed(uv, uv.GetIndexArray(), cornerPoint, data.uvs[set], 2, v => direct.Add(new FbxVector2(v[0], v[1])));
            }
            if (data.colors != null)
            {
                var colors = FbxLayerElementVertexColor.Create(mesh, "VertexColors");
                FbxLayerChannels.Layer(mesh, 0).SetVertexColors(colors);
                var direct = colors.GetDirectArray();
                FillIndexed(colors, colors.GetIndexArray(), cornerPoint, data.colors, 4, v => direct.Add(new FbxColor(v[0], v[1], v[2], v[3])));
            }
            return mesh;
        }

        static void WriteMaterials(FbxMesh mesh, int[] polygonMaterials)
        {
            var element = FbxLayerElementMaterial.Create(mesh, "Material");
            FbxLayerChannels.Layer(mesh, 0).SetMaterials(element);
            element.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            var index = element.GetIndexArray();
            bool same = Array.TrueForAll(polygonMaterials, m => m == polygonMaterials[0]);
            if (same)
            {
                element.SetMappingMode(FbxLayerElement.EMappingMode.eAllSame);
                index.SetCount(1);
                index.SetAt(0, polygonMaterials[0]);
                return;
            }
            element.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygon);
            index.SetCount(polygonMaterials.Length);
            for (int p = 0; p < polygonMaterials.Length; p++) index.SetAt(p, polygonMaterials[p]);
        }

        // Per corner, indexed; corners share an entry only on the same control point with the
        // same value (the convention FbxLayerChannels keeps for edited sets).
        static void FillIndexed(FbxLayerElement element, FbxLayerElementArrayTemplateInt index, int[] cornerPoint, double[] values, int arity, Func<double[], int> add)
        {
            element.SetMappingMode(FbxLayerElement.EMappingMode.eByPolygonVertex);
            element.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            var shared = new Dictionary<(int, FbxLayerChannels.ValueKey), int>();
            index.SetCount(cornerPoint.Length);
            for (int c = 0; c < cornerPoint.Length; c++)
            {
                var value = new double[arity];
                Array.Copy(values, c * arity, value, 0, arity);
                var key = (cornerPoint[c], new FbxLayerChannels.ValueKey(value));
                if (!shared.TryGetValue(key, out int at)) shared[key] = at = add(value);
                index.SetAt(c, at);
            }
        }

        /// <summary>
        /// A new node named <paramref name="name"/> next to <paramref name="template"/> (same
        /// parent) with its transform and, when <paramref name="geometric"/>, its geometric
        /// transform. Throws when the copy does not evaluate to the template's placement.
        /// </summary>
        internal static FbxNode AddSibling(FbxNode template, string name, bool geometric)
        {
            var parent = template.GetParent() ?? throw new FbxStructureRefusalException($"'{template.GetName()}' has no parent node");
            var node = FbxNode.Create(template.GetScene(), name);
            CopyTransform(template, node, geometric);
            parent.AddChild(node);
            if (!SamePlacement(template, node, geometric))
                throw new FbxStructureRefusalException($"'{name}' does not land where '{template.GetName()}' is after copying its transform");
            return node;
        }

        /// <summary>Whether the node's geometric scaling (baked into its import) is the same on every axis.</summary>
        internal static bool UniformGeometricScaling(FbxNode node)
        {
            var s = node.GetGeometricScaling(FbxNode.EPivotSet.eSourcePivot);
            double largest = Math.Max(Math.Abs(s.X), Math.Max(Math.Abs(s.Y), Math.Abs(s.Z)));
            return Math.Abs(s.X - s.Y) <= 1e-6 * largest && Math.Abs(s.Y - s.Z) <= 1e-6 * largest;
        }

        /// <summary>
        /// Whether an animation curve moves the node's local translation, rotation or scaling
        /// away from its value. A sibling copies the values, not the curves, so it would stay
        /// behind. Curves that only hold the value (exporters key every node) do not count.
        /// </summary>
        internal static bool HasTransformAnimation(FbxNode node)
            => Animated(node.LclTranslation) || Animated(node.LclRotation) || Animated(node.LclScaling);

        // Curves this cannot read (another stack or layer than the current one) count as animation.
        static bool Animated(FbxPropertyDouble3 property)
        {
            int sources = property.GetSrcObjectCount();
            if (sources == 0) return false;
            var curveNode = sources == 1 ? property.GetCurveNode() : null;
            if (curveNode == null) return true;
            var value = property.Get();
            for (uint channel = 0; channel < curveNode.GetChannelsCount() && channel < 3; channel++)
            {
                double rest = channel == 0 ? value.X : channel == 1 ? value.Y : value.Z;
                for (int i = 0; i < curveNode.GetCurveCount(channel); i++)
                {
                    var curve = curveNode.GetCurve(channel, (uint)i);
                    if (curve == null) continue;
                    for (int k = 0; k < curve.KeyGetCount(); k++)
                        if (Math.Abs(curve.KeyGetValue(k) - rest) > 1e-5 * Math.Max(1, Math.Abs(rest))) return true;
                }
            }
            return false;
        }

        /// <summary>A child of <paramref name="parent"/> with an identity local transform and <paramref name="geometricFrom"/>'s geometric transform.</summary>
        internal static FbxNode AddChild(FbxNode parent, string name, FbxNode geometricFrom)
        {
            var node = FbxNode.Create(parent.GetScene(), name);
            if (geometricFrom != null) CopyGeometric(geometricFrom, node);
            parent.AddChild(node);
            return node;
        }

        /// <summary>Gives <paramref name="node"/> the materials of <paramref name="from"/> listed in <paramref name="indices"/>, in that order.</summary>
        internal static void AddMaterials(FbxNode node, FbxNode from, IEnumerable<int> indices)
        {
            bool any = false;
            foreach (int i in indices)
            {
                var material = from.GetMaterial(i);
                if (material == null) continue;
                node.AddMaterial(material);
                any = true;
            }
            // A node with materials renders textured only in texture shading (checklist rule).
            if (any) node.SetShadingMode(FbxNode.EShadingMode.eTextureShading);
        }

        /// <summary>The scene's material named <paramref name="name"/>, or a new one of that name.</summary>
        internal static FbxSurfaceMaterial SceneMaterial(FbxScene scene, string name)
            => scene.GetMaterial(name) ?? FbxSurfacePhong.Create(scene, name);

        /// <summary>
        /// A new Phong material with a diffuse colour and, when <paramref name="textureFile"/> is
        /// given, a file texture on its diffuse (absolute and relative paths, as the pipeline's
        /// material rules require), so the file renders textured outside Unity too.
        /// </summary>
        internal static FbxSurfacePhong NewMaterial(FbxScene scene, string name, double r, double g, double b,
            string textureName, string textureFile, string relativeTextureFile)
        {
            var material = FbxSurfacePhong.Create(scene, name);
            material.Diffuse.Set(new FbxDouble3(r, g, b));
            if (string.IsNullOrEmpty(textureFile)) return material;
            var texture = FbxFileTexture.Create(scene, textureName);
            texture.SetFileName(textureFile);
            texture.SetRelativeFileName(relativeTextureFile ?? textureFile);
            texture.SetTextureUse(FbxTexture.ETextureUse.eStandard);
            texture.SetMappingType(FbxTexture.EMappingType.eUV);
            texture.ConnectDstProperty(material.Diffuse);
            return material;
        }

        /// <summary>
        /// Puts <paramref name="material"/> into slot <paramref name="slot"/> of <paramref name="node"/>;
        /// the other slots keep their materials and order (the polygons' material indices stay valid).
        /// </summary>
        internal static void SetMaterial(FbxNode node, int slot, FbxSurfaceMaterial material)
            => SetMaterials(node, new Dictionary<int, FbxSurfaceMaterial> { [slot] = material });

        /// <summary>
        /// Puts each material into its slot of <paramref name="node"/> in one step, so slots can
        /// exchange materials; refuses when a slot would end up holding a material that another
        /// slot of the node also holds.
        /// </summary>
        internal static void SetMaterials(FbxNode node, IDictionary<int, FbxSurfaceMaterial> bySlot)
        {
            int count = node.GetMaterialCount();
            var list = new List<FbxSurfaceMaterial>(count);
            for (int i = 0; i < count; i++) list.Add(node.GetMaterial(i));
            var final = new List<FbxSurfaceMaterial>(list);
            foreach (var kv in bySlot)
            {
                if (kv.Key < 0 || kv.Key >= count) throw new ArgumentOutOfRangeException(nameof(bySlot));
                final[kv.Key] = kv.Value;
            }
            foreach (int slot in bySlot.Keys)
                for (int i = 0; i < count; i++)
                    if (i != slot && final[i].GetName() == final[slot].GetName())
                        throw new FbxStructureRefusalException($"'{node.GetName()}': material '{final[slot].GetName()}' would fill slots {Math.Min(i, slot)} and {Math.Max(i, slot)}; one node cannot hold a material in two slots");
            // A node's material order is its connection order: reconnect them all in order.
            foreach (var m in list) node.DisconnectSrcObject(m);
            foreach (var m in final) node.AddMaterial(m);
        }

        /// <summary>
        /// Removes <paramref name="node"/> with its children; their meshes go with them when
        /// no other node shows them.
        /// </summary>
        internal static void RemoveSubtree(FbxNode node)
        {
            for (int i = node.GetChildCount() - 1; i >= 0; i--) RemoveSubtree(node.GetChild(i));
            var attribute = node.GetNodeAttribute();
            // The mesh goes first, while the node still tells who else shows it.
            if (attribute != null && attribute.GetNodeCount() == 1) attribute.Destroy();
            node.GetParent()?.RemoveChild(node);
            node.Destroy();
        }

        /// <summary>Points every node showing <paramref name="old"/> at <paramref name="replacement"/> and destroys <paramref name="old"/>.</summary>
        internal static int ReplaceMesh(FbxMesh old, FbxMesh replacement)
        {
            var nodes = new List<FbxNode>();
            for (int i = 0; i < old.GetNodeCount(); i++) nodes.Add(old.GetNode(i));
            foreach (var node in nodes) node.SetNodeAttribute(replacement);
            old.Destroy();
            return nodes.Count;
        }

        // ── Transforms ──

        static void CopyTransform(FbxNode from, FbxNode to, bool geometric)
        {
            const FbxNode.EPivotSet pivot = FbxNode.EPivotSet.eSourcePivot;
            to.LclTranslation.Set(from.LclTranslation.Get());
            to.LclRotation.Set(from.LclRotation.Get());
            to.LclScaling.Set(from.LclScaling.Get());
            from.GetRotationOrder(pivot, out int order);
            to.SetRotationOrder(pivot, (FbxEuler.EOrder)order);
            to.SetRotationActive(from.GetRotationActive());
            to.SetRotationOffset(pivot, from.GetRotationOffset(pivot));
            to.SetRotationPivot(pivot, from.GetRotationPivot(pivot));
            to.SetPreRotation(pivot, from.GetPreRotation(pivot));
            to.SetPostRotation(pivot, from.GetPostRotation(pivot));
            to.SetScalingOffset(pivot, from.GetScalingOffset(pivot));
            to.SetScalingPivot(pivot, from.GetScalingPivot(pivot));
            to.InheritType.Set(from.InheritType.Get());
            if (geometric) CopyGeometric(from, to);
        }

        static void CopyGeometric(FbxNode from, FbxNode to)
        {
            const FbxNode.EPivotSet pivot = FbxNode.EPivotSet.eSourcePivot;
            to.SetGeometricTranslation(pivot, from.GetGeometricTranslation(pivot));
            to.SetGeometricRotation(pivot, from.GetGeometricRotation(pivot));
            to.SetGeometricScaling(pivot, from.GetGeometricScaling(pivot));
        }

        /// <summary>Whether <paramref name="b"/> evaluates to the global transform of <paramref name="a"/> (and its geometric transform, when asked).</summary>
        internal static bool SamePlacement(FbxNode a, FbxNode b, bool geometric)
        {
            var ma = a.EvaluateGlobalTransform();
            var mb = b.EvaluateGlobalTransform();
            double scale = 1;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) scale = Math.Max(scale, Math.Abs(ma.Get(r, c)));
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    if (Math.Abs(ma.Get(r, c) - mb.Get(r, c)) > 1e-9 * scale) return false;
            if (!geometric) return true;
            const FbxNode.EPivotSet pivot = FbxNode.EPivotSet.eSourcePivot;
            return a.GetGeometricTranslation(pivot) == b.GetGeometricTranslation(pivot)
                && a.GetGeometricRotation(pivot) == b.GetGeometricRotation(pivot)
                && a.GetGeometricScaling(pivot) == b.GetGeometricScaling(pivot);
        }
    }
}
#endif
