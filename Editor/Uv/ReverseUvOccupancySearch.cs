using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SashaRX.UnityMeshLab
{
    // An immutable occupancy snapshot can serve every rotation of one family.
    // Each Find owns its mask/cursors. Older plugins keep the legacy path.
    internal sealed class ReverseUvOccupancySearch : IDisposable
    {
        const string Library="xatlas-unity";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr Create(int side,int width,int height,int[] spans,int spanInts,
            int[] offsets,int offsetInts,int[] occupied,int occupiedInts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Advance(IntPtr handle,ref int x,ref int y,int best,ref long work,int chunk);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Destroy(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr CreateSnapshot(int side,int[] offsets,int offsetInts,int[] occupied,int occupiedInts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr SearchFromSnapshot(IntPtr snapshot,int width,int height,int[] spans,int spanInts);
        internal sealed class Api
        {
            internal Create create;
            internal Advance advance;
            internal Destroy destroy;
            internal CreateSnapshot createSnapshot;
            internal SearchFromSnapshot searchFromSnapshot;
            internal Destroy destroySnapshot;
        }
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern int meshLabFootprintSearchVersion();
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr meshLabFootprintSearchCreate(int side,int width,int height,int[] spans,int spanInts,
            int[] offsets,int offsetInts,int[] occupied,int occupiedInts);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]
        static extern int meshLabFootprintSearchAdvance(IntPtr handle,ref int x,ref int y,int best,ref long work,int chunk);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void meshLabFootprintSearchDestroy(IntPtr handle);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr meshLabFootprintSnapshotCreate(int side,int[] offsets,int offsetInts,int[] occupied,int occupiedInts);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr meshLabFootprintSearchFromSnapshot(IntPtr snapshot,int width,int height,int[] spans,int spanInts);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] static extern void meshLabFootprintSnapshotDestroy(IntPtr handle);
        static readonly Api plugin=new Api {create=meshLabFootprintSearchCreate,advance=meshLabFootprintSearchAdvance,
            destroy=meshLabFootprintSearchDestroy,createSnapshot=meshLabFootprintSnapshotCreate,
            searchFromSnapshot=meshLabFootprintSearchFromSnapshot,destroySnapshot=meshLabFootprintSnapshotDestroy};
        static readonly Lazy<int> version=new Lazy<int>(()=>{
            try {int value=meshLabFootprintSearchVersion();return value==1 || value==2?value:0;}
            catch(Exception e) when(e is EntryPointNotFoundException || e is DllNotFoundException || e is BadImageFormatException)
            {return 0;}
        });
        readonly Api api;
        IntPtr handle;
        internal int x,y;
        ReverseUvOccupancySearch(Api api,IntPtr handle) {this.api=api;this.handle=handle;}

        internal sealed class Snapshot : IDisposable
        {
            readonly Api api;
            readonly List<(int start,int end)>[] rows;
            IntPtr handle;
            Snapshot(Api api,IntPtr handle,List<(int start,int end)>[] rows) {this.api=api;this.handle=handle;this.rows=rows;}
            internal static Snapshot TryCapture(List<(int start,int end)>[] rows,CancellationToken token,
                TransferPerformanceProfile profile,Api testApi=null)
            {
                token.ThrowIfCancellationRequested();var binding=testApi??plugin;
                if(rows.Length>8192 || (testApi==null && version.Value<2) || binding.createSnapshot==null
                    || binding.searchFromSnapshot==null || binding.destroySnapshot==null) return null;
                using var timing=profile?.Measure("Native occupancy snapshot construction");
                if(!FlattenRows(rows,token,out var offsets,out var occupied,out int count)) return null;
                var native=binding.createSnapshot(rows.Length,offsets,offsets.Length,occupied,count);
                return native==IntPtr.Zero?null:new Snapshot(binding,native,rows);
            }
            internal ReverseUvOccupancySearch CreateSearch(int width,int height,(int row,int start,int end)[] spans,
                List<(int start,int end)>[] sourceRows,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if(handle==IntPtr.Zero) throw new ObjectDisposedException(nameof(Snapshot));
                if(!ReferenceEquals(rows,sourceRows)) throw new ArgumentException("An occupancy snapshot belongs to its original row window.");
                if(width>rows.Length || height>rows.Length || spans.Length>20000000/3) return null;
                var mask=FlattenMask(spans,token);
                var native=api.searchFromSnapshot(handle,width,height,mask,spans.Length*3);
                return native==IntPtr.Zero?null:new ReverseUvOccupancySearch(api,native);
            }
            public void Dispose()
            {
                if(handle==IntPtr.Zero) return;
                var previous=handle;handle=IntPtr.Zero;api.destroySnapshot(previous);
            }
        }

        internal static ReverseUvOccupancySearch TryCreate(int width,int height,(int row,int start,int end)[] spans,
            List<(int start,int end)>[] rows,CancellationToken token,Api testApi=null)
        {
            token.ThrowIfCancellationRequested();
            if(rows.Length>8192 || width>rows.Length || height>rows.Length || spans.Length>20000000/3
                || (testApi==null && version.Value==0)) return null;
            if(!FlattenRows(rows,token,out var offsets,out var occupied,out int count)) return null;
            var mask=FlattenMask(spans,token);
            token.ThrowIfCancellationRequested();var binding=testApi??plugin;
            var native=binding.create(rows.Length,width,height,mask,spans.Length*3,offsets,offsets.Length,occupied,count);
            return native==IntPtr.Zero?null:new ReverseUvOccupancySearch(binding,native);
        }

        static bool FlattenRows(List<(int start,int end)>[] rows,CancellationToken token,
            out int[] offsets,out int[] occupied,out int count)
        {
            offsets=new int[rows.Length+1];count=0;occupied=null;
            for(int row=0;row<rows.Length;++row)
            {offsets[row]=count;count=checked(count+rows[row].Count*2);}
            offsets[rows.Length]=count;
            if(count>20000000) return false;
            occupied=new int[Math.Max(1,count)];
            for(int row=0;row<rows.Length;++row)
            {
                token.ThrowIfCancellationRequested();int i=offsets[row];
                foreach(var span in rows[row]) {occupied[i++]=span.start;occupied[i++]=span.end;}
            }
            return true;
        }

        static int[] FlattenMask((int row,int start,int end)[] spans,CancellationToken token)
        {
            var mask=new int[Math.Max(1,spans.Length*3)];
            for(int s=0;s<spans.Length;++s)
            {
                if((s&1023)==0) token.ThrowIfCancellationRequested();
                mask[s*3]=spans[s].row;mask[s*3+1]=spans[s].start;mask[s*3+2]=spans[s].end;
            }
            return mask;
        }

        internal bool Next(int best,ref long work,CancellationToken token,out bool exhausted)
        {
            if(handle==IntPtr.Zero) throw new ObjectDisposedException(nameof(ReverseUvOccupancySearch));
            for(;;)
            {
                token.ThrowIfCancellationRequested();
                int status=api.advance(handle,ref x,ref y,best,ref work,65536);
                token.ThrowIfCancellationRequested();
                exhausted=status==0;
                if(status==0 || status==-1) return false;
                if(status==1) return true;
                if(status!=2) throw new InvalidOperationException("Native footprint search rejected its validated snapshot.");
            }
        }

        public void Dispose()
        {
            if(handle==IntPtr.Zero) return;
            var previous=handle;handle=IntPtr.Zero;api.destroy(previous);
        }
    }
}
