// Xatlas C ABI. Public call signatures are retained; tests can bind one private
// native library for the complete pipeline without replacing package Plugins.
using System;
using System.Runtime.InteropServices;

namespace SashaRX.UnityMeshLab
{
    public static class XatlasNative
    {
        const string DLL="xatlas-unity";
        internal sealed class Api
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasCreateDelegate();
            internal xatlasCreateDelegate xatlasCreate;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasDestroyDelegate();
            internal xatlasDestroyDelegate xatlasDestroy;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasAddUvMeshDelegate(float[] uvData,uint vertexCount,uint[] indexData,uint indexCount,uint[] faceMaterialData,uint faceCount);
            internal xatlasAddUvMeshDelegate xatlasAddUvMesh;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasAddMeshDelegate(float[] positions,float[] normals,uint vertexCount,uint[] indexData,uint indexCount);
            internal xatlasAddMeshDelegate xatlasAddMesh;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasComputeChartsDelegate();
            internal xatlasComputeChartsDelegate xatlasComputeCharts;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasPackChartsDelegate(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis);
            internal xatlasPackChartsDelegate xatlasPackCharts;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetMeshCountDelegate();
            internal xatlasGetMeshCountDelegate xatlasGetMeshCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetAtlasWidthDelegate();
            internal xatlasGetAtlasWidthDelegate xatlasGetAtlasWidth;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetAtlasHeightDelegate();
            internal xatlasGetAtlasHeightDelegate xatlasGetAtlasHeight;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint xatlasGetChartCountDelegate();
            internal xatlasGetChartCountDelegate xatlasGetChartCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputVertexCountDelegate(int meshIndex);
            internal xatlasGetOutputVertexCountDelegate xatlasGetOutputVertexCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputIndexCountDelegate(int meshIndex);
            internal xatlasGetOutputIndexCountDelegate xatlasGetOutputIndexCount;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputVertexDataDelegate(int meshIndex,uint[] outXref,float[] outUV,uint[] outChartIndex,int maxVerts);
            internal xatlasGetOutputVertexDataDelegate xatlasGetOutputVertexData;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void xatlasPackChartsPreserveShapeDelegate(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis);
            internal xatlasPackChartsPreserveShapeDelegate xatlasPackChartsPreserveShape;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int xatlasGetOutputIndicesDelegate(int meshIndex,uint[] outIndices,int maxIndices);
            internal xatlasGetOutputIndicesDelegate xatlasGetOutputIndices;
        }
        static Api testApi;
        internal static IDisposable UseForTests(Api api) => new TestScope(api);
        sealed class TestScope : IDisposable
        {
            readonly Api previous;
            readonly Api current;
            bool disposed;
            internal TestScope(Api api)
            {
                current=api??throw new ArgumentNullException(nameof(api));
                if(!XatlasRepack.TryAcquireNativeSession()) throw new InvalidOperationException("Cannot switch an active native atlas.");
                try {previous=testApi;testApi=api;}
                finally {XatlasRepack.ReleaseNativeSession();}
            }
            public void Dispose()
            {
                if(disposed) return;
                if(!XatlasRepack.TryAcquireNativeSession()) throw new InvalidOperationException("Cannot release an active native atlas.");
                try
                {
                    if(!ReferenceEquals(testApi,current)) throw new InvalidOperationException("Native test scopes must close in their creation order reversed.");
                    testApi=previous;disposed=true;
                }
                finally {XatlasRepack.ReleaseNativeSession();}
            }
        }
        internal static void CheckPreserveShapeAvailable()
        {
            if(testApi!=null) {if(testApi.xatlasPackChartsPreserveShape==null) throw new EntryPointNotFoundException();return;}
            Action<int,uint,float,uint,int,int,int,int,int> pack=Native.xatlasPackChartsPreserveShape;
            Marshal.Prelink(pack.Method);
        }
        public static void xatlasCreate() {if(testApi!=null) testApi.xatlasCreate();else Native.xatlasCreate();}
        public static void xatlasDestroy() {if(testApi!=null) testApi.xatlasDestroy();else Native.xatlasDestroy();}
        public static int xatlasAddUvMesh(float[] uvData,uint vertexCount,uint[] indexData,uint indexCount,uint[] faceMaterialData,uint faceCount) =>testApi!=null?testApi.xatlasAddUvMesh(uvData,vertexCount,indexData,indexCount,faceMaterialData,faceCount):Native.xatlasAddUvMesh(uvData,vertexCount,indexData,indexCount,faceMaterialData,faceCount);
        public static int xatlasAddMesh(float[] positions,float[] normals,uint vertexCount,uint[] indexData,uint indexCount) =>testApi!=null?testApi.xatlasAddMesh(positions,normals,vertexCount,indexData,indexCount):Native.xatlasAddMesh(positions,normals,vertexCount,indexData,indexCount);
        public static void xatlasComputeCharts() {if(testApi!=null) testApi.xatlasComputeCharts();else Native.xatlasComputeCharts();}
        public static void xatlasPackCharts(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis) {if(testApi!=null) testApi.xatlasPackCharts(maxChartSize,padding,texelsPerUnit,resolution,bilinear,blockAlign,bruteForce,rotateCharts,rotateChartsToAxis);else Native.xatlasPackCharts(maxChartSize,padding,texelsPerUnit,resolution,bilinear,blockAlign,bruteForce,rotateCharts,rotateChartsToAxis);}
        public static int xatlasGetMeshCount() =>testApi!=null?testApi.xatlasGetMeshCount():Native.xatlasGetMeshCount();
        public static uint xatlasGetAtlasWidth() =>testApi!=null?testApi.xatlasGetAtlasWidth():Native.xatlasGetAtlasWidth();
        public static uint xatlasGetAtlasHeight() =>testApi!=null?testApi.xatlasGetAtlasHeight():Native.xatlasGetAtlasHeight();
        public static uint xatlasGetChartCount() =>testApi!=null?testApi.xatlasGetChartCount():Native.xatlasGetChartCount();
        public static int xatlasGetOutputVertexCount(int meshIndex) =>testApi!=null?testApi.xatlasGetOutputVertexCount(meshIndex):Native.xatlasGetOutputVertexCount(meshIndex);
        public static int xatlasGetOutputIndexCount(int meshIndex) =>testApi!=null?testApi.xatlasGetOutputIndexCount(meshIndex):Native.xatlasGetOutputIndexCount(meshIndex);
        public static int xatlasGetOutputVertexData(int meshIndex,uint[] outXref,float[] outUV,uint[] outChartIndex,int maxVerts) =>testApi!=null?testApi.xatlasGetOutputVertexData(meshIndex,outXref,outUV,outChartIndex,maxVerts):Native.xatlasGetOutputVertexData(meshIndex,outXref,outUV,outChartIndex,maxVerts);
        internal static void xatlasPackChartsPreserveShape(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis) {if(testApi!=null) testApi.xatlasPackChartsPreserveShape(maxChartSize,padding,texelsPerUnit,resolution,bilinear,blockAlign,bruteForce,rotateCharts,rotateChartsToAxis);else Native.xatlasPackChartsPreserveShape(maxChartSize,padding,texelsPerUnit,resolution,bilinear,blockAlign,bruteForce,rotateCharts,rotateChartsToAxis);}
        public static int xatlasGetOutputIndices(int meshIndex,uint[] outIndices,int maxIndices) =>testApi!=null?testApi.xatlasGetOutputIndices(meshIndex,outIndices,maxIndices):Native.xatlasGetOutputIndices(meshIndex,outIndices,maxIndices);
        static class Native
        {
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern void xatlasCreate();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern void xatlasDestroy();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasAddUvMesh(float[] uvData,uint vertexCount,uint[] indexData,uint indexCount,uint[] faceMaterialData,uint faceCount);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasAddMesh(float[] positions,float[] normals,uint vertexCount,uint[] indexData,uint indexCount);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern void xatlasComputeCharts();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern void xatlasPackCharts(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasGetMeshCount();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern uint xatlasGetAtlasWidth();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern uint xatlasGetAtlasHeight();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern uint xatlasGetChartCount();
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasGetOutputVertexCount(int meshIndex);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasGetOutputIndexCount(int meshIndex);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasGetOutputVertexData(int meshIndex,uint[] outXref,float[] outUV,uint[] outChartIndex,int maxVerts);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern void xatlasPackChartsPreserveShape(int maxChartSize,uint padding,float texelsPerUnit,uint resolution,int bilinear,int blockAlign,int bruteForce,int rotateCharts,int rotateChartsToAxis);
            [DllImport(DLL,CallingConvention=CallingConvention.Cdecl)] internal static extern int xatlasGetOutputIndices(int meshIndex,uint[] outIndices,int maxIndices);
        }
    }
}
