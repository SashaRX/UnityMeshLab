using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvSharedBoundaryTests
    {
        static readonly Vector2[] Triangle={Vector2.zero,Vector2.right,Vector2.up};
        static readonly Vector3[] Positions={Vector3.zero,Vector3.right,Vector3.up};
        static Vector2[] Fit(Dictionary<Vector3,Vector2?> shared,Vector2[] anchors=null,bool[] flags=null,float tolerance=2)
            =>ReverseUvTransfer.FitFrozenSharedTranslation(Triangle,Positions,anchors??Triangle,flags??new[]{true,true,true},shared,tolerance,.25f);

        [Test] public void OneSharedSurfacePointMovesTheCompleteIslandRigidly()
        {
            var moved=Fit(new Dictionary<Vector3,Vector2?> {{Vector3.zero,new Vector2(.5f,.125f)}});
            Assert.NotNull(moved);
            Assert.AreEqual(new Vector2(.5f,.125f),moved[0]);
            Assert.AreEqual(Triangle[1]-Triangle[0],moved[1]-moved[0]);
            Assert.AreEqual(Triangle[2]-Triangle[0],moved[2]-moved[0]);
            Assert.AreEqual(Vector2.zero,Triangle[0],"The frozen input is immutable.");
        }
        [Test] public void AmbiguousDonorUvBranchesDoNotAuthorizeATranslation()
            =>Assert.IsNull(Fit(new Dictionary<Vector3,Vector2?> {{Vector3.zero,null}}));
        [Test] public void GeometricallyDifferentVerticesDoNotAuthorizeATranslation()
            =>Assert.IsNull(Fit(new Dictionary<Vector3,Vector2?> {{Vector3.forward,Vector2.zero}}));
        [Test] public void DisagreeingSharedBoundaryPointsKeepTheContactRefusal()
            =>Assert.IsNull(Fit(new Dictionary<Vector3,Vector2?> {{Vector3.zero,Vector2.zero},{Vector3.right,new Vector2(1.5f,0)}}));
        [Test] public void NonInheritedCornersDoNotAuthorizeATranslation()
            =>Assert.IsNull(Fit(new Dictionary<Vector3,Vector2?> {{Vector3.zero,Vector2.one}},flags:new[]{false,true,true}));
        [Test] public void EveryOriginalFaceAnchorMustStillFitTheExistingReach()
        {
            var anchors=new[]{Vector2.zero,new Vector2(.25f,0),Vector2.up};
            Assert.IsNull(Fit(new Dictionary<Vector3,Vector2?> {{Vector3.zero,new Vector2(.5f,0)}},anchors,tolerance:1));
        }
        [Test] public void CancellationIsObservedBeforeProducingCoordinates()
        {
            using var source=new CancellationTokenSource();source.Cancel();
            Assert.Throws<OperationCanceledException>(()=>ReverseUvTransfer.FitFrozenSharedTranslation(Triangle,Positions,Triangle,
                new[]{true,true,true},new Dictionary<Vector3,Vector2?>(),2,.25f,source.Token));
        }
    }
}
