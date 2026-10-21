using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    /// <summary>
    /// The settings key that lets Reverse reuse a Full Pipeline result instead of
    /// repeating it on detached copies. A setting missing from the key, or not
    /// copied into the detached preparation, would let a stale result through.
    /// </summary>
    public class ForwardRunReuseTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>View state, pipeline flags and diagnostics; the Full Pipeline reads none of them.</summary>
        static readonly HashSet<string> NotSettings = new HashSet<string> {
            nameof(UvToolContext.CaptureNextTransfer), nameof(UvToolContext.IsolatedMeshGroup),
            nameof(UvToolContext.IsolatedMeshGroupKey), nameof(UvToolContext.PreviewUvChannel), nameof(UvToolContext.PreviewLod),
            nameof(UvToolContext.PreviewCacheVersion), nameof(UvToolContext.ShellColorKeyCacheDirty),
            nameof(UvToolContext.PostResetColoring), nameof(UvToolContext.HasRepack), nameof(UvToolContext.HasTransfer),
            nameof(UvToolContext.SourceFbxPath), nameof(UvToolContext.StandaloneMesh) };

        static readonly string[] WorkflowSettings = { "splitTargetsInSymmetryStep", "skipSymmetrySplitStep",
            "stageRunAnalyzeUv0", "stageRunWeldUv0", "stageWeldRunMeshopt", "stageRunRepack", "stageRunTransfer",
            nameof(UvTransferWorkflow.TransferBoundaryToleranceTexels) };

        /// <summary>Every scalar context field; a new one fails these tests until it is keyed or listed above.</summary>
        static IEnumerable<FieldInfo> ContextSettings() => typeof(UvToolContext).GetFields(Fields)
            .Where(f => (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string))
                && !f.IsInitOnly && !NotSettings.Contains(f.Name));

        static object Changed(object value) => value switch {
            null => "changed",
            bool b => !b,
            int i => i + 3,
            float f => f * 2 + .5f,
            string s => s + "x",
            Enum e => Enum.GetValues(e.GetType()).Cast<object>().First(v => !v.Equals(e)),
            _ => throw new NotSupportedException(value.GetType().Name)
        };

        static void Change(UvTransferWorkflow workflow, string name)
        {
            var field = typeof(UvTransferWorkflow).GetField(name, Fields);
            Assert.NotNull(field, name);
            field.SetValue(workflow, Changed(field.GetValue(workflow)));
        }

        [Test]
        public void EveryContextSettingChangesTheKey()
        {
            var workflow = new UvTransferWorkflow();
            var fields = ContextSettings().ToArray();
            Assert.Greater(fields.Length, 20);
            foreach (var field in fields)
            {
                var context = new UvToolContext();
                string before = UvTransferWorkflow.PreparationSettings(context, workflow);
                field.SetValue(context, Changed(field.GetValue(context)));
                Assert.AreNotEqual(before, UvTransferWorkflow.PreparationSettings(context, workflow), field.Name);
            }
        }

        [Test]
        public void EveryWorkflowSettingChangesTheKey()
        {
            var context = new UvToolContext();
            foreach (var name in WorkflowSettings)
            {
                var workflow = new UvTransferWorkflow();
                string before = UvTransferWorkflow.PreparationSettings(context, workflow);
                Change(workflow, name);
                Assert.AreNotEqual(before, UvTransferWorkflow.PreparationSettings(context, workflow), name);
            }
            var symmetry = new UvTransferWorkflow();
            string legacy = UvTransferWorkflow.PreparationSettings(context, symmetry);
            symmetry.SymmetrySplitMode = SymmetrySplitShells.ThresholdMode.Adaptive;
            Assert.AreNotEqual(legacy, UvTransferWorkflow.PreparationSettings(context, symmetry));
        }

        /// <summary>
        /// A run with a shared atlas, the finest included LOD as source and every stage on
        /// has the detached preparation's key, whatever the other settings are.
        /// </summary>
        [Test]
        public void DetachedPreparationCopiesEverySetting()
        {
            var context = new UvToolContext();
            foreach (var field in ContextSettings()) field.SetValue(context, Changed(field.GetValue(context)));
            context.RepackPerMesh = false; context.SourceLodIndex = 1;
            var workflow = new UvTransferWorkflow { SymmetrySplitMode = SymmetrySplitShells.ThresholdMode.Adaptive };
            foreach (var name in WorkflowSettings.Where(n => n != "stageRunRepack" && n != "stageRunTransfer")) Change(workflow, name);
            var entries = new[] { new MeshEntry { lodIndex = 3 }, new MeshEntry { lodIndex = 1 } };
            string detached = UvTransferWorkflow.DetachedPreparationSettings(context, workflow, entries);
            Assert.AreEqual(UvTransferWorkflow.PreparationSettings(context, workflow), detached);

            context.RepackPerMesh = true;
            Assert.AreNotEqual(UvTransferWorkflow.PreparationSettings(context, workflow), detached, "Per-mesh atlases are never reused.");
            context.RepackPerMesh = false; context.SourceLodIndex = 3;
            Assert.AreNotEqual(UvTransferWorkflow.PreparationSettings(context, workflow), detached, "Only the finest LOD can be the source.");
            context.SourceLodIndex = 1; Change(workflow, "stageRunTransfer");
            Assert.AreNotEqual(UvTransferWorkflow.PreparationSettings(context, workflow), detached, "A partial pipeline is not reused.");
        }
    }
}
