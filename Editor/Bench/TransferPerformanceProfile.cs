using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Opt-in benchmark wall times. Nested stages overlap;
    /// their durations must not be added to the total.</summary>
    [Serializable]
    internal sealed class TransferPerformanceProfile
    {
        [Serializable] internal sealed class Sample
        {
            public string stage;
            public int lod;
            public double milliseconds;
            public long count;
        }

        public List<Sample> samples = new List<Sample>();
        readonly object sync = new object();
        readonly Dictionary<(string stage,int lod),Sample> totals=new Dictionary<(string,int),Sample>();
        internal string nativePackingCaptureDirectory;
        internal string nativePackingReplayExecutable;
        int nativeCaptures;
        internal sealed class NativePackingOutput
        {
            internal uint width,height,chartCount;
            internal uint[] references,charts;
            internal float[] uv;
        }
        internal IDisposable Measure(string stage, int lod = -1) => new Scope(this, stage, lod, false);

        /// <summary>Like <see cref="Measure"/>, but repeated scopes add to one
        /// sample whose count is the number of completed scopes.</summary>
        internal IDisposable Accumulated(string stage, int lod = -1) => new Scope(this, stage, lod, true);

        internal void Record(string stage, long ticks, long count = 1, int lod = -1)
        {
            var sample = new Sample { stage = stage, lod = lod,
                milliseconds = ticks * (1000.0 / Stopwatch.Frequency), count = count };
            lock (sync) samples.Add(sample);
        }

        internal void Accumulate(string stage,long ticks,long count,int lod=-1)
        {
            lock(sync)
            {
                var key=(stage,lod);
                if(!totals.TryGetValue(key,out var sample))
                {sample=new Sample {stage=stage,lod=lod};totals.Add(key,sample);samples.Add(sample);}
                sample.milliseconds+=ticks*(1000.0/Stopwatch.Frequency);sample.count+=count;
            }
        }

        // Explicit private benchmark opt-in. Capture every trial, even those
        // later refused by the managed size/precision audit. Production runs
        // neither read native output here nor write any capture files.
        internal NativePackingOutput CaptureNativePacking(float[] input,uint[] indices,uint[] materials,uint padding,
            float density,uint resolution,bool bruteForce,bool rotate,bool axis)
        {
            if(string.IsNullOrEmpty(nativePackingCaptureDirectory)) return null;
            System.IO.Directory.CreateDirectory(nativePackingCaptureDirectory);
            string file=System.IO.Path.Combine(nativePackingCaptureDirectory,$"request_{nativeCaptures++:D4}.xatlaspack");
            int count=XatlasNative.xatlasGetOutputVertexCount(0);
            var refs=new uint[count];var uv=new float[count*2];var charts=new uint[count];
            if(XatlasNative.xatlasGetOutputVertexData(0,refs,uv,charts,count)!=count)
                throw new InvalidOperationException("Native packing capture is incomplete.");
            using(var stream=new System.IO.FileStream(file,System.IO.FileMode.CreateNew))
            using(var writer=new System.IO.BinaryWriter(stream))
            {
                writer.Write(0x58525031u);writer.Write(padding);writer.Write(density);writer.Write(resolution);
                writer.Write(bruteForce);writer.Write(rotate);writer.Write(axis);
                void Floats(float[] values) {writer.Write(values.Length);foreach(float value in values) writer.Write(value);}
                void Integers(uint[] values) {writer.Write(values.Length);foreach(uint value in values) writer.Write(value);}
                Floats(input);Integers(indices);Integers(materials);
                writer.Write(XatlasNative.xatlasGetAtlasWidth());writer.Write(XatlasNative.xatlasGetAtlasHeight());
                writer.Write(XatlasNative.xatlasGetChartCount());Integers(refs);Floats(uv);Integers(charts);
            }
            return string.IsNullOrEmpty(nativePackingReplayExecutable)?null:ReplayNativePacking(file,count);
        }

        NativePackingOutput ReplayNativePacking(string capture,int count)
        {
            string reply=System.IO.Path.ChangeExtension(capture,".xatlasout");
            if(capture.IndexOf('"')>=0 || reply.IndexOf('"')>=0)
                throw new ArgumentException("Native replay paths cannot contain quotes.");
            using(var process=Process.Start(new ProcessStartInfo {FileName=nativePackingReplayExecutable,
                Arguments=$"--request \"{capture}\" \"{reply}\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true}))
            {
                if(process==null) throw new InvalidOperationException("Native packing replay did not start.");
                if(!process.WaitForExit(180000)) {process.Kill();throw new InvalidOperationException("Native packing replay timed out.");}
                string error=process.StandardError.ReadToEnd();
                if(process.ExitCode!=0) throw new InvalidOperationException("Native packing replay failed: "+error);
            }
            using var reader=new System.IO.BinaryReader(System.IO.File.OpenRead(reply));
            if(reader.ReadUInt32()!=0x58524f31u) throw new InvalidOperationException("Unsupported native packing replay output.");
            var output=new NativePackingOutput {width=reader.ReadUInt32(),height=reader.ReadUInt32(),chartCount=reader.ReadUInt32()};
            double milliseconds=reader.ReadDouble();
            void Length(int expected)
            {if(reader.ReadInt32()!=expected) throw new InvalidOperationException("Native packing replay output count changed.");}
            Length(count);output.references=new uint[count];for(int i=0;i<count;++i) output.references[i]=reader.ReadUInt32();
            Length(count*2);output.uv=new float[count*2];for(int i=0;i<output.uv.Length;++i) output.uv[i]=reader.ReadSingle();
            Length(count);output.charts=new uint[count];for(int i=0;i<count;++i) output.charts[i]=reader.ReadUInt32();
            if(reader.BaseStream.Position!=reader.BaseStream.Length) throw new InvalidOperationException("Native packing replay has trailing data.");
            Record("Native replay placement",(long)(milliseconds*Stopwatch.Frequency/1000));
            return output;
        }

        sealed class Scope : IDisposable
        {
            TransferPerformanceProfile owner;
            readonly string stage;
            readonly int lod;
            readonly bool accumulate;
            readonly long started = Stopwatch.GetTimestamp();
            internal Scope(TransferPerformanceProfile owner, string stage, int lod, bool accumulate)
            { this.owner = owner; this.stage = stage; this.lod = lod; this.accumulate = accumulate; }
            public void Dispose()
            {
                if (owner == null) return;
                long ticks = Stopwatch.GetTimestamp() - started;
                if (accumulate) owner.Accumulate(stage, ticks, 1, lod);
                else owner.Record(stage, ticks, lod: lod);
                owner = null;
            }
        }
    }
}
