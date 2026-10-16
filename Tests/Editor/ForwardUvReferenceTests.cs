using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ForwardUvReferenceTests
    {
        Mesh mesh;
        readonly Vector2[] legacy={new Vector2(0,0),new Vector2(1,0),new Vector2(0,1),
            new Vector2(1,0),new Vector2(.5f,.5f),new Vector2(0,1)};

        ReverseUvTransfer.Surface Surface(bool authoredSeam=false,bool mixedReference=false)
        {
            mesh=new Mesh {vertices=new[] {new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,1,0),
                new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,1,0)},triangles=Enumerable.Range(0,6).ToArray(),
                uv2=new[] {new Vector2(0,0),new Vector2(1,0),new Vector2(0,1),
                    new Vector2(2,0),new Vector2(2,1),new Vector2(3,0)}};
            var before=(Vector2[])legacy.Clone();
            if(authoredSeam) before[3]=new Vector2(1.1f,0);
            if(mixedReference) before[4]=new Vector2(0,0);
            var reference=ForwardUvReference.Capture(mesh,mesh.triangles,before,Enumerable.Repeat(7,6).ToArray(),
                new[] {0,1},new[] {Vector3.right,Vector3.up,Vector3.forward,Vector3.right,Vector3.up,Vector3.forward},
                new System.Collections.Generic.HashSet<int> {1});
            var surface=new ReverseUvTransfer.Surface {input=new ReverseUvTransfer.Input {mesh=mesh,forwardReference=reference},
                positions=mesh.vertices,sourcePositions=mesh.vertices,indices=mesh.triangles,sourceIndices=mesh.triangles,
                sourceFaces=new[] {0,1},faces=new[] {new ReverseUvTransfer.Face(),new ReverseUvTransfer.Face()}};
            ReverseUvTransfer.RefreshMetricFrames(surface);return surface;
        }

        [TearDown] public void Cleanup() {if(mesh) UnityEngine.Object.DestroyImmediate(mesh);}

        [Test] public void ManualResolutionDerivesDensityBeforeReferenceRepair()
        {
            var surface=Surface();surface.lod=2;surface.input.usePreparedUv2=true;
            var before=mesh.uv2;
            var prepared=new System.Collections.Generic.Dictionary<ReverseUvTransfer.Surface,Vector2[]> {{surface,before}};
            var options=new ReverseUvTransfer.Options {seedDensity=0,seedResolution=512};
            typeof(ReverseUvTransfer).GetMethod("InitializeFrozenShellChain",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[] {new[] {new[] {surface}},options,new ReverseUvTransfer.Report(),0,0f,0f,default(CancellationToken),prepared});
            Assert.Greater(options.seedDensity,0,"Manual resolution must derive the existing prepared atlas density");
            Assert.AreNotSame(before,prepared[surface]);
            Assert.AreEqual(prepared[surface][1],prepared[surface][3]);
            Assert.AreEqual(prepared[surface][2],prepared[surface][5]);
            Assert.IsTrue(surface.pixels.All(p=>!float.IsNaN(p.x) && !float.IsNaN(p.y)));
            CollectionAssert.AreEqual(before,mesh.uv2);
        }

        [TestCase(false)] [TestCase(true)]
        public void DuplicatedOutsidePointPreservesExistingUvContinuityAndAuthoredSeams(bool authoredSeam)
        {
            var outside=authoredSeam?new Vector2(9,9):new Vector2(1,0);
            var extraUv=new[] {outside,new Vector2(5,5),new Vector2(6,5)};
            var surface=AppendFace(Surface(),new[] {new Vector3(1,0,0),new Vector3(2,-1,0),new Vector3(2,0,0)},extraUv,extraUv);
            var before=mesh.uv2;
            var after=ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default);
            if(authoredSeam)
            {
                Assert.AreNotSame(before,after,"A separate authored UV seam is an independent outside corner");
                Assert.AreEqual(after[1],after[3]);Assert.AreEqual(after[2],after[5]);
                CollectionAssert.AreEqual(before.Skip(6),after.Skip(6));
            }
            else Assert.AreSame(before,after,"A duplicated mesh vertex must not hide an existing UV-continuous outside point");
            CollectionAssert.AreEqual(before,mesh.uv2);
        }

        [Test] public void GeneratedSplitAroundRescuedCollapsedFaceUsesWholeReference()
        {
            var surface=Surface();var before=mesh.uv2;var snapshot=mesh.vertices;
            var after=ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default);
            Assert.AreNotSame(before,after);
            Assert.AreEqual(after[1],after[3]);Assert.AreEqual(after[2],after[5]);
            CollectionAssert.AreEqual(before,mesh.uv2);CollectionAssert.AreEqual(snapshot,mesh.vertices);
        }

        [TestCase(true,false)] [TestCase(false,true)]
        public void AuthoredSeamOrMixedReferenceIsPreserved(bool seam,bool mixed)
        {
            var surface=Surface(seam,mixed);var before=mesh.uv2;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default));
        }

        [Test] public void MissingOrStaleReferenceKeepsCurrentLayout()
        {
            var surface=Surface();var before=mesh.uv2;before[0]+=Vector2.one;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options(),default));
            surface.input.forwardReference=null;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options(),default));
        }

        [Test] public void CleanupRemapsTheExactSelectedReference()
        {
            var surface=Surface();
            using var cleaned=ReverseUvInputs.Prepare(new[] {new ReverseUvTransfer.Level {inputs=new[] {surface.input}}});
            Assert.IsNotNull(cleaned.levels[0].inputs[0].forwardReference);
            Assert.IsTrue(cleaned.levels[0].inputs[0].forwardReference.Matches(cleaned.levels[0].inputs[0].mesh));
            mesh.uv2=mesh.uv2.Select(p=>p+Vector2.one).ToArray();
            using var stale=ReverseUvInputs.Prepare(new[] {new ReverseUvTransfer.Level {inputs=new[] {surface.input}}});
            Assert.IsNull(stale.levels[0].inputs[0].forwardReference);
        }

        [Test] public void CancellationPublishesNoChanges()
        {
            var surface=Surface();var before=mesh.uv2;
            Assert.Throws<OperationCanceledException>(()=>ReverseUvTransfer.RepairForwardReference(surface,before,
                new ReverseUvTransfer.Options(),new CancellationToken(true)));
            CollectionAssert.AreEqual(before,mesh.uv2);
        }

        ReverseUvTransfer.Surface AppendFace(ReverseUvTransfer.Surface surface,Vector3[] points,Vector2[] uv,
            Vector2[] prior,bool reuseFirstFace=false,bool shareOutsideVertex=false)
        {
            var vertices=mesh.vertices;var before=mesh.uv2;
            mesh.vertices=vertices.Concat(points).ToArray();mesh.uv2=before.Concat(uv).ToArray();
            var extra=reuseFirstFace?new[] {0,1,2}:shareOutsideVertex?new[] {1,6,7}:new[] {6,7,8};
            mesh.triangles=mesh.triangles.Concat(extra).ToArray();
            surface.positions=surface.sourcePositions=mesh.vertices;surface.indices=surface.sourceIndices=mesh.triangles;
            surface.sourceFaces=new[] {0,1,2};surface.faces=new[] {new ReverseUvTransfer.Face(),new ReverseUvTransfer.Face(),new ReverseUvTransfer.Face()};
            surface.input.forwardReference=ForwardUvReference.Capture(mesh,mesh.triangles,legacy.Concat(prior).ToArray(),
                Enumerable.Repeat(7,mesh.vertexCount).ToArray(),new[] {0,1,2},
                Enumerable.Range(0,9).Select(i=>i%3==0?Vector3.right:i%3==1?Vector3.up:Vector3.forward).ToArray(),
                new System.Collections.Generic.HashSet<int> {1});
            ReverseUvTransfer.RefreshMetricFrames(surface);return surface;
        }

        [Test] public void NonmanifoldReferenceDoesNotPublishARepair()
        {
            var surface=AppendFace(Surface(),Array.Empty<Vector3>(),Array.Empty<Vector2>(),Array.Empty<Vector2>(),reuseFirstFace:true);
            var before=mesh.uv2;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default));
        }

        [Test] public void ReferenceRepairCannotMoveAnOutsideIncidentCorner()
        {
            var surface=AppendFace(Surface(),new[] {new Vector3(2,0,0),new Vector3(2,-1,0)},
                new[] {new Vector2(5,5),new Vector2(6,5)},new[] {new Vector2(5,5),new Vector2(6,5)},shareOutsideVertex:true);
            var before=mesh.uv2;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default));
        }

        [Test] public void MixedAuthoredWindingBesideARescuedFaceIsPreserved()
        {
            var surface=AppendFace(Surface(),new[] {new Vector3(0,0,0),new Vector3(0,-1,0),new Vector3(1,0,0)},
                new[] {new Vector2(0,0),new Vector2(0,1),new Vector2(1,0)},
                new[] {new Vector2(0,0),new Vector2(0,1),new Vector2(1,0)});
            var before=mesh.uv2;
            Assert.AreSame(before,ReverseUvTransfer.RepairForwardReference(surface,before,new ReverseUvTransfer.Options {seedDensity=10},default));
        }

        [TestCase(false)] [TestCase(true)]
        public void AutoTuneRestoresReferenceFromTheChosenAttempt(bool chosenHasReference)
        {
            var surface=Surface();var selected=chosenHasReference?surface.input.forwardReference:null;
            // A different trial can have the exact same geometry/current UVs
            // while its legacy donor identity differs. Mesh matching is not a
            // substitute for restoring the selected trial's provenance.
            var other=ForwardUvReference.Capture(mesh,mesh.triangles,legacy,Enumerable.Repeat(8,6).ToArray(),new[] {0,1},
                new[] {Vector3.right,Vector3.up,Vector3.forward,Vector3.right,Vector3.up,Vector3.forward},
                new System.Collections.Generic.HashSet<int> {1});
            Assert.IsTrue(other.Matches(mesh));
            var entry=new MeshEntry {fbxMesh=mesh,originalMesh=mesh,forwardUvReference=selected};
            var workflow=new UvTransferWorkflow();var context=new UvToolContext();context.MeshEntries.Add(entry);
            typeof(UvTransferWorkflow).GetField("ctx",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(workflow,context);
            var choiceType=typeof(UvTransferWorkflow).GetNestedType("AutoTuneChoice",BindingFlags.NonPublic);
            var choice=Activator.CreateInstance(choiceType,true);
            try
            {
                typeof(UvTransferWorkflow).GetMethod("CaptureAutoTuneChoice",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(workflow,new[] {choice});
                entry.forwardUvReference=other;
                typeof(UvTransferWorkflow).GetMethod("RestoreAutoTuneChoice",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(workflow,new[] {choice});
                Assert.AreSame(selected,entry.forwardUvReference);
                if(selected!=null) Assert.IsTrue(selected.Matches(entry.originalMesh));
                Assert.IsTrue(mesh,"Imported source ownership must stay with the caller");
            }
            finally
            {
                ((IDisposable)choice).Dispose();
                if(entry.originalMesh && entry.originalMesh!=mesh) UnityEngine.Object.DestroyImmediate(entry.originalMesh);
            }
        }

        [Test] public void ReleasingAnAttemptDropsItsReferenceWithoutOwningTheSource()
        {
            var surface=Surface();var entry=new MeshEntry {fbxMesh=mesh,originalMesh=mesh,forwardUvReference=surface.input.forwardReference};
            typeof(UvTransferWorkflow).GetMethod("ReleaseAttemptMeshes",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[] {entry});
            Assert.IsNull(entry.forwardUvReference);Assert.IsTrue(mesh);
        }
    }
}
