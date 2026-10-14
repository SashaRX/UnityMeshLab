using System;
using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab.Tests
{
    // Load an explicitly requested PRIVATE build for regression tests. Package
    // Plugins are never replaced. All search handles die before this library.
    internal sealed class NativeFootprintTestLibrary : IDisposable
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr library,string name);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool FreeLibrary(IntPtr library);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Version();
        IntPtr library;
        internal readonly ReverseUvOccupancySearch.Api api;
        internal readonly XatlasNative.Api packing;
        NativeFootprintTestLibrary(string path)
        {
            // Resolve this private build's optional dependencies beside its DLL.
            // The caller requires an absolute path; process search paths stay intact.
            library=LoadLibraryExW(path,IntPtr.Zero,0x8); // LOAD_WITH_ALTERED_SEARCH_PATH
            if(library==IntPtr.Zero) throw new InvalidOperationException("Private footprint test library could not be loaded: "+Marshal.GetLastWin32Error());
            try
            {
                if(Function<Version>("meshLabFootprintSearchVersion")()!=2) throw new InvalidOperationException("Private footprint ABI mismatch.");
                api=new ReverseUvOccupancySearch.Api {create=Function<ReverseUvOccupancySearch.Create>("meshLabFootprintSearchCreate"),
                    advance=Function<ReverseUvOccupancySearch.Advance>("meshLabFootprintSearchAdvance"),
                    destroy=Function<ReverseUvOccupancySearch.Destroy>("meshLabFootprintSearchDestroy"),
                    createSnapshot=Function<ReverseUvOccupancySearch.CreateSnapshot>("meshLabFootprintSnapshotCreate"),
                    searchFromSnapshot=Function<ReverseUvOccupancySearch.SearchFromSnapshot>("meshLabFootprintSearchFromSnapshot"),
                    destroySnapshot=Function<ReverseUvOccupancySearch.Destroy>("meshLabFootprintSnapshotDestroy")};
                packing=new XatlasNative.Api {
                    create=Function<XatlasNative.Api.xatlasCreateDelegate>("xatlasCreate"),
                    destroy=Function<XatlasNative.Api.xatlasDestroyDelegate>("xatlasDestroy"),
                    addUvMesh=Function<XatlasNative.Api.xatlasAddUvMeshDelegate>("xatlasAddUvMesh"),
                    addMesh=Function<XatlasNative.Api.xatlasAddMeshDelegate>("xatlasAddMesh"),
                    computeCharts=Function<XatlasNative.Api.xatlasComputeChartsDelegate>("xatlasComputeCharts"),
                    packCharts=Function<XatlasNative.Api.xatlasPackChartsDelegate>("xatlasPackCharts"),
                    getMeshCount=Function<XatlasNative.Api.xatlasGetMeshCountDelegate>("xatlasGetMeshCount"),
                    getAtlasWidth=Function<XatlasNative.Api.xatlasGetAtlasWidthDelegate>("xatlasGetAtlasWidth"),
                    getAtlasHeight=Function<XatlasNative.Api.xatlasGetAtlasHeightDelegate>("xatlasGetAtlasHeight"),
                    getChartCount=Function<XatlasNative.Api.xatlasGetChartCountDelegate>("xatlasGetChartCount"),
                    getOutputVertexCount=Function<XatlasNative.Api.xatlasGetOutputVertexCountDelegate>("xatlasGetOutputVertexCount"),
                    getOutputIndexCount=Function<XatlasNative.Api.xatlasGetOutputIndexCountDelegate>("xatlasGetOutputIndexCount"),
                    getOutputVertexData=Function<XatlasNative.Api.xatlasGetOutputVertexDataDelegate>("xatlasGetOutputVertexData"),
                    packChartsPreserveShape=Function<XatlasNative.Api.xatlasPackChartsPreserveShapeDelegate>("xatlasPackChartsPreserveShape"),
                    getOutputIndices=Function<XatlasNative.Api.xatlasGetOutputIndicesDelegate>("xatlasGetOutputIndices")
                };
            }
            catch {Dispose();throw;}
        }
        T Function<T>(string name) where T:Delegate
        {
            var address=GetProcAddress(library,name);
            if(address==IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        internal static NativeFootprintTestLibrary OpenFromEnvironment()
        {
            string path=Environment.GetEnvironmentVariable("MESHLAB_NATIVE_FOOTPRINT_LIBRARY");
            if(string.IsNullOrEmpty(path)) return null;
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT) throw new PlatformNotSupportedException("Private DLL verification requires Windows.");
            if(!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path)) throw new ArgumentException("Private footprint DLL must be an existing absolute path.");
            return new NativeFootprintTestLibrary(path);
        }
        public void Dispose()
        {
            if(library==IntPtr.Zero) return;
            var previous=library;library=IntPtr.Zero;
            if(!FreeLibrary(previous)) throw new InvalidOperationException("Private footprint DLL unload failed.");
        }
    }
}
