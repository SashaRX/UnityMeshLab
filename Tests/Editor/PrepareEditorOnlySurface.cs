// PrepareEditorOnlySurface.cs — a surface component in the editor tests' assembly, which Unity
// will not add to an object (an editor script): the binding reports it instead of failing.
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareEditorOnlySurface : MonoBehaviour
    {
        [SerializeField] PrepareTestSurfaceData _material;

        public PrepareTestSurfaceData Material => _material;
    }
}
