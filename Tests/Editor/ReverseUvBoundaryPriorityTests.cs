using System.Linq;
using NUnit.Framework;
using UnityEngine;
namespace SashaRX.UnityMeshLab.Tests
{
    public class ReverseUvBoundaryPriorityTests
    {
        [Test]
        public void ExistingAffineMatchWinsBeforeOptionalRigidBoundaryRescue()
        {
            // A three-triangle bevel fan. The last child has a separate,
            // stretched lightmap island meeting its neighbour at one vertex.
            // A translated rigid fit is contact-free, but its existing affine
            // correspondence has the better metric and must retain priority.
            // Local-coordinate Float32 fixture: shared positions and separate
            // child UV charts are intentional. No imported asset is required.
            var coarse=new[] {new Vector3(0f,0f,0f),
                new Vector3(0.00379395485f,-0.0172524452f,0.0523891449f),
                new Vector3(5.73396683e-05f,-0.00414299965f,0.055996418f),
                new Vector3(0f,0f,0f),
                new Vector3(0.00377929211f,-0.0138528347f,1.38282776e-05f),
                new Vector3(0.00379395485f,-0.0172524452f,0.0523891449f),
                new Vector3(0f,0f,0f),
                new Vector3(0.00875365734f,-0.00503563881f,-0.0803861618f),
                new Vector3(0.00377929211f,-0.0138528347f,1.38282776e-05f)};
            var fine=new[] {new Vector3(-3.57627869e-07f,-9.53674316e-07f,-3.33786011e-06f),
                new Vector3(0.00380122662f,-0.0138540268f,0.0393075943f),
                new Vector3(5.57899475e-05f,-9.53674316e-07f,0.0405082703f),
                new Vector3(-3.57627869e-07f,-9.53674316e-07f,-3.33786011e-06f),
                new Vector3(0.00377869606f,-0.0138537884f,1.04904175e-05f),
                new Vector3(0.00380122662f,-0.0138540268f,0.0393075943f),
                new Vector3(-3.57627869e-07f,-9.53674316e-07f,-3.33786011e-06f),
                new Vector3(0.00875210762f,-0.00503659248f,-0.049860239f),
                new Vector3(0.00377869606f,-0.0138537884f,1.04904175e-05f)};
            var coarseUv=new[] {new Vector2(0f,0f),
                new Vector2(0.00100052357f,0.00302115083f),
                new Vector2(0.000239253044f,0.0032312572f),
                new Vector2(0f,0f),
                new Vector2(0.000804185867f,-1.25169754e-06f),
                new Vector2(0.00100052357f,0.00302115083f),
                new Vector2(0f,0f),
                new Vector2(0.00030118227f,-0.00464367867f),
                new Vector2(0.000804185867f,-1.25169754e-06f)};
            var fineUv=new[] {new Vector2(0f,0f),
                new Vector2(1.49011612e-08f,0.00214207172f),
                new Vector2(0.000724695623f,0.00194764137f),
                new Vector2(0f,0f),
                new Vector2(-0.000703305006f,0.000252902508f),
                new Vector2(1.49011612e-08f,0.00214207172f),
                new Vector2(0f,0f),
                new Vector2(-0.00074505806f,-0.00255507231f),
                new Vector2(-0.000760376453f,-1.56760216e-05f)};
            var source=new Mesh {vertices=coarse,uv2=coarseUv,triangles=Enumerable.Range(0,9).ToArray()};
            var target=new Mesh {vertices=fine,uv2=fineUv,triangles=Enumerable.Range(0,9).ToArray()};
            try
            {
                var levels=new[] {new ReverseUvTransfer.Level {lod=1,inputs=new[] {new ReverseUvTransfer.Input {mesh=source,key="Panel_LOD1",usePreparedUv2=true}}},
                    new ReverseUvTransfer.Level {lod=0,inputs=new[] {new ReverseUvTransfer.Input {mesh=target,key="Panel_LOD0",usePreparedUv2=true}}}};
                using var result=ReverseUvTransfer.Build(levels,new ReverseUvTransfer.Options {seedResolution=128,seedDensity=32,
                    projectionReach=.05f,regroupPreparedAtlas=true,frozenShellCascade=true,repackFinalAtlas=true}).GetAwaiter().GetResult();
                var output=result.meshes[1][0];
                Assert.Less(TransferUvQuality.Measure(output,output.uv2,Vector2.one,Matrix4x4.identity).worstAnisotropy,1.05);
                Assert.AreEqual(3,result.report.inheritedFaces);
                Assert.AreEqual(0,result.report.overlapFaces);
            }
            finally {Object.DestroyImmediate(source);Object.DestroyImmediate(target);}
        }
    }
}
