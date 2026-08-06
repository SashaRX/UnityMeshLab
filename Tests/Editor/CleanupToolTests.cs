using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class CleanupToolTests
    {
        static bool TryParseLodIndex(string name, out int lodIndex)
        {
            var method = typeof(CleanupTool).GetMethod(
                "TryParseLodIndex", BindingFlags.NonPublic | BindingFlags.Static);
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
