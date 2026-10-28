using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshInstancesTests
    {
        readonly List<Mesh> meshes = new List<Mesh>();

        [TearDown]
        public void TearDown()
        {
            foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
            meshes.Clear();
        }

        // A separate mesh object per call, as Unity imports an instanced FBX mesh once per node.
        Mesh Quad(string name, float width = 1)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, new Vector3(width, 0, 0), new Vector3(width, 1, 0), new Vector3(0, 1, 0) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            meshes.Add(mesh);
            return mesh;
        }

        static MeshEntry Entry(Mesh mesh, int lod) => new MeshEntry { fbxMesh = mesh, originalMesh = mesh, lodIndex = lod };

        static Dictionary<MeshEntry, MeshEntry> Copies(int sourceLod, params MeshEntry[] entries)
            => MeshInstances.Copies(entries, sourceLod, mesh => mesh.name.StartsWith("Other") ? "Other.fbx" : "Model.fbx");

        /// <summary>Every copy of one FBX mesh, in any LOD, takes the result of the one copy processed.</summary>
        [Test]
        public void CopiesOfOneFbxMeshShareOneProcessedEntry()
        {
            MeshEntry a = Entry(Quad("Relief"), 0), b = Entry(Quad("Relief"), 0), c = Entry(Quad("Relief"), 1), d = Entry(Quad("Door"), 0);
            var copies = Copies(0, a, b, c, d);
            Assert.AreEqual(2, copies.Count);
            Assert.AreSame(a, copies[b]);
            Assert.AreSame(a, copies[c]);
        }

        /// <summary>The source LOD's copy is processed: the target LODs take its result, not their own transfer.</summary>
        [Test]
        public void TheSourceLodCopyIsTheOneProcessed()
        {
            MeshEntry target = Entry(Quad("Relief"), 0), source = Entry(Quad("Relief"), 2), lower = Entry(Quad("Relief"), 1);
            var copies = Copies(2, target, lower, source);
            Assert.AreSame(source, copies[target]);
            Assert.AreSame(source, copies[lower]);
            Assert.IsFalse(copies.ContainsKey(source));
        }

        /// <summary>A target of a copy's mesh group transfers from the copy's processed entry, not from the first source.</summary>
        [Test]
        public void ATargetOfACopysGroupTransfersFromItsProcessedEntry()
        {
            MeshEntry unrelated = Entry(Quad("Door"), 0), processed = Entry(Quad("Relief"), 0), target = Entry(Quad("Relief_LOD1"), 1);
            unrelated.meshGroupKey = "Door"; processed.meshGroupKey = "Relief_A"; target.meshGroupKey = "Relief_B";
            var sources = new List<MeshEntry> { unrelated, processed };
            Assert.AreSame(unrelated, UvTransferWorkflow.SourceFor(target, sources), "Without the copy's group the first source is taken.");
            Assert.AreSame(processed, UvTransferWorkflow.SourceFor(target, sources, new Dictionary<string, MeshEntry> { ["Relief_B"] = processed }));
            Assert.AreSame(unrelated, UvTransferWorkflow.SourceFor(unrelated, sources, new Dictionary<string, MeshEntry> { ["Door"] = processed }),
                "A group with a source of its own keeps it.");
        }

        /// <summary>Different meshes under one name, other files' meshes, excluded entries and meshes outside an asset stay apart.</summary>
        [Test]
        public void OnlyIncludedCopiesOfOneAssetMeshAreShared()
        {
            Mesh wide = Quad("Relief", 2);
            MeshEntry a = Entry(Quad("Relief"), 0), different = Entry(wide, 0);
            Assert.IsFalse(MeshInstances.SameContent(a.fbxMesh, wide));
            Assert.IsEmpty(Copies(0, a, different), "Same name, different content: not instances.");

            MeshEntry other = Entry(Quad("Other"), 0), otherCopy = Entry(Quad("Other"), 0);
            MeshEntry excluded = Entry(Quad("Relief"), 1);
            excluded.include = false;
            var copies = Copies(0, a, other, otherCopy, excluded);
            CollectionAssert.AreEquivalent(new[] { otherCopy }, copies.Keys.ToArray(), "Copies pair within their own file only; an excluded entry stays as it is.");
            Assert.IsEmpty(MeshInstances.Copies(new[] { a, Entry(Quad("Relief"), 0) }, 0, mesh => ""), "Meshes outside an asset are no FBX copies.");
        }
    }
}
