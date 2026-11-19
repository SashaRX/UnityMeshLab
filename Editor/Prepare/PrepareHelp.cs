// PrepareHelp.cs — what a finding means and where it can be seen: for each check of a prepare
// report, one sentence on why it matters and how it is fixed, and the Mesh Lab tab (with the
// view to pick in it) that shows the faces, edges or UVs in question. The Prepare tab draws it
// under a warning or an error; a check without an entry has nothing to add to its message.

using System.Collections.Generic;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Why a finding matters and how it is fixed, and where it is shown.</summary>
    internal readonly struct PrepareHint
    {
        internal PrepareHint(string why, string toolId = null, string toolName = null, string view = null)
        {
            Why = why; ToolId = toolId; ToolName = toolName; View = view;
        }

        /// <summary>Why it matters and what fixes it.</summary>
        internal string Why { get; }

        /// <summary>The id of the tab that shows it (<see cref="IUvTool.ToolId"/>), or null.</summary>
        internal string ToolId { get; }

        /// <summary>That tab's name, or null.</summary>
        internal string ToolName { get; }

        /// <summary>What to pick in the tab to see it ("Scene Preview ▸ Problems"), or null.</summary>
        internal string View { get; }

        /// <summary>"Prefab Builder ▸ Scene Preview ▸ Problems", or null without a tab.</summary>
        internal string Where
        {
            get
            {
                if (ToolId == null) return null;
                return View == null ? ToolName : $"{ToolName} ▸ {View}";
            }
        }
    }

    internal static class PrepareHelp
    {
        const string Builder = "prefab_builder", BuilderName = "Prefab Builder";
        const string Problems = "Scene Preview ▸ Problems";

        static readonly Dictionary<string, PrepareHint> Hints = new Dictionary<string, PrepareHint>
        {
            ["Lightmap UV"] = new PrepareHint(
                "In the lightmap atlas the texels past the unit square belong to another object, so light bleeds across. "
                + "The Fix step clamps a small overshoot; a larger one needs a repack (UV2 Transfer with Repack) or a fix in the DCC.",
                Builder, BuilderName, "Scene Preview ▸ UV1"),
            ["Degenerate faces"] = new PrepareHint(
                "A face without area has no normal and no UV area: it breaks lightmap UVs, tangents and collision cooking. Remove it in Cleanup or the DCC.",
                Builder, BuilderName, Problems + " (red)"),
            ["Tiny faces"] = new PrepareHint(
                "A face this small renders as noise and gets no lightmap texel; usually a leftover of a boolean or a bevel. Merge or remove it.",
                Builder, BuilderName, Problems + " (orange)"),
            ["Short edges"] = new PrepareHint(
                "An edge this short is usually two vertices meant to be one. Weld them in Cleanup or the DCC.",
                Builder, BuilderName, Problems + " (magenta)"),
            ["Unused vertices"] = new PrepareHint(
                "Vertices no face uses only add to the mesh's size and bounds. Cleanup removes them.",
                Builder, BuilderName, Problems + " (dots)"),
            ["Non-manifold edges"] = new PrepareHint(
                "More than two faces share the edge: an inner face or a doubled surface, which LOD generation and unwrapping handle badly. Remove the inner faces in the DCC.",
                Builder, BuilderName, "Scene Preview ▸ Edges"),
            ["Flipped normals"] = new PrepareHint(
                "The faces' normals point into the surface, so they are lit from behind and culled from the front. Flip the faces or recalculate the normals in the DCC.",
                Builder, BuilderName, "Scene Preview ▸ Normals"),
            ["Concave faces"] = new PrepareHint(
                "A concave polygon is triangulated by each tool its own way, so the surface and its shading differ between the DCC and Unity. Triangulate it in the DCC."),
            ["Channels"] = new PrepareHint(
                "A static prop is lit and textured through these channels. Export them from the DCC, or let the importer generate them (the recipe's preset).",
                Builder, BuilderName, "Scene Preview (the channel)"),
            ["Finite values"] = new PrepareHint(
                "NaN or infinity in a vertex breaks bounds, lighting and collision. Re-export the mesh; Cleanup can drop the faces on such vertices."),
            ["Vertex limit"] = new PrepareHint(
                "Past 65535 vertices the mesh needs 32-bit indices, which some platforms lack. Split the mesh or reduce it."),
            ["LOD chain"] = new PrepareHint(
                "Parts are paired into LOD levels by name: a part whose name differs between levels (a typo, a stray prefix) reads as two parts, each missing a level. "
                + "Rename the nodes alike, or generate the missing level.",
                "lod_generation", "LOD Gen"),
            ["LOD triangles"] = new PrepareHint(
                "A LOD that keeps its triangles costs a draw-call switch for nothing. Reduce it further, or drop the level.",
                "lod_generation", "LOD Gen"),
            ["Names"] = new PrepareHint("Steps find nodes by name; give each sibling its own."),
            ["Import scale"] = new PrepareHint("A scaled import hides a file in the wrong units; the recipe's preset sets Scale Factor 1 and Convert Units."),
            ["Units"] = new PrepareHint("The file is not in metres: export it from the DCC in metres, so that every model shares one scale."),
            ["Node scale"] = new PrepareHint(
                "A scaled node skews colliders, lightmap density and anything placed under it; a negative scale mirrors the mesh. Apply (freeze) the transforms in the DCC."),
            ["Size"] = new PrepareHint("A model this small or large is usually in the wrong units. Check the export scale."),
            ["Smoothing groups"] = new PrepareHint(
                "Without smoothing groups the importer cannot tell hard edges from soft ones. Export the FBX with smoothing groups on."),
        };

        /// <summary>The hint of a report line's check; false when there is none.</summary>
        internal static bool TryGet(string check, out PrepareHint hint) => Hints.TryGetValue(check ?? "", out hint);

        /// <summary>
        /// The node a line's subject names, as a path below the root: the text in its first
        /// quotes ('Seat_LOD0' (mesh 'Seat') → Seat_LOD0); null without quotes.
        /// </summary>
        internal static string SubjectPath(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return null;
            int open = subject.IndexOf('\'');
            int close = open < 0 ? -1 : subject.IndexOf('\'', open + 1);
            return close > open ? subject.Substring(open + 1, close - open - 1) : null;
        }
    }
}
