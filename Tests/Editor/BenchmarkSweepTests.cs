using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class BenchmarkSweepTests
    {
        static System.Type SweepType =>
            typeof(UvShellExtractor).Assembly.GetType("SashaRX.UnityMeshLab.BenchmarkSweep");

        static string EscapeCsv(string value)
        {
            var method = SweepType.GetMethod("Csv", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { value });
        }

        static List<string> ParseCsvRow(string line)
        {
            var method = SweepType.GetMethod("ParseCsvRow", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return (List<string>)method.Invoke(null, new object[] { line });
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

        // AggregateRun reads reports with File.ReadAllLines and parses each
        // physical line as one record, so an escaped field must never introduce
        // a line break — otherwise every column after it shifts.
        [Test]
        public void Csv_FieldWithLineBreaks_SurvivesLineSplitRoundTrip()
        {
            string row = string.Join(",",
                EscapeCsv("Mesh\r\nLOD0"), EscapeCsv("=1,2"), EscapeCsv("7"));

            var physicalLines = row.Split('\n');
            Assert.AreEqual(1, physicalLines.Length,
                "One logical record must stay on one physical line.");

            var cells = ParseCsvRow(physicalLines[0]);
            Assert.AreEqual(3, cells.Count);
            Assert.AreEqual("Mesh  LOD0", cells[0]);
            Assert.AreEqual("'=1,2", cells[1]);
            Assert.AreEqual("7", cells[2]);
        }
    }
}
