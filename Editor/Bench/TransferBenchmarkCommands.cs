using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Public only for Unity -executeMethod. No dependency on Unity MCP assemblies.</summary>
    public static class TransferBenchmarkCommands
    {
        const string Menu = "Tools/Mesh Lab/Diagnostics/";
        static bool running;

        [MenuItem(Menu + "Benchmark Transfer - Synthetic Corpus")]
        public static void Synthetic() => _ = Run(new TransferBenchmark.Config(), false);

        [MenuItem(Menu + "Benchmark Transfer - Last Capture")]
        public static void LastCapture() => _ = Run(new TransferBenchmark.Config { captures = new[] { TransferCaseCapture.LastManifest }, includeSynthetic = false }, false);

        [MenuItem(Menu + "Benchmark Transfer - Config File")]
        public static void ConfigFile()
        {
            string path = EditorUtility.OpenFilePanel("UV transfer benchmark config", SweepRunner.ReportsRoot(), "json");
            if (!string.IsNullOrEmpty(path)) _ = RunFile(path, false);
        }

        /// <summary>Do not pass -quit: this async operation exits the owned batch editor after saving.</summary>
        public static void RunBatch()
        {
            if (!Application.isBatchMode) {
                UvtLog.Error(UvtLog.Category.Benchmark, "[TransferBenchmark] RunBatch requires a separate batch-mode editor."); return;
            }
            string config = Argument("-meshLabTransferBench");
            if (string.IsNullOrEmpty(config)) _ = Run(new TransferBenchmark.Config(), true);
            else _ = RunFile(config, true);
        }

        static string Argument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; ++i)
                if (arguments[i] == name) return arguments[i + 1];
            return "";
        }

        static async Task RunFile(string path, bool exit)
        {
            try {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 1024 * 1024) throw new InvalidDataException("Missing or oversized benchmark config.");
                var config = JsonUtility.FromJson<TransferBenchmark.Config>(await File.ReadAllTextAsync(file.FullName));
                if (config == null) throw new InvalidDataException("Invalid benchmark config.");
                // Relative corpus/output paths are relative to the config file, not the editor working directory.
                if (config.captures != null)
                    for (int i = 0; i < config.captures.Length; ++i) config.captures[i] = Resolve(file.DirectoryName, config.captures[i]);
                if (!string.IsNullOrEmpty(config.outputRoot)) config.outputRoot = Resolve(file.DirectoryName, config.outputRoot);
                await Run(config, exit);
            }
            catch (Exception error) {
                UvtLog.Error(UvtLog.Category.Benchmark, "[TransferBenchmark] " + error);
                if (exit) EditorApplication.Exit(2);
            }
        }
        static string Resolve(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

        static async Task Run(TransferBenchmark.Config config, bool exit)
        {
            if (running) { UvtLog.Warn(UvtLog.Category.Benchmark, "[TransferBenchmark] A comparison is already running."); return; }
            running = true;
            try {
                var report = await TransferBenchmark.Run(config);
                if (exit) EditorApplication.Exit(report.complete ? 0 : 2);
            }
            catch (Exception error) {
                UvtLog.Error(UvtLog.Category.Benchmark, "[TransferBenchmark] " + error);
                if (exit) EditorApplication.Exit(2);
            }
            finally { running = false; }
        }
    }
}
