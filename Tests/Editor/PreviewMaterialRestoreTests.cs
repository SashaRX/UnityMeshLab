using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class PreviewMaterialRestoreTests
    {
        readonly List<Object> owned = new List<Object>();

        [SetUp]
        public void RestorePreviousPreviews()
        {
            CheckerTexturePreview.Restore();
            ShellColorModelPreview.Restore();
        }

        [TearDown]
        public void Cleanup()
        {
            CheckerTexturePreview.ReleaseResources();
            ShellColorModelPreview.ReleaseResources();
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
        }

        T Own<T>(T item) where T : Object
        {
            owned.Add(item);
            return item;
        }

        Mesh Triangle(string name, float size)
        {
            var mesh = Own(new Mesh { name = name });
            mesh.vertices = new[] { Vector3.zero, Vector3.right * size, Vector3.up * size };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.uv2 = mesh.uv;
            mesh.colors32 = new[] { new Color32(10, 20, 30, 255), new Color32(40, 50, 60, 255), new Color32(70, 80, 90, 255) };
            mesh.RecalculateNormals();
            return mesh;
        }

        Material[] AuthoredMaterials()
        {
            var shader = Shader.Find("Standard");
            Assert.IsNotNull(shader);
            return new[] { Own(new Material(shader) { name = "AuthoredFirst" }), null,
                Own(new Material(shader) { name = "AuthoredLast" }) };
        }

        Renderer CreateRenderer(bool skinned, Mesh mesh, Material[] materials)
        {
            var root = Own(new GameObject("Duplicate preview renderer"));
            Renderer renderer;
            if (skinned) {
                var skin = root.AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh = mesh;
                renderer = skin;
            }
            else {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = root.AddComponent<MeshRenderer>();
            }
            renderer.sharedMaterials = materials;
            return renderer;
        }

        static Mesh DisplayedMesh(Renderer renderer) => renderer is SkinnedMeshRenderer skinned
            ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;

        static void AssertRestored(Renderer renderer, Mesh mesh, Material[] materials)
        {
            Assert.AreSame(mesh, DisplayedMesh(renderer), "Restore must return the authored mesh, not an earlier preview.");
            CollectionAssert.AreEqual(materials, renderer.sharedMaterials, "Every authored material slot, including null, must survive preview.");
        }

        [Test]
        public void DuplicateCheckerRendererShowsLastMeshAndRestoresAuthoredState()
        {
            var original = Triangle("Original", 1);
            var first = Triangle("First checker input", 2);
            var last = Triangle("Last checker input", 3);
            var materials = AuthoredMaterials();
            var renderer = CreateRenderer(false, original, materials);

            CheckerTexturePreview.Apply(new List<(Renderer, Mesh)> { (renderer, first), (renderer, last) });

            Assert.IsTrue(CheckerTexturePreview.IsActive);
            Assert.AreSame(last, DisplayedMesh(renderer), "The last request for a renderer remains the visible preview.");
            Assert.AreEqual(materials.Length, renderer.sharedMaterials.Length);
            Assert.IsTrue(renderer.sharedMaterials.All(m => m != null && m.shader.name == "Hidden/UnityMeshLab/CheckerUV2"));
            CheckerTexturePreview.Restore();
            AssertRestored(renderer, original, materials);
            Assert.IsFalse(CheckerTexturePreview.IsActive);
            CheckerTexturePreview.Restore();
            AssertRestored(renderer, original, materials);
            Assert.IsTrue(original && first && last, "Checker preview must not destroy any caller-owned mesh.");
        }

        [Test]
        public void DuplicateShellRendererRestoresAuthoredStateAndReleasesEveryClone(
            [Values(false, true)] bool cachedShells, [Values(false, true)] bool skinned)
        {
            var original = Triangle("Original", 1);
            var first = Triangle("First shell input", 2);
            var last = Triangle("Last shell input", 3);
            var firstColors = first.colors32;
            var lastColors = last.colors32;
            var materials = AuthoredMaterials();
            var renderer = CreateRenderer(skinned, original, materials);
            var before = new HashSet<Mesh>(Resources.FindObjectsOfTypeAll<Mesh>());
            var palette = new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255) };

            if (cachedShells)
                ShellColorModelPreview.Apply(new List<(Renderer, Mesh)> { (renderer, first), (renderer, last) },
                    palette, new ShellColorModelPreview.PreviewShellCache(1));
            else
                ShellColorModelPreview.Apply(new List<(Renderer, Mesh, int[])> {
                    (renderer, first, new[] { 0 }), (renderer, last, new[] { 1 }) }, palette);

            var clones = Resources.FindObjectsOfTypeAll<Mesh>().Where(mesh => !before.Contains(mesh)).ToArray();
            foreach (var clone in clones) owned.Add(clone);
            var preview = DisplayedMesh(renderer);
            Assert.IsTrue(ShellColorModelPreview.IsActive);
            Assert.AreNotSame(last, preview, "Shell colours belong to an owned clone, not the caller's mesh.");
            CollectionAssert.AreEqual(last.vertices, preview.vertices, "The last request for a renderer remains the visible preview.");
            CollectionAssert.Contains(clones, preview);
            CollectionAssert.AreEqual(firstColors, first.colors32);
            CollectionAssert.AreEqual(lastColors, last.colors32);

            ShellColorModelPreview.Restore();
            AssertRestored(renderer, original, materials);
            Assert.IsFalse(ShellColorModelPreview.IsActive);
            foreach (var clone in clones) Assert.IsTrue(clone == null, "Restore must destroy every preview-owned clone, including replaced duplicates.");
            ShellColorModelPreview.Restore();
            AssertRestored(renderer, original, materials);
            Assert.IsTrue(original && first && last, "Shell preview must keep every caller-owned mesh alive.");
        }

        static Material ApplyPreview(bool shells, Renderer renderer, Mesh mesh)
        {
            if (shells)
                ShellColorModelPreview.Apply(new List<(Renderer, Mesh, int[])> { (renderer, mesh, new[] { 0 }) },
                    new[] { new Color32(255, 0, 0, 255) });
            else
                CheckerTexturePreview.Apply(new List<(Renderer, Mesh)> { (renderer, mesh) });
            return renderer.sharedMaterials[0];
        }

        static Material[] ReadPreviewMaterials(bool shells, Renderer renderer, bool forAssetWrite)
        {
            Material[] materials;
            bool found = shells
                ? ShellColorModelPreview.TryGetOriginalMaterials(renderer, out materials, forAssetWrite)
                : CheckerTexturePreview.TryGetOriginalMaterials(renderer, out materials, forAssetWrite);
            Assert.IsTrue(found);
            return materials;
        }

        static void AssertWriteSnapshot(bool shells, Renderer renderer, Material[] captured, Material[] expected)
        {
            var current = renderer.sharedMaterials;
            var shown = DisplayedMesh(renderer);
            var snapshot = ReadPreviewMaterials(shells, renderer, true);
            CollectionAssert.AreEqual(expected, snapshot);
            CollectionAssert.AreEqual(current, renderer.sharedMaterials, "Reading export slots must leave the live assignments alone.");
            CollectionAssert.AreEqual(captured, ReadPreviewMaterials(shells, renderer, false), "Reading export slots must not rewrite the restoration backup.");
            Assert.AreSame(shown, DisplayedMesh(renderer));
            Assert.IsTrue(shells ? ShellColorModelPreview.IsActive : CheckerTexturePreview.IsActive);

            snapshot[0] = null;
            CollectionAssert.AreEqual(expected, ReadPreviewMaterials(shells, renderer, true), "The export snapshot must not alias the saved material array.");
            CollectionAssert.AreEqual(current, renderer.sharedMaterials);
            CollectionAssert.AreEqual(captured, ReadPreviewMaterials(shells, renderer, false));
        }

        [Test]
        public void AssetWriteSnapshotKeepsLaterAssignmentsAndUnknownPreviewMaterial([Values(false, true)] bool shells)
        {
            var mesh = Triangle("Preview input", 1);
            var authored = AuthoredMaterials();
            var captured = authored.Concat(new[] { authored[0] }).ToArray();
            var renderer = CreateRenderer(false, mesh, captured);
            var preview = ApplyPreview(shells, renderer, mesh);
            var replacement = Own(new Material(captured[0]) { name = "Later authored assignment" });
            var unknownPreview = Own(new Material(preview) { name = "Unowned material with the same preview shader" });
            Assert.AreSame(preview.shader, unknownPreview.shader);
            Assert.IsFalse(UnityEditor.EditorUtility.IsPersistent(unknownPreview));
            renderer.sharedMaterials = new[] { preview, replacement, unknownPreview, null };

            // Only the first slot still belongs to this preview. The same shader on
            // another material must reach the normal non-asset export validation.
            AssertWriteSnapshot(shells, renderer, captured, new[] { captured[0], replacement, unknownPreview, null });
        }

        [Test]
        public void AssetWriteSnapshotUnwindsPreviewLayersFromDifferentSystems(
            [Values(false, true)] bool shellFirst)
        {
            var mesh = Triangle("Preview stack input", 1);
            var authored = AuthoredMaterials();
            var renderer = CreateRenderer(false, mesh, authored);
            try {
                var first = ApplyPreview(shellFirst, renderer, mesh);
                var second = ApplyPreview(!shellFirst, renderer, mesh);
                Assert.AreNotSame(first, second, "each preview system installs its own material");
                var shown = renderer.sharedMaterials;

                CollectionAssert.AreEqual(authored, UvToolHub.OriginalPreviewMaterials(renderer, forAssetWrite: true),
                    "Both preview layers must be unwound, whatever order they were applied in.");
                CollectionAssert.AreEqual(authored, UvToolHub.OriginalPreviewMaterials(renderer),
                    "The authored snapshot unwinds the same layers.");
                CollectionAssert.AreEqual(shown, renderer.sharedMaterials, "Taking a snapshot must not change either preview.");

                var replacement = Own(new Material(authored[0]) { name = "Later stacked assignment" });
                renderer.sharedMaterials = new[] { shown[0], replacement, shown[2] };
                var current = renderer.sharedMaterials;
                CollectionAssert.AreEqual(new[] { authored[0], replacement, authored[2] },
                    UvToolHub.OriginalPreviewMaterials(renderer, forAssetWrite: true),
                    "A real assignment between two preview slots survives the export snapshot.");
                CollectionAssert.AreEqual(authored, UvToolHub.OriginalPreviewMaterials(renderer),
                    "The authored snapshot still ignores assignments made after preview.");
                CollectionAssert.AreEqual(current, renderer.sharedMaterials);
            }
            finally {
                if (shellFirst) { CheckerTexturePreview.Restore(); ShellColorModelPreview.Restore(); }
                else { ShellColorModelPreview.Restore(); CheckerTexturePreview.Restore(); }
            }
        }

        [Test]
        public void AssetWriteSnapshotKeepsTheCurrentSlotCount(
            [Values(false, true)] bool shells, [Values(false, true)] bool extend)
        {
            var mesh = Triangle("Preview input", 1);
            var captured = AuthoredMaterials();
            var renderer = CreateRenderer(false, mesh, captured);
            var preview = ApplyPreview(shells, renderer, mesh);
            renderer.sharedMaterials = extend
                ? new[] { preview, preview, preview, preview, null }
                : new[] { preview };
            var expected = extend
                ? new[] { captured[0], null, captured[2], preview, null }
                : new[] { captured[0] };

            // An added slot has no captured counterpart: do not invent a material
            // for it or silently drop it. Shrinking must not bring old slots back.
            AssertWriteSnapshot(shells, renderer, captured, expected);
        }
    }
}
