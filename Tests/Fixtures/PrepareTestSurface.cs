// PrepareTestSurface.cs — a stand-in for a game's surface component (a ballistic material, say)
// for the surface binding's tests: an object field that takes a PrepareTestSurfaceData.
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class PrepareTestSurface : MonoBehaviour
    {
        [SerializeField] PrepareTestSurfaceData _material;

        internal PrepareTestSurfaceData Material => _material;
    }
}
