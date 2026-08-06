using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class BenchmarkRecorderTests
    {
        static string Csv(string value)
        {
            var method = typeof(BenchmarkRecorder).GetMethod(
                "Csv", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return (string)method.Invoke(null, new object[] { value });
        }

        [TestCase("=1+1", "'=1+1")]
        [TestCase("+SUM(A1:A2)", "'+SUM(A1:A2)")]
        [TestCase("-1+2", "'-1+2")]
        [TestCase("@SUM(A1:A2)", "'@SUM(A1:A2)")]
        [TestCase("\t=1+1", "'\t=1+1")]
        [TestCase("\r=1+1", "\"'\r=1+1\"")]
        [TestCase("\n=1+1", "\"'\n=1+1\"")]
        public void Csv_FormulaPrefix_NeutralizesCell(string value, string expected)
        {
            Assert.AreEqual(expected, Csv(value));
        }

        [Test]
        public void Csv_FormulaWithDelimiter_NeutralizesAndQuotesCell()
        {
            Assert.AreEqual("\"'=HYPERLINK(\"\"https://example.invalid\"\",\"\"open\"\")\"",
                Csv("=HYPERLINK(\"https://example.invalid\",\"open\")"));
        }

        [TestCase("Mesh_LOD0")]
        [TestCase("1-mesh")]
        [TestCase("")]
        public void Csv_SafeValue_RemainsUnchanged(string value)
        {
            Assert.AreEqual(value, Csv(value));
        }
    }
}
