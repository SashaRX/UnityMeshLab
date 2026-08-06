using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class FbxMetricsExporterTests
    {
        static string Csv(string value)
        {
            var method = typeof(FbxMetricsExporter).GetMethod(
                "Csv", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return (string)method.Invoke(null, new object[] { value });
        }

        [TestCase("=1+1", "'=1+1")]
        [TestCase("+1+1", "'+1+1")]
        [TestCase("-1+1", "'-1+1")]
        [TestCase("@SUM(A1:A2)", "'@SUM(A1:A2)")]
        [TestCase("safe", "safe")]
        [TestCase("safe,value", "\"safe,value\"")]
        [TestCase("=SUM(1,2)", "\"'=SUM(1,2)\"")]
        public void Csv_NeutralizesFormulaPrefixesAndPreservesEscaping(
            string value, string expected)
        {
            Assert.AreEqual(expected, Csv(value));
        }
    }
}
