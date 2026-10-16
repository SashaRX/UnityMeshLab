using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
namespace SashaRX.UnityMeshLab.Tests {
 public class ForwardUvContinuityTests {
  static (Vector3[] p,Vector2[] legacy,Vector2[] uv) Fan() {
   var p=new[] {Vector3.zero,Vector3.right,Vector3.up, Vector3.zero,Vector3.up,Vector3.left,
    Vector3.zero,Vector3.left,Vector3.down};
   var legacy=p.Select(v=>new Vector2(v.x,v.y)*.2f+Vector2.one*.5f).ToArray();
   var uv=(Vector2[])legacy.Clone();uv[3]=uv[6]=new Vector2(.5000001f,.5f);
   return (p,legacy,uv);
  }
  [Test] public void AStaleLegacyOverlapFlagDoesNotSplitACurrentlyHealthySharedPoint() {
   var (p,legacy,uv)=Fan();
   ReverseUvTransfer.KeepForwardContinuity(p,legacy,new[] {true,true,false},new HashSet<int>{1,2},uv,
    new int[9],.01f/512,Matrix4x4.identity);
   Assert.AreEqual(uv[0],uv[3],"The healthy fan must share its original lightmap point.");
   Assert.AreEqual(uv[0],uv[6],"The current third face passes the actual metric/winding test.");
  }
  [Test] public void AnActuallyCollapsedIncidentFaceCannotAuthorizeASeamSnap() {
   var (p,legacy,uv)=Fan();uv[7]=uv[8]=uv[6];var old=(Vector2[])uv.Clone();
   ReverseUvTransfer.KeepForwardContinuity(p,legacy,new[] {true,true,false},new HashSet<int>{1,2},uv,
    new int[9],.01f/512,Matrix4x4.identity);
   CollectionAssert.AreEqual(old,uv,"A degenerate current face must keep the proposal refused.");
  }
  [Test] public void AnUnsafeNeighbourDoesNotAuthorizeJoiningDifferentDonors() {
   var (p,legacy,uv)=Fan();var old=(Vector2[])uv.Clone();var labels=new int[9];labels[3]=1;
   ReverseUvTransfer.KeepForwardContinuity(p,legacy,new[] {true,true,false},new HashSet<int>{1,2},uv,
    labels,.01f/512,Matrix4x4.identity);
   CollectionAssert.AreEqual(old,uv);
  }
  [Test] public void ARealLegacySeamRemainsSeparateEvenWithinThePrecisionBand() {
   var (p,legacy,uv)=Fan();legacy[3]=uv[3];var old=(Vector2[])uv.Clone();
   ReverseUvTransfer.KeepForwardContinuity(p,legacy,new[] {true,true,false},new HashSet<int>{1,2},uv,
    new int[9],.01f/512,Matrix4x4.identity);
   CollectionAssert.AreEqual(old,uv);
  }
 }
}
