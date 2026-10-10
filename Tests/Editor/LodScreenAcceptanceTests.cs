using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class LodScreenAcceptanceTests
    {
        const int Size = 64;
        static Color[] Field(Color color) => Enumerable.Repeat(color,Size*Size).ToArray();
        static Color[] Mask(int x,int y,int width,int height)
        {
            var mask = new Color[Size*Size];
            for (int row = y; row < y+height; row++) for (int column = x; column < x+width; column++) mask[row*Size+column] = Color.white;
            return mask;
        }
        static LodScreenAcceptance Check(Color[] source,Color[] mask,LodScreenAcceptance.Settings? settings = null)
            => new LodScreenAcceptance(source,mask,Size,Size,settings ?? LodScreenAcceptance.Settings.Default);

        [Test] public void MissingSmallThinDetailFailsDespiteSmallGlobalCoverageLoss()
        {
            var original = Mask(4,4,44,48); var target = (Color[])original.Clone();
            for (int y = 18; y < 42; y++) original[y*Size+55] = Color.white;
            var report = Check(Field(Color.white),original).Measure(Field(Color.white),target);
            Assert.That(24f/(44*48+24),Is.LessThan(.012f));
            Assert.That(report.vanishedThinRegions,Is.EqualTo(1)); Assert.That(report.lostComponents,Is.EqualTo(1));
            Assert.That(report.detailAccepted,Is.False);
        }
        [Test] public void ThickSurfaceIsNotMistakenForThinBoundaryBand()
        {
            var mask = Mask(8,8,40,40); var report = Check(Field(Color.white),mask).Measure(Field(Color.white),mask);
            Assert.That(report.thinRegions,Is.Zero); Assert.That(report.detailAccepted,Is.True);
            Assert.That(report.colorEvaluated,Is.False); Assert.That(report.colorAccepted,Is.False);
        }
        [Test] public void MissingThickVisibleComponentAlsoFails()
        {
            var original = Mask(4,4,32,40); var target = (Color[])original.Clone();
            var part = Mask(48,16,8,12);
            for (int i = 0; i < part.Length; i++) if (part[i].r > 0) original[i] = part[i];
            var report = Check(Field(Color.white),original).Measure(Field(Color.white),target);
            Assert.That(report.lostComponents,Is.EqualTo(1)); Assert.That(report.detailAccepted,Is.False);
        }
        [TestCase(1,true)] [TestCase(3,false)]
        public void PixelCorrespondenceToleranceIsBounded(int shift,bool accepted)
        {
            var source = Mask(24,8,1,40); var target = Mask(24+shift,8,1,40);
            Assert.That(Check(Field(Color.white),source).Measure(Field(Color.white),target).detailAccepted,Is.EqualTo(accepted));
        }
        [TestCase(false)] [TestCase(true)]
        public void BlurredRgbOrAlphaBoundaryFailsEvenWithSmallGlobalRms(bool alpha)
        {
            var source = Field(Color.black); var target = Field(Color.black); var mask = Field(Color.white);
            for (int y = 24; y < 40; y++) for (int x = 28; x < 36; x++)
            {
                int i = y*Size+x;
                source[i] = alpha ? new Color(0,0,0,0) : Color.red;
                target[i] = alpha ? new Color(0,0,0,.5f) : new Color(.5f,0,0,1);
            }
            float rms = Mathf.Sqrt(128f/(Size*Size)*.25f); Assert.That(rms,Is.LessThan(.09f));
            var report = Check(source,mask).Measure(target,mask);
            Assert.That(report.colorEvaluated,Is.True); Assert.That(report.colorAccepted,Is.False);
            Assert.That(report.worstColorRegionLoss,Is.GreaterThan(.9f));
        }
        [Test] public void SmoothGradientDoesNotClaimCategoricalCoverage()
        {
            var source = Field(Color.white); for (int i = 0; i < source.Length; i++) source[i].r = i%Size*.01f;
            var report = Check(source,Field(Color.white)).Measure(source,Field(Color.white));
            Assert.That(report.sharpColorPairs,Is.Zero); Assert.That(report.colorEvaluated,Is.False);
        }
        [Test] public void ModelsWithoutAuthoredVaryingColorsDoNotClaimPaintAcceptance()
        {
            var colors = Field(Color.white); for (int i = 0; i < Size; i++) colors[i] = Color.black;
            var settings = LodScreenAcceptance.Settings.Default; settings.checkColorBoundaries = false;
            var report = Check(colors,Field(Color.white),settings).Measure(colors,Field(Color.white));
            Assert.That(report.colorEvaluated,Is.False); Assert.That(report.colorAccepted,Is.False);
        }
        [Test] public void HdrFieldsAndIndependentAlphaSurviveDownsampling()
        {
            var source = Field(new Color(10,2,-1,.3f));
            for (int y = 0; y < Size; y++) for (int x = Size/2; x < Size; x++) source[y*Size+x].r = 12;
            var settings = LodScreenAcceptance.Settings.Default; settings.divisor = 2;
            var report = Check(source,Field(Color.white),settings).Measure(source,Field(Color.white));
            Assert.That(report.width,Is.EqualTo(32)); Assert.That(report.colorEvaluated,Is.True);
            Assert.That(report.colorAccepted,Is.True); Assert.That(report.lostColorPairs,Is.Zero);
        }
        [Test] public void LostImageBuffersAndNonfiniteFieldsCannotPass()
        {
            var source = Field(Color.white); var mask = Field(Color.white);
            Assert.Throws<ArgumentException>(() => Check(new Color[1],mask));
            source[2].a = float.NaN;
            Assert.Throws<ArgumentException>(() => Check(source,mask));
        }
        [Test] public void AnalysisAndMeasurementAreCancellable()
        {
            var colors = Field(Color.white); var mask = Field(Color.white);
            Assert.Throws<OperationCanceledException>(() => new LodScreenAcceptance(colors,mask,Size,Size,LodScreenAcceptance.Settings.Default,() => true));
            Assert.Throws<OperationCanceledException>(() => Check(colors,mask).Measure(colors,mask,() => true));
        }

        [Serializable] sealed class Replay
        {
            public string fixture, sourceSha256, unity, gpu, graphicsApi;
            public int resolution;
            public bool varyingVertexColors;
            public List<LodVisualQualityTests.Capture> captures;
        }
        [Serializable] sealed class Evaluation
        {
            public string fixture, sourceSha256, captureFile;
            public List<Row> rows = new List<Row>();
        }
        [Serializable] sealed class Row
        {
            public string variant, view;
            public int triangles, sourceWidth, sourceHeight;
            public List<LodScreenAcceptance.Report> screens = new List<LodScreenAcceptance.Report>();
        }
        static Color[] Readback(string path,int expected)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            Assert.That(reader.ReadInt32(),Is.EqualTo(expected)); Assert.That(reader.ReadInt32(),Is.EqualTo(expected));
            Assert.That(reader.BaseStream.Length,Is.EqualTo(8L+expected*expected*16L));
            var colors = new Color[expected*expected];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
            return colors;
        }
        [Test] [Timeout(300000)]
        public void ExistingActualProjectGpuReadbacksProduceIndependentAcceptanceReports()
        {
            var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args,"-meshlabLodScreenInput");
            if (at < 0 || at+1 >= args.Length) Assert.Ignore("Specify actual GPU readbacks with -meshlabLodScreenInput.");
            string directory = args[at+1], output = LodVisualQualityTests.OutputDirectory();
            Assert.That(output,Is.Not.Null.And.Not.Empty); Directory.CreateDirectory(output);
            var files = Directory.GetFiles(directory,"*-metrics.json"); Assert.That(files.Length,Is.EqualTo(8));
            foreach (var file in files.OrderBy(f => f))
            {
                var replay = JsonUtility.FromJson<Replay>(File.ReadAllText(file));
                Assert.That(replay.resolution,Is.EqualTo(384)); Assert.That(replay.graphicsApi,Is.Not.EqualTo("Null"));
                Assert.That(replay.gpu,Is.Not.Empty); Assert.That(replay.captures,Has.Count.EqualTo(22));
                var evaluation = new Evaluation { fixture = replay.fixture,sourceSha256 = replay.sourceSha256,captureFile = Path.GetFileName(file) };
                foreach (string view in new[] { "front","oblique" })
                {
                    var original = Readback(Path.Combine(directory,$"{replay.fixture}-source-{view}-color.rgba"),384);
                    var sourceMask = Readback(Path.Combine(directory,$"{replay.fixture}-source-{view}-coverage.rgba"),384);
                    var screens = new List<LodScreenAcceptance>();
                    foreach (int divisor in new[] {1,2,4})
                    {
                        var settings = LodScreenAcceptance.Settings.Default; settings.divisor = divisor;
                        settings.checkColorBoundaries = replay.varyingVertexColors;
                        screens.Add(new LodScreenAcceptance(original,sourceMask,384,384,settings));
                    }
                    foreach (var capture in replay.captures.Where(c => c.view == view))
                    {
                        var color = Readback(Path.Combine(directory,$"{replay.fixture}-{capture.variant}-{view}-color.rgba"),384);
                        var mask = Readback(Path.Combine(directory,$"{replay.fixture}-{capture.variant}-{view}-coverage.rgba"),384);
                        var row = new Row { variant = capture.variant,view = view,triangles = capture.triangles,sourceWidth = 384,sourceHeight = 384 };
                        foreach (var screen in screens) row.screens.Add(screen.Measure(color,mask));
                        if (capture.variant == "source") foreach (var result in row.screens)
                        {
                            Assert.That(result.lostThinPixels,Is.Zero); Assert.That(result.lostComponents,Is.Zero);
                            Assert.That(result.lostColorPairs,Is.Zero); Assert.That(result.detailAccepted,Is.True);
                            Assert.That(result.colorAccepted,Is.EqualTo(result.colorEvaluated));
                        }
                        evaluation.rows.Add(row);
                    }
                }
                File.WriteAllText(Path.Combine(output,replay.fixture+"-screen.json"),JsonUtility.ToJson(evaluation,true));
            }
        }
    }
}
