using System.Reflection;
using NUnit.Framework;

namespace SashaRX.UnityMeshLab.Tests
{
    public class BenchmarkRecorderTests
    {
        static string Csv(string value)
        {
            var method = typeof(BenchmarkRecorder).GetMethod("Csv",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { value });
        }

        [TestCase("=2+3", "'=2+3")]
        [TestCase("+2+3", "'+2+3")]
        [TestCase("-2+3", "'-2+3")]
        [TestCase("@SUM(A1:A2)", "'@SUM(A1:A2)")]
        [TestCase("\t=2+3", "'\t=2+3")]
        [TestCase("safe", "safe")]
        [TestCase("safe,value", "\"safe,value\"")]
        [TestCase("=2,3", "\"'=2,3\"")]
        public void Csv_NeutralizesFormulasAndEscapesStructure(string value, string expected)
        {
            Assert.AreEqual(expected, Csv(value));
        }
    }
}
