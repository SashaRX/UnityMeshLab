using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    // A preview swaps a prefab instance's materials. A tool that records the renderer while it is
    // on (an Undo record, a prefab-modification record) records the preview's material as the
    // instance's override; the preview's restore must not leave it there.
    public sealed class PreviewPrefabOverrideTests
    {
        const string Folder = "Assets/__MeshLabPreviewOverrideTests";
        Material authored, userOverride;
        GameObject prefab, instance;

        [SetUp]
        public void CreatePrefab()
        {
            CheckerTexturePreview.Restore();
            ShellColorModelPreview.Restore();
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", Folder.Substring("Assets/".Length));
            var shader = Shader.Find("Standard");
            authored = new Material(shader) { name = "Authored" };
            AssetDatabase.CreateAsset(authored, Folder + "/Authored.mat");
            userOverride = new Material(shader) { name = "UserOverride" };
            AssetDatabase.CreateAsset(userOverride, Folder + "/UserOverride.mat");

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = authored;
            prefab = PrefabUtility.SaveAsPrefabAsset(go, Folder + "/Model.prefab");
            Object.DestroyImmediate(go);
            instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        }

        [TearDown]
        public void Cleanup()
        {
            CheckerTexturePreview.ReleaseResources();
            ShellColorModelPreview.ReleaseResources();
            if (instance) Object.DestroyImmediate(instance);
            AssetDatabase.DeleteAsset(Folder);
        }

        MeshRenderer Renderer => instance.GetComponent<MeshRenderer>();

        // What another tool does while the preview is on: records the renderer and changes it.
        void RecordWhilePreviewed()
        {
            var renderer = Renderer;
            Undo.RecordObject(renderer, "Edit while previewed");
            renderer.enabled = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        PropertyModification[] MaterialOverrides()
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(Renderer);
            return (PrefabUtility.GetPropertyModifications(instance) ?? new PropertyModification[0])
                .Where(m => m.target == source && m.propertyPath.StartsWith("m_Materials.Array.data["))
                .ToArray();
        }

        static bool IsPreview(Object material)
            => material is Material m && m != null && m.shader != null && CheckerTexturePreview.IsPreviewShader(m.shader.name);

        [Test]
        public void CheckerRestoreLeavesNoPreviewOverride()
        {
            CheckerTexturePreview.Apply(new System.Collections.Generic.List<(Renderer, Mesh)> { (Renderer, null) });
            RecordWhilePreviewed();
            Assert.IsTrue(MaterialOverrides().Any(m => IsPreview(m.objectReference)), "The record must have taken the checker (the leak this guards).");

            CheckerTexturePreview.Restore();

            Assert.AreSame(authored, Renderer.sharedMaterial);
            Assert.IsEmpty(MaterialOverrides(), "A slot that is the prefab's own again keeps no override.");
            Assert.IsFalse(Renderer.enabled, "The tool's own change stays an override.");
        }

        [Test]
        public void CheckerRestoreKeepsTheUsersMaterialOverride()
        {
            Renderer.sharedMaterial = userOverride;
            PrefabUtility.RecordPrefabInstancePropertyModifications(Renderer);
            CheckerTexturePreview.Apply(new System.Collections.Generic.List<(Renderer, Mesh)> { (Renderer, null) });
            RecordWhilePreviewed();

            CheckerTexturePreview.Restore();

            Assert.AreEqual(userOverride, Renderer.sharedMaterial);
            var overrides = MaterialOverrides();
            Assert.AreEqual(1, overrides.Length);
            Assert.AreEqual(userOverride, overrides[0].objectReference, "The user's own override survives the preview.");
        }

        [Test]
        public void ShellColourRestoreLeavesNoPreviewOverride()
        {
            var mesh = instance.GetComponent<MeshFilter>().sharedMesh;
            ShellColorModelPreview.Apply(new System.Collections.Generic.List<(Renderer, Mesh, int[])>
                { (Renderer, mesh, new int[mesh.triangles.Length / 3]) }, new[] { new Color32(200, 40, 40, 255) });
            if (!ShellColorModelPreview.IsActive) Assert.Inconclusive("The shell preview did not apply to the cube.");
            RecordWhilePreviewed();

            ShellColorModelPreview.Restore();

            Assert.AreSame(authored, Renderer.sharedMaterial);
            Assert.IsEmpty(MaterialOverrides());
        }

        [Test]
        public void CheckerRestoreLeavesNoPreviewMeshOverride()
        {
            var filter = instance.GetComponent<MeshFilter>();
            var authoredMesh = filter.sharedMesh;
            var previewMesh = Object.Instantiate(authoredMesh);
            try
            {
                CheckerTexturePreview.Apply(new System.Collections.Generic.List<(Renderer, Mesh)> { (Renderer, previewMesh) });
                Undo.RecordObject(filter, "Edit while previewed");
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                var source = PrefabUtility.GetCorrespondingObjectFromSource(filter);
                bool MeshOverride(Object value) => PrefabUtility.GetPropertyModifications(instance)
                    .Any(m => m.target == source && m.propertyPath == "m_Mesh" && m.objectReference == value);
                Assert.IsTrue(MeshOverride(previewMesh), "The record must have taken the preview mesh (the leak this guards).");

                CheckerTexturePreview.Restore();

                Assert.AreEqual(authoredMesh, filter.sharedMesh);
                Assert.IsFalse(MeshOverride(previewMesh), "No override keeps the preview's mesh.");
                Assert.IsFalse(PrefabUtility.GetPropertyModifications(instance).Any(m => m.target == source && m.propertyPath == "m_Mesh"),
                    "The prefab's own mesh keeps no override.");
            }
            finally { Object.DestroyImmediate(previewMesh); }
        }

        [Test]
        public void RestoreWithoutALeakChangesNoModification()
        {
            CheckerTexturePreview.Apply(new System.Collections.Generic.List<(Renderer, Mesh)> { (Renderer, null) });
            CheckerTexturePreview.Restore();

            Assert.IsEmpty(PrefabUtility.GetPropertyModifications(instance)
                .Where(m => m.target == PrefabUtility.GetCorrespondingObjectFromSource(Renderer)));
        }
    }
}
