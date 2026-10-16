using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>One resident atlas per final pack. The global native lease stays
    /// owned until disposal, so no other caller can replace its prepared charts.</summary>
    internal sealed class ReverseUvNativeChartSession : IDisposable
    {
        readonly TransferPerformanceProfile performance;
        float[] coordinates;
        uint[] indices, materials;
        PreparedInput resident;
        bool acquired, created, disposed;
        long hits, misses;

        internal ReverseUvNativeChartSession(TransferPerformanceProfile performance = null)
            => this.performance = performance;

        internal bool TryAcquire(CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReverseUvNativeChartSession));
            token.ThrowIfCancellationRequested();
            if (!acquired) acquired = XatlasRepack.TryAcquireNativeSession();
            return acquired;
        }

        internal sealed class PreparedInput
        {
            internal Vector2[][] hulls;
            internal Vector2[] points;
            internal int[] starts;
            internal float[] flat;
            internal uint[] indices, materials;
            internal int error;
        }

        internal PreparedInput Input(Vector2[][] hulls,CancellationToken token)
        {
            if(disposed) throw new ObjectDisposedException(nameof(ReverseUvNativeChartSession));
            if(!acquired) throw new InvalidOperationException("Native chart preparation requires its atlas lease.");
            token.ThrowIfCancellationRequested();
            // FootprintCache owns immutable masks for this operation. Its
            // identity key avoids rebuilding every flat/index/material buffer.
            if(resident!=null && resident.hulls.Length==hulls.Length
                && Enumerable.Range(0,hulls.Length).All(i=>ReferenceEquals(resident.hulls[i],hulls[i])))
            {++hits;return resident;}
            var input=BuildInput(hulls,token);
            input.error=Prepare(input.flat,input.indices,input.materials,token,false);
            if(input.error==0) resident=input;
            return input;
        }

        internal static PreparedInput BuildInput(Vector2[][] hulls,CancellationToken token)
        {
            var points=new List<Vector2>();var triangles=new List<uint>();var faceMaterials=new List<uint>();
            var starts=new int[hulls.Length];
            for(int g=0;g<hulls.Length;++g)
            {
                token.ThrowIfCancellationRequested();starts[g]=points.Count;points.AddRange(hulls[g]);
                for(int k=0;k<hulls[g].Length;k+=3)
                {triangles.Add((uint)(starts[g]+k));triangles.Add((uint)(starts[g]+k+1));triangles.Add((uint)(starts[g]+k+2));faceMaterials.Add((uint)g);}
            }
            return new PreparedInput {hulls=(Vector2[][])hulls.Clone(),points=points.ToArray(),starts=starts,
                flat=points.SelectMany(p=>new[] {p.x,p.y}).ToArray(),indices=triangles.ToArray(),materials=faceMaterials.ToArray()};
        }

        internal int Prepare(float[] uv, uint[] triangles, uint[] faceMaterials, CancellationToken token,bool copyKey=true)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ReverseUvNativeChartSession));
            if (!acquired) throw new InvalidOperationException("Native chart preparation requires its atlas lease.");
            token.ThrowIfCancellationRequested();
            resident=null;
            if (Equal(coordinates, uv) && Equal(indices, triangles) && Equal(materials, faceMaterials))
            { ++hits; return 0; }
            ClearAtlas();
            ++misses;
            XatlasNative.xatlasCreate();
            created = true;
            int result = XatlasNative.xatlasAddUvMesh(uv, (uint)(uv.Length / 2), triangles,
                (uint)triangles.Length, faceMaterials, (uint)faceMaterials.Length);
            if (result != 0) return result;
            XatlasNative.xatlasComputeCharts();
            token.ThrowIfCancellationRequested();
            // Own the key: callers can subsequently reuse or change their arrays.
            coordinates = copyKey?(float[])uv.Clone():uv;
            indices = copyKey?(uint[])triangles.Clone():triangles;
            materials = copyKey?(uint[])faceMaterials.Clone():faceMaterials;
            return 0;
        }

        static bool Equal(float[] a, float[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        static bool Equal(uint[] a, uint[] b)
        {
            if (a == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false;
            return true;
        }

        void ClearAtlas()
        {
            coordinates = null; indices = null; materials = null; resident=null;
            if (!created) return;
            created = false;
            XatlasNative.xatlasDestroy();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { ClearAtlas(); }
            finally
            {
                if (acquired) { acquired = false; XatlasRepack.ReleaseNativeSession(); }
                performance?.Record("Native chart cache hits", 0, hits);
                performance?.Record("Native chart cache misses", 0, misses);
            }
        }
    }
}
