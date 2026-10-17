using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ForwardUvExactFamilyProjectionTests
    {
        static Vector2[] Improve(Vector3[] source,Vector2[] sourceUv,List<UvShell> shells,
            Vector3[] positions,int[] indices,Vector2[] original,int[] donors,out string reason,
            CancellationToken token=default,long comparisonBudget=200000)
        {
            ForwardUvExactFamilyProjection.TryImprove(source,sourceUv,shells,positions,indices,original,
                donors,out var improved,out reason,token,comparisonBudget);
            return improved;
        }

        static Vector3[] Positions(int triangles=2,float scale=1)
            =>Enumerable.Range(0,triangles).SelectMany(i=>new[] {new Vector3(i*2,0,0),new Vector3(i*2+1,0,0),new Vector3(i*2,1,0)})
                .Select(p=>p*scale).ToArray();
        static Vector2[] Source(int triangles=2)
            =>Enumerable.Range(0,triangles).SelectMany(i=>new[] {new Vector2(i*2,0),new Vector2(i*2+1,0),new Vector2(i*2,1)}).ToArray();
        static List<UvShell> Shells(int triangles=2)
            =>Enumerable.Range(0,triangles).Select(i=>new UvShell {vertexIndices=new HashSet<int>(Enumerable.Range(i*3,3))}).ToList();
        static int[] Donors(int triangles=2)=>Enumerable.Range(0,triangles).SelectMany(i=>Enumerable.Repeat(i,3)).ToArray();
        static Vector2[] Overlap()=>new[] {Vector2.zero,Vector2.right,Vector2.up,Vector2.zero,Vector2.right,Vector2.up};

        [TestCase(1e-8f)] [TestCase(1f)] [TestCase(1000f)]
        public void ExactSelectedDonorsRemoveContactsWithoutGeometryOrInputEdits(float scale)
        {
            var p=Positions(scale:scale);var source=Source();var old=Overlap();var geometry=(Vector3[])p.Clone();var oldCopy=(Vector2[])old.Clone();
            var result=Improve(p,source,Shells(),p,Enumerable.Range(0,6).ToArray(),old,Donors(),out _);
            Assert.That(result,Is.Not.Null);CollectionAssert.AreEqual(source,result);CollectionAssert.AreEqual(oldCopy,old);CollectionAssert.AreEqual(geometry,p);
        }

        [Test] public void AmbiguousCoincidentSourceUvDoesNotChooseAnArbitrarySide()
        {
            var p=Positions().Concat(new[] {Positions()[3]}).ToArray();var uv=Source().Concat(new[] {new Vector2(2,2)}).ToArray();var shells=Shells();shells[1].vertexIndices.Add(6);
            Assert.That(Improve(p,uv,shells,Positions(),Enumerable.Range(0,6).ToArray(),Overlap(),Donors(),out _),Is.Null);
        }

        [Test] public void CoincidentGeometryInAnUnselectedSourceShellCannotReplaceProvenance()
        {
            Assert.That(Improve(Positions(),Source(),Shells(),Positions(),Enumerable.Range(0,6).ToArray(),Overlap(),new int[6],out _),Is.Null);
        }

        [Test] public void NearlyCoincidentSourceVerticesAreNotExactDonors()
        {
            var p=Positions();for(int i=3;i<6;++i)p[i]+=Vector3.forward*.000001f;
            Assert.That(Improve(p,Source(),Shells(),Positions(),Enumerable.Range(0,6).ToArray(),Overlap(),Donors(),out _),Is.Null);
        }

        [Test] public void RelativeMetricRefusalRetainsOriginalAliasSets()
        {
            var uv=Source();uv[4]=new Vector2(5,0);
            Assert.That(Improve(Positions(),uv,Shells(),Positions(),Enumerable.Range(0,6).ToArray(),Overlap(),Donors(),out _),Is.Null);
        }

        [Test] public void UnusedOutsideAliasCannotBeMovedByAnActiveDuplicateIndex()
        {
            var p=Positions().Concat(new[] {Positions()[3]}).ToArray();var old=Overlap().Concat(new[] {Vector2.zero}).ToArray();var donors=Donors().Concat(new[] {1}).ToArray();
            Assert.That(Improve(Positions(),Source(),Shells(),p,Enumerable.Range(0,6).ToArray(),old,donors,out _),Is.Null);
        }

        [Test] public void OneRefusedFaceDoesNotDiscardIndependentSafeContactRecovery()
        {
            var p=Positions(3);var old=Overlap().Concat(Source(3).Skip(6)).ToArray();var source=Source(3);source[7]=new Vector2(7,0);
            var result=Improve(p,source,Shells(3),p,Enumerable.Range(0,9).ToArray(),old,Donors(3),out _);
            Assert.That(result,Is.Not.Null);CollectionAssert.AreEqual(Source(),result.Take(6));CollectionAssert.AreEqual(old.Skip(6),result.Skip(6));
        }

        [Test] public void HealthyOrdinaryOutputIsNotRepositioned()
        {
            var source=Source().Select(u=>u+new Vector2(10,0)).ToArray();
            Assert.That(Improve(Positions(),source,Shells(),Positions(),Enumerable.Range(0,6).ToArray(),Source(),Donors(),out _),Is.Null);
        }

        [Test] public void ExhaustedCompleteContactBudgetPublishesNoPartialProjection()
        {
            var old=Overlap();var copy=(Vector2[])old.Clone();
            Assert.That(Improve(Positions(),Source(),Shells(),Positions(),Enumerable.Range(0,6).ToArray(),old,Donors(),out _,comparisonBudget:1),Is.Null);
            CollectionAssert.AreEqual(copy,old);
        }

        [Test] public void CancellationOccursBeforeAnyInputMutation()
        {
            using var cancel=new CancellationTokenSource();cancel.Cancel();var old=Overlap();var copy=(Vector2[])old.Clone();
            Assert.Throws<OperationCanceledException>(()=>Improve(Positions(),Source(),Shells(),Positions(),Enumerable.Range(0,6).ToArray(),old,Donors(),out _,cancel.Token));
            CollectionAssert.AreEqual(copy,old);
        }
    }
}
