using System;
using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab.Tests
{
    internal sealed class NativeQueryTestLibrary : IDisposable
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr library,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr library);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Version();
        IntPtr library;
        internal readonly ReverseUvQueryBackend.Api api;
        NativeQueryTestLibrary(string path)
        {
            library=LoadLibraryExW(path,IntPtr.Zero,8);
            if(library==IntPtr.Zero) throw new InvalidOperationException("Private query library load failed: "+Marshal.GetLastWin32Error());
            try {
                if(Function<Version>("meshLabEmbreeVersion")()!=1) throw new InvalidOperationException("Query ABI mismatch.");
                api=new ReverseUvQueryBackend.Api {create=Function<ReverseUvQueryBackend.Create>("meshLabEmbreeCreate"),
                    destroy=Function<ReverseUvQueryBackend.Destroy>("meshLabEmbreeDestroy"),
                    proposals=Function<ReverseUvQueryBackend.Proposals>("meshLabEmbreeQueries"),
                    candidates=Function<ReverseUvQueryBackend.Candidates>("meshLabEmbreeCandidates")};
            } catch {Dispose();throw;}
        }
        T Function<T>(string name) where T:Delegate
        {var address=GetProcAddress(library,name);if(address==IntPtr.Zero) throw new InvalidOperationException("Query export missing: "+name);return Marshal.GetDelegateForFunctionPointer<T>(address);}
        internal static NativeQueryTestLibrary OpenFromEnvironment()
        {
            string path=Environment.GetEnvironmentVariable("MESHLAB_NATIVE_QUERY_LIBRARY");
            if(string.IsNullOrEmpty(path)) return null;
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT) throw new PlatformNotSupportedException("Private query verification requires Windows.");
            if(!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path)) throw new ArgumentException("Private query library must be an existing absolute path.");
            return new NativeQueryTestLibrary(path);
        }
        public void Dispose()
        {if(library==IntPtr.Zero) return;var previous=library;library=IntPtr.Zero;if(!FreeLibrary(previous)) throw new InvalidOperationException("Query library unload failed.");}
    }
}
