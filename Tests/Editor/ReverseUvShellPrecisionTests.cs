using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvShellPrecisionTests
    {
        [Test]
        public void SubTexelFaceRemainsConnectedAtLargeAtlasOffset()
        {
            var a=Vector3.zero;var b=new Vector3(.25f,1e-7f,0);
            var c=Vector3.right;var d=new Vector3(.4f,1,0);
            var vertices=new[] {a,b,c,b,d,c};
            var source=vertices.Select(p=>new Vector2(p.x,p.y)).ToArray();
            var output=source.Select(p=>p+Vector2.one*500).ToArray();
            var frames=Enumerable.Range(0,2).Select(f=>ReverseUvTransfer.TriangleFrame(vertices[f*3],vertices[f*3+1],vertices[f*3+2])).ToArray();
            Assert.IsTrue(ReverseUvShellPrecision.Stabilize(source,output,frames,4,default));
            Assert.AreEqual(output[1],output[3]);Assert.AreEqual(output[2],output[5]);
            for(int f=0;f<2;++f)
                Assert.IsTrue(ReverseUvShellPrecision.Fits(frames[f],output[f*3],output[f*3+1],output[f*3+2],4));
            for(int i=0;i<output.Length;++i)
                Assert.LessOrEqual(Vector2.Distance(source[i]+Vector2.one*500,output[i]),.01);
        }

        [Test]
        public void VisibleCollapsedFaceCannotUseThePrecisionException()
        {
            var source=new[] {Vector2.zero,Vector2.right,Vector2.up};
            var collapsed=new[] {Vector2.zero,Vector2.right,Vector2.right*.5f};
            var frame=ReverseUvTransfer.TriangleFrame(Vector3.zero,Vector3.right,Vector3.up);
            Assert.IsFalse(ReverseUvShellPrecision.Stabilize(source,collapsed,new[] {frame},4,default));
            Assert.IsFalse(ReverseUvShellPrecision.Fits(frame,collapsed[0],collapsed[1],collapsed[2],4));
        }

        [Test]
        public void RepresentabilityFailureCannotSpendMoreThanPointZeroOneTexel()
        {
            var source=new[] {Vector2.zero,Vector2.right,new Vector2(.25f,1e-7f)};
            var placed=source.Select(p=>p+Vector2.one*1000000).ToArray();
            var frame=ReverseUvTransfer.TriangleFrame(Vector3.zero,Vector3.right,new Vector3(.25f,1e-7f,0));
            Assert.IsFalse(ReverseUvShellPrecision.Stabilize(source,placed,new[] {frame},4,default));
        }
    }
}
