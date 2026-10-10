using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Shared semantics for the UV canvas and every 3D tool overlay.</summary>
    internal static class ViewportHighlight
    {
        internal static readonly Color Hover = new Color(.25f, 1f, .95f, 1f);
        internal static readonly Color Selected = new Color(1f, .85f, .15f, 1f);
        internal static readonly Color Border = new Color(1f, .35f, .05f, .95f);
        internal static readonly Color Hole = new Color(.2f, .85f, 1f, 1f);
        internal static readonly Color Refused = new Color(1f, .12f, .15f, 1f);
        internal static readonly Color Cap = new Color(1f, .5f, .12f, 1f);
        internal static readonly Color Closure = new Color(.65f, .35f, .95f, 1f);
        internal const float EdgeWidth = 2f, ActiveWidth = 3f, VertexSize = 7f;
        internal static Color Fill(Color color) { color.a = .35f; return color; }
    }
}
