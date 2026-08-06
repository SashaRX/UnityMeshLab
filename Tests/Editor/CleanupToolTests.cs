using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CleanupToolTests
    {
        // The helper moved out of CleanupTool into the shared MeshHygieneUtility
        // (CleanupTool and ModelBuilderTool both parse _LOD{N} suffixes). That
        // class is internal, so it is reached through the assembly rather than
        // a typeof().
        static bool TryParseLodIndex(string name, out int lodIndex)
        {
            var type = typeof(CleanupTool).Assembly.GetType(
                "SashaRX.UnityMeshLab.MeshHygieneUtility");
            Assert.IsNotNull(type, "MeshHygieneUtility not found in the editor assembly");
            var method = type.GetMethod(
                "TryParseLodIndex", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "MeshHygieneUtility.TryParseLodIndex not found");
            var arguments = new object[] { name, 0 };
            bool result = (bool)method.Invoke(null, arguments);
            lodIndex = (int)arguments[1];
            return result;
        }

        [TestCase("Part_LOD0", 0)]
        [TestCase("Part_lod7", 7)]
        public void TryParseLodIndex_ValidIndex_ReturnsTrue(string name, int expected)
        {
            Assert.IsTrue(TryParseLodIndex(name, out int actual));
            Assert.AreEqual(expected, actual);
        }

        [TestCase("Part_LOD8")]
        [TestCase("Part_LOD100000000")]
        [TestCase("Part_LOD999999999999999999999999999999")]
        public void TryParseLodIndex_UnsupportedOrOverflowingIndex_ReturnsFalse(string name)
        {
            Assert.IsFalse(TryParseLodIndex(name, out _));
        }
    }
}
