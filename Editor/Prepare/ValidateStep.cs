// ValidateStep.cs — the Validate step, first in the chain: the model is checked against its
// recipe's requirements (ModelValidation: the file, the nodes, the LOD chain, every mesh), and
// an Error blocks Apply; a model that passes lists what it was checked for. It changes nothing (ICheckStep). A model whose importer the Preset or
// Materials step changes is checked again once it is reimported, since the preset may set what
// was missing (Scale Factor, generated lightmap UVs, tangents); its errors then stop the steps
// after it. A model or prefab asset is checked as an asset, a scene object as placed (its own
// root's scale aside); one made from a model is held to that model's file checks too (import
// scale, units, smoothing groups, concave polygons), and one that is not lists none of them.

namespace SashaRX.UnityMeshLab
{
    internal sealed class ValidateStep : ICheckStep
    {
        internal const string StepName = "Validate";
        const string Check = "Requirements";

        readonly ModelRequirements requirements;

        /// <param name="requirements">What the model must satisfy; the default set when null.</param>
        internal ValidateStep(ModelRequirements requirements = null)
        {
            this.requirements = requirements ?? ModelRequirements.Default;
        }

        public string Name => StepName;

        public string Purpose => "The model is checked against the recipe's requirements; an error blocks Apply.";

        public string SkipReason(PrepareContext context) => null;

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report);

        // Changes nothing: Apply checks again what the steps before it left.
        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report);

        // The FBX's own polygons are read through the FBX SDK, which comes with the FBX Exporter package.
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        const bool PolygonsRead = true;
#else
        const bool PolygonsRead = false;
#endif

        void Run(PrepareContext context, PrepareReport report)
        {
            var target = context.Target;
            // What is made from a model (a scene object, a prefab) is held to its model's file checks too.
            string model = target.ModelPath;
            if (target.Kind == PrepareTargetKind.SceneObject)
            {
                if (model != null) ModelValidation.ValidateFile(model, requirements, report);
                ModelValidation.Validate(target.SceneObject, requirements, report);
            }
            else
            {
                if (model != null && model != target.AssetPath) ModelValidation.ValidateFile(model, requirements, report);
                ModelValidation.Validate(target.AssetPath, requirements, report);
            }
            if (report.Lines.Count > 0) return;
            bool fbx = model != null && model.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
            report.Add(PrepareLevel.Unchanged, $"'{target.Display}'", Check, "the model meets every requirement",
                requirements.Checked(model != null, fbx, PolygonsRead));
        }
    }
}
