using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ForwardUvRejectedCutTests
    {
        static ReverseUvTransfer.RejectedForwardInput Square(bool splitStreams = false)
        {
            var p = new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up };
            var indices = new[] {0,1,2,0,2,3};
            if (splitStreams)
            {
                p = new[] {p[0],p[1],p[2],p[0],p[2],p[3]};
                indices = new[] {0,1,2,3,4,5};
            }
            var m = new Vector3(.5f,0,.5f); var n = new Vector3(.5f,.5f,0);
            var input = new ReverseUvTransfer.RejectedForwardInput {positions=p, indices=indices, originalIndices=indices,
                legacyUv=p.Select(v=>new Vector2(v.x,v.y)*.8f+Vector2.one*.1f).ToArray(), legacySources=new int[p.Length],
                projected=new HashSet<int>(), output=new ReverseUvTransfer.RejectedForwardOutput {
                    sourceFaces=new[] {0,0,1,1},originalFaces=new[] {0,0,1,1},
                    barycentrics=new[] {Vector3.right,Vector3.up,m,m,Vector3.up,Vector3.forward,
                        Vector3.right,n,Vector3.forward,n,Vector3.up,Vector3.forward},sources=new int[12]}};
            SetLegacy(input);
            return input;
        }

        static void SetLegacy(ReverseUvTransfer.RejectedForwardInput input)
        {
            var data=input.output;
            var uv=new Vector2[data.barycentrics.Length];
            for(int c=0;c<uv.Length;++c)
            {
                int t=data.originalFaces[c/3]*3;var w=data.barycentrics[c];
                uv[c]=input.legacyUv[input.originalIndices[t]]*w.x+input.legacyUv[input.originalIndices[t+1]]*w.y
                    +input.legacyUv[input.originalIndices[t+2]]*w.z;
            }
            data.uv=uv;input.legacyCorners=(Vector2[])uv.Clone();
            input.legacyCornerSources=new int[uv.Length];
        }

        static ReverseUvTransfer.RejectedForwardOutput Restore(ReverseUvTransfer.RejectedForwardInput input,
            HashSet<(int,int)> allowed = null, CancellationToken token = default)
            =>ReverseUvTransfer.RestoreRejectedForwardCuts(input,allowed??new HashSet<(int,int)>(),Matrix4x4.identity,token);

        [Test]
        public void CompletelyRejectedAdjacentPiecesReturnTheOriginalTriangles()
        {
            var input=Square();var originalUv=(Vector2[])input.output.uv.Clone();
            var output=Restore(input);
            Assert.NotNull(output);Assert.AreEqual(2,output.restoredFaces);
            CollectionAssert.AreEqual(new[] {0,1},output.sourceFaces);
            CollectionAssert.AreEqual(new[] {Vector3.right,Vector3.up,Vector3.forward,Vector3.right,Vector3.up,Vector3.forward},output.barycentrics);
            CollectionAssert.AreEqual(originalUv,input.output.uv,"Planning must leave the validated input immutable.");
        }

        [Test]
        public void AcceptedNeighbourKeepsBothSidesOfTheSharedCut()
        {
            var input=Square();input.projected.Add(2);
            Assert.IsNull(Restore(input),"Restoring only the refused side would create a new T-junction.");
        }

        [Test]
        public void AChangedUvCornerIsNotAnUnchangedLegacyFace()
        {
            var input=Square();input.output.uv[7]+=new Vector2(1e-7f,0);
            Assert.IsNull(Restore(input),"The restoration test uses exact UV equality, without a new precision band.");
        }

        [Test]
        public void AChangedDonorLabelKeepsTheCut()
        {
            var input=Square();input.output.sources[7]=9;
            Assert.IsNull(Restore(input));
        }

        [Test]
        public void SplitNormalVerticesStillShareTheGeometricCutFrontier()
        {
            var input=Square(true);input.projected.Add(2);
            Assert.IsNull(Restore(input),"Index-disconnected normal/UV vertices cannot hide a geometric T-junction.");
        }

        [Test]
        public void AnIndependentInteriorSubdivisionCanBeRestored()
        {
            var input=Square();var centre=Vector3.one/3;
            input.output.sourceFaces=input.output.originalFaces=new[] {0,0,0,1};
            input.output.barycentrics=new[] {Vector3.right,Vector3.up,centre,Vector3.up,Vector3.forward,centre,
                Vector3.forward,Vector3.right,centre,Vector3.right,Vector3.up,Vector3.forward};
            input.projected.Add(3);SetLegacy(input);
            var output=Restore(input);
            Assert.NotNull(output);Assert.AreEqual(1,output.restoredFaces);Assert.AreEqual(2,output.sourceFaces.Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OriginalContactAllowanceIsAppliedToRestoredTriangles(bool allowExistingContact)
        {
            var input=Square(true);
            input.legacyUv[3]=input.legacyUv[0];input.legacyUv[4]=input.legacyUv[1];input.legacyUv[5]=input.legacyUv[2];
            SetLegacy(input);
            var allowed=new HashSet<(int,int)>();
            if(allowExistingContact) allowed.Add((0,1));
            var result=Restore(input,allowed);
            Assert.AreEqual(allowExistingContact,result!=null);
        }

        [Test]
        public void RestorationCopiesOriginalStreamsAndMaterialSlots()
        {
            var input=Square(true);var output=Restore(input);
            Assert.NotNull(output);
            var source=new Mesh();Mesh result=null;
            try
            {
                source.vertices=input.positions;
                var normals=new[] {Vector3.forward,Vector3.forward,Vector3.forward,Vector3.back,Vector3.back,Vector3.back};
                source.normals=normals;source.uv=input.legacyUv;source.subMeshCount=2;
                source.SetTriangles(new[] {0,1,2},0);source.SetTriangles(new[] {3,4,5},1);
                result=ReverseUvRefinedMesh.Copy(source,output.sourceFaces,output.barycentrics,output.uv);
                Assert.AreEqual(6,result.vertexCount);Assert.AreEqual(2,result.subMeshCount);
                CollectionAssert.AreEqual(source.vertices,result.vertices);
                CollectionAssert.AreEqual(normals,result.normals);
                CollectionAssert.AreEqual(source.uv,result.uv);
                CollectionAssert.AreEqual(source.GetTriangles(0),result.GetTriangles(0));
                CollectionAssert.AreEqual(source.GetTriangles(1),result.GetTriangles(1));
            }
            finally {if(result) Object.DestroyImmediate(result);Object.DestroyImmediate(source);}
        }

        [Test]
        public void AnUnmodifiedValidatedProjectionKeepsItsGeometryUvAndWinding()
        {
            var input=Square();var centre=Vector3.one/3;
            input.output.sourceFaces=input.output.originalFaces=new[] {0,0,0,1};
            input.output.barycentrics=new[] {Vector3.right,Vector3.up,centre,Vector3.up,Vector3.forward,centre,
                Vector3.forward,Vector3.right,centre,Vector3.right,Vector3.up,Vector3.forward};
            SetLegacy(input);input.projected.Add(3);
            // The separate already-accepted projection has opposite UV orientation.
            // Restoration must neither alter nor reclassify this unrelated face.
            input.output.uv[9]=new Vector2(2,2);
            input.output.uv[10]=new Vector2(2,3);
            input.output.uv[11]=new Vector2(3,2);
            var originalUv=(Vector2[])input.output.uv.Clone();
            var output=Restore(input);
            Assert.NotNull(output);Assert.AreEqual(1,output.restoredFaces);
            for(int c=0;c<3;++c)
            {
                Assert.AreEqual(originalUv[9+c],output.uv[3+c]);
                Assert.AreEqual(input.output.barycentrics[9+c],output.barycentrics[3+c]);
                Assert.AreEqual(input.output.sources[9+c],output.sources[3+c]);
            }
            CollectionAssert.AreEqual(originalUv,input.output.uv);
        }

        [Test]
        public void ForwardPipelinePublishesRestoredTrianglesAndOriginalVertexStreams()
        {
            var source=new Mesh {vertices=new[] {Vector3.zero,new Vector3(.5f,0,0),new Vector3(.5f,1,0),Vector3.up,
                new Vector3(.5f,0,0),Vector3.right,new Vector3(1,1,0),new Vector3(.5f,1,0)},
                triangles=new[] {0,1,2,0,2,3,4,5,6,4,6,7},
                uv=new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up,Vector2.zero,Vector2.right,Vector2.one,Vector2.up},
                uv2=new[] {new Vector2(.1f,.1f),new Vector2(.3f,.1f),new Vector2(.3f,.5f),new Vector2(.1f,.5f),
                    new Vector2(.6f,.1f),new Vector2(.8f,.1f),new Vector2(.8f,.5f),new Vector2(.6f,.5f)}};
            var positions=new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up,
                new Vector3(3,0,0),new Vector3(3.5f,0,0),new Vector3(3.5f,1,0),new Vector3(3,1,0),
                new Vector3(5,0,0),new Vector3(5.5f,0,0),new Vector3(5.5f,1,0),new Vector3(5,1,0)};
            var indices=new[] {0,1,2,0,2,3,4,5,6,4,6,7,8,9,10,8,10,11};
            var legacy=new[] {new Vector2(.2f,.65f),new Vector2(.4f,.65f),new Vector2(.4f,.85f),new Vector2(.2f,.85f),
                new Vector2(.1f,.1f),new Vector2(.3f,.1f),new Vector2(.3f,.5f),new Vector2(.1f,.5f),
                new Vector2(.6f,.1f),new Vector2(.8f,.1f),new Vector2(.8f,.5f),new Vector2(.6f,.5f)};
            var uv0=positions.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var normals=positions.Select(_=>Vector3.forward).ToArray();
            var colors=positions.Select((_,i)=>new Color(i/16f,.25f,.75f,1)).ToArray();
            var target=new Mesh {vertices=positions,triangles=indices,uv=uv0,normals=normals,colors=colors};
            Mesh actual=null;
            var sourceBefore=TransferMeshSnapshot.Capture(source);var targetBefore=TransferMeshSnapshot.Capture(target);
            try
            {
                // Both projected donor charts hit distant, unchanged target blockers.
                // All pieces must be rejected, and their generated subdivisions removed.
                actual=ReverseUvTransfer.PrepareForwardSeams(source,target,legacy,new int[positions.Length],128,out var labels);
                Assert.NotNull(actual);Assert.AreEqual(indices.Length,actual.triangles.Length);
                var actualIndices=actual.triangles;var vertices=actual.vertices;var actualUv=actual.uv;var uv2=actual.uv2;
                var actualNormals=actual.normals;var actualColors=actual.colors;
                for(int c=0;c<indices.Length;++c)
                {
                    int old=indices[c],v=actualIndices[c];
                    Assert.AreEqual(positions[old],vertices[v]);Assert.AreEqual(uv0[old],actualUv[v]);
                    Assert.AreEqual(legacy[old],uv2[v]);Assert.AreEqual(0,labels[v]);
                    Assert.AreEqual(normals[old],actualNormals[v]);Assert.AreEqual(colors[old],actualColors[v]);
                }
                CollectionAssert.AreEqual(sourceBefore,TransferMeshSnapshot.Capture(source));
                CollectionAssert.AreEqual(targetBefore,TransferMeshSnapshot.Capture(target));
            }
            finally {if(actual) Object.DestroyImmediate(actual);Object.DestroyImmediate(source);Object.DestroyImmediate(target);}
        }

        [Test]
        public void CancelledRestorationCannotProduceAnOutput()
        {
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(()=>Restore(Square(),token:cancellation.Token));
        }
    }
}
