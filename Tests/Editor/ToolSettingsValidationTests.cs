using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class ToolSettingsValidationTests
    {
        static object Invoke(string methodName, object value)
        {
            var method = typeof(LightmapTransferTool).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, $"Missing validation method {methodName}");
            return method.Invoke(null, new[] { value });
        }

        [TestCase(-1, 64)]
        [TestCase(0, 64)]
        [TestCase(1024, 1024)]
        [TestCase(int.MaxValue, 4096)]
        public void AtlasResolution_IsClampedToSupportedRange(int input, int expected)
            => Assert.AreEqual(expected, Invoke("SanitizeAtlasResolution", input));

        [TestCase(-1, 0)]
        [TestCase(2, 2)]
        [TestCase(int.MaxValue, 16)]
        public void Padding_IsClampedToUiRange(int input, int expected)
            => Assert.AreEqual(expected, Invoke("SanitizePadding", input));

        [TestCase("Assets/Generated", true)]
        [TestCase("Assets", true)]
        [TestCase("Assets/../Library", false)]
        [TestCase("/tmp/output", false)]
        [TestCase("Packages/output", false)]
        [TestCase("", false)]
        public void SavePath_MustRemainInsideAssets(string input, bool expected)
            => Assert.AreEqual(expected, Invoke("IsSafeAssetFolderPath", input));
    }
}
