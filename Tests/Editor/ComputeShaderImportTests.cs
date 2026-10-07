using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SashaRX.UnityMeshLab.Tests
{
    public sealed class ComputeShaderImportTests
    {
        [Test]
        public void VertexAoRejectsAMissingKernelBeforePreparingGpuGeometry()
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.sasharx.unitymeshlab/Shaders/BvhQueries.compute");
            Assert.IsNotNull(shader);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            try {
                var targets = new List<(Mesh mesh, Matrix4x4 transform)> { (mesh, Matrix4x4.identity) };
                var error = Assert.Throws<InvalidOperationException>(() =>
                    new VertexAOBaker.GpuAOBakeJob(shader, targets, null, new VertexAOSettings(), null, null));
                Assert.That(error.Message, Does.Contain("Vertex AO kernels"));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        [TestCase("BvhQueries", "Raycast", "Nearest")]
        [TestCase("SourceAORayTrace", "BakeAO", "FinalizeAO")]
        [TestCase("VertexAORayTrace", "BakeAO", "FinalizeAO")]
        public void PackageImportCompilesSharedBvhIncludeAndEveryKernel(string shaderName, string firstKernel, string secondKernel)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsComputeShaders)
                Assert.Ignore("Compute shader import tests need a graphics device with compute support; omit -nographics.");

            string path = "Packages/com.sasharx.unitymeshlab/Shaders/" + shaderName + ".compute";
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.IsNotNull(shader, "The canonical package path must resolve after a synchronous import: " + path);
            foreach (string kernelName in new[] { firstKernel, secondKernel }) {
                int kernel = shader.FindKernel(kernelName);
                Assert.IsTrue(shader.IsSupported(kernel), path + " must compile kernel " + kernelName + " for the active graphics API.");
            }

            // The licence-free compile gate uses Unity 2021 reference assemblies;
            // read the real Unity 6 compiler API at test runtime without a shim.
            var getMessages = typeof(ShaderUtil).GetMethod("GetComputeShaderMessages", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(ComputeShader) }, null);
            Assert.IsNotNull(getMessages, "Actual Unity compute compiler diagnostics must be available.");
            var messages = (Array)getMessages.Invoke(null, new object[] { shader });
            Assert.IsNotNull(messages, "The compiler must return diagnostics, including an empty array on success.");
            foreach (object diagnostic in messages) {
                string severity = DiagnosticValue(diagnostic, "severity").ToString();
                string message = DiagnosticValue(diagnostic, "message").ToString();
                Assert.AreNotEqual("Error", severity, path + ": " + message);
                string lower = message.ToLowerInvariant();
                Assert.That(lower, Does.Not.Contain("couldn't open include file"), path + ": " + message);
                Assert.That(lower, Does.Not.Contain("cannot open include file"), path + ": " + message);
                Assert.That(lower, Does.Not.Contain("uninitialized"), path + ": " + message);
            }
        }

        static object DiagnosticValue(object diagnostic, string memberName)
        {
            var type = diagnostic.GetType();
            object value = type.GetField(memberName)?.GetValue(diagnostic) ?? type.GetProperty(memberName)?.GetValue(diagnostic);
            Assert.IsNotNull(value, "Compiler diagnostic must expose " + memberName + ".");
            return value;
        }
    }
}
