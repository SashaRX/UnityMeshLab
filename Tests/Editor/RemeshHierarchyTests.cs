using System.Collections.Generic;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace SashaRX.UnityMeshLab.Tests.Editor
{
    /// <summary>Keep-hierarchy remesh: the renderer collection shared by the weld capture and the
    /// per-node scope, and the settings plumbing. Native remeshing itself is covered by the
    /// Native~ ctest battery and the DllNotFoundException-skipping stage tests.</summary>
    public sealed class RemeshHierarchyTests
    {
        static Mesh TwoHoleBox()
        {
            var mesh = new Mesh { name = "TwoHoleBox" };
            mesh.vertices = new[] { new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(-1,1,-1),
                new Vector3(-1,-1,1), new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1) };
            mesh.triangles = new[] { 0,1,5,0,5,4, 3,7,6,3,6,2, 0,4,7,0,7,3, 1,2,6,1,6,5 };
            mesh.uv = mesh.vertices.Select(p => new Vector2((p.x + 1) * .5f, (p.y + p.z + 2) * .25f)).ToArray();
            mesh.RecalculateNormals(); return mesh;
        }

        [UnityTest]
        public IEnumerator StandaloneClosureShowsAllHierarchyNodesAndKeepsOriginalMeshes()
        {
            var root = new GameObject("Closure preview root"); var mesh = TwoHoleBox();
            var material = new Material(Shader.Find("Standard"));
            root.transform.SetPositionAndRotation(new Vector3(5, 3, 2), Quaternion.Euler(20, 30, 40));
            for (int i = 0; i < 2; ++i) {
                var child = new GameObject("part " + i); child.transform.SetParent(root.transform, false);
                child.transform.localPosition = new Vector3(i * 4, 0, 0);
                child.AddComponent<MeshFilter>().sharedMesh = mesh; child.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            var settings = new RemeshSettings { planarCap = true, planarCapLoops = "0", shell = false,
                keepHierarchy = true, minPartSize = 0, minRodVoxels = 0 };
            using var pipeline = new RemeshPipeline();
            try {
                var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                Assert.IsTrue(pipeline.Has(RemeshPipeline.Stage.Prepare));
                Assert.IsFalse(pipeline.Has(RemeshPipeline.Stage.Remesh)); Assert.IsNull(pipeline.VoxelMesh);
                Assert.AreEqual(2, pipeline.Nodes.Count); Assert.AreEqual(20 * 3, pipeline.ClosureMesh.triangles.Length);
                Assert.AreEqual(MeshTopology.Lines, pipeline.ClosureRims.GetTopology(0));
                Assert.AreEqual(32, pipeline.ClosureRims.GetIndexCount(0));
                Assert.AreEqual(5, pipeline.ClosureContourNames.Length);
                Assert.AreEqual(4, pipeline.ClosureContourEdges.Length);
                Assert.IsTrue(pipeline.ClosureContourEdges.All(pairs => pairs.Length == 8));
                var bounds = pipeline.ClosureMesh.bounds;
                Assert.That(bounds.size.x, Is.EqualTo(6).Within(1e-4));
                foreach (var node in pipeline.Nodes) {
                    Assert.AreEqual(2, node.support.addedFaces); Assert.AreEqual(4, RemeshTopology.Inspect(node.support.positions, node.support.indices).boundary.Count);
                    Assert.AreEqual(8, node.source.indices.Length / 3);
                }
                Assert.AreEqual(12, pipeline.ClosureMesh.colors.Count(c => c.r > .9f), "Only added faces are orange");
                Assert.AreEqual(8, mesh.triangles.Length / 3);
                Assert.IsTrue(root.GetComponentsInChildren<MeshFilter>().All(f => f.sharedMesh == mesh));
                var preview = pipeline.ClosureMesh; var rims = pipeline.ClosureRims;
                var capture = pipeline.Source;
                pipeline.ClearFrom(RemeshPipeline.Stage.Remesh);
                Assert.AreSame(preview, pipeline.ClosureMesh); Assert.AreSame(capture, pipeline.Source);
                Assert.IsTrue(pipeline.Has(RemeshPipeline.Stage.Prepare));
                settings.trimToSource = !settings.trimToSource;
                Assert.IsFalse(pipeline.IsStale(RemeshPipeline.Stage.Prepare, settings, root));
                settings.minRodVoxels = 3;
                Assert.AreEqual(RemeshPipeline.Stage.Prepare, pipeline.FirstStale(RemeshPipeline.Stage.Bake, settings, root));
                pipeline.ClearFrom(RemeshPipeline.Stage.Prepare);
                Assert.IsFalse(preview); Assert.IsFalse(rims); Assert.IsNull(pipeline.SourceMesh); Assert.AreEqual(0, pipeline.Nodes.Count);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
        }

        [UnityTest]
        public IEnumerator RefusedContoursAreRedWithReasonsWhileOtherHierarchyCapsRemainPrepared()
        {
            var root = new GameObject("Mixed closure"); var good = TwoHoleBox(); var bad = TwoHoleBox();
            var material = new Material(Shader.Find("Standard"));
            bad.vertices = bad.vertices.Concat(new[] {new Vector3(0,-.5f,-2),new Vector3(0,-.5f,0),new Vector3(.5f,.5f,-1)}).ToArray();
            bad.triangles = bad.triangles.Concat(new[] {8,9,10}).ToArray();
            bad.uv = bad.vertices.Select(p => new Vector2((p.x + 1) * .5f, (p.y + p.z + 2) * .25f)).ToArray();
            bad.RecalculateNormals();
            var saved = bad.triangles;
            for (int i = 0; i < 2; ++i) {
                var child = new GameObject(i == 0 ? "refused part" : "good part"); child.transform.SetParent(root.transform, false);
                child.transform.localPosition = Vector3.right * i * 4;
                child.AddComponent<MeshFilter>().sharedMesh = i == 0 ? bad : good;
                child.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            using var pipeline = new RemeshPipeline();
            var settings = new RemeshSettings { planarCap = true, planarCapLoops = "all", shell = false,
                keepHierarchy = true, minPartSize = 0, minRodVoxels = 0 };
            try {
                var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status); Assert.IsTrue(pipeline.Has(RemeshPipeline.Stage.Prepare));
                Assert.AreEqual(6, pipeline.Nodes.Sum(node => node.support.addedFaces));
                Assert.AreEqual(2, pipeline.Nodes.Sum(node => node.support.loopFailures.Count));
                Assert.AreEqual(5, pipeline.ClosureContourReasons.Length);
                Assert.AreEqual(2, pipeline.ClosureContourReasons.Count(reason => !string.IsNullOrEmpty(reason)));
                for (int i = 0; i < pipeline.ClosureContourReasons.Length; ++i) {
                    var c = pipeline.ClosureContourColors[i];
                    if (!string.IsNullOrEmpty(pipeline.ClosureContourReasons[i])) {
                        Assert.Greater(c.r, .9f); Assert.Less(c.g, .2f);
                        StringAssert.Contains("REFUSED", pipeline.ClosureContourNames[i + 1]);
                    }
                    else { Assert.Less(c.r, .3f); Assert.Greater(c.b, .9f); }
                }
                Assert.IsTrue(pipeline.ClosureRims.colors.Any(c => c.r > .9f && c.g < .2f));
                Assert.IsTrue(pipeline.ClosureRims.colors.Any(c => c.r < .3f && c.b > .9f));
                Assert.AreEqual((17 + 6) * 3, pipeline.ClosureMesh.triangles.Length);
                CollectionAssert.AreEqual(saved, bad.triangles);
                Assert.AreSame(bad, root.transform.GetChild(0).GetComponent<MeshFilter>().sharedMesh);
                Assert.AreSame(good, root.transform.GetChild(1).GetComponent<MeshFilter>().sharedMesh);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(good); Object.DestroyImmediate(bad); Object.DestroyImmediate(material); }
        }

        [UnityTest]
        public IEnumerator IncompleteSolidRefusesRemeshButRetainsInspectableClosureAndCanPrepareAgain()
        {
            var root = new GameObject("Incomplete closure"); var mesh = TwoHoleBox();
            var material = new Material(Shader.Find("Standard"));
            root.AddComponent<MeshFilter>().sharedMesh = mesh; root.AddComponent<MeshRenderer>().sharedMaterial = material;
            using var pipeline = new RemeshPipeline();
            var settings = new RemeshSettings { planarCap = true, planarCapLoops = "0", shell = false, minPartSize = 0, minRodVoxels = 0 };
            try {
                var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                var preview = pipeline.ClosureMesh; var support = pipeline.Primary.support;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Cap support still has 4 boundary edges"));
                run = pipeline.Run(root, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Remesh);
                while (!run.IsCompleted) yield return null;
                Assert.IsFalse(run.Result); Assert.IsTrue(pipeline.Has(RemeshPipeline.Stage.Prepare));
                Assert.AreSame(preview, pipeline.ClosureMesh); Assert.AreSame(support, pipeline.Primary.support);
                Assert.IsFalse(pipeline.Has(RemeshPipeline.Stage.Remesh));
                settings.planarCapLoops = "all";
                run = pipeline.Run(root, settings, RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status); Assert.IsFalse(preview);
                Assert.AreEqual(4, pipeline.Primary.support.addedFaces);
                Assert.AreEqual(0, RemeshTopology.Inspect(pipeline.Primary.support.positions, pipeline.Primary.support.indices).boundary.Count);
                Assert.AreEqual(RemeshPipeline.Stage.Remesh, pipeline.FirstStale(RemeshPipeline.Stage.Remesh, settings, null));
                Assert.AreEqual(8, mesh.triangles.Length / 3);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(material); }
        }

        [UnityTest]
        public IEnumerator DisposeDuringPreparationCancelsAndReleasesSnapshot()
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube); var pipeline = new RemeshPipeline();
            try {
                pipeline.Changed = () => { if (pipeline.RunningStage == RemeshPipeline.Stage.Prepare) pipeline.Dispose(); };
                var run = pipeline.Run(root, new RemeshSettings(), RemeshPipeline.Stage.Prepare, RemeshPipeline.Stage.Prepare);
                while (!run.IsCompleted) yield return null;
                Assert.IsFalse(run.Result); Assert.IsFalse(pipeline.IsRunning);
                Assert.AreEqual(0, pipeline.Nodes.Count); Assert.IsNull(pipeline.ClosureMesh); Assert.IsNull(pipeline.SourceMesh);
            }
            finally { pipeline.Dispose(); Object.DestroyImmediate(root); }
        }

        [Test]
        public void SourcePreviewRejectsPositionlessGeometryAtomicallyAndCanRetry()
        {
            var root=new GameObject("preview source");
            var valid=GameObject.CreatePrimitive(PrimitiveType.Cube);valid.transform.SetParent(root.transform);
            var invalid=new GameObject("positionless child");invalid.transform.SetParent(root.transform);
            var mesh=new Mesh {name="missing Position"};
            mesh.SetVertexBufferParams(3,new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Normal));
            var filter=invalid.AddComponent<MeshFilter>();filter.sharedMesh=mesh;invalid.AddComponent<MeshRenderer>();
            var tool=new RemeshBakeTool();
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var error=typeof(RemeshBakeTool).GetField("sourcePreviewError",flags);
            try {
                tool.SetSource(root);var entries=new List<MeshEntry>();
                Assert.DoesNotThrow(()=>tool.GetUvContent(entries));Assert.IsEmpty(entries);
                StringAssert.Contains("Position",(string)error.GetValue(tool));
                var items=new List<MeshViewport3D.Item>();Assert.DoesNotThrow(()=>tool.Get3DContent(items));Assert.IsEmpty(items);
                filter.sharedMesh=valid.GetComponent<MeshFilter>().sharedMesh;
                tool.OnRefresh();entries.Clear();Assert.IsTrue(tool.GetUvContent(entries));
                Assert.AreEqual(2,entries.Count);Assert.IsNull(error.GetValue(tool));
                Assert.IsTrue(mesh);Assert.AreEqual(3,mesh.vertexCount);
            }
            finally {tool.ClearSourcePreview();Object.DestroyImmediate(root);Object.DestroyImmediate(mesh);}
        }

        [UnityTest]
        public IEnumerator SourcePreviewDefersMeshReadsDuringGuiAndCancelsQueuedWorkOnClear()
        {
            var root=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh=root.GetComponent<MeshFilter>().sharedMesh;
            var tool=new RemeshBakeTool(); tool.SetSource(root);
            var window=ScriptableObject.CreateInstance<SashaRX.UnityMeshLab.Tests.ViewportSpotInputWindow>();
            var entries=new List<MeshEntry>();
            int guiReads=0;
            try {
                window.Input=evt=> {
                    if(evt.type!=EventType.Layout && evt.type!=EventType.Repaint)return;
                    using(new GUILayout.VerticalScope()) {
                        tool.GetUvContent(entries);guiReads++;
                        GUILayout.Label("Toolbar after source preview");
                    }
                };
                window.Show();window.SendEvent(new Event {type=EventType.Layout});
                Assert.Greater(guiReads,0);Assert.IsEmpty(entries,"GUI must only queue source reads.");
                Assert.AreEqual(1,EditorApplication.update.GetInvocationList().Count(callback=>callback.Target==tool));
                tool.ClearSourcePreview();
                Assert.IsFalse(EditorApplication.update?.GetInvocationList().Any(callback=>callback.Target==tool) ?? false);
                window.Input=null;
                for(int i=0;i<3;++i)yield return null;
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                Assert.IsEmpty((List<MeshEntry>)typeof(RemeshBakeTool).GetField("sourceEntries",flags).GetValue(tool));
                Assert.AreSame(mesh,root.GetComponent<MeshFilter>().sharedMesh);
                window.Input=evt=> {
                    if(evt.type==EventType.Layout) tool.GetUvContent(entries);
                };
                window.SendEvent(new Event {type=EventType.Layout});
                Assert.IsEmpty(entries);
                window.Input=null;
                for(int i=0;i<3;++i)yield return null;
                Assert.AreEqual(1,((List<MeshEntry>)typeof(RemeshBakeTool).GetField("sourceEntries",flags).GetValue(tool)).Count);
                tool.GetUvContent(entries);Assert.AreEqual(1,entries.Count);
            }
            finally {window.Close();tool.ClearSourcePreview();Object.DestroyImmediate(root);}
        }

        [UnityTest]
        public IEnumerator UnfilteredHierarchyBakeRetainsFullyRemovedNodesAndReusesGeometry()
        {
            var root = new GameObject("unfiltered hierarchy donor");
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rod = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rodMesh = Object.Instantiate(rod.GetComponent<MeshFilter>().sharedMesh);
            rodMesh.vertices = System.Array.ConvertAll(rodMesh.vertices, p => Vector3.Scale(p, new Vector3(.001f, .001f, 2)));
            rodMesh.RecalculateBounds(); rod.GetComponent<MeshFilter>().sharedMesh = rodMesh;
            using (var pipeline = new RemeshPipeline())
            try {
                body.transform.SetParent(root.transform, false); rod.transform.SetParent(root.transform, false);
                rod.transform.localPosition = new Vector3(.2f, .2f, .2f);
                root.transform.SetPositionAndRotation(new Vector3(10, 3, -7), Quaternion.Euler(0, 30, 0));
                var settings = new RemeshSettings { keepHierarchy = true, sourceShape = RemeshShape.BoundingBox,
                    simplify = false, textureResolution = 64, bakeSamples = 1, padding = 1, dilationRadius = 0,
                    minPartSize = 0, minRodVoxels = 1, gpuProjection = false, reduceUvFragmentation = false };
                var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Bake);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status); Assert.AreEqual(1, pipeline.Nodes.Count);
                var node = pipeline.Primary;
                Assert.AreEqual(12, node.source.indices.Length / 3);
                var donor = pipeline.ProjectionSource(node, true);
                Assert.AreEqual(24, donor.indices.Length / 3, "The fully filtered renderer remains in the full-root donor");
                var geometry = node.geometry; var mesh = node.mesh;
                settings.bakeFilteredParts = true;
                Assert.AreEqual(RemeshPipeline.Stage.Bake, pipeline.FirstStale(RemeshPipeline.Stage.Bake, settings, root));
                run = pipeline.Run(root, settings, RemeshPipeline.Stage.Bake, RemeshPipeline.Stage.Bake);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                Assert.AreSame(geometry, node.geometry); Assert.AreSame(mesh, node.mesh);
                Assert.AreSame(donor, pipeline.ProjectionSource(node, true), "Reuse the captured donor on rebake");
                Assert.AreSame(node.source, pipeline.ProjectionSource(node, false));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(rodMesh); }
        }

        [UnityTest]
        public IEnumerator FilterPreviewShowsRemovedRodsRefreshesThresholdsAndReleasesOldModel()
        {
            var root = new GameObject("filter preview root");
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rod = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var other = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rodMesh = Object.Instantiate(rod.GetComponent<MeshFilter>().sharedMesh);
            rodMesh.vertices = System.Array.ConvertAll(rodMesh.vertices, p => Vector3.Scale(p, new Vector3(.001f, .001f, 2)));
            rodMesh.RecalculateBounds(); rod.GetComponent<MeshFilter>().sharedMesh = rodMesh;
            var tool = new RemeshBakeTool { highlightFilterPreview = true };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var settings = (RemeshSettings)typeof(RemeshBakeTool).GetField("settings", flags).GetValue(tool);
            var highlight = (RemeshCaptureHighlight)typeof(RemeshBakeTool).GetField("highlight", flags).GetValue(tool);
            try {
                settings.keepHierarchy = true; settings.minPartSize = 0; settings.minRodVoxels = 1;
                body.transform.SetParent(root.transform, false); rod.transform.SetParent(root.transform, false);
                root.transform.position = new Vector3(12, -3, 5);
                var original = rod.GetComponent<MeshFilter>().sharedMesh;
                tool.SetSource(root);
                var items = new List<MeshViewport3D.Item>();
                Assert.IsTrue(tool.Get3DContent(items));
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!highlight.PreviewMesh && EditorApplication.timeSinceStartup < deadline) yield return null;
                var first = highlight.PreviewMesh; Assert.IsTrue(first);
                Assert.That(System.Array.FindAll(first.colors32, c => c.r > 200 && c.g < 100).Length, Is.GreaterThan(0));
                items.Clear(); Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreSame(first, items[0].mesh);
                Assert.That(Vector3.Distance(items[0].matrix.MultiplyPoint3x4(first.bounds.center),
                    first.bounds.center - root.transform.position), Is.LessThan(1e-5f));
                settings.minRodVoxels = 0;
                items.Clear(); tool.Get3DContent(items);
                Assert.IsFalse(first, "Threshold changes release the old paint");
                deadline = EditorApplication.timeSinceStartup + 10;
                while (!highlight.PreviewMesh && EditorApplication.timeSinceStartup < deadline) yield return null;
                var second = highlight.PreviewMesh; Assert.IsTrue(second);
                Assert.IsTrue(System.Array.TrueForAll(second.colors32, c => c.g > c.r), "Disabled filters keep every captured face");
                tool.SetSource(other); Assert.IsFalse(second, "Changing the model releases the previous paint");
                items.Clear(); tool.Get3DContent(items);
                deadline = EditorApplication.timeSinceStartup + 10;
                while (!highlight.PreviewMesh && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.IsTrue(highlight.PreviewMesh);
                Assert.AreSame(original, rod.GetComponent<MeshFilter>().sharedMesh, "Highlight never replaces scene meshes");
            }
            finally {
                highlight.Dispose(); tool.ClearSourcePreview();
                Object.DestroyImmediate(root); Object.DestroyImmediate(other); Object.DestroyImmediate(rodMesh);
            }
        }

        [UnityTest]
        public IEnumerator CagePreviewDefersWorkAndKeepsLatestDistance()
        {
            var mesh = TriangleMesh();
            using (var preview = new RemeshPreview())
            try {
                var data = new RemeshPreview.Data { geometry = new RemeshNative.Geometry {
                    positions = mesh.vertices, indices = mesh.triangles, normals = mesh.normals },
                    cageDistance = .1f, cageSmoothing = 2f };
                var outerField = typeof(RemeshPreview).GetField("cageOuter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var innerField = typeof(RemeshPreview).GetField("cageInner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                int repaints = 0; preview.RequestRepaint = () => ++repaints;
                preview.PrepareCage(mesh, data);
                Assert.IsNull(outerField.GetValue(preview), "Drawing must not synchronously build a cage");
                data.cageDistance = .2f; preview.PrepareCage(mesh, data);
                Mesh outer = null, inner = null;
                double deadline = EditorApplication.timeSinceStartup + 10;
                while (!outer && EditorApplication.timeSinceStartup < deadline) {
                    yield return null;
                    outer = (Mesh)outerField.GetValue(preview); inner = (Mesh)innerField.GetValue(preview);
                }
                Assert.IsTrue(outer); Assert.IsTrue(inner);
                Assert.AreEqual(1, repaints);
                Assert.That(outer.vertices[0].z, Is.EqualTo(.2f).Within(1e-6f));
                Assert.That(inner.vertices[0].z, Is.EqualTo(-.2f).Within(1e-6f));
                Assert.AreEqual(6, outer.GetIndexCount(0));
                preview.Invalidate();
                Assert.IsFalse(outer); Assert.IsFalse(inner);
                preview.PrepareCage(mesh, data);
                preview.Dispose();
                for (int i = 0; i < 5; ++i) yield return null;
                Assert.IsNull(outerField.GetValue(preview));
                Assert.AreEqual(1, repaints, "Disposed cage work cannot upload after the tool closes");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SkinnedSourcePreviewKeepsSkinAttributesAndReleasesItsPoseMesh()
        {
            var root = new GameObject("skin source preview");
            var mesh = new Mesh { name = "skin source" };
            var tool = new RemeshBakeTool();
            Mesh posed = null;
            try {
                mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                mesh.bindposes = new[] { Matrix4x4.identity };
                mesh.boneWeights = new[] {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                    new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
                var skin = root.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh; skin.bones = new[] { root.transform }; skin.rootBone = root.transform;
                tool.SetSource(root);
                var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                Assert.IsTrue(tool.GetUvContent(entries)); Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(1, entries.Count);
                posed = entries[0].originalMesh;
                Assert.AreNotSame(mesh, posed); Assert.AreSame(posed, items[0].mesh);
                using (var inspection = new MeshInspection())
                    Assert.IsTrue(MeshInspection.Supports(posed, MeshViewport3D.Shading.BoneWeights), inspection.Report(posed));
                Assert.IsNotNull(MeshViewport3D.EncodeColors(posed, MeshViewport3D.Shading.BoneIndices));
                CollectionAssert.AreEqual(mesh.uv, posed.uv);
                tool.ClearSourcePreview();
                Assert.IsFalse(posed, "owned pose mesh is destroyed");
                Assert.IsTrue(mesh, "the original skin mesh remains intact");
                Assert.AreSame(mesh, skin.sharedMesh);
            }
            finally { tool.ClearSourcePreview(); Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SourceUvPreviewIsAvailableBeforeRemeshingAndFollowsSourceRoot()
        {
            var root = new GameObject("source UV hierarchy");
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var other = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var original = first.GetComponent<MeshFilter>().sharedMesh;
            var mesh = Object.Instantiate(original);
            var tool = new RemeshBakeTool();
            try {
                first.transform.SetParent(root.transform); second.transform.SetParent(root.transform);
                first.transform.localRotation = Quaternion.Euler(45, 25, 10);
                second.transform.localPosition = Vector3.right * 2;
                root.transform.SetPositionAndRotation(new Vector3(12, -3, 5), Quaternion.Euler(20, 70, -15));
                root.transform.localScale = Vector3.one * 2;
                mesh.uv2 = mesh.uv;
                mesh.SetUVs(7, new List<Vector2>(mesh.uv));
                first.GetComponent<MeshFilter>().sharedMesh = mesh;
                tool.SetSource(root);
                var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                Assert.IsTrue(tool.GetUvContent(entries));
                Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(2, entries.Count); Assert.AreEqual(2, items.Count);
                var canvas = new UvCanvasView { EntriesOverride = entries };
                var context = new UvToolContext();
                Assert.IsTrue(canvas.HasPreviewChannel(context, 0));
                Assert.IsTrue(canvas.HasPreviewChannel(context, 1));
                Assert.IsTrue(canvas.HasPreviewChannel(context, 7));
                for (int i = 0; i < entries.Count; ++i) Assert.AreSame(entries[i].originalMesh, items[i].mesh);
                var index = items.FindIndex(item => item.mesh == mesh);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                var expected = Matrix4x4.Scale(root.transform.localScale) *
                    Matrix4x4.TRS(first.transform.localPosition, first.transform.localRotation, first.transform.localScale);
                for (int k = 0; k < 16; ++k) Assert.That(items[index].matrix[k], Is.EqualTo(expected[k]).Within(1e-5f));
                Assert.That(Quaternion.Angle(Quaternion.Euler(20, 70, -15), root.transform.rotation), Is.LessThan(.01f));
                Assert.AreSame(mesh, first.GetComponent<MeshFilter>().sharedMesh);
                // Switching the source field must replace the preview instead of
                // showing meshes inherited from the hub or the previous root.
                tool.SetSource(other); entries.Clear(); items.Clear();
                Assert.IsTrue(tool.GetUvContent(entries)); Assert.IsTrue(tool.Get3DContent(items));
                Assert.AreEqual(1, entries.Count); Assert.AreEqual(1, items.Count);
                Assert.AreSame(other.GetComponent<MeshFilter>().sharedMesh, entries[0].originalMesh);
                tool.ClearSourcePreview();
                Assert.IsTrue(mesh, "a readable original remains owned by its source");
            }
            finally {
                tool.ClearSourcePreview();
                Object.DestroyImmediate(root); Object.DestroyImmediate(other); Object.DestroyImmediate(mesh);
            }
        }

        [UnityTest]
        public IEnumerator RemeshToolUsesSameStageMeshForUvAnd3D()
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetPositionAndRotation(new Vector3(3, -4, 5), Quaternion.Euler(35, 20, 10));
            source.transform.localScale = Vector3.one * 2;
            var tool = new RemeshBakeTool();
            tool.SetSource(source);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var pipeline = (RemeshPipeline)typeof(RemeshBakeTool).GetField("pipeline", flags).GetValue(tool);
            var preview = (RemeshPreview)typeof(RemeshBakeTool).GetField("previews", flags).GetValue(tool);
            try {
                var settings = new RemeshSettings { sourceShape = RemeshShape.BoundingBox,
                    simplify = false, textureResolution = 64, bakeSamples = 1, minPartSize = 0, minRodVoxels = 0 };
                var run = pipeline.Run(source, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Bake);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                foreach (RemeshPreview.Stage stage in System.Enum.GetValues(typeof(RemeshPreview.Stage))) {
                    preview.Show(stage);
                    var entries = new List<MeshEntry>(); var items = new List<MeshViewport3D.Item>();
                    Assert.IsTrue(tool.GetUvContent(entries));
                    Assert.IsTrue(tool.Get3DContent(items));
                    Assert.AreEqual(1, entries.Count);
                    Assert.AreSame(items[0].mesh, entries[0].originalMesh, stage.ToString());
                    Assert.That(Quaternion.Angle(Quaternion.identity, items[0].matrix.rotation), Is.LessThan(.01f));
                    Assert.That(((Vector3)items[0].matrix.GetColumn(3)).magnitude, Is.LessThan(1e-5f));
                    var context = new UvToolContext { PreviewUvChannel = 1 };
                    var canvas = new UvCanvasView { EntriesOverride = entries };
                    canvas.EnsurePreviewChannel(context);
                    bool hasUv = canvas.HasPreviewChannel(context, 0);
                    Assert.IsTrue(hasUv, "Every stage supplies UV0, including temporary planar UVs");
                    Assert.AreEqual(items[0].mesh.vertexCount, items[0].mesh.normals.Length);
                    Assert.AreEqual(stage == RemeshPreview.Stage.Remesh || stage == RemeshPreview.Stage.Simplified || stage == RemeshPreview.Stage.Closure,
                        entries[0].draftUv, "Draft UV provenance follows the displayed stage mesh");
                    if (hasUv) {
                        Assert.IsTrue(canvas.HasPreviewChannel(context, context.PreviewUvChannel));
                        if (stage == RemeshPreview.Stage.Source) {
                            Assert.AreSame(source.GetComponent<MeshFilter>().sharedMesh, entries[0].originalMesh);
                            CollectionAssert.AreEqual(source.GetComponent<MeshFilter>().sharedMesh.uv2, entries[0].originalMesh.uv2);
                        }
                        else Assert.AreEqual(0, context.PreviewUvChannel);
                    }
                    if (stage == RemeshPreview.Stage.Remesh || stage == RemeshPreview.Stage.Simplified)
                        Assert.IsNull(entries[0].previewTexture, "temporary planar UVs must not display the baked atlas map");
                }
            }
            finally {
                tool.ClearSourcePreview(); preview.Dispose(); pipeline.Dispose(); Object.DestroyImmediate(source);
            }
        }

        [UnityTest]
        public IEnumerator CapturedChildPreviewKeepsRelativeTransformAfterSceneRootChanges()
        {
            var root = new GameObject("PreviewRoot");
            var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
            child.transform.SetParent(root.transform, false);
            root.transform.SetPositionAndRotation(new Vector3(9, -2, 4), Quaternion.Euler(40, 15, 80));
            root.transform.localScale = Vector3.one * 2;
            child.transform.localPosition = new Vector3(1, 2, 3);
            child.transform.localRotation = Quaternion.Euler(10, 20, 30);
            child.transform.localScale = new Vector3(.5f, 1, 2);
            var expected = Matrix4x4.Scale(root.transform.localScale) *
                Matrix4x4.TRS(child.transform.localPosition, child.transform.localRotation, child.transform.localScale);
            using (var pipeline = new RemeshPipeline())
            try {
                var settings = new RemeshSettings { keepHierarchy = true, sourceShape = RemeshShape.BoundingBox,
                    minPartSize = 0, minRodVoxels = 0 };
                var run = pipeline.Run(root, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Remesh);
                while (!run.IsCompleted) yield return null;
                Assert.IsTrue(run.Result, pipeline.Status);
                var captured = pipeline.PreviewSpaceToWorld;
                var authored = child.GetComponent<MeshFilter>().sharedMesh.vertices;
                var previewVertices = pipeline.SourceMesh.vertices;
                Assert.AreEqual(authored.Length, previewVertices.Length);
                for (int v = 0; v < authored.Length; ++v)
                    Assert.That((captured.MultiplyPoint3x4(previewVertices[v]) - expected.MultiplyPoint3x4(authored[v])).magnitude,
                        Is.LessThan(1e-5f), "node capture compensates its TRS decomposition");
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                root.transform.localScale = Vector3.one;
                Assert.AreEqual(captured, pipeline.PreviewSpaceToWorld);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [UnityTest]
        public IEnumerator RotatedSourceUsesZeroRootPreviewRotationAndPreservesSavedOrientation()
        {
            const string folder = "Assets/MeshLabOrientationRegression";
            Assert.IsFalse(AssetDatabase.IsValidFolder(folder), "test folder must be unused");
            AssetDatabase.CreateFolder("Assets", "MeshLabOrientationRegression");
            var parent = new GameObject("RotatedParent");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(parent.transform, false);
            parent.transform.rotation = Quaternion.Euler(0, 25, 15);
            parent.transform.localScale = Vector3.one * 2;
            parent.transform.position = new Vector3(10, 20, 30);
            var expectedRotation = Quaternion.identity;
            try {
                for (int mode = 0; mode < 3; ++mode) {
                    source.name = "Orientation" + mode;
                    source.transform.localRotation = Quaternion.Euler(-90, 0, 0);
                    source.transform.localScale = Vector3.one * .01f;
                    expectedRotation = source.transform.rotation;
                    var settings = new RemeshSettings { sourceShape = RemeshShape.BoundingBox,
                        keepHierarchy = mode == 2, normalizeSize = mode != 0,
                        simplify = false, textureResolution = 64, bakeSamples = 1,
                        minPartSize = 0, minRodVoxels = 0 };
                    using (var pipeline = new RemeshPipeline())
                    using (var preview = new RemeshPreview()) {
                        var run = pipeline.Run(source, settings, RemeshPipeline.Stage.Remesh, RemeshPipeline.Stage.Bake);
                        while (!run.IsCompleted) yield return null;
                        Assert.IsTrue(run.Result, pipeline.Status);
                        Assert.IsTrue(UvTopology.HasUv(pipeline.SourceMesh, 0), "captured source preview retains UV0");
                        Assert.That(Quaternion.Angle(expectedRotation, pipeline.RootRotation), Is.LessThan(.01f));
                        var data = new RemeshPreview.Data { spaceToWorld = pipeline.PreviewSpaceToWorld };
                        data.meshes[0] = pipeline.SourceMesh; data.meshes[1] = pipeline.VoxelMesh;
                        data.meshes[2] = pipeline.SimplifiedMesh; data.meshes[3] = pipeline.ResultMesh;
                        data.meshes[(int)RemeshPreview.Stage.Closure] = pipeline.ClosureMesh;
                        foreach (RemeshPreview.Stage stage in System.Enum.GetValues(typeof(RemeshPreview.Stage))) {
                            preview.Show(stage);
                            var items = new List<MeshViewport3D.Item>();
                            Assert.IsTrue(preview.Fill3D(data, items), stage.ToString());
                            Assert.That(Quaternion.Angle(Quaternion.identity, items[0].matrix.rotation), Is.LessThan(.01f));
                            var vertex = items[0].mesh.vertices[0];
                            Assert.That((items[0].matrix.MultiplyPoint3x4(vertex) -
                                Vector3.Scale(source.transform.lossyScale, vertex)).magnitude, Is.LessThan(1e-5f));
                        }
                        // Saving uses the captured transform even if the source changes later.
                        source.transform.rotation = Quaternion.identity;
                        RemeshExporter.Export(pipeline, settings, folder);
                        string path = folder + "/" + source.name + "_Remesh/" + source.name + ".prefab";
                        var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        Assert.IsNotNull(saved);
                        Assert.That(Quaternion.Angle(mode == 1 ? Quaternion.identity : expectedRotation,
                            saved.transform.localRotation), Is.LessThan(.01f));
                        Assert.That(saved.transform.localPosition, Is.EqualTo(Vector3.zero));
                        if (mode == 1) {
                            Assert.AreEqual(Vector3.one, saved.transform.localScale);
                            var expectedBounds = new Bounds();
                            var matrix = pipeline.Primary.spaceToWorld;
                            matrix.SetColumn(3, new Vector4(0, 0, 0, 1));
                            var originalVertices = pipeline.ResultMesh.vertices;
                            expectedBounds = new Bounds(matrix.MultiplyPoint3x4(originalVertices[0]), Vector3.zero);
                            foreach (var v in originalVertices) expectedBounds.Encapsulate(matrix.MultiplyPoint3x4(v));
                            var filter = saved.GetComponentInChildren<MeshFilter>();
                            var savedVertices = filter.sharedMesh.vertices;
                            var savedBounds = new Bounds(filter.transform.TransformPoint(savedVertices[0]), Vector3.zero);
                            foreach (var v in savedVertices) savedBounds.Encapsulate(filter.transform.TransformPoint(v));
                            Assert.That((savedBounds.size - expectedBounds.size).magnitude, Is.LessThan(1e-5f));
                            Assert.That((savedBounds.center - expectedBounds.center).magnitude, Is.LessThan(1e-5f));
                        }
                    }
                }
            }
            finally {
                Object.DestroyImmediate(parent);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        static Mesh TriangleMesh()
        {
            var mesh = new Mesh { name = "TestTriangle" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            return mesh;
        }

        [Test]
        public void CollectRenderers_AppliesTheCaptureFilters()
        {
            var mesh = TriangleMesh();
            var collisionMesh = TriangleMesh(); collisionMesh.name = "Body_COL";
            var root = new GameObject("Root");
            try {
                var body = new GameObject("Body");
                body.transform.SetParent(root.transform);
                body.AddComponent<MeshFilter>().sharedMesh = mesh;
                body.AddComponent<MeshRenderer>();

                var lod1 = new GameObject("Body_LOD1");
                lod1.transform.SetParent(root.transform);
                lod1.AddComponent<MeshFilter>().sharedMesh = mesh;
                lod1.AddComponent<MeshRenderer>();

                var collision = new GameObject("Body_COL");
                collision.transform.SetParent(root.transform);
                collision.AddComponent<MeshFilter>().sharedMesh = mesh;
                collision.AddComponent<MeshRenderer>();

                var inactive = new GameObject("Dark");
                inactive.transform.SetParent(root.transform);
                inactive.AddComponent<MeshFilter>().sharedMesh = mesh;
                var hidden = inactive.AddComponent<MeshRenderer>();
                hidden.enabled = false;

                // A normally named object carrying a collision mesh asset is filtered
                // by the mesh name, exactly like the weld capture does.
                var hull = new GameObject("Hull");
                hull.transform.SetParent(root.transform);
                hull.AddComponent<MeshFilter>().sharedMesh = collisionMesh;
                hull.AddComponent<MeshRenderer>();

                var collected = RemeshSource.CollectRenderers(root, lod0Only: true);
                Assert.AreEqual(1, collected.Count, "only the enabled LOD0 non-collision renderer is a node");
                Assert.AreEqual(body.transform, collected[0].transform);

                var withLods = RemeshSource.CollectRenderers(root, lod0Only: false);
                Assert.AreEqual(2, withLods.Count, "lod0Only=false still excludes the collision nodes and the disabled renderer");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(collisionMesh); }
        }

        [Test]
        public void BakeScaleIntoMesh_MirrorsWindingAndHandednessAndRefreshesBounds()
        {
            var mesh = TriangleMesh();
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
            try {
                RemeshExporter.BakeScaleIntoMesh(mesh, new Vector3(-2, 3, 1));
                Assert.AreEqual(new Vector3(-2, 0, 0), mesh.vertices[1]);
                Assert.AreEqual(new Vector3(0, 3, 0), mesh.vertices[2]);
                Assert.AreEqual(new Vector3(-2, 3, 0) * 0.5f, mesh.bounds.center, "bounds follow the scaled vertices");
                Assert.AreEqual(new[] { 0, 2, 1 }, mesh.triangles, "a mirroring scale flips the winding");
                var t = mesh.tangents[0];
                Assert.AreEqual(-1f, t.x, 1e-5f, "tangents take the forward scale");
                Assert.AreEqual(-1f, t.w, "and the handedness flips with the mirror");
                Assert.AreEqual(1f, Mathf.Abs(Vector3.Dot(mesh.normals[0], Vector3.forward)), 1e-5f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void KeepHierarchy_SurvivesTheSettingsRoundTrip()
        {
            var settings = new RemeshSettings { keepHierarchy = true, voxelResolution = 64 };
            var restored = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(settings));
            Assert.IsTrue(restored.keepHierarchy, "keepHierarchy persists through the EditorPrefs JSON");
            Assert.AreEqual(64, restored.voxelResolution);
            Assert.IsFalse(new RemeshSettings().keepHierarchy, "the weld remains the default");
        }
    }
}
