// PrepareTestSurface.cs — a stand-in for a game's surface component (a ballistic material, say)
// for the surface binding's tests: an object field that takes a PrepareTestSurfaceData. Outside
// the editor tests' assembly: Unity adds no component whose script is in an editor assembly.
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareTestSurface : MonoBehaviour
    {
        [SerializeField] PrepareTestSurfaceData _material;

        public PrepareTestSurfaceData Material => _material;
    }
}
