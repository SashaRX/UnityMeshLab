// PreviewLodSwitchTests.cs — the 3D preview follows the preview LOD switched from the
// canvas toolbar: the checker moves to the renderers now shown, the previous LOD gets
// its materials back, and the UV channel the user picked returns when the LOD has it.
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab.Tests
{
    internal sealed class ViewportSpotInputWindow : UnityEditor.EditorWindow
    {
        internal System.Action<Event> Input;
        void OnEnable() => wantsMouseMove = true;
        void OnGUI() => Input?.Invoke(Event.current);
    }

    public class PreviewLodSwitchTests
    {
        [Test]
        public void LightmapCopyRequiresUv2AndPreservesSourceUvAndMeshLifetime()
        {
            var source=new Mesh {vertices=new[] {Vector3.zero,Vector3.right,Vector3.up},triangles=new[] {0,1,2},
                uv=new[] {Vector2.zero,Vector2.right,Vector2.up}};
            var original=(Vector2[])source.uv.Clone();
            var create=typeof(UvToolHub).GetMethod("CreateLightmapPreviewMesh",BindingFlags.Static|BindingFlags.NonPublic);
            int meshes=Resources.FindObjectsOfTypeAll<Mesh>().Length;
            Mesh preview=null;
            try {
                Assert.IsNull(create.Invoke(null,new object[] {source,new Vector4(.5f,.5f,.25f,.25f)}));
                Assert.AreEqual(meshes,Resources.FindObjectsOfTypeAll<Mesh>().Length);
                CollectionAssert.AreEqual(original,source.uv);
                source.uv2=new[] {Vector2.up,Vector2.zero,Vector2.right};
                preview=(Mesh)create.Invoke(null,new object[] {source,new Vector4(.5f,.5f,.25f,.25f)});
                Assert.NotNull(preview);
                CollectionAssert.AreEqual(source.uv2.Select(v=>v*.5f+Vector2.one*.25f).ToArray(),preview.uv);
                CollectionAssert.AreEqual(original,source.uv);
            }
            finally {if (preview) Object.DestroyImmediate(preview);Object.DestroyImmediate(source);}
        }

        [Test]
        public void ContextRefreshKeepsIntentionalReplacementAndReleasesOldWorkingCopies(
            [Values(false,true)] bool standalone,
            [Values(UvCanvasView.PreviewMode.Off,UvCanvasView.PreviewMode.Checker,UvCanvasView.PreviewMode.Shells3D,UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode)
        {
            var group=Group(true,out var renderer,out _); var ctx=Open(group);
            if (standalone) ctx.RefreshStandalone(renderer);
            var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer); var source=entry.fbxMesh;
            var sourceBytes=TransferMeshSnapshot.Capture(source);
            var original=Object.Instantiate(source); var repacked=Object.Instantiate(source); var transferred=Object.Instantiate(source);
            owned.Add(original); owned.Add(repacked); owned.Add(transferred);
            entry.originalMesh=original; entry.repackedMesh=repacked; entry.transferredMesh=transferred;
            renderer.GetComponent<MeshFilter>().sharedMesh=original;
            var replacement=Object.Instantiate(source); owned.Add(replacement);
            replacement.vertices=source.vertices.Select(v=>v*3).ToArray(); replacement.RecalculateBounds();
            var replacementBytes=TransferMeshSnapshot.Capture(replacement);
            var previousLightmaps=LightmapSettings.lightmaps;
            try {
                if (mode==UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub,"ApplyPreviewMode",mode);
                renderer.GetComponent<MeshFilter>().sharedMesh=replacement;
                if (standalone) ctx.RefreshStandalone(renderer); else ctx.Refresh(group);
                Assert.AreSame(replacement,renderer.GetComponent<MeshFilter>().sharedMesh);
                Assert.AreSame(replacement,ctx.MeshEntries.Find(e=>e.renderer==renderer).fbxMesh);
                Assert.IsFalse(original); Assert.IsFalse(repacked); Assert.IsFalse(transferred);
                CollectionAssert.AreEqual(sourceBytes,TransferMeshSnapshot.Capture(source));
                CollectionAssert.AreEqual(replacementBytes,TransferMeshSnapshot.Capture(replacement));
                Assert.AreEqual(mode,Get<UvCanvasView>(hub,"canvas").CurrentPreviewMode);
                ctx.Refresh(Group(true,out _,out _));
                Assert.AreSame(replacement,renderer.GetComponent<MeshFilter>().sharedMesh);
                CollectionAssert.AreEqual(replacementBytes,TransferMeshSnapshot.Capture(replacement));
            }
            finally {LightmapSettings.lightmaps=previousLightmaps;}
        }

        [Test]
        public void ContextRefreshKeepsCleanupSceneEditsAndTheirFileReference([Values(false,true)] bool standalone)
        {
            // Cleanup's fixes clone the imported mesh onto the renderer with Undo. That clone is
            // a scene edit, not a pipeline working copy: a refresh must neither swap the imported
            // mesh back nor destroy the clone, and the rebuilt entry keeps the imported mesh as
            // its file reference so a save can still write the edited faces into the FBX.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group);
                if (standalone) ctx.RefreshStandalone(renderer);
                var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(imported,entry.fbxMesh);
                var importedBytes=TransferMeshSnapshot.Capture(imported);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edited));
                owned.Add(edited);
                Assert.AreNotSame(imported,edited); Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.Baseline);
                edited.vertices=edited.vertices.Select(v=>v*2).ToArray(); edited.RecalculateBounds();
                var editedBytes=TransferMeshSnapshot.Capture(edited);
                Assert.AreSame(edited,filter.sharedMesh);

                Call(hub,"RestoreWorkingMeshes");
                Assert.AreSame(edited,filter.sharedMesh,"restoring working meshes keeps the scene edit on the renderer");
                if (standalone) ctx.RefreshStandalone(renderer); else ctx.Refresh(group);
                Assert.IsTrue(edited,"a context refresh must not destroy the scene edit");
                Assert.AreSame(edited,filter.sharedMesh);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(edited,entry.originalMesh);
                Assert.AreSame(imported,entry.fbxMesh,"the rebuilt entry keeps the imported mesh as its file reference");
                Assert.AreSame(edited,entry.sceneEditMesh);
                CollectionAssert.AreEqual(editedBytes,TransferMeshSnapshot.Capture(edited));
                CollectionAssert.AreEqual(importedBytes,TransferMeshSnapshot.Capture(imported));

                // The transfer pipeline takes its working copy from the edit and resets to it.
                typeof(UvTransferWorkflow).GetMethod("EnsureTransferWorkingMesh",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {entry});
                var working=entry.originalMesh; owned.Add(working);
                Assert.AreNotSame(edited,working); Assert.AreSame(edited,entry.sceneEditMesh);
                Call(hub,"RestoreWorkingMeshes");
                Assert.IsFalse(working,"the pipeline copy is released"); Assert.IsTrue(edited); Assert.AreSame(edited,filter.sharedMesh);

                // Another model, then back: the renderer still shows the edit and the rebuilt
                // entry still knows which imported mesh it stands for.
                ctx.Refresh(Group(true,out _,out _));
                Assert.IsTrue(edited); Assert.AreSame(edited,filter.sharedMesh);
                if (standalone) ctx.RefreshStandalone(renderer); else ctx.Refresh(group);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(edited,entry.originalMesh); Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh);

                // A context built after the window reopens or scripts reload still knows which
                // imported mesh the edit stands for.
                var reopened=new UvToolContext();
                if (standalone) reopened.RefreshStandalone(renderer); else reopened.Refresh(group);
                entry=reopened.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(imported,entry.fbxMesh,"a new context takes the edit's imported mesh from the registry");
                Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.originalMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ReimportRebindingKeepsTheSceneEditAsTheBaseline([Values(false,true)] bool sceneEdit)
        {
            // Precision preparation reimports the model and rebinds each entry to the fresh sub-asset.
            // A scene edit stays the renderer's mesh and the working baseline; only the entry's file
            // reference moves, and the edit now stands for the fresh import.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Mesh edited=null;
                if (sceneEdit) { Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out edited)); owned.Add(edited); }
                var fresh=Object.Instantiate(imported); fresh.name=imported.name; owned.Add(fresh); // stands for the reimported sub-asset
                UvTransferInputPreparation.Rebind(entry,fresh);
                Assert.AreSame(fresh,entry.fbxMesh);
                if (!sceneEdit) {
                    Assert.AreSame(fresh,entry.originalMesh); Assert.AreSame(fresh,filter.sharedMesh);
                    return;
                }
                Assert.AreSame(edited,entry.originalMesh); Assert.AreSame(edited,entry.Baseline); Assert.AreSame(edited,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out var behind)); Assert.AreSame(fresh,behind);
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(fresh,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.originalMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void SourceSaveReleasesASceneEditOnlyWhenItWroteTheEditedGeometry()
        {
            // After a source save the working copies are released. A scene edit is released only
            // when the save wrote its geometry into the file; a save of channels alone leaves it
            // on the renderer as the only record of its faces.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edited)); owned.Add(edited);
                var copy=Object.Instantiate(edited); copy.name=edited.name+"_wc"; owned.Add(copy);
                entry.originalMesh=copy; filter.sharedMesh=copy; // the pipeline's working copy, as during a save

                ctx.Assets.ReleaseWorkingMeshes(); // a channel save: nothing of the edit's faces reached the file
                Assert.IsFalse(copy,"the pipeline copy is released"); Assert.IsTrue(edited,"the scene edit stays");
                Assert.AreSame(edited,filter.sharedMesh); Assert.AreSame(edited,entry.sceneEditMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out var behind)); Assert.AreSame(imported,behind);

                UnityEditor.Undo.IncrementCurrentGroup();
                ctx.Assets.MarkSceneEditsSaved(asset,new[] {entry},_=>true);
                ctx.Assets.ReleaseWorkingMeshes(); // a document save wrote the edited faces
                Assert.IsFalse(edited,"the written edit is released"); Assert.AreSame(imported,filter.sharedMesh);
                Assert.IsNull(entry.sceneEditMesh); Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(imported,entry.originalMesh);
                Assert.IsFalse(SceneEditRegistry.TryGetImported(filter,imported,out _));

                // Undo of the save brings the edit back under the renderer, still known as an edit of the imported mesh.
                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(edited,"Undo restores the released edit"); Assert.AreSame(edited,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out behind)); Assert.AreSame(imported,behind);
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.originalMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void CleanupOnAPipelineWorkingCopyMovesTheFixIntoASceneEditUndoTakesBack()
        {
            // Cleanup after Weld or Symmetry edits the transfer workflow's working copy. The fix goes
            // into a clone that is the renderer's scene edit, so the resets that destroyed the copy
            // keep the fix; the copy goes through Undo and comes back as the pipeline copy it was.
            // A tool-made mesh that is the entry's own baseline is edited in place as before.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
            Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"In place",out var same));
            Assert.AreSame(imported,same,"a non-asset baseline is edited in place"); Assert.IsNull(entry.sceneEditMesh);
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var working=Object.Instantiate(imported); working.name=imported.name+"_wc"; owned.Add(working);
                entry.originalMesh=working; filter.sharedMesh=working; // as after Weld
                UnityEditor.Undo.IncrementCurrentGroup();
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Remove Degenerate Triangles",out var edited)); owned.Add(edited);
                Assert.AreNotSame(working,edited); Assert.IsFalse(working,"the pipeline copy went through Undo");
                Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.originalMesh); Assert.AreSame(edited,filter.sharedMesh);
                Assert.AreEqual(imported.name,edited.name);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out var behind)); Assert.AreSame(imported,behind);
                Call(hub,"RestoreWorkingMeshes");
                Assert.IsTrue(edited,"the edit is no working copy to destroy"); Assert.AreSame(edited,filter.sharedMesh);
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh); Assert.AreSame(edited,entry.originalMesh);

                // Undo: the copy is back under the renderer as the pipeline copy it was, not as a
                // scene edit, and the next restore releases it as before.
                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(working); Assert.IsFalse(edited); Assert.AreSame(working,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,working,out behind,out bool workingCopy)); Assert.AreSame(imported,behind); Assert.IsTrue(workingCopy);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer); // the hub refreshed on the Undo
                Assert.AreSame(imported,entry.fbxMesh); Assert.IsNull(entry.sceneEditMesh); Assert.AreSame(working,entry.originalMesh);
                Call(hub,"RestoreWorkingMeshes");
                Assert.IsFalse(working,"a pipeline copy is released as before"); Assert.AreSame(imported,filter.sharedMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ASaveReleasesARegisteredWorkingCopyToTheImportFoundAgain()
        {
            // Undo brought the pipeline copy Cleanup promoted back under the renderer; a save then
            // reimports the file and recreates the imported mesh. The release after the save finds
            // the import the copy stands for again instead of leaving the renderer without a mesh.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                var working=Object.Instantiate(imported); working.name=imported.name+"_wc"; owned.Add(working);
                entry.originalMesh=working; filter.sharedMesh=working; // as after Weld
                UnityEditor.Undo.IncrementCurrentGroup();
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Remove Degenerate Triangles",out var edited)); owned.Add(edited);
                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(working); Assert.AreSame(working,filter.sharedMesh);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer); // the hub refreshed on the Undo
                Assert.AreSame(working,entry.originalMesh); Assert.AreSame(imported,entry.fbxMesh); Assert.IsNull(entry.sceneEditMesh);

                // The save's import replaces the file's mesh by another object of the same name.
                var replacement=Object.Instantiate(imported); replacement.name=imported.name; owned.Add(replacement);
                UnityEditor.AssetDatabase.DeleteAsset(asset);
                UnityEditor.AssetDatabase.CreateAsset(replacement,asset);
                Assert.IsFalse(imported); Assert.IsFalse(entry.Baseline,"the entry's baseline went with the import");
                ctx.Assets.ReleaseWorkingMeshes();
                Assert.IsFalse(working,"the copy is released"); Assert.AreSame(replacement,filter.sharedMesh,"the renderer takes the import found again");
                Assert.AreSame(replacement,entry.fbxMesh);
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(replacement,entry.fbxMesh); Assert.IsNull(entry.sceneEditMesh); Assert.AreSame(replacement,entry.originalMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ARelinkKeepsAWorkingCopyOnlyForTheEditItWasTakenFrom()
        {
            // The relink after a save leaves a scene edit on its renderer, and a pipeline working copy
            // taken from a living scene edit (its release returns the renderer to that edit); a working
            // copy of the import itself is relinked by name like any copy.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var edit=Object.Instantiate(imported); edit.name=imported.name; owned.Add(edit);
                var copy=Object.Instantiate(imported); copy.name=imported.name; owned.Add(copy); // named like the import: a relink by name would take it
                SceneEditRegistry.Register(filter,imported,edit);
                filter.sharedMesh=edit;
                FbxExport.RelinkSceneMeshReferences(asset,null,group);
                Assert.AreSame(edit,filter.sharedMesh,"a scene edit stays"); Assert.IsTrue(SceneEditRegistry.KeepsRenderer(filter,edit));

                SceneEditRegistry.Register(filter,imported,copy,workingCopy:true,baseline:edit);
                filter.sharedMesh=copy;
                FbxExport.RelinkSceneMeshReferences(asset,null,group);
                Assert.AreSame(copy,filter.sharedMesh,"a working copy of a living edit stays"); Assert.IsTrue(SceneEditRegistry.KeepsRenderer(filter,copy));

                SceneEditRegistry.Register(filter,imported,copy,workingCopy:true);
                Assert.IsFalse(SceneEditRegistry.KeepsRenderer(filter,copy));
                FbxExport.RelinkSceneMeshReferences(asset,null,group);
                Assert.AreSame(imported,filter.sharedMesh,"a working copy of the import is relinked by name");
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ASceneEditWithRestoredReverseProvenanceIsStillTheResultToSave()
        {
            // Reverse provenance restored from the sidecar means the file has the reverse result and an
            // import has nothing to write; a scene edit the restore checked it against is still the
            // renderer's unsaved mesh, and the save writes it.
            var group=Group(true,out var renderer,out _); var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
            var imported=entry.fbxMesh; var filter=renderer.GetComponent<MeshFilter>();
            entry.reverseTransferJson="ancestry";
            Assert.IsNull(ctx.Assets.GetResultMesh(entry),"an import with the reverse result in its file has nothing to write");
            var edit=Object.Instantiate(imported); edit.name=imported.name; owned.Add(edit);
            entry.originalMesh=entry.sceneEditMesh=edit; filter.sharedMesh=edit;
            Assert.AreSame(edit,ctx.Assets.GetResultMesh(entry),"a scene edit is written whatever its provenance");
            var transferred=Object.Instantiate(edit); owned.Add(transferred); entry.transferredMesh=transferred;
            Assert.AreSame(transferred,ctx.Assets.GetResultMesh(entry),"a reverse result mesh comes first");
        }

        [Test]
        public void ApplyUv2SyncsAKeptSceneEditWhenTheImportWasRecreated()
        {
            // Apply UV2 reimports the file right before it hands the applied channels to a kept scene
            // edit; the imported mesh the entry knew may be gone with that import, and the edit's
            // link finds the one that replaced it.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh; string name=imported.name;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edit)); owned.Add(edit);
                var result=Object.Instantiate(edit); owned.Add(result); result.uv2=ShiftedUvs(.3f); entry.transferredMesh=result; // the pipeline's UV2 for the edit
                var applied=(Vector2[])result.uv2.Clone(); CollectionAssert.AreNotEqual(applied,edit.uv2);
                // The apply's import replaces the file's mesh by another object of the same name.
                var replacement=Object.Instantiate(imported); replacement.name=name; owned.Add(replacement);
                UnityEditor.AssetDatabase.DeleteAsset(asset); UnityEditor.AssetDatabase.CreateAsset(replacement,asset);
                Assert.IsFalse(entry.fbxMesh,"the imported mesh went with the apply's import");
                Call(ctx.Assets,"SwitchToPostApplyView",new Dictionary<string,List<MeshUv2Entry>> { [asset]=new List<MeshUv2Entry> { new MeshUv2Entry { meshName=name, targetUvChannel=1 } } });
                CollectionAssert.AreEqual(applied,edit.uv2,"the edit took the applied channel");
                Assert.AreSame(replacement,entry.fbxMesh);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(replacement,entry.fbxMesh); Assert.AreSame(edit,entry.sceneEditMesh); Assert.AreSame(edit,filter.sharedMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void ADuplicatedRendererShowingAnEditIsKnownByTheEdit()
        {
            // Ctrl+D copies the renderer with the edit it shows: the registry knows the edit for the
            // original renderer, so the copy's entry keeps the import as its file reference and the
            // copy is registered; a pipeline working copy is not adopted.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edit)); owned.Add(edit);
                var duplicate=Object.Instantiate(renderer.gameObject); owned.Add(duplicate); var copyFilter=duplicate.GetComponent<MeshFilter>();
                Assert.AreSame(edit,copyFilter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(copyFilter,edit,out var behind)); Assert.AreSame(imported,behind);
                ctx.RefreshStandalone(duplicate.GetComponent<MeshRenderer>()); entry=ctx.MeshEntries[0];
                Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(edit,entry.sceneEditMesh); Assert.AreSame(edit,entry.originalMesh);
                var working=Object.Instantiate(imported); working.name=imported.name+"_wc"; owned.Add(working);
                SceneEditRegistry.Register(filter,imported,working,workingCopy:true);
                Assert.IsFalse(SceneEditRegistry.TryGetImported(copyFilter,working,out _),"a working copy belongs to the pipeline of the renderer it was made for");
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void MergeDestroysTheEditsItAbsorbsThroughUndoUnlessAnotherRendererShowsThem()
        {
            // Merge Same Material leaves the merged mesh on the first renderer and destroys the others;
            // the scene edits they showed go through Undo with the merge, so the merged state keeps no
            // copy of them and the Undo of the merge brings them back. An edit a duplicate still shows stays.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var lod0,out var lod1); var filter0=lod0.GetComponent<MeshFilter>(); var filter1=lod1.GetComponent<MeshFilter>();
            var imported0=filter0.sharedMesh; var imported1=filter1.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported0,folder+"/imported0.asset"); UnityEditor.AssetDatabase.CreateAsset(imported1,folder+"/imported1.asset");
            try {
                var ctx=Open(group); var entry0=ctx.MeshEntries.Find(e=>e.renderer==lod0); var entry1=ctx.MeshEntries.Find(e=>e.renderer==lod1);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry0,"Scene edit",out var edit0)); owned.Add(edit0);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry1,"Scene edit",out var edit1)); owned.Add(edit1);
                var duplicate=Object.Instantiate(lod0.gameObject); owned.Add(duplicate); // Ctrl+D: shows edit0 too
                Assert.AreSame(edit0,duplicate.GetComponent<MeshFilter>().sharedMesh);
                UnityEditor.Undo.IncrementCurrentGroup();
                int merged=MeshSplitMerge.MergeSameMaterial(ctx,new[] { new MeshSplitMerge.MergeGroup { lodIndex=0, material=lod0.sharedMaterial, entries=new List<MeshEntry> { entry0, entry1 } } },"Merge");
                Assert.AreEqual(1,merged);
                Assert.IsTrue(edit0,"an edit another renderer shows stays"); Assert.AreSame(edit0,duplicate.GetComponent<MeshFilter>().sharedMesh);
                Assert.IsFalse(edit1,"the absorbed edit went through Undo with its renderer"); Assert.IsFalse(lod1);
                entry0=ctx.MeshEntries.Find(e=>e.renderer==lod0);
                Assert.IsNotNull(entry0); Assert.IsNull(entry0.sceneEditMesh); Assert.AreNotSame(edit0,entry0.fbxMesh); Assert.AreSame(filter0.sharedMesh,entry0.fbxMesh);

                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(edit1); Assert.IsTrue(lod1); Assert.AreSame(edit1,filter1.sharedMesh); Assert.AreSame(edit0,filter0.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter1,edit1,out var behind)); Assert.AreSame(imported1,behind);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter0,edit0,out behind)); Assert.AreSame(imported0,behind);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void CleanupOnADuplicateCopiesTheSharedEditFirst()
        {
            // Ctrl+D leaves both renderers showing one scene edit; Cleanup on the duplicate edits a
            // copy of its own, registered for the duplicate, and leaves the original's edit as it is.
            // An edit one renderer alone shows is edited in place as before.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edit)); owned.Add(edit);
                var before=TransferMeshSnapshot.Capture(edit);
                var duplicate=Object.Instantiate(renderer.gameObject); owned.Add(duplicate); var copyFilter=duplicate.GetComponent<MeshFilter>();
                ctx.RefreshStandalone(duplicate.GetComponent<MeshRenderer>()); var copyEntry=ctx.MeshEntries[0];
                Assert.AreSame(edit,copyEntry.sceneEditMesh);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(copyEntry,"Remove Degenerate Triangles",out var own)); owned.Add(own);
                Assert.AreNotSame(edit,own,"a shared edit is copied before it is edited"); Assert.AreSame(own,copyFilter.sharedMesh); Assert.AreSame(edit,filter.sharedMesh);
                Assert.AreSame(own,copyEntry.sceneEditMesh); Assert.AreSame(own,copyEntry.originalMesh); Assert.AreSame(imported,copyEntry.fbxMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(copyFilter,own,out var behind)); Assert.AreSame(imported,behind);
                own.vertices=own.vertices.Select(v=>v*2).ToArray(); // the fix on the duplicate
                CollectionAssert.AreEqual(before,TransferMeshSnapshot.Capture(edit),"the original's edit is untouched");
                ctx.RefreshStandalone(duplicate.GetComponent<MeshRenderer>()); copyEntry=ctx.MeshEntries[0];
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(copyEntry,"Again",out var same)); Assert.AreSame(own,same,"an edit shown by one renderer is edited in place");
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void CleanupKeepsAWorkingCopyAndAnEarlierEditAnotherRendererShows()
        {
            // Duplicates made while the renderer showed an edit, then its pipeline copy: Cleanup on
            // the copy moves the fix into a clone and leaves the copy and the earlier edit alive, since
            // the duplicates show them; the Undo of the fix brings the copy back as the copy it was.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"First fix",out var first)); owned.Add(first);
                var showsEdit=Object.Instantiate(renderer.gameObject); owned.Add(showsEdit);
                var working=Object.Instantiate(first); working.name=imported.name+"_wc"; owned.Add(working);
                entry.originalMesh=working; filter.sharedMesh=working; // as after Weld, taken from the edit
                var showsCopy=Object.Instantiate(renderer.gameObject); owned.Add(showsCopy);
                UnityEditor.Undo.IncrementCurrentGroup();
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Second fix",out var second)); owned.Add(second);
                Assert.AreSame(second,filter.sharedMesh); Assert.AreSame(second,entry.sceneEditMesh);
                Assert.IsTrue(working,"a duplicate shows the copy"); Assert.AreSame(working,showsCopy.GetComponent<MeshFilter>().sharedMesh);
                Assert.IsTrue(first,"a duplicate shows the earlier edit"); Assert.AreSame(first,showsEdit.GetComponent<MeshFilter>().sharedMesh);
                UnityEditor.Undo.PerformUndo();
                Assert.IsFalse(second); Assert.AreSame(working,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,working,out var behind,out bool workingCopy,out var baseline));
                Assert.AreSame(imported,behind); Assert.IsTrue(workingCopy); Assert.AreSame(first,baseline);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void SavingASharedSceneEditRebindsEveryRendererShowingIt()
        {
            // A save that wrote a scene edit's faces releases it: every renderer showing it (a
            // duplicate shares it) takes the file's mesh before the edit goes through Undo, and the
            // Undo of the save puts the edit back on each of them.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edit)); owned.Add(edit);
                var duplicate=Object.Instantiate(renderer.gameObject); owned.Add(duplicate); var copyFilter=duplicate.GetComponent<MeshFilter>();
                Assert.AreSame(edit,copyFilter.sharedMesh);
                UnityEditor.Undo.IncrementCurrentGroup();
                ctx.Assets.MarkSceneEditsSaved(asset,new[] {entry},_=>true);
                ctx.Assets.ReleaseWorkingMeshes(); // a document save wrote the edited faces
                Assert.IsFalse(edit,"the saved edit went through Undo"); Assert.AreSame(imported,filter.sharedMesh);
                Assert.AreSame(imported,copyFilter.sharedMesh,"the duplicate takes the file's mesh too");
                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(edit); Assert.AreSame(edit,filter.sharedMesh); Assert.AreSame(edit,copyFilter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(copyFilter,edit,out var behind)); Assert.AreSame(imported,behind);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void SceneEditLinksOutliveTheirObjects()
        {
            // The imported mesh a reimport recreated is found again by its file and name; a renderer
            // deleted through Undo (Split by Material) keeps its link for the Undo that brings it back.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N"); string asset=folder+"/imported.asset";
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,asset);
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edited)); owned.Add(edited);
                // The file's mesh is replaced by another object of the same name, as a reimport can do.
                var replacement=Object.Instantiate(imported); replacement.name=imported.name; owned.Add(replacement);
                UnityEditor.AssetDatabase.DeleteAsset(asset);
                Assert.IsFalse(imported,"the old sub-asset is gone");
                UnityEditor.AssetDatabase.CreateAsset(replacement,asset);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out var behind)); Assert.AreSame(replacement,behind);
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(replacement,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh);

                // The renderer goes through Undo and comes back.
                ctx.RefreshStandalone(renderer);
                UnityEditor.Undo.IncrementCurrentGroup();
                UnityEditor.Undo.DestroyObjectImmediate(renderer.gameObject);
                ctx.RefreshStandalone(renderer); // a refresh with the renderer gone prunes nothing it may need again
                Assert.AreEqual(0,ctx.MeshEntries.Count);
                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(filter); Assert.AreSame(edited,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out behind)); Assert.AreSame(replacement,behind);
                ctx.RefreshStandalone(renderer); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsNotNull(entry); Assert.AreSame(replacement,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void AFullRegistryEvictsOnlyLinksWhoseEditIsGone()
        {
            // The oldest link is a living edit and the registry fills with links whose edits are gone
            // (saved edits, Undo histories cleared): the next link takes a dead one's place, never the
            // living edit's; once every link has a living edit the registry grows instead.
            SceneEditRegistry.Collect(Enumerable.Empty<MeshEntry>()); // what earlier tests left behind
            var go=new GameObject("registry"); owned.Add(go); var filter=go.AddComponent<MeshFilter>();
            var imported=new Mesh {name="imported"}; owned.Add(imported);
            try {
                var first=new Mesh {name="first edit"}; owned.Add(first);
                SceneEditRegistry.Register(filter,imported,first);
                while (SceneEditRegistry.Count<SceneEditRegistry.Capacity) {
                    var gone=new Mesh(); SceneEditRegistry.Register(filter,imported,gone); Object.DestroyImmediate(gone);
                }
                var second=new Mesh {name="second edit"}; owned.Add(second);
                SceneEditRegistry.Register(filter,imported,second);
                Assert.AreEqual(SceneEditRegistry.Capacity,SceneEditRegistry.Count,"a link whose edit is gone made room");
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,first,out var behind),"the oldest link, to a living edit, stays"); Assert.AreSame(imported,behind);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,second,out behind)); Assert.AreSame(imported,behind);

                var living=new List<Mesh> {first,second};
                while (living.Count<=SceneEditRegistry.Capacity) {
                    var edit=new Mesh(); owned.Add(edit); living.Add(edit); SceneEditRegistry.Register(filter,imported,edit);
                }
                Assert.GreaterOrEqual(SceneEditRegistry.Count,living.Count,"with every link to a living edit the registry grows past its capacity");
                foreach (var edit in living) { Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edit,out behind),edit.name); Assert.AreSame(imported,behind); }
            }
            finally { Object.DestroyImmediate(imported); SceneEditRegistry.Collect(Enumerable.Empty<MeshEntry>()); }
        }

        [Test]
        public void CleanupOnACopyOfAnEarlierEditKeepsThatEditAsTheBaselineThroughUndo()
        {
            // Edit A, then the pipeline's copy of A, then Cleanup on the copy: the fix goes into a
            // clone that supersedes A; Undo brings A back as the baseline and the copy back as the
            // pipeline copy of A it was.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"First fix",out var first)); owned.Add(first);
                var working=Object.Instantiate(first); working.name=imported.name+"_wc"; owned.Add(working);
                entry.originalMesh=working; filter.sharedMesh=working; // as after Weld, taken from the edit
                UnityEditor.Undo.IncrementCurrentGroup();
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Second fix",out var second)); owned.Add(second);
                Assert.IsFalse(working); Assert.IsFalse(first,"the earlier edit is part of the clone now and went through Undo");
                Assert.AreSame(second,entry.sceneEditMesh); Assert.AreSame(second,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,second,out var behind)); Assert.AreSame(imported,behind);

                UnityEditor.Undo.PerformUndo();
                Assert.IsTrue(first); Assert.IsTrue(working); Assert.IsFalse(second); Assert.AreSame(working,filter.sharedMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,working,out behind,out bool workingCopy,out var baseline));
                Assert.AreSame(imported,behind); Assert.IsTrue(workingCopy); Assert.AreSame(first,baseline);
                entry=ctx.MeshEntries.Find(e=>e.renderer==renderer); // the hub refreshed on the Undo
                Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(working,entry.originalMesh); Assert.AreSame(first,entry.sceneEditMesh); Assert.AreSame(first,entry.Baseline);
                Call(hub,"RestoreWorkingMeshes");
                Assert.IsFalse(working,"the pipeline copy is released"); Assert.IsTrue(first); Assert.AreSame(first,filter.sharedMesh,"the earlier edit is the baseline again");
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void MergeDoesNotRebindAnEditToTheMergedMesh()
        {
            // Merge replaces the renderer's mesh and the entry's references with the merged mesh and
            // refreshes; the edit it absorbed keeps the provenance it had, for the Undo of the merge.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edited)); owned.Add(edited);
                var merged=Object.Instantiate(edited); merged.name="Prop_LOD0"; owned.Add(merged);
                entry.fbxMesh=entry.originalMesh=merged; entry.sceneEditMesh=null; filter.sharedMesh=merged; // as MergeSameMaterial leaves the first entry
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(merged,entry.fbxMesh); Assert.IsNull(entry.sceneEditMesh);
                Assert.IsTrue(SceneEditRegistry.TryGetImported(filter,edited,out var behind),"the absorbed edit keeps its link"); Assert.AreSame(imported,behind);
                filter.sharedMesh=edited; // as the Undo of the merge leaves the renderer
                ctx.Refresh(group); entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.AreSame(imported,entry.fbxMesh); Assert.AreSame(edited,entry.sceneEditMesh);
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void AKeptSceneEditTakesTheChannelsASaveWroteForItsVertices()
        {
            // A channel save or Apply writes the result's channels into the file and keeps the edit
            // for its faces; the edit takes the same channels when the result has its vertices, and
            // keeps its own, with a warning, when a weld or split made the result for other vertices.
            string folder="Assets/__MeshLabSceneEditTests_"+System.Guid.NewGuid().ToString("N");
            var group=Group(true,out var renderer,out _); var filter=renderer.GetComponent<MeshFilter>(); var imported=filter.sharedMesh;
            UnityEditor.AssetDatabase.CreateFolder("Assets",folder.Substring("Assets/".Length));
            UnityEditor.AssetDatabase.CreateAsset(imported,folder+"/imported.asset");
            try {
                var ctx=Open(group); var entry=ctx.MeshEntries.Find(e=>e.renderer==renderer);
                Assert.IsTrue(MeshHygieneUtility.PrepareWritable(entry,"Scene edit",out var edited)); owned.Add(edited);
                var before=edited.uv2;
                var result=Object.Instantiate(edited); owned.Add(result);
                result.uv2=edited.uv2.Select(uv=>uv*.5f+Vector2.one*.25f).ToArray();
                MeshAssetOperations.SyncSceneEditChannels(entry,result,FbxExportIntent.UV1,"Test");
                CollectionAssert.AreEqual(result.uv2,edited.uv2,"the edit takes the written UV1");
                CollectionAssert.AreEqual(result.uv,edited.uv,"channels the save did not write are left");
                var other=new Mesh { name="other", vertices=new[] {Vector3.zero,Vector3.right,Vector3.up}, triangles=new[] {0,1,2},
                    uv2=new[] {Vector2.zero,Vector2.right,Vector2.up} }; owned.Add(other);
                LogAssert.Expect(LogType.Warning,new System.Text.RegularExpressions.Regex("keeps its vertex channels"));
                MeshAssetOperations.SyncSceneEditChannels(entry,other,FbxExportIntent.UV1,"Test");
                CollectionAssert.AreEqual(result.uv2,edited.uv2,"a result for other vertices changes nothing");
                UnityEditor.Undo.PerformUndo();
                CollectionAssert.AreEqual(before,edited.uv2,"the sync is undoable");
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); }
        }

        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        readonly List<Object> owned = new List<Object>();
        UvToolHub hub;
        GameObject previousSelection;
        string previousHubPreferences;
        bool hadHubPreferences;

        [SetUp]
        public void RememberSelection()
        {
            previousSelection = UnityEditor.Selection.activeGameObject;
            string key = MeshLabWindowPreferences.Key("Hub");
            hadHubPreferences = UnityEditor.EditorPrefs.HasKey(key);
            previousHubPreferences = UnityEditor.EditorPrefs.GetString(key, "");
            // A preceding test can persist a material channel on window teardown.
            // Each test chooses its own mode; preserve the user's prefs around it.
            UnityEditor.EditorPrefs.DeleteKey(key);
        }

        [TearDown]
        public void Cleanup()
        {
            // Closing the hub restores an active preview; the materials go after it.
            if (hub != null) Object.DestroyImmediate(hub);
            hub = null;
            UnityEditor.Selection.activeGameObject = previousSelection;
            foreach (var item in owned) if (item) Object.DestroyImmediate(item);
            owned.Clear();
            string key = MeshLabWindowPreferences.Key("Hub");
            if (hadHubPreferences) UnityEditor.EditorPrefs.SetString(key, previousHubPreferences);
            else UnityEditor.EditorPrefs.DeleteKey(key);
        }

        Mesh Quad(string name, bool withUv1)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            if (withUv1) mesh.uv2 = new[] { Vector2.zero, Vector2.right * .5f, Vector2.one * .5f, Vector2.up * .5f };
            mesh.RecalculateNormals();
            owned.Add(mesh);
            return mesh;
        }

        MeshRenderer Lod(Transform root, string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = name + "_Mat" };
            owned.Add(material);
            renderer.sharedMaterial = material;
            return renderer;
        }

        LODGroup Group(bool lod1HasUv1, out MeshRenderer lod0, out MeshRenderer lod1)
        {
            var root = new GameObject("Prop");
            owned.Add(root);
            lod0 = Lod(root.transform, "Prop_LOD0", Quad("Prop_LOD0", true));
            lod1 = Lod(root.transform, "Prop_LOD1", Quad("Prop_LOD1", lod1HasUv1));
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { lod0 }), new LOD(.1f, new Renderer[] { lod1 }) });
            return group;
        }

        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
        static Bounds PreviewBounds(IReadOnlyList<MeshViewport3D.Item> items) => (Bounds)typeof(MeshViewport3D)
            .GetMethod("BoundsOf", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { items, false });
        static bool ShowsChecker(Renderer renderer) => CheckerTexturePreview.IsPreviewShader(renderer.sharedMaterial.shader.name);

        static ViewportSpotInputWindow OpenSpotInputWindow()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("IMGUI mouse input requires a graphics device; run without -nographics.");
            var window = ScriptableObject.CreateInstance<ViewportSpotInputWindow>();
            window.Show();
            return window;
        }

        static void ChooseCameraFrame(MeshViewport3D viewport)
        {
            viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
            Set(viewport, "pivot", new Vector3(3, 4, 5));
            Set(viewport, "orbit", new Vector2(25, -15));
            Set(viewport, "distance", 2f);
            Set(viewport, "radius", .75f);
        }

        static void AssertCameraFrame(MeshViewport3D viewport)
        {
            Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(new Vector3(3, 4, 5)));
            Assert.That(Get<Vector2>(viewport, "orbit"), Is.EqualTo(new Vector2(25, -15)));
            Assert.That(Get<float>(viewport, "distance"), Is.EqualTo(2f));
        }

        // The hub on the group with the transfer tool active and UV1 picked, after one
        // frame's entry collection — the state the LOD row is clicked from.
        UvToolContext Open(LODGroup group)
        {
            hub = ScriptableObject.CreateInstance<UvToolHub>();
            Call(hub, "SelectToolById", "uv2_transfer");
            var ctx = Get<UvToolContext>(hub, "ctx");
            ctx.Refresh(group);
            Call(hub, "OnPreviewChannelChanged", 1);
            Call(hub, "CollectCanvasEntries");
            return ctx;
        }

        // The LOD row click, then the frame after it.
        void SwitchLod(int lod)
        {
            Call(hub, "SetPreviewLod", lod);
            Call(hub, "CollectCanvasEntries");
        }

        MeshRenderer Standalone(string name, bool withUv1 = true)
        {
            var renderer = Lod(null, name, Quad(name, withUv1));
            owned.Add(renderer.gameObject);
            return renderer;
        }

        void SelectModel(GameObject model)
        {
            UnityEditor.Selection.activeGameObject = model;
            Call(hub, "OnSelectionChange");
            Call(hub, "CollectCanvasEntries");
        }

        [TestCase(UvCanvasView.PreviewMode.Checker)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void SourceTextureMetricReadsAuthoredMaterialsWithoutChangingPreview(UvCanvasView.PreviewMode mode)
        {
            var group = Group(true, out var renderer, out _);
            var texture = new Texture2D(8, 16) { name = "Rectangular authored albedo" }; owned.Add(texture);
            var authored = new Material(Shader.Find("Standard")) { mainTexture = texture, mainTextureScale = new Vector2(1, 2) };
            owned.Add(authored);
            var second = new Material(authored); owned.Add(second);
            renderer.sharedMaterials = new[] { authored, second };
            var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
            var uv0 = mesh.uv;
            var expected = SourceTextureUvMetric.Resolve(renderer, mesh: mesh);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Open(group);
                Call(hub, "ApplyPreviewMode", mode);
                var previewMaterials = renderer.sharedMaterials;
                Assert.AreNotSame(authored, previewMaterials[0]);
                var metric = SourceTextureUvMetric.Resolve(renderer, mesh: mesh);
                Assert.AreEqual(expected.uvScale, metric.uvScale);
                Assert.AreEqual(2, metric.textures.Count, "Retain every authored material slot.");
                foreach (var info in metric.textures) {
                    Assert.AreEqual(texture.name, info.texture);
                    Assert.AreEqual(new Vector2(1, 2), info.tiling);
                    Assert.AreEqual(.25f, info.aspect);
                }
                CollectionAssert.AreEqual(previewMaterials, renderer.sharedMaterials);
                Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
                CollectionAssert.AreEqual(uv0, mesh.uv);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                CollectionAssert.AreEqual(new[] { authored, second }, renderer.sharedMaterials);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CheckerMovesToTheNextModelAndRestoresAllPreviousMaterialSlots(bool fromStandalone, bool toStandalone)
        {
            var group = Group(true, out var oldRenderer, out var oldHidden);
            var context = Open(group);
            if (fromStandalone) {
                oldRenderer = Standalone("First standalone");
                SelectModel(oldRenderer.gameObject);
            }
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var firstMaterial = oldRenderer.sharedMaterial;
            var secondMaterial = new Material(firstMaterial); owned.Add(secondMaterial);
            var originals = new[] { firstMaterial, secondMaterial };
            oldRenderer.sharedMaterials = originals;
            var originalMesh = oldRenderer.GetComponent<MeshFilter>().sharedMesh;
            var working = Quad("Temporary repacked first model", true);
            context.MeshEntries.Find(entry => entry.renderer == oldRenderer).repackedMesh = working;
            Set(hub, "_checkerColorMode", true); Set(hub, "_checkerShowR", false);
            Call(hub, "CollectCanvasEntries");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            ChooseCameraFrame(viewport);
            canvas.Zoom = 3f; canvas.Pan = new Vector2(87, -31);
            Assert.IsTrue(ShowsChecker(oldRenderer));
            Assert.AreSame(working, oldRenderer.GetComponent<MeshFilter>().sharedMesh);

            LODGroup nextGroup = null;
            MeshRenderer nextRenderer, nextHidden = null;
            if (toStandalone) nextRenderer = Standalone("Next standalone");
            else nextGroup = Group(true, out nextRenderer, out nextHidden);
            var nextMaterials = nextRenderer.sharedMaterials;
            var nextMesh = nextRenderer.GetComponent<MeshFilter>().sharedMesh;
            UnityEditor.Selection.activeGameObject = toStandalone ? nextRenderer.gameObject : nextGroup.gameObject;
            Call(hub, "OnSelectionChange");
            CollectionAssert.AreEqual(originals, oldRenderer.sharedMaterials, "restore before collecting the new preview");
            Assert.IsFalse(CheckerTexturePreview.IsActive);
            Call(hub, "CollectCanvasEntries");

            Assert.AreEqual(UvCanvasView.PreviewMode.Checker, canvas.CurrentPreviewMode);
            Assert.IsTrue(canvas.CheckerEnabled); Assert.IsTrue(ShowsChecker(nextRenderer));
            Assert.IsTrue(canvas.CheckerColorMode); Assert.IsFalse(canvas.CheckerShowR); Assert.IsTrue(canvas.CheckerShowG);
            Assert.AreEqual(1, context.PreviewUvChannel);
            CollectionAssert.AreEqual(originals, oldRenderer.sharedMaterials);
            Assert.AreSame(originalMesh, oldRenderer.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(working == null, "the old working mesh is destroyed after its preview is restored");
            Assert.IsFalse(ShowsChecker(oldHidden));
            if (nextHidden) Assert.IsFalse(ShowsChecker(nextHidden));
            AssertCameraFrame(viewport); Assert.AreEqual(3f, canvas.Zoom); Assert.AreEqual(new Vector2(87, -31), canvas.Pan);
            Assert.AreSame(nextMesh, context.MeshEntries.Find(entry => entry.renderer == nextRenderer).fbxMesh);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            CollectionAssert.AreEqual(nextMaterials, nextRenderer.sharedMaterials);
            Assert.AreSame(nextMesh, nextRenderer.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsFalse(CheckerTexturePreview.IsActive);
        }

        void AssignLightmap(params MeshRenderer[] renderers)
        {
            var texture = new Texture2D(8, 8); owned.Add(texture);
            LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = texture } };
            foreach (var renderer in renderers) {
                renderer.lightmapIndex = 0;
                renderer.lightmapScaleOffset = new Vector4(.5f, .5f, .1f, .2f);
            }
        }

        [TestCase(UvCanvasView.PreviewMode.Off)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void DisplayModeFollowsTheNextGroupAndRestoresItsPredecessor(UvCanvasView.PreviewMode mode)
        {
            var firstGroup = Group(true, out var first, out _);
            var context = Open(firstGroup);
            var secondGroup = Group(true, out var second, out _);
            var firstMaterial = first.sharedMaterial; var firstMesh = first.GetComponent<MeshFilter>().sharedMesh;
            var secondMaterial = second.sharedMaterial; var secondMesh = second.GetComponent<MeshFilter>().sharedMesh;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, second);
                Call(hub, "ApplyPreviewMode", mode);
                var oldPreviewMesh = first.GetComponent<MeshFilter>().sharedMesh;
                SelectModel(secondGroup.gameObject);
                Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
                Assert.AreSame(firstMaterial, first.sharedMaterial);
                Assert.AreSame(firstMesh, first.GetComponent<MeshFilter>().sharedMesh);
                if (mode != UvCanvasView.PreviewMode.Off) {
                    Assert.IsTrue(oldPreviewMesh == null, "the previous preview clone was destroyed");
                    Assert.AreNotSame(secondMaterial, second.sharedMaterial);
                } else Assert.AreSame(secondMaterial, second.sharedMaterial);
                Assert.AreSame(secondMesh, context.MeshEntries.Find(entry => entry.renderer == second).fbxMesh);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(secondMaterial, second.sharedMaterial);
                Assert.AreSame(secondMesh, second.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase(UvCanvasView.PreviewMode.Checker)]
        [TestCase(UvCanvasView.PreviewMode.Shells3D)]
        [TestCase(UvCanvasView.PreviewMode.Lightmap)]
        public void MissingPreviewDataDoesNotDiscardTheSelectedDisplayMode(UvCanvasView.PreviewMode mode)
        {
            var firstGroup = Group(true, out var first, out _);
            var context = Open(firstGroup);
            var firstMaterial = first.sharedMaterial;
            var unsupported = Standalone("Model without UVs", false);
            unsupported.GetComponent<MeshFilter>().sharedMesh.uv = System.Array.Empty<Vector2>();
            var unsupportedMaterial = unsupported.sharedMaterial;
            var supported = Standalone("Next model with UVs");
            var supportedMaterial = supported.sharedMaterial;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, supported);
                Call(hub, "ApplyPreviewMode", mode);
                SelectModel(unsupported.gameObject);
                var canvas = Get<UvCanvasView>(hub, "canvas");
                Assert.AreEqual(mode, canvas.CurrentPreviewMode);
                Assert.IsFalse(canvas.CheckerEnabled);
                Assert.IsFalse(CheckerTexturePreview.IsActive); Assert.IsFalse(ShellColorModelPreview.IsActive);
                Assert.AreSame(firstMaterial, first.sharedMaterial);
                Assert.AreSame(unsupportedMaterial, unsupported.sharedMaterial);
                SelectModel(supported.gameObject);
                Assert.AreEqual(mode, canvas.CurrentPreviewMode);
                Assert.AreNotSame(supportedMaterial, supported.sharedMaterial);
                Assert.AreEqual(1, context.PreviewUvChannel);
                Assert.AreSame(unsupportedMaterial, unsupported.sharedMaterial);
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(supportedMaterial, supported.sharedMaterial);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreferredCheckerChannelReturnsAcrossModelsAndOffRemainsOff()
        {
            Open(Group(true, out var first, out _));
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var context = Get<UvToolContext>(hub, "ctx");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            var uv0Only = Standalone("UV0-only model", false);
            var uv0Material = uv0Only.sharedMaterial;
            SelectModel(uv0Only.gameObject);
            Assert.AreEqual(UvCanvasView.PreviewMode.Checker, canvas.CurrentPreviewMode);
            Assert.AreEqual(0, context.PreviewUvChannel); Assert.IsTrue(ShowsChecker(uv0Only)); Assert.IsFalse(ShowsChecker(first));
            var uv1Model = Standalone("UV1 model");
            SelectModel(uv1Model.gameObject);
            Assert.AreEqual(1, context.PreviewUvChannel); Assert.IsTrue(ShowsChecker(uv1Model));
            Assert.AreSame(uv0Material, uv0Only.sharedMaterial);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            SelectModel(uv0Only.gameObject);
            Assert.AreEqual(UvCanvasView.PreviewMode.Off, canvas.CurrentPreviewMode);
            Assert.IsFalse(ShowsChecker(uv0Only)); Assert.IsFalse(ShowsChecker(uv1Model));
        }

        [TestCase(MeshViewport3D.Shading.Normals)]
        [TestCase(MeshViewport3D.Shading.Albedo)]
        [TestCase(MeshViewport3D.Shading.NormalMap)]
        [TestCase(MeshViewport3D.Shading.Gloss)]
        [TestCase(MeshViewport3D.Shading.Metalness)]
        [TestCase(MeshViewport3D.Shading.AO)]
        public void ModelSelectionClampsAnUnavailableLodAndRetainsInspectionShading(MeshViewport3D.Shading shading)
        {
            Open(Group(true, out _, out _));
            SwitchLod(1);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            viewport.Mode = shading;
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            SwitchLod(0); Assert.AreEqual(shading, viewport.Mode);
            SwitchLod(1); Assert.AreEqual(shading, viewport.Mode);
            var singleLod = Group(true, out var next, out _);
            var original = next.sharedMaterial;
            singleLod.SetLODs(new[] { new LOD(.5f, new Renderer[] { next }) });
            SelectModel(singleLod.gameObject);
            Assert.AreEqual(0, Get<UvToolContext>(hub, "ctx").PreviewLod);
            Assert.AreEqual(shading, viewport.Mode);
            Assert.IsTrue(ShowsChecker(next));
            if (MeshViewport3D.IsMaterialMode(shading)) {
                Call(hub, "CollectViewportItems");
                var item = Get<List<MeshViewport3D.Item>>(hub, "viewportItems")[0];
                Assert.AreSame(original, item.materials[0], "Unlit channels must read the authored material beneath Checker");
            }
        }

        [Test]
        public void UvAnd3DModesKeepTheCameraFrameUntilFrameIsRequested()
        {
            Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Call(hub, "SetCanvas3D", true);
            viewport.Frame(bounds);
            ChooseCameraFrame(viewport);

            Call(hub, "SetCanvas3D", false);
            Call(hub, "SetCanvas3D", true);
            Call(viewport, "FrameIfRequested", bounds, true);
            AssertCameraFrame(viewport);

            viewport.FrameContent();
            Call(viewport, "FrameIfRequested", bounds, true);
            Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(bounds.center));
            Assert.That(Get<float>(viewport, "distance"), Is.Not.EqualTo(2f));
        }

        [Test]
        public void LodSwitchKeepsBothCameraAndUvFrame()
        {
            var ctx = Open(Group(lod1HasUv1: true, out var lod0, out var lod1));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var firstMesh = lod0.GetComponent<MeshFilter>().sharedMesh;
            var secondMesh = lod1.GetComponent<MeshFilter>().sharedMesh;
            viewport.Frame(firstMesh.bounds);
            ChooseCameraFrame(viewport);
            canvas.Zoom = 3f;
            canvas.Pan = new Vector2(87, -31);

            SwitchLod(1);
            Call(viewport, "FrameIfRequested", secondMesh.bounds, true);
            AssertCameraFrame(viewport);
            Assert.That(canvas.Zoom, Is.EqualTo(3f));
            Assert.That(canvas.Pan, Is.EqualTo(new Vector2(87, -31)));

            SwitchLod(0);
            Call(viewport, "FrameIfRequested", firstMesh.bounds, true);
            AssertCameraFrame(viewport);
            Assert.That(ctx.PreviewLod, Is.EqualTo(0));
        }

        [Test]
        public void SelectingAnotherModelKeepsTheCameraFrame()
        {
            var ctx = Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
            ChooseCameraFrame(viewport);

            var nextGroup = Group(lod1HasUv1: true, out _, out _);
            nextGroup.transform.position = new Vector3(20, -7, 30);
            Call(hub, "RestoreWorkingMeshes");
            ctx.Refresh(nextGroup);
            Call(hub, "CollectCanvasEntries");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            Assert.That(items[0].matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(nextGroup.transform.position, Is.EqualTo(new Vector3(20, -7, 30)), "preview does not move the scene object");
            var bounds = PreviewBounds(items);
            Call(viewport, "FrameIfRequested", bounds, true);
            AssertCameraFrame(viewport);
        }

        [Test]
        public void OriginPlacementPreservesChildPlacementAndLodSwitch()
        {
            var group = Group(lod1HasUv1: true, out var lod0, out var lod1);
            var offset = new Vector3(40, -10, 70);
            group.transform.position = offset;
            group.transform.rotation = Quaternion.Euler(17, 35, -11);
            group.transform.localScale = new Vector3(2, 3, .5f);
            lod0.transform.localPosition = new Vector3(1, 2, -3);
            lod1.transform.localPosition = lod0.transform.localPosition;
            lod0.transform.localRotation = Quaternion.Euler(0, 25, 0);
            lod1.transform.localRotation = lod0.transform.localRotation;
            Open(group);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            ChooseCameraFrame(viewport);
            for (int lod = 0; lod < 2; ++lod) {
                SwitchLod(lod);
                var item = Get<List<MeshViewport3D.Item>>(hub, "viewportItems")[0];
                var renderer = lod == 0 ? lod0 : lod1;
                foreach (var point in new[] { Vector3.zero, Vector3.one, new Vector3(-2, 3, 4) }) {
                    var expected = Matrix4x4.TRS(renderer.transform.localPosition, renderer.transform.localRotation, renderer.transform.localScale).MultiplyPoint3x4(point);
                    Assert.That(Vector3.Distance(item.matrix.MultiplyPoint3x4(point), expected), Is.LessThan(1e-5f));
                }
                Call(viewport, "FrameIfRequested", PreviewBounds(new[] { item }), true);
                AssertCameraFrame(viewport);
            }
            Assert.That(group.transform.position, Is.EqualTo(offset));
        }

        [Test]
        public void StandalonePreviewUsesZeroPositionWithoutChangingItsSceneTransform()
        {
            var group = Group(lod1HasUv1: true, out var renderer, out _);
            var ctx = Open(group);
            renderer.transform.position = new Vector3(-30, 12, 17);
            renderer.transform.rotation = Quaternion.Euler(5, 25, 45);
            renderer.transform.localScale = new Vector3(2, 1, 3);
            var original = renderer.localToWorldMatrix;
            Call(hub, "RestoreWorkingMeshes");
            ctx.RefreshStandalone(renderer);
            Call(hub, "CollectCanvasEntries");
            var item = Get<List<MeshViewport3D.Item>>(hub, "viewportItems")[0];
            Assert.That(item.matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(Vector3.Distance(item.matrix.MultiplyVector(Vector3.one), Vector3.one), Is.LessThan(1e-5f));
            Assert.That(renderer.localToWorldMatrix, Is.EqualTo(original));
        }

        [Test]
        public void TextureAoPreviewUsesCapturedRootLocalGeometry()
        {
            var panel = new TextureAoBakePanel();
            var resultType = typeof(TextureAoBakePanel).GetNestedType("Result", BindingFlags.NonPublic);
            var result = System.Activator.CreateInstance(resultType, true);
            var mesh = Quad("AO preview", true);
            var snapshot=TransferMeshSnapshot.Capture(mesh);
            resultType.GetField("mesh", Private).SetValue(result, mesh);
            ((System.Collections.IList)typeof(TextureAoBakePanel).GetField("results", Private).GetValue(panel)).Add(result);
            var items = new List<MeshViewport3D.Item>();
            Assert.IsTrue(panel.Get3DContent(items));
            Assert.That(items[0].matrix.MultiplyPoint3x4(Vector3.zero), Is.EqualTo(Vector3.zero));
            Assert.That(items[0].matrix.MultiplyVector(Vector3.one), Is.EqualTo(Vector3.one));
            CollectionAssert.AreEqual(snapshot,TransferMeshSnapshot.Capture(mesh));
        }

        [TestCase(.001f)]
        [TestCase(1f)]
        [TestCase(1000f)]
        public void FirstContentIsFramedThenOnlyExplicitRequestsReframe(float size)
        {
            using (var viewport = new MeshViewport3D())
            {
                var bounds = new Bounds(new Vector3(20, 0, 0), new Vector3(4, 3, 1) * size);
                Call(viewport, "FrameIfRequested", bounds, false);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(Vector3.zero));
                Assert.That(Get<float>(viewport, "distance"), Is.EqualTo(5f));
                Call(viewport, "FrameIfRequested", bounds, true);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(bounds.center));
                Assert.Greater(Get<float>(viewport, "distance"), bounds.extents.magnitude);

                ChooseCameraFrame(viewport);
                Call(viewport, "FrameIfRequested", bounds, true);
                AssertCameraFrame(viewport);
                viewport.FrameContent();
                Call(viewport, "FrameIfRequested", bounds, false);
                AssertCameraFrame(viewport);
                Call(viewport, "FrameIfRequested", bounds, true);
                Assert.That(Get<Vector3>(viewport, "pivot"), Is.EqualTo(bounds.center));
            }
        }

        [Test]
        public void ContentSizeRefreshesZoomBoundsWithoutReframing([Values(.001f, 1000f)] float size)
        {
            using var view = new MeshViewport3D(); view.Frame(new Bounds(Vector3.zero, Vector3.one)); ChooseCameraFrame(view);
            var bounds = new Bounds(new Vector3(40, -20, 10), Vector3.one * size);
            Call(view, "FrameIfRequested", bounds, true);
            AssertCameraFrame(view);
            Assert.AreEqual(bounds.extents.magnitude, Get<float>(view, "radius"), size * .00001f);
        }

        [UnityTest]
        public IEnumerator DistantContentRemainsVisibleWithoutReframing()
        {
            var mesh = Quad("Distant preview", true);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            var material = new Material(Shader.Find("Hidden/MeshLab/UvOverlay")); owned.Add(material);
            material.SetColor("_Color", Color.red);
            var items = new[] { new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.forward * 20), new[] { material }) };
            using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowAxes = false, ShowGrid = false };
            view.Frame(mesh.bounds);
            Set(view, "pivot", new Vector3(.5f, .5f, 0)); Set(view, "distance", 2f);
            var window = OpenSpotInputWindow(); int redPixels = 0;
            try {
                window.Input = current => {
                    if (current.type != EventType.Repaint) return;
                    view.Draw(new Rect(0, 0, 256, 128), items, null);
                    var copy = GpuReadback.Read(Get<RenderTexture>(view, "offscreen"), 256, 128, hdr: false);
                    if (!copy) return;
                    try { redPixels = 0; foreach (var pixel in copy.GetPixels()) if (pixel.r > .5f && pixel.g < .2f) ++redPixels; }
                    finally { Object.DestroyImmediate(copy); }
                };
                for (int i = 0; i < 30 && redPixels < 30; ++i) { window.Repaint(); yield return null; }
                Assert.Greater(redPixels, 30, "Current bounds must extend the clip plane without moving the camera.");
                Assert.AreEqual(new Vector3(.5f, .5f, -2), view.Camera.transform.position);
                Assert.AreEqual(2f, Get<float>(view, "distance"));
                Assert.Greater(view.Camera.farClipPlane, 22f);
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); }
        }

        [TestCase("uv2_transfer")]
        [TestCase("cleanup")]
        [TestCase("prefab_builder")]
        public void CanvasPlacementAndPickingIgnoreSceneRootAcrossToolsAndLods(string toolId)
        {
            var group = Group(true, out var lod0, out var lod1);
            var ancestor = new GameObject("SceneAncestor"); owned.Add(ancestor);
            group.transform.SetParent(ancestor.transform, false);
            ancestor.transform.SetPositionAndRotation(new Vector3(50, -30, 40), Quaternion.Euler(20, 75, 30));
            ancestor.transform.localScale = new Vector3(-2, 3, .5f);
            group.transform.localPosition = new Vector3(9, 4, 2);
            group.transform.localRotation = Quaternion.Euler(15, 35, 70);
            group.transform.localScale = new Vector3(3, .2f, 4);
            lod0.transform.localPosition = lod1.transform.localPosition = Vector3.right * 2;
            Open(group);
            Call(hub, "SelectToolById", toolId);
            using (var inspection = new MeshInspection())
            for (int lod = 0; lod < 2; ++lod) {
                SwitchLod(lod);
                var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
                Assert.That(items.Count, Is.EqualTo(1));
                Assert.AreEqual(Matrix4x4.Translate(Vector3.right * 2), items[0].matrix);
                Assert.IsTrue(inspection.Pick(items, new Vector3(2.3f, .2f, -2), Vector3.forward, out int picked));
                Assert.AreEqual(0, picked);
                group.transform.localScale = Vector3.zero;
            }
            Assert.AreEqual(new Vector3(9, 4, 2), group.transform.localPosition, "preview must not edit scene transforms");
            Assert.That(Quaternion.Angle(Quaternion.Euler(15, 35, 70), group.transform.localRotation), Is.LessThan(.01f));
            Assert.AreEqual(new Vector3(-2, 3, .5f), ancestor.transform.localScale);
        }

        [Test]
        public void CheckerFollowsThePreviewLod()
        {
            var group = Group(lod1HasUv1: true, out var lod0, out var lod1);
            var ctx = Open(group);
            var canvas = Get<UvCanvasView>(hub, "canvas");

            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);

            SwitchLod(1);
            Assert.That(ctx.PreviewLod, Is.EqualTo(1));
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker), "the mode survives the switch");
            Assert.That(ShowsChecker(lod1), Is.True, "the checker moves to the LOD now shown");
            Assert.That(ShowsChecker(lod0), Is.False, "the hidden LOD gets its material back");

            SwitchLod(0);
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);
        }

        [Test]
        public void PreviewFollowsCompletedAndRepeatedTransfer(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool threeD)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var originalMaterial = targetRenderer.sharedMaterial;
            var originalMesh = target.fbxMesh;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(targetRenderer);
                SwitchLod(1); Call(hub, "SetCanvas3D", threeD);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                for (int pass = 0; pass < 2; ++pass) {
                    var previousOutput = target.transferredMesh;
                    var packed = Object.Instantiate(source.fbxMesh); owned.Add(packed);
                    packed.uv2 = ShiftedUvs(.1f + pass * .2f); source.repackedMesh = packed;
                    source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
                    UvProgress.Begin("Preview transfer regression", cancelable: true);
                    try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
                    finally { UvProgress.End(); }
                    Assert.NotNull(target.transferredMesh);
                    Assert.IsTrue(previousOutput == null, "Repeated transfer releases its previous output.");
                    Assert.IsNotNull(targetRenderer.GetComponent<MeshFilter>().sharedMesh,
                        "Replacing a transfer output must not leave a renderer bound to a destroyed mesh until repaint.");
                    Call(hub, "CollectCanvasEntries");
                    AssertFreshPreview(ctx, target, mode);
                    AssertPreviewFrame();
                }
                Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
                Assert.AreSame(originalMaterial, targetRenderer.sharedMaterial);
                Assert.AreSame(originalMesh, targetRenderer.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewFollowsMeshReplacementAndInPlaceUvChanges(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values("original", "repack", "transfer")] string stage,
            [Values(false, true)] bool inPlace)
        {
            var ctx = Open(Group(true, out var source, out var target));
            SwitchLod(stage == "transfer" ? 1 : 0);
            var entry = ctx.MeshEntries.Find(e => e.renderer == (stage == "transfer" ? target : source));
            var mesh = Object.Instantiate(entry.fbxMesh); owned.Add(mesh);
            SetStageMesh(entry, stage, mesh);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(entry.renderer as MeshRenderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame();
                Call(hub, "ApplyPreviewMode", mode); Call(hub, "CollectCanvasEntries");
                var oldPreview = entry.meshFilter.sharedMesh;
                if (inPlace) VertexChannels.SetUvs(mesh, 1, ShiftedUvs(.3f));
                else {
                    mesh = Object.Instantiate(entry.fbxMesh); owned.Add(mesh); mesh.uv2 = ShiftedUvs(.3f);
                    SetStageMesh(entry, stage, mesh);
                }
                var canvas = Get<UvCanvasView>(hub, "canvas");
                canvas.HasHoveredShell = true; canvas.HoveredShellId = 777;
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
                Assert.IsFalse(canvas.HasHoveredShell, "Spot cannot retain a hit on obsolete UV data.");
                if (mode != UvCanvasView.PreviewMode.Checker) Assert.IsTrue(oldPreview == null, "Old preview clones are released.");
                var currentPreview = entry.meshFilter.sharedMesh;
                Call(hub, "CollectCanvasEntries");
                Assert.AreSame(currentPreview, entry.meshFilter.sharedMesh, "Repainting unchanged data must not recreate preview clones.");
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void AutoTuneRestoresTheSourceAtlasTogetherWithItsTransfer()
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var bestType = typeof(UvTransferWorkflow).GetNestedType("AutoTuneChoice", BindingFlags.NonPublic);
            var best = System.Activator.CreateInstance(bestType, true);
            source.repackedMesh = Object.Instantiate(source.fbxMesh);
            source.repackedMesh.uv2 = ShiftedUvs(.1f);
            source.repackedAtlasWidth = 512; source.repackedAtlasHeight = 512;
            target.transferredMesh = Object.Instantiate(target.fbxMesh);
            target.transferredMesh.uv2 = ShiftedUvs(.1f);
            var expected = source.repackedMesh.uv2;
            CallWorkflow("CaptureAutoTuneChoice", best);
            var savedAtlas = SavedAutoTuneMesh(best, "Atlases", source);
            var savedTransfer = SavedAutoTuneMesh(best, "Transfers", target);
            CallWorkflow("CaptureAutoTuneChoice", best);
            Assert.IsTrue(savedAtlas == null, "Replacing the best attempt releases its atlas snapshot.");
            Assert.IsTrue(savedTransfer == null, "Replacing the best attempt releases its transfer snapshot.");
            var oldAtlas = source.repackedMesh; var oldTransfer = target.transferredMesh;
            source.repackedMesh.uv2 = ShiftedUvs(.5f);
            source.repackedAtlasWidth = 1024; source.repackedAtlasHeight = 1024;
            target.transferredMesh.uv2 = ShiftedUvs(.5f);
            var restore = typeof(UvTransferWorkflow).GetMethod("RestoreAutoTuneChoice", Private | BindingFlags.Static);
            restore.Invoke(restore.IsStatic ? null : hub.DiagnosticWorkflow, new[] { best });
            CollectionAssert.AreEqual(expected, source.repackedMesh.uv2, "The target cannot use an atlas from a discarded attempt.");
            CollectionAssert.AreEqual(expected, target.transferredMesh.uv2);
            Assert.AreEqual(512, source.repackedAtlasWidth); Assert.AreEqual(512, source.repackedAtlasHeight);
            Assert.IsTrue(oldAtlas == null); Assert.IsTrue(oldTransfer == null);
            ((System.IDisposable)best).Dispose();
            Assert.IsNotNull(source.repackedMesh); Assert.IsNotNull(target.transferredMesh);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FullPipelineCannotTransferOrCompleteAfterFailedSourceRepack(bool partialSuccess)
        {
            var group = Group(true, out var sourceRenderer, out var targetRenderer);
            sourceRenderer.GetComponent<MeshFilter>().sharedMesh.uv = System.Array.Empty<Vector2>();
            if (partialSuccess) {
                var valid = Lod(group.transform, "Other_LOD0", Quad("Other_LOD0", true));
                group.SetLODs(new[] { new LOD(.5f, new Renderer[] { sourceRenderer, valid }),
                    new LOD(.1f, new Renderer[] { targetRenderer }) });
            }
            var ctx = Open(group); ctx.AtlasResolution = 64; ctx.InternalOversample = 1;
            ctx.RepackResolutionMode = ResolutionMode.Manual; ctx.RepackPerMesh = true;
            typeof(UvTransferWorkflow).GetField("stageRunAnalyzeUv0", Private).SetValue(hub.DiagnosticWorkflow, false);
            typeof(UvTransferWorkflow).GetField("stageRunWeldUv0", Private).SetValue(hub.DiagnosticWorkflow, false);
            typeof(UvTransferWorkflow).GetField("skipSymmetrySplitStep", Private).SetValue(hub.DiagnosticWorkflow, true);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("\\[Pipeline\\] Repack failed"));
            var completed = ((System.Threading.Tasks.Task<bool>)CallWorkflow("ExecFullPipelineCoreImpl", false)).GetAwaiter().GetResult();
            Assert.IsFalse(completed, "A partial/all failed repack is not a completed pipeline.");
            Assert.IsFalse(ctx.HasTransfer);
            Assert.IsNull(ctx.MeshEntries.Find(e => e.renderer == targetRenderer).transferredMesh);
            var stages = (System.Array)typeof(UvTransferWorkflow).GetField("stageOutcome", Private).GetValue(hub.DiagnosticWorkflow);
            Assert.AreEqual("Failed", stages.GetValue(4).ToString());
            Assert.AreEqual("Skipped", stages.GetValue(5).ToString());
            Assert.IsFalse(ctx.HasRepack, "A failed auto-tune attempt rolls back every partial source output.");
            Assert.IsTrue(ctx.MeshEntries.All(e => e.repackedMesh == null && e.transferredMesh == null));
        }

        [TestCase(".001")]
        [TestCase(".002")]
        public void NumericInstanceLodTransfersFromItsOwnSourceInsteadOfTheFirstRenderer(string instance)
        {
            var root = new GameObject("Train"); owned.Add(root);
            var door = Lod(root.transform, "Door_LOD0", Quad("Door_LOD0", true));
            var source = Lod(root.transform, "Seat_LOD0" + instance, Quad("Seat_LOD0" + instance, true));
            var target = Lod(root.transform, "Seat_LOD1" + instance, Quad("Seat_LOD1" + instance, false));
            var expected = ShiftedUvs(.5f); source.GetComponent<MeshFilter>().sharedMesh.uv2 = expected;
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(.5f, new Renderer[] { door, source }), new LOD(.1f, new Renderer[] { target }) });
            var ctx = Open(group); ctx.SourceLodIndex = 0;
            UvProgress.Begin("Instance source regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            var output = ctx.MeshEntries.Find(e => e.renderer == target).transferredMesh;
            Assert.IsNotNull(output);
            for (int i = 0; i < expected.Length; ++i)
                Assert.That(Vector2.Distance(expected[i], output.uv2[i]), Is.LessThan(1e-5), "LOD instance must retain its matching source atlas.");
        }

        static Mesh SavedAutoTuneMesh(object best, string field, MeshEntry entry)
        {
            var dictionary = (System.Collections.IDictionary)best.GetType().GetField(field, Private).GetValue(best);
            var snapshot = dictionary[entry];
            return (Mesh)snapshot.GetType().GetField("Item1").GetValue(snapshot);
        }

        [TestCase("replacement")]
        [TestCase("winner")]
        [TestCase("in-place")]
        public void TransferAfterSourceReplacementUsesTheCurrentChartIndices(string change)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var bestType = typeof(UvTransferWorkflow).GetNestedType("AutoTuneChoice", BindingFlags.NonPublic);
            var best = System.Activator.CreateInstance(bestType, true);
            source.repackedMesh = NearbyChartAtlas(false);
            source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
            try {
                ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferAllImpl", false)).GetAwaiter().GetResult();
                Assert.AreEqual(0, target.shellTransferResult.targetShellToSourceShell[0]);
                var expected = target.transferredMesh.uv2;
                CallWorkflow("CaptureAutoTuneChoice", best);

                Object.DestroyImmediate(source.repackedMesh);
                source.repackedMesh = NearbyChartAtlas(true);
                ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferAllImpl", false)).GetAwaiter().GetResult();
                Assert.AreEqual(1, target.shellTransferResult.targetShellToSourceShell[0],
                    "The discarded atlas stores the same physical chart at a different index.");

                if (change == "winner") CallWorkflow("RestoreAutoTuneChoice", best);
                else if (change == "in-place") {
                    var replacement = NearbyChartAtlas(false);
                    source.repackedMesh.vertices = replacement.vertices;
                    source.repackedMesh.uv = replacement.uv;
                    source.repackedMesh.uv2 = replacement.uv2;
                    source.repackedMesh.triangles = replacement.triangles;
                    source.repackedMesh.RecalculateNormals();
                    VertexChannels.RaiseChanged(source.repackedMesh);
                }
                else {
                    Object.DestroyImmediate(source.repackedMesh);
                    source.repackedMesh = NearbyChartAtlas(false);
                }
                UvProgress.Begin("Source chart index regression", cancelable: true);
                try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
                finally { UvProgress.End(); }
                Assert.AreEqual(0, target.shellTransferResult.targetShellToSourceShell[0],
                    "Hints from the discarded atlas must not select the nearby physical chart.");
                for (int i = 0; i < expected.Length; ++i)
                    Assert.That(Vector2.Distance(expected[i], target.transferredMesh.uv2[i]), Is.LessThan(.00001f));
            }
            finally { ((System.IDisposable)best).Dispose(); }
        }

        Mesh NearbyChartAtlas(bool reverseOrder)
        {
            var mesh = new Mesh { name = "Nearby charts with different atlas locations" };
            var positions = new Vector3[8]; var uv0 = new Vector2[8]; var uv2 = new Vector2[8];
            var quad = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            var authored = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            for (int chart = 0; chart < 2; ++chart) {
                bool nearby = (chart == 1) != reverseOrder;
                var packed = ShiftedUvs(nearby ? .7f : .1f);
                for (int corner = 0; corner < 4; ++corner) {
                    int vertex = chart * 4 + corner;
                    positions[vertex] = quad[corner] + (nearby ? Vector3.forward * .01f : Vector3.zero);
                    uv0[vertex] = authored[corner]; uv2[vertex] = packed[corner];
                }
            }
            mesh.vertices = positions; mesh.uv = uv0; mesh.uv2 = uv2;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            mesh.RecalculateNormals(); owned.Add(mesh); return mesh;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TransferReleasesSceneBindingsToItsPreviousWorkingOutput(bool checker)
        {
            var ctx = Open(Group(true, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            source.repackedMesh = Object.Instantiate(source.fbxMesh);
            source.repackedAtlasWidth = source.repackedAtlasHeight = 512;
            var previousOutput = target.transferredMesh = Object.Instantiate(target.fbxMesh);
            target.meshFilter.sharedMesh = previousOutput;
            SwitchLod(1);
            if (checker) Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            UvProgress.Begin("Working output binding regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            Assert.IsTrue(previousOutput == null);
            Assert.AreSame(target.fbxMesh, target.meshFilter.sharedMesh,
                "Even a preview backup may refer to the previous working output.");
            Call(hub, "CollectCanvasEntries");
            if (checker) AssertFreshPreview(ctx, target, UvCanvasView.PreviewMode.Checker);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Off);
            Assert.AreSame(target.fbxMesh, target.meshFilter.sharedMesh);
        }

        [Test]
        public void PreviewRestoresPreferredChannelWhenTransferCreatesUv2()
        {
            var ctx = Open(Group(false, out var sourceRenderer, out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            SwitchLod(1); Assert.AreEqual(0, ctx.PreviewUvChannel);
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            source.repackedMesh = Object.Instantiate(source.fbxMesh); owned.Add(source.repackedMesh);
            source.repackedMesh.uv2 = ShiftedUvs(.2f);
            UvProgress.Begin("Preview transfer channel regression", cancelable: true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl", 1, false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            Call(hub, "CollectCanvasEntries");
            Assert.AreEqual(1, ctx.PreviewUvChannel);
            AssertFreshPreview(ctx, target, UvCanvasView.PreviewMode.Checker);
        }

        [TestCase(false,false)] [TestCase(false,true)] [TestCase(true,false)] [TestCase(true,true)]
        public void ReverseTransferPublishesTheWholeChainAndKeepsCheckerAndCamera(bool prepareSeed,bool fillAtlas)
        {
            var ctx = Open(Group(true, out var fineRenderer, out var coarseRenderer));
            var workflow = hub.DiagnosticWorkflow;
            typeof(UvTransferWorkflow).GetField("reverseUseForwardCharts",Private).SetValue(workflow,false);
            typeof(UvTransferWorkflow).GetField("reversePrepareSeed",Private).SetValue(workflow,prepareSeed);
            typeof(UvTransferWorkflow).GetField("reverseFillAtlas",Private).SetValue(workflow,fillAtlas);
            var oldSource = ctx.SourceLodIndex;
            ctx.RepackResolutionMode = ResolutionMode.Manual; ctx.AtlasResolution = 128;
            var coarse = ctx.MeshEntries.Find(e => e.renderer == coarseRenderer);
            var fine = ctx.MeshEntries.Find(e => e.renderer == fineRenderer);
            // This fixture's third corner has Z=1: make it planar to establish an isotropic seed.
            coarse.originalMesh.vertices = fine.originalMesh.vertices = new[] {Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up};
            coarse.originalMesh.uv2 = ShiftedUvs(.7f);
            var originalSeedUvs=coarse.originalMesh.uv2;
            var originalCoarseIndices = coarse.originalMesh.triangles.Concat(new[] { 0, 0, 0 }).ToArray();
            var originalFineIndices = fine.originalMesh.triangles.Concat(new[] { 0, 0, 0 }).ToArray();
            coarse.originalMesh.triangles = originalCoarseIndices;
            fine.originalMesh.triangles = originalFineIndices;
            var existingCoarse=coarse.transferredMesh=Object.Instantiate(coarse.originalMesh);
            // A valid current working layout must be consumed even if the raw
            // donor UV2 is unusable. Preparation still uses original UV0.
            coarse.originalMesh.uv2=Enumerable.Repeat(Vector2.one*.7f,coarse.originalMesh.vertexCount).ToArray();
            originalSeedUvs=coarse.originalMesh.uv2;
            var prior = fine.transferredMesh = Object.Instantiate(fine.originalMesh);
            var previousDirectory = BenchmarkRecorder.OutputDirectoryOverride; var previousLevel = UvtLog.Current;
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"MeshLabReversePreview_"+System.Guid.NewGuid().ToString("N"));
            try
            {
                BenchmarkRecorder.OutputDirectoryOverride = directory;
                UvtLog.Current = UvtLog.Level.Verbose; // the audit report is written only at Verbose
                Call(hub,"CollectCanvasEntries"); SetPreviewFrame(); Call(hub,"ApplyPreviewMode",UvCanvasView.PreviewMode.Checker);
                workflow.RunReverseTransfer(false).GetAwaiter().GetResult();
                Assert.IsTrue(prior == null);Assert.IsTrue(existingCoarse==null); Assert.AreEqual(oldSource,ctx.SourceLodIndex);
                Assert.IsTrue(ctx.HasRepack); Assert.IsTrue(ctx.HasTransfer);
                Assert.IsNotNull(coarse.repackedMesh); Assert.IsNotNull(fine.transferredMesh);
                var finalMin=coarse.repackedMesh.uv2.Aggregate(Vector2.Min);
                Assert.Greater((finalMin-originalSeedUvs.Aggregate(Vector2.Min)).magnitude,.1f,
                    "The complete seed must move even when preparation or optional uniform filling is disabled.");
                foreach(var vertex in coarse.repackedMesh.vertices.Distinct())
                {
                    var a=coarse.repackedMesh.uv2[System.Array.IndexOf(coarse.repackedMesh.vertices,vertex)];
                    var b=fine.transferredMesh.uv2[System.Array.IndexOf(fine.transferredMesh.vertices,vertex)];
                    Assert.Less((a-b).magnitude,.0001f,"The final donor move must propagate to its recipient.");
                }
                CollectionAssert.AreEqual(originalSeedUvs,coarse.originalMesh.uv2);
                Assert.AreEqual(coarse.repackedAtlasWidth,fine.repackedAtlasWidth);
                Assert.IsNotEmpty(fine.reverseTransferJson);
                Assert.AreSame(coarse.repackedMesh, ctx.DMesh(coarse));
                Assert.AreSame(fine.transferredMesh, ctx.DMesh(fine));
                Assert.AreSame(coarse.repackedMesh, ctx.Assets.GetResultMesh(coarse));
                Assert.AreSame(fine.transferredMesh, ctx.Assets.GetResultMesh(fine));
                var reverseFine = fine.transferredMesh; var reverseCoarse = coarse.repackedMesh;
                Assert.Throws<System.InvalidOperationException>(() => ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl",1,false)).GetAwaiter().GetResult());
                Assert.AreSame(reverseFine,fine.transferredMesh); Assert.AreSame(reverseCoarse,coarse.repackedMesh);
                CollectionAssert.AreEqual(originalCoarseIndices, coarse.originalMesh.triangles);
                CollectionAssert.AreEqual(originalFineIndices, fine.originalMesh.triangles);
                Assert.AreEqual(originalCoarseIndices.Length - 3, coarse.repackedMesh.triangles.Length);
                Assert.AreEqual(originalFineIndices.Length - 3, fine.transferredMesh.triangles.Length);
                Assert.AreEqual(1, ReverseUvAudit.ParseProvenance(fine.reverseTransferJson).removedSourceFaces.Length);
                Call(hub,"CollectCanvasEntries"); AssertFreshPreview(ctx,fine,UvCanvasView.PreviewMode.Checker); AssertPreviewFrame();
                Assert.AreEqual(1,System.IO.Directory.GetFiles(directory,"*.json").Length);
                var saved = SidecarStore.TryBuildEntry(fine,fine.transferredMesh,false,SidecarStore.AoUvTarget.None,out var sidecar);
                Assert.IsFalse(saved, "Legacy sidecars cannot replay the cleaned topology; refuse rather than silently losing it.");
                Assert.IsNull(sidecar);
            }
            finally
            {
                BenchmarkRecorder.OutputDirectoryOverride = previousDirectory; UvtLog.Current = previousLevel;
                if(System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory,true);
                ctx.SourceLodIndex = oldSource;
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void FrozenTableForwardAfterReverseMatchesFreshPipeline(bool cuts)
        {
            string fixtures = System.Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_FIXTURES");
            if (string.IsNullOrEmpty(fixtures)) Assert.Ignore("Optional private FBX fixtures are not configured.");
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(fixtures+"/Cafe_Table_A_01.fbx");
            Assert.NotNull(prefab);
            var instance = Object.Instantiate(prefab); owned.Add(instance);
            var group = instance.GetComponent<LODGroup>() ?? instance.AddComponent<LODGroup>();
            group.SetLODs(Enumerable.Range(0,3).Select(lod => new LOD(.6f/(lod+1),
                instance.GetComponentsInChildren<Renderer>().Where(r => r.GetComponent<MeshFilter>()
                    && r.GetComponent<MeshFilter>().sharedMesh.name.Contains("LOD"+lod)).ToArray())).ToArray());
            var ctx = Open(group); ctx.SourceLodIndex = 0; ctx.CutNarrowUvJunctions = cuts;
            ctx.LightmapDensity = 10; ctx.RepackResolutionMode = ResolutionMode.AutoFromTexelDensity;
            ctx.ShellPaddingPx = 2; ctx.BorderPaddingPx = 0;
            hub.DiagnosticWorkflow.SymmetrySplitMode = SymmetrySplitShells.ThresholdMode.Adaptive;
            var oldOutput = BenchmarkRecorder.OutputDirectoryOverride;
            string output = System.Environment.GetEnvironmentVariable("MESHLAB_JUNCTION_OUTPUT");
            if(!string.IsNullOrEmpty(output)) System.IO.Directory.CreateDirectory(output);
            try
            {
                BenchmarkRecorder.OutputDirectoryOverride = output;
                ((System.Threading.Tasks.Task)CallWorkflow("ExecFullPipelineImpl","forward-before-reverse",false)).GetAwaiter().GetResult();
                Assert.IsTrue(ctx.HasRepack && ctx.HasTransfer,"The forward pipeline must complete before comparing outputs.");
                var first = ctx.MeshEntries.ToDictionary(e => e, e => TransferMeshSnapshot.Capture(ctx.Assets.GetResultMesh(e)));
                foreach (var entry in ctx.MeshEntries)
                {
                    var result = ctx.Assets.GetResultMesh(entry);
                    var q = TransferUvQuality.Measure(result,result.uv2,Vector2.one,Matrix4x4.identity);
                    if (!string.IsNullOrEmpty(output))
                        System.IO.File.WriteAllText(System.IO.Path.Combine(output,$"pipeline-{cuts}-{entry.lodIndex}.json"),UnityEngine.JsonUtility.ToJson(q,true));
                }
                hub.DiagnosticWorkflow.RunReverseTransfer(false).GetAwaiter().GetResult();
                Assert.AreEqual(0,ctx.SourceLodIndex);
                ((System.Threading.Tasks.Task)CallWorkflow("ExecFullPipelineImpl","forward-after-reverse",false)).GetAwaiter().GetResult();
                Assert.IsTrue(ctx.HasRepack && ctx.HasTransfer);
                Assert.AreEqual(0,ctx.SourceLodIndex);
                foreach (var entry in ctx.MeshEntries)
                {
                    Assert.IsNull(entry.reverseTransferJson);
                    CollectionAssert.AreEqual(first[entry],TransferMeshSnapshot.Capture(ctx.Assets.GetResultMesh(entry)));
                }
            }
            finally { BenchmarkRecorder.OutputDirectoryOverride = oldOutput; }
        }

        [TestCase("Assets/JunctionFixtures/Cafe_Table_A_01.fbx")]
        [TestCase("Assets/TransferBenchmarkInputs/Modern_Kitchen_A.fbx")]
        [TestCase("Assets/KamazFixtures/kamaz_Off.fbx")]
        public void FrozenForwardModelQualityCapture(string asset)
        {
            string output=System.Environment.GetEnvironmentVariable("MESHLAB_FORWARD_QUALITY_OUTPUT");
            if (string.IsNullOrEmpty(output)) Assert.Ignore("Optional private forward quality fixtures are not configured.");
            var prefab=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(asset); Assert.NotNull(prefab,asset);
            var instance=Object.Instantiate(prefab); owned.Add(instance);
            var group=instance.GetComponent<LODGroup>() ?? instance.AddComponent<LODGroup>();
            var renderers=instance.GetComponentsInChildren<Renderer>(true).Where(r=>r.GetComponent<MeshFilter>() && r.GetComponent<MeshFilter>().sharedMesh).ToArray();
            var levels=renderers.Select(r=>(renderer:r,match:System.Text.RegularExpressions.Regex.Match(r.GetComponent<MeshFilter>().sharedMesh.name,@"_LOD(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                .Where(p=>p.match.Success).GroupBy(p=>int.Parse(p.match.Groups[1].Value)).OrderBy(g=>g.Key).ToArray();
            Assert.GreaterOrEqual(levels.Length,2);
            group.SetLODs(levels.Select(g=>new LOD(.6f/(g.Key+1),g.Select(p=>p.renderer).ToArray())).ToArray());
            var ctx=Open(group); ctx.SourceLodIndex=0; ctx.LightmapDensity=10; ctx.CutNarrowUvJunctions=true;
            if(float.TryParse(System.Environment.GetEnvironmentVariable("MESHLAB_FORWARD_DENSITY"),System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out var density)) ctx.LightmapDensity=density;
            ctx.RepackResolutionMode=ResolutionMode.AutoFromTexelDensity; ctx.ShellPaddingPx=2; ctx.BorderPaddingPx=0;
            ctx.CaptureNextTransfer=true;
            if(float.TryParse(System.Environment.GetEnvironmentVariable("MESHLAB_TRANSFER_BOUNDARY_TEXELS"),System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out var tolerance)) hub.DiagnosticWorkflow.TransferBoundaryToleranceTexels=tolerance;
            hub.DiagnosticWorkflow.SymmetrySplitMode=SymmetrySplitShells.ThresholdMode.Adaptive;
            string directory=System.IO.Path.Combine(output,System.IO.Path.GetFileNameWithoutExtension(asset));
            System.IO.Directory.CreateDirectory(directory);
            ((System.Threading.Tasks.Task)CallWorkflow("ExecFullPipelineImpl","forward-quality",false)).GetAwaiter().GetResult();
            Assert.IsTrue(ctx.HasRepack && ctx.HasTransfer);
            TransferBenchmarkExport.Forward(directory,ctx);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"capture.txt"),TransferCaseCapture.LastManifest);
            var measurements=new Dictionary<MeshEntry,TransferUvQuality>();
            foreach (var entry in ctx.MeshEntries)
            {
                var result=ctx.Assets.GetResultMesh(entry);
                var quality=TransferUvQuality.Measure(result,result.uv2,Vector2.one,entry.renderer.localToWorldMatrix);
                measurements.Add(entry,quality);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,entry.fbxMesh.name+".json"),JsonUtility.ToJson(quality,true));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,entry.fbxMesh.name+".bin"),TransferMeshSnapshot.Capture(result));
            }
            foreach (var entry in ctx.MeshEntries)
            {
                var quality=measurements[entry];
                Assert.AreEqual(0,quality.invalidFaces,entry.fbxMesh.name);
                Assert.AreEqual(0,quality.outOfBoundsVertices,entry.fbxMesh.name);
                Assert.IsTrue(quality.overlapScanComplete,entry.fbxMesh.name);
                if(entry.fbxMesh.name.StartsWith("Cafe_Table_A_01"))
                {
                    Assert.AreEqual(0,quality.overlapPairs);
                    Assert.AreEqual(0,quality.degenerateFaces);
                    if(entry.lodIndex<2) Assert.Less(quality.worstAnisotropy,1.6);
                    else
                    {
                        Assert.Less(quality.areaWeightedAnisotropy,7);
                        Assert.Less(quality.worstAnisotropy,2500);
                    }
                }
                if(entry.fbxMesh.name=="Kamaz_Typhoon_LOD1")
                {
                    Assert.Less(quality.areaWeightedAnisotropy,1200);
                    Assert.LessOrEqual(quality.degenerateFaces,160);
                    Assert.LessOrEqual(quality.overlapPairs,600);
                }
                if(entry.fbxMesh.name=="Kamaz_Typhoon_LOD2")
                {
                    Assert.Less(quality.areaWeightedAnisotropy,600);
                    Assert.LessOrEqual(quality.degenerateFaces,110);
                    // Frozen pre-cut layout: 96 intersecting pairs with this
                    // pair-area sum. Subdivision multiplies pairs without adding
                    // a contact; retain the same 1% area regression bound.
                    Assert.LessOrEqual(quality.overlapPairArea,.000226321546*1.01);
                }
            }
        }

        [System.Serializable] sealed class PairedTransferMode
        {
            public string mode, error;
            public bool completed;
            public int atlasWidth, atlasHeight;
            public TransferPerformanceProfile performance = new TransferPerformanceProfile();
        }

        [System.Serializable] sealed class PairedTransferComparison
        {
            public string asset;
            public float density, boundaryToleranceTexels;
            public int padding = 2, borderPadding;
            public bool rotation = true, axisAlignment = true, junctionCuts = true;
            public bool originalMeshesUnchanged;
            public string[] inputKeys, inputHashes;
            public Matrix4x4[] inputFrames;
            public List<PairedTransferMode> modes = new List<PairedTransferMode>();
        }

        static byte[] PairedSnapshot(Mesh mesh)
        {
            var readable = MeshAccess.Readable(mesh, out bool owns);
            try {return TransferMeshSnapshot.Capture(readable);}
            finally {if (owns) Object.DestroyImmediate(readable);}
        }

        [TestCase("Assets/JunctionFixtures/Cafe_Table_A_01.fbx", 10)]
        [TestCase("Assets/JunctionFixtures/Cafe_Table_A_01.fbx", 32)]
        [TestCase("Assets/TransferBenchmarkInputs/Modern_Kitchen_A.fbx", 10)]
        [TestCase("Assets/TransferBenchmarkInputs/Modern_Kitchen_A.fbx", 32)]
        [TestCase("Assets/KamazFixtures/kamaz_Off.fbx", 10)]
        [TestCase("Assets/KamazFixtures/kamaz_Off.fbx", 32)]
        [Timeout(600000)]
        public void FrozenForwardReverseComparison(string asset, int density)
        {
            string output = System.Environment.GetEnvironmentVariable("MESHLAB_PAIRED_TRANSFER_OUTPUT");
            if (string.IsNullOrEmpty(output)) Assert.Ignore("Optional private paired transfer fixtures are not configured.");
            using var nativeSearch=NativeFootprintTestLibrary.OpenFromEnvironment();
            using var nativeQuery=NativeQueryTestLibrary.OpenFromEnvironment();
            using var burstCompiler=System.Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_BURST_RASTER")=="1"
                ?ReverseUvRasterAcceleration.beginTestScope?.Invoke():null;
            using var nativePacking=System.Environment.GetEnvironmentVariable("MESHLAB_COMBINED_NATIVE")=="1"
                ?XatlasNative.UseForTests(nativeSearch?.packing??throw new System.InvalidOperationException("Combined native test library is missing.")):null;
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(asset); Assert.NotNull(prefab, asset);
            var instance = Object.Instantiate(prefab); owned.Add(instance);
            var group = instance.GetComponent<LODGroup>() ?? instance.AddComponent<LODGroup>();
            var renderers = instance.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh)
                .Select(f => (filter: f, renderer: f.GetComponent<Renderer>(),
                    match: System.Text.RegularExpressions.Regex.Match(f.sharedMesh.name, @"_LOD(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                .Where(p => p.renderer && p.match.Success).GroupBy(p => int.Parse(p.match.Groups[1].Value)).OrderBy(g => g.Key).ToArray();
            Assert.GreaterOrEqual(renderers.Length, 2);
            group.SetLODs(renderers.Select(g => new LOD(.6f / (g.Key + 1), g.Select(p => p.renderer).ToArray())).ToArray());
            var ctx = Open(group);
            ctx.SourceLodIndex = 0; ctx.LightmapDensity = density; ctx.AtlasResolution = 256;
            ctx.RepackResolutionMode = ResolutionMode.AutoFromTexelDensity;
            ctx.RepackPerMesh = false; ctx.ShellPaddingPx = 2; ctx.BorderPaddingPx = 0;
            ctx.CutNarrowUvJunctions = ctx.XatlasRotateCharts = ctx.XatlasRotateChartsToAxis = true;
            hub.DiagnosticWorkflow.SymmetrySplitMode = SymmetrySplitShells.ThresholdMode.Adaptive;
            float tolerance = UvTransferTolerance.BoundaryTexels;
            if (float.TryParse(System.Environment.GetEnvironmentVariable("MESHLAB_TRANSFER_BOUNDARY_TEXELS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configured)) tolerance = configured;
            hub.DiagnosticWorkflow.TransferBoundaryToleranceTexels = tolerance;
            hub.DiagnosticWorkflow.PrepareTransferInputs();
            var entries = ctx.MeshEntries.Where(e => e.include).ToArray();
            var originals = entries.Select(e => PairedSnapshot(e.fbxMesh)).ToArray();
            // Freeze the same precise initial working meshes before forward Analyze/Weld
            // changes its working copies. The legacy diagnostic can still use
            // these inputs; the default comparison regroups the forward result.
            var levels = entries.GroupBy(e => e.lodIndex).OrderByDescending(g => g.Key).Select(g => new ReverseUvTransfer.Level {
                lod = g.Key, inputs = g.Select(e => {
                    var copy = MeshAccess.ReadableCopy(e.originalMesh); copy.name = e.fbxMesh.name; owned.Add(copy);
                    return new ReverseUvTransfer.Input {mesh = copy, key = e.fbxMesh.name, toWorld = e.renderer.localToWorldMatrix};
                }).ToArray()
            }).ToArray();
            var inputs = levels.SelectMany(l => l.inputs).ToArray();
            var snapshots = inputs.Select(i => TransferMeshSnapshot.Capture(i.mesh)).ToArray();
            string directory = System.IO.Path.Combine(output, System.IO.Path.GetFileNameWithoutExtension(asset), density.ToString());
            System.IO.Directory.CreateDirectory(directory);
            var comparison = new PairedTransferComparison {asset = asset, density = density, boundaryToleranceTexels = tolerance,
                inputKeys = inputs.Select(i => i.key).ToArray(), inputFrames = inputs.Select(i => i.toWorld).ToArray()};
            using (var hash = System.Security.Cryptography.SHA256.Create())
                comparison.inputHashes = snapshots.Select(s => System.BitConverter.ToString(hash.ComputeHash(s)).Replace("-", "")).ToArray();
            var previousOutput = BenchmarkRecorder.OutputDirectoryOverride;
            try
            {
                var forward = new PairedTransferMode {mode = "forward"}; comparison.modes.Add(forward);
                BenchmarkRecorder.OutputDirectoryOverride = System.IO.Path.Combine(directory, forward.mode);
                hub.DiagnosticWorkflow.ForwardPerformance = forward.performance;
                try
                {
                    using (forward.performance.Measure("Forward pipeline"))
                        ((System.Threading.Tasks.Task)CallWorkflow("ExecFullPipelineImpl", "paired-forward", false)).GetAwaiter().GetResult();
                }
                finally { hub.DiagnosticWorkflow.ForwardPerformance = null; }
                Assert.IsTrue(forward.performance.samples.Any(s => s.stage == "Forward shell transfer" && s.count > 0),
                    "Ordinary Transfer must record its stage timings.");
                Assert.IsTrue(ctx.HasRepack && ctx.HasTransfer);
                System.IO.Directory.CreateDirectory(BenchmarkRecorder.OutputDirectoryOverride);
                TransferBenchmarkExport.Forward(BenchmarkRecorder.OutputDirectoryOverride, ctx);
                forward.completed = true;
                forward.atlasWidth = (int)entries.Max(e => e.repackedAtlasWidth);
                forward.atlasHeight = (int)entries.Max(e => e.repackedAtlasHeight);
                bool includeExisting=System.Environment.GetEnvironmentVariable("MESHLAB_PAIRED_EXISTING_SEED")=="1";
                bool useForward=System.Environment.GetEnvironmentVariable("MESHLAB_REVERSE_FROM_FORWARD")!="0";
                UvTransferWorkflow.ForwardReverseInputs detached;
                using (forward.performance.Measure("Detached forward preparation"))
                    detached=useForward?hub.DiagnosticWorkflow.PrepareForwardReverseInputs(true,false,default).GetAwaiter().GetResult():null;
                using var detachedLifetime = detached;
                var reverseSources=levels;
                if(useForward)
                {
                    reverseSources=detached.levels;
                    Assert.AreEqual(forward.atlasWidth,detached.atlasSize);
                    foreach(var level in reverseSources)
                        foreach(var input in level.inputs)
                        {
                            var entry=entries.Single(e=>e.lodIndex==level.lod && e.fbxMesh.name==input.key);
                            CollectionAssert.AreEqual(TransferMeshSnapshot.Capture(ctx.Assets.GetResultMesh(entry)),
                                TransferMeshSnapshot.Capture(input.mesh),"Detached preparation must equal the ordinary pipeline.");
                        }
                }
                foreach(bool prepareSeed in includeExisting?new[] {true,false}:new[] {true})
                foreach (bool overlap in new[] {false, true})
                {
                    var reverse = new PairedTransferMode {mode = (prepareSeed?"reverse-":"reverse-existing-")
                        +(overlap?"overlap":"exclusive")}; comparison.modes.Add(reverse);
                    BenchmarkRecorder.OutputDirectoryOverride = System.IO.Path.Combine(directory, reverse.mode);
                    try
                    {
                        if(System.Environment.GetEnvironmentVariable("MESHLAB_CAPTURE_NATIVE_PACKING")=="1")
                        {
                            reverse.performance.nativePackingCaptureDirectory=System.IO.Path.Combine(BenchmarkRecorder.OutputDirectoryOverride,"native-requests");
                            reverse.performance.nativePackingReplayExecutable=System.Environment.GetEnvironmentVariable("MESHLAB_NATIVE_PACKING_REPLAY_EXECUTABLE");
                        }
                        var options = new ReverseUvTransfer.Options {
                            performance = reverse.performance,
                            cachePackingFootprints = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_FOOTPRINT_CACHE") != "1",
                            indexFootprintSearch = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_FOOTPRINT_SEARCH_INDEX") != "1",
                            cacheCollisionRows = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_COLLISION_ROW_CACHE") != "1",
                            nativeFootprintSearch = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_NATIVE_FOOTPRINT_SEARCH") != "1",
                            reuseOccupancySnapshots = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_OCCUPANCY_SNAPSHOT_REUSE") != "1",
                            burstFootprintRaster = System.Environment.GetEnvironmentVariable("MESHLAB_DISABLE_BURST_RASTER") != "1",
                            parallelFamilyValidation = System.Environment.GetEnvironmentVariable("MESHLAB_PARALLEL_VALIDATION") == "1",
                            footprintSearchApi = nativeSearch?.api,
                            queryApi=nativeQuery?.api,
                            queryBackend=System.Environment.GetEnvironmentVariable("MESHLAB_QUERY_BACKEND")=="legacy"
                                ?ReverseUvQueryBackend.Mode.Legacy:ReverseUvQueryBackend.Mode.Auto,
                            seedResolution = useForward ? forward.atlasWidth : 256, padding = 2, boundaryToleranceTexels = tolerance,
                            preserveProjectedOverlap = overlap, projectionReach = .05f, cutNarrowJunctions = true,
                            rotateCharts = true, rotateChartsToAxis = true, splitDonorSeams = true,
                            fillAtlasVacancies = true, repackFinalAtlas = true, fillFinalAtlas = true
                            , regroupPreparedAtlas=useForward, frozenShellCascade=useForward && !overlap
                        };
                        string capture=System.Environment.GetEnvironmentVariable("MESHLAB_FROZEN_CAPTURE");
                        if(options.frozenShellCascade && !string.IsNullOrEmpty(capture))
                            options.beforeFinalPacking=result=>TransferBenchmarkExport.Reverse(
                                System.IO.Path.Combine(capture,System.IO.Path.GetFileNameWithoutExtension(asset)),result,reverseSources,finalAtlas:false);
                        // Model the editor's existing repacked working seed on
                        // detached inputs. Reverse must consume it unchanged and
                        // still repack every LOD after projection.
                        using var existing=prepareSeed?null:ReverseUvInputs.Prepare(reverseSources);
                        if(existing!=null)
                        {
                            options.seedResolution=existing.PrepareSeed(256,2,density,default,
                                rotateCharts:true,rotateChartsToAxis:true);
                            options.seedDensity=existing.seedDensity;
                        }
                        var existingSnapshots=existing?.levels.SelectMany(l=>l.inputs)
                            .Select(i=>TransferMeshSnapshot.Capture(i.mesh)).ToArray();
                        // The editor UI runs Reverse asynchronously: final packing on workers,
                        // continuations on the editor context. The benchmark can measure that path.
                        bool asyncReverse = System.Environment.GetEnvironmentVariable("MESHLAB_PAIRED_ASYNC_REVERSE") == "1";
                        System.Threading.Tasks.Task<ReverseUvJunctionTrial.PreparedResult> Build() =>
                            ReverseUvJunctionTrial.Build(existing?.levels??reverseSources, options, prepareSeed, density, useAsync: asyncReverse);
                        using var prepared = asyncReverse ? RunOnMainThreadQueue(Build) : Build().GetAwaiter().GetResult();
                        TransferBenchmarkExport.Reverse(BenchmarkRecorder.OutputDirectoryOverride, prepared.result, prepared.inputs.levels);
                        ReverseUvAudit.Write(prepared.result, prepared.inputs.levels);
                        Assert.AreEqual("shared-chart-families",prepared.result.report.finalPacking,prepared.result.report.finalPackingRefusal);
                        if(existing!=null)
                        {
                            var retained=existing.levels.SelectMany(l=>l.inputs).ToArray();
                            for(int i=0;i<retained.Length;++i)
                                CollectionAssert.AreEqual(existingSnapshots[i],TransferMeshSnapshot.Capture(retained[i].mesh));
                        }
                        reverse.atlasWidth = reverse.atlasHeight = prepared.result.report.atlasSize;
                        if(overlap && System.Environment.GetEnvironmentVariable("MESHLAB_REQUIRE_BURST_RASTER")=="1")
                        {
                            Assert.IsTrue(ReverseUvRasterAcceleration.Available,"Required Burst assembly must register.");
                            if(reverse.performance.samples.Any(s=>s.stage=="Burst raster qualifying triangles" && s.count>0))
                                Assert.IsTrue(reverse.performance.samples.Any(s=>s.stage=="Burst footprint raster spans" && s.count>0),
                                    "Qualifying model masks must execute compiled Burst jobs.");
                        }
                        reverse.completed = true;
                    }
                    catch (System.InvalidOperationException error) {reverse.error = error.Message;}
                }
                for (int i = 0; i < entries.Length; ++i) CollectionAssert.AreEqual(originals[i], PairedSnapshot(entries[i].fbxMesh));
                for (int i = 0; i < inputs.Length; ++i) CollectionAssert.AreEqual(snapshots[i], TransferMeshSnapshot.Capture(inputs[i].mesh));
                comparison.originalMeshesUnchanged = true;
            }
            finally
            {
                BenchmarkRecorder.OutputDirectoryOverride = previousOutput;
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "comparison.json"), JsonUtility.ToJson(comparison, true));
            }
            Assert.IsTrue(comparison.modes.All(m=>m.completed),string.Join("\n",
                comparison.modes.Where(m=>!m.completed).Select(m=>m.mode+": "+m.error)));
        }

        [Test]
        public void ForwardTransferKeepsReverseExportGuardOnUntouchedEntries()
        {
            var ctx = Open(Group(true,out var sourceRenderer,out var targetRenderer));
            var source = ctx.MeshEntries.Find(e => e.renderer == sourceRenderer);
            var target = ctx.MeshEntries.Find(e => e.renderer == targetRenderer);
            var untouched = source.repackedMesh = ReverseUvMesh.Copy(source.fbxMesh,source.fbxMesh.triangles,
                new[] {Vector2.zero,Vector2.right*.2f,Vector2.one*.2f,Vector2.one*.6f,Vector2.one*.8f,Vector2.up*.8f});
            source.repackedAtlasWidth = source.repackedAtlasHeight = 128;
            source.reverseTransferJson = "source ancestry";
            var oldRepack = target.repackedMesh = Object.Instantiate(target.fbxMesh);
            var oldTransfer = target.transferredMesh = Object.Instantiate(target.fbxMesh);
            target.reverseTransferJson = "target ancestry";
            UvProgress.Begin("Forward transfer provenance regression",cancelable:true);
            try { ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl",1,false)).GetAwaiter().GetResult(); }
            finally { UvProgress.End(); }
            Assert.AreSame(untouched,source.repackedMesh); Assert.AreEqual("source ancestry",source.reverseTransferJson);
            Assert.IsFalse(SidecarStore.TryBuildEntry(source,untouched,false,SidecarStore.AoUvTarget.None,out var sidecar));
            Assert.IsNull(sidecar);
            Assert.IsTrue(oldRepack == null); Assert.IsTrue(oldTransfer == null);
            Assert.IsNull(target.reverseTransferJson); Assert.IsNotNull(target.transferredMesh);
        }

        [Test]
        public void SelectedRepackDiscardsOnlyItsReverseMeshesAndLeavesOtherExportGuards()
        {
            var ctx = Open(Group(true,out var selectedRenderer,out var untouchedRenderer));
            var selected = ctx.MeshEntries.Find(e => e.renderer == selectedRenderer);
            var untouched = ctx.MeshEntries.Find(e => e.renderer == untouchedRenderer);
            var oldRepack = selected.repackedMesh = Object.Instantiate(selected.fbxMesh);
            var oldTransfer = selected.transferredMesh = Object.Instantiate(selected.fbxMesh);
            selected.reverseTransferJson = "selected ancestry";
            var protectedMesh = untouched.transferredMesh = ReverseUvMesh.Copy(untouched.fbxMesh,untouched.fbxMesh.triangles,
                new[] {Vector2.zero,Vector2.right*.2f,Vector2.one*.2f,Vector2.one*.6f,Vector2.one*.8f,Vector2.up*.8f});
            untouched.reverseTransferJson = "untouched ancestry";
            ctx.AtlasResolution = 128; ctx.RepackResolutionMode = ResolutionMode.Manual;
            ((System.Threading.Tasks.Task)CallWorkflow("ExecRepackImpl",new List<MeshEntry> {selected},false)).GetAwaiter().GetResult();
            Assert.IsTrue(oldRepack == null); Assert.IsTrue(oldTransfer == null);
            Assert.IsNull(selected.reverseTransferJson); Assert.IsNull(selected.transferredMesh); Assert.IsNotNull(selected.repackedMesh);
            Assert.AreSame(protectedMesh,untouched.transferredMesh); Assert.AreEqual("untouched ancestry",untouched.reverseTransferJson);
            Assert.IsFalse(SidecarStore.TryBuildEntry(untouched,protectedMesh,false,SidecarStore.AoUvTarget.None,out var sidecar));
            Assert.IsNull(sidecar);
        }

        [Test]
        public void TransferWithoutTargetsKeepsReverseMeshesAndTheirExportGuards()
        {
            var ctx = Open(Group(true,out var renderer,out _));
            var entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
            var previous = entry.transferredMesh = Object.Instantiate(entry.fbxMesh); entry.reverseTransferJson = "ancestry";
            ((System.Threading.Tasks.Task)CallWorkflow("ExecTransferLodImpl",99,false)).GetAwaiter().GetResult();
            Assert.AreSame(previous,entry.transferredMesh); Assert.AreEqual("ancestry",entry.reverseTransferJson);
        }

        [TestCase(false)] [TestCase(true)]
        public void RegroupReversePublishesFromDetachedForwardOrExistingForward(bool prepare)
        {
            var ctx=Open(Group(true,out _,out _));
            ctx.AtlasResolution=128;ctx.RepackResolutionMode=ResolutionMode.Manual;ctx.RepackPerMesh=false;
            foreach(var entry in ctx.MeshEntries)
                entry.originalMesh.vertices=new[] {Vector3.zero,Vector3.right,new Vector3(1,1,0),Vector3.up};
            var originals=ctx.MeshEntries.Select(e=>TransferMeshSnapshot.Capture(e.fbxMesh)).ToArray();
            if(!prepare) ((System.Threading.Tasks.Task)CallWorkflow("ExecFullPipelineImpl","prepare-forward",false)).GetAwaiter().GetResult();
            var workflow=hub.DiagnosticWorkflow;
            typeof(UvTransferWorkflow).GetField("reversePrepareSeed",Private).SetValue(workflow,prepare);
            Call(hub,"CollectCanvasEntries");SetPreviewFrame();
            Call(hub,"ApplyPreviewMode",UvCanvasView.PreviewMode.Checker);
            workflow.RunReverseTransfer(false).GetAwaiter().GetResult();
            Assert.AreEqual(0,ctx.SourceLodIndex);
            Call(hub,"CollectCanvasEntries");
            AssertPreviewFrame();
            AssertFreshPreview(ctx,ctx.MeshEntries.First(e=>e.lodIndex==0),UvCanvasView.PreviewMode.Checker);
            for(int i=0;i<ctx.MeshEntries.Count;++i)
            {
                var entry=ctx.MeshEntries[i];
                CollectionAssert.AreEqual(originals[i],TransferMeshSnapshot.Capture(entry.fbxMesh));
                Assert.IsNotEmpty(entry.reverseTransferJson);
                var output=ctx.Assets.GetResultMesh(entry);
                Assert.AreEqual(entry.fbxMesh.triangles.Length,output.triangles.Length);
                Assert.AreEqual(1,UvShellExtractor.Extract(output.uv2,output.triangles).Count);
            }
        }

        [Test] public void FailedReverseTransferKeepsThePreviousChainAndProvenance()
        {
            var ctx = Open(Group(true,out var fineRenderer,out var coarseRenderer));
            var fine = ctx.MeshEntries.Find(e=>e.renderer==fineRenderer);
            var coarse = ctx.MeshEntries.Find(e=>e.renderer==coarseRenderer);
            fine.transferredMesh=Object.Instantiate(fine.originalMesh); coarse.repackedMesh=Object.Instantiate(coarse.originalMesh);
            fine.reverseTransferJson="prior"; var previousFine=fine.transferredMesh; var previousCoarse=coarse.repackedMesh;
            ctx.HasTransfer=ctx.HasRepack=true;
            typeof(UvTransferWorkflow).GetField("reverseReach",Private).SetValue(hub.DiagnosticWorkflow,float.NaN);
            ctx.RepackResolutionMode=ResolutionMode.Manual; ctx.AtlasResolution=128;
            Assert.Catch(()=>hub.DiagnosticWorkflow.RunReverseTransfer(false).GetAwaiter().GetResult());
            Assert.AreSame(previousFine,fine.transferredMesh); Assert.AreSame(previousCoarse,coarse.repackedMesh);
            Assert.AreEqual("prior",fine.reverseTransferJson); Assert.IsTrue(ctx.HasTransfer); Assert.AreEqual(0,ctx.SourceLodIndex);
        }

        [Test]
        public void PreviewSurvivesResetUndoAndContextRefresh(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values("reset", "working-reset", "undo", "refresh")] string change)
        {
            var group = Group(true, out var renderer, out _); var ctx = Open(group);
            var entry = ctx.MeshEntries.Find(e => e.renderer == renderer); var baseline = entry.fbxMesh;
            entry.repackedMesh = Object.Instantiate(baseline); owned.Add(entry.repackedMesh);
            entry.repackedMesh.uv2 = ShiftedUvs(.3f);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                if (change == "reset") CallWorkflow("ResetWorkingMeshesToFbx");
                else if (change == "working-reset") CallWorkflow("ResetWorkingCopies");
                else if (change == "undo") Call(hub, "OnUndoRedo");
                else ctx.Refresh(group);
                Call(hub, "CollectCanvasEntries");
                entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
                Assert.AreSame(baseline, entry.fbxMesh, "Refresh must never capture a temporary preview as the imported baseline.");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewSurvivesFailedAssetPreparation(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool throwDuringPreparation)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                if (throwDuringPreparation) {
                    ctx.Assets.BeforeWrite += () => { throw new System.InvalidOperationException("Preparation regression"); };
                    Assert.Throws<System.InvalidOperationException>(() => ctx.Assets.ApplyUv2Public());
                } else ctx.Assets.ApplyUv2Public(); // no FBX path: exits before any file write
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewFollowsEveryUvChannelChange(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(0, 1, 2)] int channel)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
            entry.repackedMesh = Object.Instantiate(entry.fbxMesh); owned.Add(entry.repackedMesh);
            entry.repackedMesh.uv2 = ShiftedUvs(.3f);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "CollectCanvasEntries"); SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                Call(hub, "OnPreviewChannelChanged", channel); Call(hub, "CollectCanvasEntries");
                Assert.AreEqual(channel == 2 ? 0 : channel, ctx.PreviewUvChannel, "A missing channel uses the available fallback.");
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [TestCase("texture")]
        [TestCase("index")]
        [TestCase("scale-offset")]
        [TestCase("pixels")]
        [TestCase("added")]
        public void PreviewFollowsLightmapMetadataAndTextureUpdates(string change)
        {
            var ctx = Open(Group(true, out var renderer, out _)); var entry = ctx.MeshEntries.Find(e => e.renderer == renderer);
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (change == "added") { renderer.lightmapIndex = -1; LightmapSettings.lightmaps = new LightmapData[0]; }
                else AssignLightmap(renderer);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Lightmap); Call(hub, "CollectCanvasEntries");
                var oldPreview = entry.meshFilter.sharedMesh;
                if (change == "added") AssignLightmap(renderer);
                else if (change == "scale-offset") renderer.lightmapScaleOffset = new Vector4(.25f, .5f, .6f, .1f);
                else if (change == "pixels") {
                    var texture = LightmapSettings.lightmaps[0].lightmapColor; texture.SetPixel(0, 0, Color.green); texture.Apply();
                } else {
                    var texture = new Texture2D(8, 8); owned.Add(texture);
                    if (change == "texture") LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = texture } };
                    else {
                        LightmapSettings.lightmaps = new[] { LightmapSettings.lightmaps[0], new LightmapData { lightmapColor = texture } };
                        renderer.lightmapIndex = 1;
                    }
                }
                Call(hub, "CollectCanvasEntries");
                AssertFreshPreview(ctx, entry, UvCanvasView.PreviewMode.Lightmap); AssertPreviewFrame();
                Assert.AreSame(LightmapSettings.lightmaps[renderer.lightmapIndex].lightmapColor, renderer.sharedMaterial.mainTexture);
                if (change != "added") Assert.IsTrue(oldPreview == null, "The old lightmap clone is released after metadata changes.");
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewDetectsLodRendererChangesWithUnchangedCounts(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode,
            [Values(false, true)] bool reorder)
        {
            var group = Group(true, out var first, out var second); var ctx = Open(group);
            var oldMaterial = first.sharedMaterial; var oldMesh = first.GetComponent<MeshFilter>().sharedMesh;
            var replacement = reorder ? second : Standalone("Replacement_LOD0");
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(first, second, replacement);
                SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                group.SetLODs(new[] { new LOD(.5f, new Renderer[] { replacement }), new LOD(.1f, new Renderer[] { reorder ? first : second }) });
                Call(hub, "RefreshLodStructure"); Call(hub, "CollectCanvasEntries");
                var entry = ctx.MeshEntries.Find(e => e.renderer == replacement);
                Assert.NotNull(entry); Assert.AreEqual(0, entry.lodIndex);
                AssertFreshPreview(ctx, entry, mode); AssertPreviewFrame();
                Assert.AreSame(oldMaterial, first.sharedMaterial); Assert.AreSame(oldMesh, first.GetComponent<MeshFilter>().sharedMesh);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewStaysSuspendedUntilNestedAssetWritesFinish(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode)
        {
            var ctx = Open(Group(true, out var renderer, out _)); var material = renderer.sharedMaterial;
            var previousLightmaps = LightmapSettings.lightmaps;
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                Call(hub, "ApplyPreviewMode", mode);
                using (ctx.Assets.PreservePreviewDuringWrite()) {
                    ctx.Assets.BeforeWrite.Invoke();
                    using (ctx.Assets.PreservePreviewDuringWrite()) { ctx.Assets.BeforeWrite.Invoke(); Call(hub, "CollectCanvasEntries"); }
                    Call(hub, "CollectCanvasEntries");
                    Assert.AreSame(material, renderer.sharedMaterial, "Export must read authored materials even when the window repaints.");
                }
                Call(hub, "CollectCanvasEntries"); AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode);
            }
            finally { LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void PreviewSurvivesSavingMeshAssets(
            [Values(UvCanvasView.PreviewMode.Checker, UvCanvasView.PreviewMode.Shells3D, UvCanvasView.PreviewMode.Lightmap)] UvCanvasView.PreviewMode mode)
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var previousLightmaps = LightmapSettings.lightmaps;
            string folder = "Assets/PreviewWriteRegression_" + System.Guid.NewGuid().ToString("N");
            try {
                if (mode == UvCanvasView.PreviewMode.Lightmap) AssignLightmap(renderer);
                ctx.PipeSettings.savePath = folder; SetPreviewFrame(); Call(hub, "ApplyPreviewMode", mode);
                ctx.Assets.SaveAllPublic();
                Assert.AreEqual(2, UnityEditor.AssetDatabase.FindAssets("t:Mesh", new[] { folder }).Length);
                Call(hub, "CollectCanvasEntries"); AssertFreshPreview(ctx, ctx.MeshEntries.Find(e => e.renderer == renderer), mode); AssertPreviewFrame();
            }
            finally { UnityEditor.AssetDatabase.DeleteAsset(folder); LightmapSettings.lightmaps = previousLightmaps; }
        }

        [Test]
        public void SurfaceAreaDisplayUpdatesWhenGeometryChangesInPlace()
        {
            var ctx = Open(Group(true, out var renderer, out _));
            var mesh = ctx.MeshEntries.Find(e => e.renderer == renderer).originalMesh;
            double before = (double)CallWorkflow("GetSourceAreaPreview");
            var positions = mesh.vertices;
            for (int i = 0; i < positions.Length; ++i) positions[i] *= 2;
            mesh.vertices = positions; VertexChannels.RaiseChanged(mesh);
            Assert.AreEqual(before * 4, (double)CallWorkflow("GetSourceAreaPreview"), .00001);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LightmapOverlaySamplesTheRenderersAtlasRegion(bool rightHalf)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            owned.Add(texture);
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = i % 8 < 4 ? Color.red : Color.green;
            texture.SetPixels(pixels); texture.Apply();
            var material = new Material(Shader.Find("Hidden/MeshLab/UvOverlay")); owned.Add(material);
            material.SetVector("_UvScaleOffset", new Vector4(.5f, 1, rightHalf ? .5f : 0, 0));
            var copy = GpuReadback.Read(texture, 16, 16, hdr: false, material: material);
            Assert.NotNull(copy); owned.Add(copy);
            foreach (var color in copy.GetPixels()) {
                Assert.Greater(rightHalf ? color.g : color.r, .8f);
                Assert.Less(rightHalf ? color.r : color.g, .1f);
            }
        }

        [UnityTest]
        public IEnumerator SharedMeshInstancesRenderTheirOwnLightmapLayers()
        {
            var first = Standalone("First"); var second = Standalone("Second");
            var mesh = first.GetComponent<MeshFilter>().sharedMesh; second.GetComponent<MeshFilter>().sharedMesh = mesh;
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1, 1, 0), Vector3.up };
            mesh.RecalculateNormals();
            var red = new Texture2D(8, 8); var green = new Texture2D(8, 8); owned.Add(red); owned.Add(green);
            var reds = new Color[64]; var greens = new Color[64];
            for (int i = 0; i < 64; ++i) { reds[i] = Color.red; greens[i] = Color.green; }
            red.SetPixels(reds); red.Apply(); green.SetPixels(greens); green.Apply();
            var previousLightmaps = LightmapSettings.lightmaps;
            var context = new UvToolContext();
            var entries = new List<MeshEntry> { new MeshEntry { originalMesh = mesh, renderer = first }, new MeshEntry { originalMesh = mesh, renderer = second } };
            var items = new List<MeshViewport3D.Item> { new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.left * 1.5f)),
                new MeshViewport3D.Item(mesh, Matrix4x4.Translate(Vector3.right * .5f)) };
            var canvas = new UvCanvasView { CurrentPreviewMode = UvCanvasView.PreviewMode.Lightmap, FillHidden = true, ShowBorder = false, ShowWireframe = false };
            canvas.Init();
            using var layer = new UvLayer3D(); using var view = new MeshViewport3D { ViewProjection = MeshViewport3D.Projection.XY, ShowGrid = false, ShowAxes = false };
            var window = OpenSpotInputWindow();
            int redPixels = 0, greenPixels = 0;
            try {
                LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = red }, new LightmapData { lightmapColor = green } };
                first.lightmapIndex = 0; second.lightmapIndex = 1;
                first.lightmapScaleOffset = second.lightmapScaleOffset = new Vector4(1, 1, 0, 0);
                view.Frame(new Bounds(new Vector3(0, .5f, 0), new Vector3(3, 1, .1f)));
                window.Input = current => {
                    if (current.type != EventType.Repaint) return;
                    view.Draw(new Rect(0, 0, 256, 128), items, overlay: viewport => layer.Draw(viewport, canvas, context, items, entries));
                    var frame = Get<RenderTexture>(view, "offscreen");
                    var copy = GpuReadback.Read(frame, 256, 128, hdr: false);
                    if (!copy) return;
                    try {
                        redPixels = greenPixels = 0;
                        foreach (var pixel in copy.GetPixels()) {
                            if (pixel.r > .5f && pixel.g < .2f && pixel.b < .2f) ++redPixels;
                            if (pixel.g > .5f && pixel.r < .2f && pixel.b < .2f) ++greenPixels;
                        }
                    }
                    finally { Object.DestroyImmediate(copy); }
                };
                for (int i = 0; i < 60 && (redPixels < 30 || greenPixels < 30); ++i) { window.Repaint(); yield return null; }
                Assert.Greater(redPixels, 30);
                Assert.Greater(greenPixels, 30, "The second instance must not reuse the first instance's UV-layer texture.");
            }
            finally { window.Input = null; window.Close(); Object.DestroyImmediate(window); canvas.Cleanup(); LightmapSettings.lightmaps = previousLightmaps; }
        }

        // Async editor work resumes its continuations on the main thread. Batch
        // tests have no running editor loop, so this context queues them and the
        // waiting test thread runs them in order, as the editor loop would.
        sealed class MainThreadQueue : System.Threading.SynchronizationContext
        {
            readonly System.Collections.Concurrent.BlockingCollection<(System.Threading.SendOrPostCallback callback, object state)> queue =
                new System.Collections.Concurrent.BlockingCollection<(System.Threading.SendOrPostCallback, object)>();
            public override void Post(System.Threading.SendOrPostCallback callback, object state) => queue.Add((callback, state));
            public override void Send(System.Threading.SendOrPostCallback callback, object state) =>
                throw new System.NotSupportedException("Synchronous dispatch to the test thread would deadlock.");
            internal void RunUntilComplete(System.Threading.Tasks.Task task)
            {
                while (!task.IsCompleted)
                    if (queue.TryTake(out var item, 1)) item.callback(item.state);
            }
        }

        static T RunOnMainThreadQueue<T>(System.Func<System.Threading.Tasks.Task<T>> start)
        {
            var previous = System.Threading.SynchronizationContext.Current;
            var context = new MainThreadQueue();
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = start();
                context.RunUntilComplete(task);
                return task.GetAwaiter().GetResult();
            }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
        }

        object CallWorkflow(string method, params object[] args) => typeof(UvTransferWorkflow).GetMethod(method, Private).Invoke(hub.DiagnosticWorkflow, args);

        static Vector2[] ShiftedUvs(float offset) => new[] { new Vector2(offset, .1f), new Vector2(offset + .1f, .1f),
            new Vector2(offset + .1f, .2f), new Vector2(offset, .2f) };

        static void SetStageMesh(MeshEntry entry, string stage, Mesh mesh)
        {
            if (stage == "repack") entry.repackedMesh = mesh;
            else if (stage == "transfer") entry.transferredMesh = mesh;
            else entry.originalMesh = mesh;
        }

        void SetPreviewFrame()
        {
            var viewport = Get<MeshViewport3D>(hub, "viewport"); viewport.Frame(new Bounds(Vector3.zero, Vector3.one));
            ChooseCameraFrame(viewport);
            var canvas = Get<UvCanvasView>(hub, "canvas"); canvas.Zoom = 3; canvas.Pan = new Vector2(87, -31);
        }

        void AssertPreviewFrame()
        {
            AssertCameraFrame(Get<MeshViewport3D>(hub, "viewport"));
            var canvas = Get<UvCanvasView>(hub, "canvas"); Assert.AreEqual(3, canvas.Zoom); Assert.AreEqual(new Vector2(87, -31), canvas.Pan);
        }

        void AssertFreshPreview(UvToolContext ctx, MeshEntry entry, UvCanvasView.PreviewMode mode)
        {
            Assert.AreEqual(mode, Get<UvCanvasView>(hub, "canvas").CurrentPreviewMode);
            var mesh = ctx.DMesh(entry); var shown = entry.meshFilter.sharedMesh;
            CollectionAssert.AreEqual(mesh.uv2, shown.uv2, "The scene preview must use the current atlas.");
            if (mode == UvCanvasView.PreviewMode.Checker) Assert.AreSame(mesh, shown);
            if (mode == UvCanvasView.PreviewMode.Lightmap) {
                var so = entry.renderer.lightmapScaleOffset; var expected = mesh.uv2;
                for (int i = 0; i < expected.Length; ++i) expected[i] = new Vector2(expected[i].x * so.x + so.z, expected[i].y * so.y + so.w);
                CollectionAssert.AreEqual(expected, shown.uv);
            }
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            var item = items.Find(value => value.mesh == mesh);
            Assert.AreSame(mesh, item.mesh, "The window preview and scene preview must use the same canonical data.");
            Assert.AreSame(entry.renderer.sharedMaterial, item.materials[0], "The same frame must not retain a destroyed preview material.");
            Assert.IsTrue(item.materials[0]);
        }

        [UnityTest]
        public IEnumerator ThreeDSpotUsesTheCurrentInputRect()
        {
            var group = Group(lod1HasUv1: true, out _, out _);
            group.transform.position = new Vector3(60, -13, 42);
            var ctx = Open(group);
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            var entries = Get<List<MeshEntry>>(hub, "viewportEntries");
            var mesh = items[0].mesh;
            Call(hub, "SetCanvas3D", true);
            canvas.SpotMode = true;
            Set(viewport, "orbit", Vector2.zero);
            viewport.Frame(mesh.bounds);
            // A Layout pass or resize can leave a different rect from the input event.
            Set(viewport, "currentRect", new Rect(0, 0, 1, 1));
            long key = ((long)mesh.GetInstanceID() << 8) | (uint)ctx.PreviewUvChannel;
            ctx.PreviewShellDataCache[key] = UvTopology.BuildShellData(mesh.uv2, mesh.triangles);
            ctx.PreviewBvhCache[mesh.GetInstanceID()] = new TriangleBvh(mesh.vertices, mesh.triangles);

            var rect = new Rect(20, 30, 200, 100);
            var inputWindow = OpenSpotInputWindow();
            try {
                yield return null;
                foreach (var eventType in new[] { EventType.MouseMove, EventType.MouseDown }) {
                    Set(viewport, "currentRect", new Rect(0, 0, 1, 1));
                    bool handled = false;
                    inputWindow.Input = input => {
                        if (input.type != eventType) return;
                        handled = true;
                        Call(hub, "HandleViewportSpot", rect, input);
                        if (eventType == EventType.MouseDown)
                            Assert.That(input.type, Is.EqualTo(EventType.Used), "selection precedes camera orbit input");
                    };
                    inputWindow.SendEvent(new Event { type = eventType, mousePosition = rect.center, button = 0, clickCount = 1 });
                    Assert.That(handled, Is.True, "input must reach an actual OnGUI callback");
                    Assert.That(canvas.HasHoveredShell, Is.True, "Spot must use the rect of this input event");
                    Assert.That(canvas.HoverHitValid, Is.True);
                    Assert.That(canvas.HoveredShell.meshEntry, Is.SameAs(entries[0]));
                    Assert.That(canvas.HasSelectedShell, Is.EqualTo(eventType == EventType.MouseDown));
                }
            }
            finally { inputWindow.Close(); }
        }

        [Test]
        public void LayoutKeepsTheRectForPendingSpotPicking()
        {
            using (var viewport = new MeshViewport3D()) {
                var rect = new Rect(20, 30, 200, 100);
                viewport.PrepareRect(rect, EventType.Repaint);
                viewport.PrepareRect(new Rect(0, 0, 1, 1), EventType.Layout);
                Assert.That(viewport.LastRect, Is.EqualTo(rect));
                Assert.That(viewport.TryScreenRay(rect.center, out _, out _), Is.True);
            }
        }

        [UnityTest]
        public IEnumerator ThreeDSpotCompletesTheFirstClickAfterPreviewPreparation()
        {
            var ctx = Open(Group(lod1HasUv1: true, out _, out _));
            var viewport = Get<MeshViewport3D>(hub, "viewport");
            var canvas = Get<UvCanvasView>(hub, "canvas");
            var items = Get<List<MeshViewport3D.Item>>(hub, "viewportItems");
            Call(hub, "SetCanvas3D", true);
            canvas.SpotMode = true;
            Set(viewport, "orbit", Vector2.zero);
            viewport.Frame(items[0].mesh.bounds);
            var rect = new Rect(20, 30, 200, 100);
            var inputWindow = OpenSpotInputWindow();
            try {
                yield return null;
                bool handled = false;
                inputWindow.Input = input => {
                    if (input.type != EventType.MouseDown) return;
                    handled = true; Call(hub, "HandleViewportSpot", rect, input);
                };
                inputWindow.SendEvent(new Event { type = EventType.MouseDown, mousePosition = rect.center, button = 0, clickCount = 1 });
                Assert.That(handled, Is.True, "input must reach an actual OnGUI callback");
                Assert.That(canvas.HasSelectedShell, Is.False, "the first click queues missing preview data");
                viewport.PrepareRect(new Rect(0, 0, 1, 1), EventType.Layout);

                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 10;
                while (!canvas.HasSelectedShell && UnityEditor.EditorApplication.timeSinceStartup < deadline) {
                    canvas.PollPreviewJobs(); yield return null;
                }
                Assert.That(canvas.HasHoveredShell, Is.True);
                Assert.That(canvas.HasSelectedShell, Is.True, "worker completion must finish the click without another mouse event");
                Assert.That(canvas.SelectedShellDebug, Is.Not.Null);
                Assert.That(canvas.SelectedShellDebug.uvChannel, Is.EqualTo(ctx.PreviewUvChannel));
            }
            finally { inputWindow.Close(); }
        }

        [Test]
        public void PickedUvChannelReturnsWhenOnlyAGeneratedMeshCarriesIt()
        {
            // After a repack the source LOD's UV1 lives on repackedMesh, not on the original:
            // the return to UV1 must be judged on the mesh the context displays at UV1.
            var group = Group(lod1HasUv1: false, out var lod0, out var lod1);
            lod0.GetComponent<MeshFilter>().sharedMesh = Quad("Prop_LOD0_NoUv1", false);
            var ctx = Open(group);
            ctx.MeshEntries.Find(e => e.lodIndex == 0).repackedMesh = Quad("Prop_LOD0_Repacked", true);
            var canvas = Get<UvCanvasView>(hub, "canvas");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);
            Assert.That(ShowsChecker(lod0), Is.True, "UV1 on the repacked mesh is enough for the checker");

            SwitchLod(1);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(0));

            SwitchLod(0);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(1), "the repacked mesh carries UV1, so the picked channel returns");
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker));
            Assert.That(ShowsChecker(lod0), Is.True);
        }

        [Test]
        public void PickedUvChannelReturnsWhenTheLodHasIt()
        {
            var group = Group(lod1HasUv1: false, out var lod0, out var lod1);
            var ctx = Open(group);
            var canvas = Get<UvCanvasView>(hub, "canvas");
            Call(hub, "ApplyPreviewMode", UvCanvasView.PreviewMode.Checker);

            SwitchLod(1);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(0), "LOD1 has no UV1: the canvas falls back to a channel it has");
            Assert.That(canvas.CurrentPreviewMode, Is.EqualTo(UvCanvasView.PreviewMode.Checker));
            Assert.That(ShowsChecker(lod1), Is.True);

            SwitchLod(0);
            Assert.That(ctx.PreviewUvChannel, Is.EqualTo(1), "back on LOD0 the channel the user picked returns");
            Assert.That(ShowsChecker(lod0), Is.True);
            Assert.That(ShowsChecker(lod1), Is.False);
        }
    }
}
