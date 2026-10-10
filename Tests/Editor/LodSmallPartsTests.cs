using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using Autodesk.Fbx;
#endif

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodSmallPartsTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();
        GameObject root;
        string assetFolder;
        Mesh Track(Mesh mesh) { meshes.Add(mesh); return mesh; }
        [TearDown] public void Cleanup()
        {
            if (root) Object.DestroyImmediate(root);
            foreach (var mesh in meshes) if (mesh && !AssetDatabase.Contains(mesh)) Object.DestroyImmediate(mesh);
            meshes.Clear();
            if (assetFolder != null) AssetDatabase.DeleteAsset(assetFolder);
            assetFolder = null;
        }

        Mesh Model(bool triangles = false)
        {
            var v = new List<Vector3>(); var q = new List<int>();
            for (int y = 0; y <= 4; y++) for (int x = 0; x <= 4; x++) v.Add(new Vector3(x*2.5f,y*2.5f,x*.25f));
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
            { int a = y*5+x; q.AddRange(new[] {a,a+1,a+6,a+5}); }
            v.AddRange(new[] {new Vector3(4,4,.5f),new Vector3(4.01f,4,.5f),new Vector3(4.01f,4.01f,.5f),new Vector3(4,4.01f,.5f)});
            q.AddRange(new[] {25,26,27,28});
            var mesh = Track(new Mesh { name = "Parts", vertices = v.ToArray() });
            mesh.SetIndices(q.ToArray(),MeshTopology.Quads,0);
            if (triangles) mesh.SetTriangles(LodMeshData.Triangles(mesh,0),0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            mesh.colors = v.Select(p => new Color(p.x/10,p.y/10,.1234567f,.4567891f)).ToArray();
            mesh.SetUVs(2,v.Select(p => new Vector4(p.x,p.y,p.z,4)).ToList());
            MeshUvState.SetDraft(mesh,true);
            return mesh;
        }
        static LodSmallParts.Settings Settings() => new LodSmallParts.Settings { enabled = true, firstLod = 2,
            screenHeight = 1080, maxPixels = 2, maxAreaFraction = .02f, maxTriangleFraction = .2f };
        static LodSmallParts.Analysis Analyze(Mesh m) => LodSmallParts.Analyze(new MeshEntry { fbxMesh = m },m,new Dictionary<string,object>());
        static LodSmallParts.Plan Select(LodSmallParts.Analysis a,LodSmallParts.Settings s,int lod = 2)
            => LodSmallParts.Select(a,s,lod,.25f,Matrix4x4.identity,10);

        [TestCase(false)] [TestCase(true)]
        public void WholePartRemovalKeepsSourceAndEveryRetainedChannel(bool triangles)
        {
            Mesh mesh = Model(triangles); var sourceIndices = mesh.GetIndices(0); var analysis = Analyze(mesh);
            Assert.That(analysis.parts,Has.Length.EqualTo(2)); Assert.That(analysis.Matches(mesh),Is.True);
            var plan = Select(analysis,Settings()); Assert.That(plan.removed,Has.Count.EqualTo(1)); Assert.That(plan.triangles,Is.EqualTo(2));
            var retained = LodSmallParts.Retain(analysis,plan); Track(retained.data.source);
            Assert.That(LodMeshData.TriangleCount(retained.data.source),Is.EqualTo(32));
            Assert.That(retained.data.source.GetVertexAttributeFormat(VertexAttribute.Color),Is.EqualTo(VertexAttributeFormat.Float32));
            Assert.That(retained.data.source.GetVertexAttributeDimension(VertexAttribute.TexCoord2),Is.EqualTo(4));
            Assert.That(MeshUvState.IsDraft(retained.data.source),Is.True);
            CollectionAssert.AreEqual(mesh.colors.Take(25),retained.data.source.colors);
            CollectionAssert.AreEqual(sourceIndices,mesh.GetIndices(0));
            Assert.That(Select(analysis,Settings(),1).removed,Is.Empty);
        }

        [Test]
        public void SeamDuplicatesDoNotBecomeIndependentParts()
        {
            Mesh mesh = Model(true); var v = mesh.vertices.ToList(); var t = mesh.triangles;
            for (int i = 0; i < 3; i++) { v.Add(v[t[i]]); t[i] = v.Count-1; }
            mesh.vertices = v.ToArray(); mesh.triangles = t;
            Assert.That(Analyze(mesh).parts,Has.Length.EqualTo(2));
        }

        [Test]
        public void LossBudgetsAndUserProtectionBlockRemoval()
        {
            var mesh = Model(); var a = Analyze(mesh); var s = Settings();
            s.maxTriangleFraction = .01f; Assert.That(Select(a,s).removed,Is.Empty);
            s = Settings(); s.maxAreaFraction = 0; Assert.That(Select(a,s).removed,Is.Empty);
            s = Settings(); var protection = new LodSmallParts.Protection { analysis = a }; protection.ids.Add(16);
            s.protection = new Dictionary<Mesh,LodSmallParts.Protection> { [mesh] = protection };
            Assert.That(Select(a,s).removed,Is.Empty);
            var v = mesh.vertices; v[0] += Vector3.one; mesh.vertices = v;
            Assert.That(Select(Analyze(mesh),s).note,Does.Contain("geometry changed"));
        }

        [TestCase(false)] [TestCase(true)]
        public void ThinLongPartsAndBoundsExtremaAreRetained(bool extrema)
        {
            var mesh = Model(); var v = mesh.vertices;
            v[25] = new Vector3(extrema ? -1 : 1,4,.5f); v[26] = new Vector3(9,4,.5f);
            v[27] = new Vector3(9,4.001f,.5f); v[28] = new Vector3(extrema ? -1 : 1,4.001f,.5f); mesh.vertices = v;
            var a = Analyze(mesh); var plan = Select(a,Settings()); Assert.That(plan.removed,Is.Empty);
            if (extrema) Assert.That(a.parts.Last().protectedReason,Is.EqualTo("Model bounds extremum"));
        }

        [Test]
        public void MorphMeshesAreProtectedAndScaleDoesNotChangeScreenEstimate()
        {
            var mesh = Model(); var zero = new Vector3[mesh.vertexCount]; mesh.AddBlendShapeFrame("shape",100,zero,zero,zero);
            var a = Analyze(mesh); Assert.That(a.parts.All(p => p.protectedReason == "Deformation channels"),Is.True);
            Assert.That(Select(a,Settings()).removed,Is.Empty);
            float original = LodSmallParts.Pixels(a.parts.Last(),Matrix4x4.identity,10,.25f,1080);
            Assert.That(LodSmallParts.Pixels(a.parts.Last(),Matrix4x4.Scale(Vector3.one*7),70,.25f,1080),Is.EqualTo(original).Within(1e-5));
        }

        [Test]
        public void CancelledAnalysisAndInvalidPreflightPreserveExistingLods()
        {
            Mesh mesh = Model();
            Assert.Throws<System.OperationCanceledException>(() => LodSmallParts.Analyze(new MeshEntry { fbxMesh = mesh },mesh,new Dictionary<string,object>(),() => true));
            root = new GameObject("Parts"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var ctx = new UvToolContext(); ctx.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 1, ratios = new[] {1f}, targetError = .2f, smallParts = Settings() };
            var before = LodPipelineOps.Generate(ctx,1,options);
            foreach (var go in before.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            var lods = ctx.LodGroup.GetLODs(); var s = Settings(); s.maxPixels = float.NaN; options.smallParts = s;
            var result = LodPipelineOps.Generate(ctx,2,options);
            Assert.That(result.ok,Is.False); Assert.That(result.error,Does.Contain("Invalid small-part"));
            Assert.That(ctx.LodGroup.GetLODs().Length,Is.EqualTo(lods.Length));
            Assert.That(before.generatedObjects.All(go => go),Is.True);
        }

        [Test]
        public void UnreadableSourceInvalidatesPreviewWithoutReadingMeshData()
        {
            var mesh = Model(); var analysis = Analyze(mesh); mesh.UploadMeshData(true);
            Assert.That(analysis.Matches(mesh),Is.False);
            Assert.Throws<System.InvalidOperationException>(() => Analyze(mesh));
        }

        [Test]
        public void RetainedMeshKeepsEmptyMaterialSlots()
        {
            Mesh mesh = Model(); int[] q = mesh.GetIndices(0);
            mesh.subMeshCount = 2; mesh.SetIndices(q.Take(64).ToArray(),MeshTopology.Quads,0); mesh.SetIndices(q.Skip(64).ToArray(),MeshTopology.Quads,1);
            var a = Analyze(mesh); var retained = LodSmallParts.Retain(a,Select(a,Settings())); Track(retained.data.source);
            Assert.That(retained.data.source.subMeshCount,Is.EqualTo(2)); Assert.That(retained.data.source.GetIndexCount(1),Is.Zero);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        [Test]
        public void FbxControlPointsKeepCoincidentDisconnectedShellsSeparate()
        {
            assetFolder = "Assets/__LodParts_"+System.Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets",assetFolder.Substring(7));
            string path = assetFolder+"/Coincident.fbx";
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager,Globals.IOSROOT); manager.SetIOSettings(io);
                var scene = FbxScene.Create(manager,"scene"); var node = FbxNode.Create(scene,"Shells"); var geometry = FbxMesh.Create(scene,"Shells");
                geometry.InitControlPoints(6);
                for (int shell = 0; shell < 2; shell++)
                {
                    geometry.SetControlPointAt(new FbxVector4(0,0,0),shell*3);
                    geometry.SetControlPointAt(new FbxVector4(100,0,0),shell*3+1);
                    geometry.SetControlPointAt(new FbxVector4(0,100,0),shell*3+2);
                    geometry.BeginPolygon(); for (int i = 0; i < 3; i++) geometry.AddPolygon(shell*3+i); geometry.EndPolygon();
                }
                node.SetNodeAttribute(geometry); scene.GetRootNode().AddChild(node);
                using var exporter = FbxExporter.Create(manager,"exporter");
                Assert.That(exporter.Initialize(System.IO.Path.GetFullPath(path),-1,io),Is.True); Assert.That(exporter.Export(scene),Is.True);
            }
            AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path); importer.isReadable = true; importer.SaveAndReimport();
            Mesh mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().First(); var analysis = Analyze(mesh);
            Assert.That(analysis.connectivity,Is.EqualTo("Source polygon control points"));
            Assert.That(analysis.parts,Has.Length.EqualTo(2),"Distinct FBX control points must survive coincident positions.");
            Assert.That(analysis.triangles,Is.EqualTo(2));
        }
#endif

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void GeneratePrunesOnlyFarLevelsAndBudgetStillUsesOriginal(int mode)
        {
            Mesh mesh = Model(); root = new GameObject("Parts"); root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>();
            var ctx = new UvToolContext(); ctx.Refresh(LodGenerationTool.CreateLodGroupFromRenderers(root));
            var options = new LodPipelineOps.Options { count = 2, ratios = new[] {1f,1f}, targetError = .2f,
                reductionMode = (LodReductionMode)mode, candidateCount = 1, maxNormalAngle = 15, maxColorError = .02f, smallParts = Settings() };
            var result = LodPipelineOps.Generate(ctx,1,options);
            foreach (var go in result.generatedObjects) Track(go.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(result.ok,Is.True,result.error); Assert.That(result.perLod,Has.Count.EqualTo(2));
            Assert.That(result.perLod[0].removedParts,Is.Zero); Assert.That(result.perLod[0].simplifiedTris,Is.EqualTo(34));
            Assert.That(result.perLod[1].removedParts,Is.EqualTo(1)); Assert.That(result.perLod[1].simplifiedTris,Is.EqualTo(32));
            Assert.That(result.perLod[1].targetTris,Is.EqualTo(34)); Assert.That(result.perLod[1].actualRatio,Is.EqualTo(32f/34));
            Assert.That(result.perLod[1].reductionNote,Does.Contain("retained LOD0"));
            Assert.That(LodMeshData.TriangleCount(mesh),Is.EqualTo(34));
        }
    }
}
