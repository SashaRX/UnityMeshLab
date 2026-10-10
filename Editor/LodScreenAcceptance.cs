using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Independent diagnostics on linear GPU readbacks. Image regions are visible
    // features, not recovered mesh components or semantic paint labels.
    internal sealed class LodScreenAcceptance
    {
        [Serializable] internal sealed class Report
        {
            public int width, height, objectWidth, objectHeight;
            public int thinPixels, thinRegions, lostThinPixels, vanishedThinRegions;
            public int visibleComponents, lostComponents;
            public int sharpColorPairs, colorRegions, lostColorPairs;
            public float thinLoss, worstThinRegionLoss, worstComponentLoss;
            public float colorBoundaryLoss, worstColorRegionLoss;
            public bool detailEvaluated, colorEvaluated, detailAccepted, colorAccepted;
        }
        internal struct Settings
        {
            internal int divisor, radius, tolerance, minimumPixels, minimumPairs;
            internal float contrast, colorTolerance, maximumLoss;
            internal bool checkColorBoundaries;
            internal static Settings Default => new Settings { divisor = 1, radius = 2,
                tolerance = 1, minimumPixels = 8, minimumPairs = 4,
                contrast = .15f, colorTolerance = .1f, maximumLoss = .25f, checkColorBoundaries = true };
        }
        sealed class Frame
        {
            internal int width, height;
            internal bool[] covered, full;
            internal Color[] colors;
        }
        struct Pair { internal int a, b, region; }
        readonly Settings settings;
        readonly Frame source;
        readonly bool[] colorInterior;
        readonly int inputWidth, inputHeight;
        readonly int[] thinLabels, componentLabels, colorLabels;
        readonly int[] thinCounts, componentCounts, colorCounts;
        readonly List<Pair> pairs = new List<Pair>();

        internal LodScreenAcceptance(Color[] colors,Color[] coverage,int width,int height,
            Settings settings,Func<bool> cancelled = null)
        {
            if (settings.divisor < 1 || settings.radius < 1 || settings.radius > 8 ||
                settings.tolerance < 0 || settings.tolerance > 4 || settings.minimumPixels < 1 ||
                settings.minimumPairs < 1 || !Finite(settings.contrast) || settings.contrast <= 0 ||
                !Finite(settings.colorTolerance) || settings.colorTolerance < 0 ||
                !Finite(settings.maximumLoss) || settings.maximumLoss < 0 || settings.maximumLoss > 1)
                throw new ArgumentException("Invalid LOD screen-acceptance settings.");
            this.settings = settings; inputWidth = width; inputHeight = height;
            source = Read(colors,coverage,width,height,settings.divisor,cancelled);
            var core = Morph(source.covered,source.width,source.height,settings.radius,true,cancelled);
            var opened = Morph(core,source.width,source.height,settings.radius,false,cancelled);
            var thin = new bool[source.covered.Length];
            for (int i = 0; i < thin.Length; i++) thin[i] = source.covered[i] && !opened[i];
            thinLabels = Label(thin,source.width,source.height,out thinCounts,cancelled);
            componentLabels = Label(source.covered,source.width,source.height,out componentCounts,cancelled);
            colorInterior = Morph(source.full,source.width,source.height,1,true,cancelled);
            var boundaries = new bool[thin.Length];
            for (int y = 0; y < source.height; y++) for (int x = 0; x < source.width; x++)
            {
                Check(cancelled,y*source.width+x);
                int a = y*source.width+x;
                if (!settings.checkColorBoundaries || !colorInterior[a]) continue;
                if (x+1 < source.width) AddPair(a,a+1,boundaries);
                if (y+1 < source.height) AddPair(a,a+source.width,boundaries);
            }
            colorLabels = Label(boundaries,source.width,source.height,out var unused,cancelled);
            colorCounts = new int[unused.Length];
            for (int i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i]; pair.region = colorLabels[pair.a]; pairs[i] = pair;
                colorCounts[pair.region]++;
            }
        }

        void AddPair(int a,int b,bool[] boundary)
        {
            if (!colorInterior[b] || Difference(source.colors[a],source.colors[b]) < settings.contrast) return;
            pairs.Add(new Pair { a = a,b = b }); boundary[a] = boundary[b] = true;
        }

        internal Report Measure(Color[] colors,Color[] coverage,Func<bool> cancelled = null)
        {
            var target = Read(colors,coverage,inputWidth,inputHeight,settings.divisor,cancelled);
            var supported = Morph(target.covered,source.width,source.height,settings.tolerance,false,cancelled);
            var report = new Report { width = source.width,height = source.height };
            int left = source.width, right = -1, bottom = source.height, top = -1;
            var thinLost = new int[thinCounts.Length]; var componentLost = new int[componentCounts.Length];
            for (int i = 0; i < source.covered.Length; i++)
            {
                Check(cancelled,i);
                if (!source.covered[i]) continue;
                int x = i%source.width, y = i/source.width;
                left = Mathf.Min(left,x); right = Mathf.Max(right,x); bottom = Mathf.Min(bottom,y); top = Mathf.Max(top,y);
                if (thinLabels[i] >= 0)
                {
                    report.thinPixels++;
                    if (!supported[i]) { report.lostThinPixels++; thinLost[thinLabels[i]]++; }
                }
                if (!supported[i]) componentLost[componentLabels[i]]++;
            }
            report.objectWidth = Mathf.Max(0,right-left+1); report.objectHeight = Mathf.Max(0,top-bottom+1);
            report.thinLoss = Fraction(report.lostThinPixels,report.thinPixels);
            for (int i = 0; i < thinCounts.Length; i++) if (thinCounts[i] >= settings.minimumPixels)
            {
                report.thinRegions++; float loss = Fraction(thinLost[i],thinCounts[i]);
                report.worstThinRegionLoss = Mathf.Max(report.worstThinRegionLoss,loss);
                if (loss >= .95f) report.vanishedThinRegions++;
            }
            for (int i = 0; i < componentCounts.Length; i++) if (componentCounts[i] >= settings.minimumPixels)
            {
                report.visibleComponents++; float loss = Fraction(componentLost[i],componentCounts[i]);
                report.worstComponentLoss = Mathf.Max(report.worstComponentLoss,loss);
                if (loss >= .95f) report.lostComponents++;
            }
            report.detailEvaluated = report.visibleComponents > 0;
            report.detailAccepted = report.detailEvaluated && report.worstThinRegionLoss <= settings.maximumLoss &&
                report.worstComponentLoss <= settings.maximumLoss;
            var colorLost = new int[colorCounts.Length];
            for (int i = 0; i < pairs.Count; i++)
            {
                Check(cancelled,i);
                var pair = pairs[i];
                report.sharpColorPairs++;
                if (!PairRetained(pair,target)) { report.lostColorPairs++; colorLost[pair.region]++; }
            }
            report.colorBoundaryLoss = Fraction(report.lostColorPairs,report.sharpColorPairs);
            for (int i = 0; i < colorCounts.Length; i++) if (colorCounts[i] >= settings.minimumPairs)
            {
                report.colorRegions++;
                report.worstColorRegionLoss = Mathf.Max(report.worstColorRegionLoss,Fraction(colorLost[i],colorCounts[i]));
            }
            report.colorEvaluated = report.colorRegions > 0;
            report.colorAccepted = report.colorEvaluated && report.worstColorRegionLoss <= settings.maximumLoss;
            return report;
        }

        bool PairRetained(Pair pair,Frame target)
        {
            int ax = pair.a%source.width, ay = pair.a/source.width;
            int bx = pair.b%source.width, by = pair.b/source.width;
            Color delta = source.colors[pair.b]-source.colors[pair.a];
            for (int dy = -settings.tolerance; dy <= settings.tolerance; dy++)
                for (int dx = -settings.tolerance; dx <= settings.tolerance; dx++)
                {
                    int x0 = ax+dx, y0 = ay+dy, x1 = bx+dx, y1 = by+dy;
                    if (x0 < 0 || y0 < 0 || x1 < 0 || y1 < 0 || x0 >= source.width || x1 >= source.width ||
                        y0 >= source.height || y1 >= source.height) continue;
                    int a = y0*source.width+x0, b = y1*source.width+x1;
                    if (!target.full[a] || !target.full[b]) continue;
                    if (Difference(target.colors[a],source.colors[pair.a]) <= settings.colorTolerance &&
                        Difference(target.colors[b],source.colors[pair.b]) <= settings.colorTolerance &&
                        Difference(target.colors[b]-target.colors[a],delta) <= settings.colorTolerance) return true;
                }
            return false;
        }

        static Frame Read(Color[] colors,Color[] coverage,int width,int height,int divisor,Func<bool> cancelled)
        {
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || width%divisor != 0 || height%divisor != 0 ||
                colors == null || coverage == null || colors.Length != width*height || coverage.Length != colors.Length)
                throw new ArgumentException("Invalid LOD GPU readback dimensions or buffers.");
            var frame = new Frame { width = width/divisor,height = height/divisor };
            int count = frame.width*frame.height;
            frame.covered = new bool[count]; frame.full = new bool[count]; frame.colors = new Color[count];
            for (int y = 0; y < frame.height; y++) for (int x = 0; x < frame.width; x++)
            {
                Check(cancelled,y*frame.width+x);
                Color sum = default; int covered = 0, full = 0;
                for (int dy = 0; dy < divisor; dy++) for (int dx = 0; dx < divisor; dx++)
                {
                    int pixel = (y*divisor+dy)*width+x*divisor+dx;
                    if (!Finite(coverage[pixel].r) || !Finite(colors[pixel].r) || !Finite(colors[pixel].g) ||
                        !Finite(colors[pixel].b) || !Finite(colors[pixel].a)) throw new ArgumentException("Nonfinite LOD GPU readback.");
                    if (coverage[pixel].r >= .5f) { sum += colors[pixel]; covered++; }
                    if (coverage[pixel].r >= .99999f) full++;
                }
                int i = y*frame.width+x;
                frame.covered[i] = covered*2 >= divisor*divisor;
                frame.full[i] = full == divisor*divisor;
                frame.colors[i] = covered > 0 ? sum/covered : default;
            }
            return frame;
        }

        static bool[] Morph(bool[] mask,int width,int height,int radius,bool erosion,Func<bool> cancelled)
        {
            // Summed-area coverage makes morphology independent of neighborhood area.
            var sums = new int[(width+1)*(height+1)];
            for (int y = 0; y < height; y++)
            {
                Check(cancelled,y*width); int row = 0;
                for (int x = 0; x < width; x++)
                {
                    if (mask[y*width+x]) row++;
                    sums[(y+1)*(width+1)+x+1] = sums[y*(width+1)+x+1]+row;
                }
            }
            var result = new bool[mask.Length];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Check(cancelled,y*width+x);
                int x0 = Mathf.Max(0,x-radius), x1 = Mathf.Min(width,x+radius+1);
                int y0 = Mathf.Max(0,y-radius), y1 = Mathf.Min(height,y+radius+1);
                int sum = sums[y1*(width+1)+x1]-sums[y0*(width+1)+x1]-sums[y1*(width+1)+x0]+sums[y0*(width+1)+x0];
                result[y*width+x] = erosion ? x-radius >= 0 && y-radius >= 0 && x+radius < width && y+radius < height &&
                    sum == (2*radius+1)*(2*radius+1) : sum > 0;
            }
            return result;
        }

        static int[] Label(bool[] mask,int width,int height,out int[] counts,Func<bool> cancelled)
        {
            var labels = new int[mask.Length]; for (int i = 0; i < labels.Length; i++) labels[i] = -1;
            var sizes = new List<int>(); var queue = new Queue<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                Check(cancelled,start);
                if (!mask[start] || labels[start] >= 0) continue;
                int label = sizes.Count, count = 0; labels[start] = label; queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue(); count++; Check(cancelled,count);
                    int x = i%width, y = i/width;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x+dx, ny = y+dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int next = ny*width+nx;
                        if (!mask[next] || labels[next] >= 0) continue;
                        labels[next] = label; queue.Enqueue(next);
                    }
                }
                sizes.Add(count);
            }
            counts = sizes.ToArray(); return labels;
        }
        static float Fraction(int value,int count) => count > 0 ? (float)value/count : 0;
        static float Difference(Color a,Color b) => Mathf.Max(Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Abs(a.g-b.g)),
            Mathf.Max(Mathf.Abs(a.b-b.b),Mathf.Abs(a.a-b.a)));
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Check(Func<bool> cancelled,int index)
        {
            if ((index & 1023) == 0 && cancelled?.Invoke() == true)
                throw new OperationCanceledException("LOD screen acceptance cancelled.");
        }
    }
}
