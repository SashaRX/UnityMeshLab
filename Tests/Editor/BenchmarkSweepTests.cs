using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class BenchmarkSweepTests
    {
        static string EscapeCsv(string value)
        {
            var type = typeof(UvShellExtractor).Assembly.GetType("SashaRX.UnityMeshLab.BenchmarkSweep");
            var method = type.GetMethod("Csv", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { value });
        }

        [TestCase("=SUM(1,1)", "\"'=SUM(1,1)\"")]
        [TestCase("+cmd", "'+cmd")]
        [TestCase("-2+3", "'-2+3")]
        [TestCase("@SUM(1,1)", "\"'@SUM(1,1)\"")]
        public void Csv_FormulaLeadingValue_PrefixesApostrophe(string value, string expected)
        {
            Assert.AreEqual(expected, EscapeCsv(value));
        }

        [Test]
        public void Csv_OrdinaryFilename_RemainsUnchanged()
        {
            Assert.AreEqual("report.csv", EscapeCsv("report.csv"));
        }
    }
}
