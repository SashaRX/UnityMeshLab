// ValidateStep.cs — the Validate step, first in the chain: the model is checked against its
// recipe's requirements (ModelValidation: the file, the nodes, the LOD chain, every mesh), and
// an Error blocks Apply. It changes nothing (ICheckStep). A model whose importer the Preset or
// Materials step changes is checked again once it is reimported, since the preset may set what
// was missing (Scale Factor, generated lightmap UVs, tangents); its errors then stop the steps
// after it. A model or prefab asset is checked as an asset, a scene object as placed (its own
// root's scale aside).

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

        void Run(PrepareContext context, PrepareReport report)
        {
            var target = context.Target;
            if (target.Kind == PrepareTargetKind.SceneObject) ModelValidation.Validate(target.SceneObject, requirements, report);
            else ModelValidation.Validate(target.AssetPath, requirements, report);
            if (report.Lines.Count == 0) report.Add(PrepareLevel.Unchanged, $"'{target.Display}'", Check, "the model meets every requirement");
        }
    }
}
