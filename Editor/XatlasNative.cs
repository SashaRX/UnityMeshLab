// Xatlas C ABI. Public call signatures are retained; tests can bind one private
// native library for the complete pipeline without replacing package Plugins.
using System;
using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab
{
    public static class XatlasNative
    {
        const string DLL = "xatlas-unity";
        internal sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasCreateDelegate();
            internal xatlasCreateDelegate create;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasDestroyDelegate();
            internal xatlasDestroyDelegate destroy;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasAddUvMeshDelegate(float[] uvData, uint vertexCount, uint[] indexData, uint indexCount, uint[] faceMaterialData, uint faceCount);
            internal xatlasAddUvMeshDelegate addUvMesh;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasAddMeshDelegate(float[] positions, float[] normals, uint vertexCount, uint[] indexData, uint indexCount);
            internal xatlasAddMeshDelegate addMesh;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasComputeChartsDelegate();
            internal xatlasComputeChartsDelegate computeCharts;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasPackChartsDelegate(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis);
            internal xatlasPackChartsDelegate packCharts;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetMeshCountDelegate();
            internal xatlasGetMeshCountDelegate getMeshCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetAtlasWidthDelegate();
            internal xatlasGetAtlasWidthDelegate getAtlasWidth;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetAtlasHeightDelegate();
            internal xatlasGetAtlasHeightDelegate getAtlasHeight;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetChartCountDelegate();
            internal xatlasGetChartCountDelegate getChartCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputVertexCountDelegate(int meshIndex);
            internal xatlasGetOutputVertexCountDelegate getOutputVertexCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputIndexCountDelegate(int meshIndex);
            internal xatlasGetOutputIndexCountDelegate getOutputIndexCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputVertexDataDelegate(int meshIndex, uint[] outXref, float[] outUV, uint[] outChartIndex, int maxVerts);
            internal xatlasGetOutputVertexDataDelegate getOutputVertexData;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasPackChartsPreserveShapeDelegate(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis);
            internal xatlasPackChartsPreserveShapeDelegate packChartsPreserveShape;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputIndicesDelegate(int meshIndex, uint[] outIndices, int maxIndices);
            internal xatlasGetOutputIndicesDelegate getOutputIndices;
        }
        static Api testApi;
        internal static IDisposable UseForTests(Api api) => new TestScope(api);
        sealed class TestScope : IDisposable
        {
            static Api ReplaceBinding(Api value)
            {
                var previous = testApi;
                testApi = value;
                return previous;
            }
            readonly Api previous;
            readonly Api current;
            bool disposed;
            internal TestScope(Api api)
            {
                current = api ?? throw new ArgumentNullException(nameof(api));
                if (!XatlasRepack.TryAcquireNativeSession())
                    throw new InvalidOperationException("Cannot switch an active native atlas.");
                try
                {
                    previous = ReplaceBinding(api);
                }
                finally { XatlasRepack.ReleaseNativeSession(); }
            }
            public void Dispose()
            {
                if (!disposed)
                    Restore();
            }
            void Restore()
            {
                if (!XatlasRepack.TryAcquireNativeSession())
                    throw new InvalidOperationException("Cannot release an active native atlas.");
                try
                {
                    if (!ReferenceEquals(testApi, current))
                        throw new InvalidOperationException("Native test scopes must close in their creation order reversed.");
                    ReplaceBinding(previous);
                    disposed = true;
                }
                finally { XatlasRepack.ReleaseNativeSession(); }
            }
        }
        internal static void CheckPreserveShapeAvailable()
        {
            if (testApi != null)
            {
                if (testApi.packChartsPreserveShape == null)
                    throw new EntryPointNotFoundException();
                return;
            }
            Action<int, uint, float, uint, int, int, int, int, int> pack = Native.PackChartsPreserveShape;
            Marshal.Prelink(pack.Method);
        }
        public static void xatlasCreate()
        {
            if (testApi != null)
                testApi.create();
            else
                Native.Create();
        }
        public static void xatlasDestroy()
        {
            if (testApi != null)
                testApi.destroy();
            else
                Native.Destroy();
        }
        public static int xatlasAddUvMesh(float[] uvData, uint vertexCount, uint[] indexData, uint indexCount, uint[] faceMaterialData, uint faceCount) => testApi != null ? testApi.addUvMesh(uvData, vertexCount, indexData, indexCount, faceMaterialData, faceCount) : Native.AddUvMesh(uvData, vertexCount, indexData, indexCount, faceMaterialData, faceCount);
        public static int xatlasAddMesh(float[] positions, float[] normals, uint vertexCount, uint[] indexData, uint indexCount) => testApi != null ? testApi.addMesh(positions, normals, vertexCount, indexData, indexCount) : Native.AddMesh(positions, normals, vertexCount, indexData, indexCount);
        public static void xatlasComputeCharts()
        {
            if (testApi != null)
                testApi.computeCharts();
            else
                Native.ComputeCharts();
        }
        public static void xatlasPackCharts(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis)
        {
            if (testApi != null)
                testApi.packCharts(maxChartSize, padding, texelsPerUnit, resolution, bilinear, blockAlign, bruteForce, rotateCharts, rotateChartsToAxis);
            else
                Native.PackCharts(maxChartSize, padding, texelsPerUnit, resolution, bilinear, blockAlign, bruteForce, rotateCharts, rotateChartsToAxis);
        }
        public static int xatlasGetMeshCount() => testApi != null ? testApi.getMeshCount() : Native.GetMeshCount();
        public static uint xatlasGetAtlasWidth() => testApi != null ? testApi.getAtlasWidth() : Native.GetAtlasWidth();
        public static uint xatlasGetAtlasHeight() => testApi != null ? testApi.getAtlasHeight() : Native.GetAtlasHeight();
        public static uint xatlasGetChartCount() => testApi != null ? testApi.getChartCount() : Native.GetChartCount();
        public static int xatlasGetOutputVertexCount(int meshIndex) => testApi != null ? testApi.getOutputVertexCount(meshIndex) : Native.GetOutputVertexCount(meshIndex);
        public static int xatlasGetOutputIndexCount(int meshIndex) => testApi != null ? testApi.getOutputIndexCount(meshIndex) : Native.GetOutputIndexCount(meshIndex);
        public static int xatlasGetOutputVertexData(int meshIndex, uint[] outXref, float[] outUV, uint[] outChartIndex, int maxVerts) => testApi != null ? testApi.getOutputVertexData(meshIndex, outXref, outUV, outChartIndex, maxVerts) : Native.GetOutputVertexData(meshIndex, outXref, outUV, outChartIndex, maxVerts);
        internal static void xatlasPackChartsPreserveShape(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis)
        {
            if (testApi != null)
                testApi.packChartsPreserveShape(maxChartSize, padding, texelsPerUnit, resolution, bilinear, blockAlign, bruteForce, rotateCharts, rotateChartsToAxis);
            else
                Native.PackChartsPreserveShape(maxChartSize, padding, texelsPerUnit, resolution, bilinear, blockAlign, bruteForce, rotateCharts, rotateChartsToAxis);
        }
        public static int xatlasGetOutputIndices(int meshIndex, uint[] outIndices, int maxIndices) => testApi != null ? testApi.getOutputIndices(meshIndex, outIndices, maxIndices) : Native.GetOutputIndices(meshIndex, outIndices, maxIndices);
        static class Native
        {
            [DllImport(DLL, EntryPoint = "xatlasCreate", CallingConvention = CallingConvention.Cdecl)] internal static extern void Create();
            [DllImport(DLL, EntryPoint = "xatlasDestroy", CallingConvention = CallingConvention.Cdecl)] internal static extern void Destroy();
            [DllImport(DLL, EntryPoint = "xatlasAddUvMesh", CallingConvention = CallingConvention.Cdecl)] internal static extern int AddUvMesh(float[] uvData, uint vertexCount, uint[] indexData, uint indexCount, uint[] faceMaterialData, uint faceCount);
            [DllImport(DLL, EntryPoint = "xatlasAddMesh", CallingConvention = CallingConvention.Cdecl)] internal static extern int AddMesh(float[] positions, float[] normals, uint vertexCount, uint[] indexData, uint indexCount);
            [DllImport(DLL, EntryPoint = "xatlasComputeCharts", CallingConvention = CallingConvention.Cdecl)] internal static extern void ComputeCharts();
            [DllImport(DLL, EntryPoint = "xatlasPackCharts", CallingConvention = CallingConvention.Cdecl)] internal static extern void PackCharts(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis);
            [DllImport(DLL, EntryPoint = "xatlasGetMeshCount", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetMeshCount();
            [DllImport(DLL, EntryPoint = "xatlasGetAtlasWidth", CallingConvention = CallingConvention.Cdecl)] internal static extern uint GetAtlasWidth();
            [DllImport(DLL, EntryPoint = "xatlasGetAtlasHeight", CallingConvention = CallingConvention.Cdecl)] internal static extern uint GetAtlasHeight();
            [DllImport(DLL, EntryPoint = "xatlasGetChartCount", CallingConvention = CallingConvention.Cdecl)] internal static extern uint GetChartCount();
            [DllImport(DLL, EntryPoint = "xatlasGetOutputVertexCount", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetOutputVertexCount(int meshIndex);
            [DllImport(DLL, EntryPoint = "xatlasGetOutputIndexCount", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetOutputIndexCount(int meshIndex);
            [DllImport(DLL, EntryPoint = "xatlasGetOutputVertexData", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetOutputVertexData(int meshIndex, uint[] outXref, float[] outUV, uint[] outChartIndex, int maxVerts);
            [DllImport(DLL, EntryPoint = "xatlasPackChartsPreserveShape", CallingConvention = CallingConvention.Cdecl)] internal static extern void PackChartsPreserveShape(int maxChartSize, uint padding, float texelsPerUnit, uint resolution, int bilinear, int blockAlign, int bruteForce, int rotateCharts, int rotateChartsToAxis);
            [DllImport(DLL, EntryPoint = "xatlasGetOutputIndices", CallingConvention = CallingConvention.Cdecl)] internal static extern int GetOutputIndices(int meshIndex, uint[] outIndices, int maxIndices);
        }
    }
}
