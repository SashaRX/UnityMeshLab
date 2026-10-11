using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public sealed class TextureChannelPackerWindow : EditorWindow
    {
        private static readonly string[] Suffixes = { "_Albedo", "_Normal", "_MRCA" };
        private static readonly string[] Channels = { "R", "G", "B", "A" };
        private static readonly int[] Sizes = { 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
        private static readonly string[] SizeLabels = { "64", "128", "256", "512", "1024", "2048", "4096", "8192" };

        [Serializable]
        private sealed class Slot
        {
            public string path = "";
            public int channel;
            public int constant = 255;
        }

        [Serializable]
        private sealed class Output
        {
            public bool enabled;
            public int suffix;
            public bool split;
            public bool sourceAlpha = true;
            public Slot rgb = new Slot();
            public Slot[] slots = { new Slot(), new Slot { channel = 1 },
                new Slot { channel = 2 }, new Slot { channel = 3 } };
        }

        private sealed class LoadedSource
        {
            public TexturePackingImage image;
            public Texture2D thumbnail;
        }

        [SerializeField] private Output albedo = new Output { suffix = 0 };
        [SerializeField] private Output mrca = new Output { suffix = 2, split = true };
        [SerializeField] private Output normal = new Output { suffix = 1 };
        [SerializeField] private string textureName = "Texture";
        [SerializeField] private string outputFolder = "";
        [SerializeField] private bool prefix = true;
        [SerializeField] private bool followFbx = true;
        [SerializeField] private int widthIndex = 4;
        [SerializeField] private int heightIndex = 4;
        [SerializeField] private bool square = true;
        private readonly Dictionary<string, LoadedSource> loaded = new Dictionary<string, LoadedSource>();
        private readonly Dictionary<Output, Texture2D> previews = new Dictionary<Output, Texture2D>();
        private readonly HashSet<string> pendingSources = new HashSet<string>();
        private readonly Dictionary<string, TexturePackingLoad> activeLoads = new Dictionary<string, TexturePackingLoad>();
        private Vector2 scroll;
        private string error = "";
        private string status = "";
        private Task<List<SaveRequest>> saveTask;
        private Task importTask;
        private List<SaveRequest> committedRequests;
        private CancellationTokenSource saveCancellation;
        private bool selectionPending;

        private sealed class SaveRequest
        {
            public string path;
            public string staged;
            public string assetPath;
            public Func<CancellationToken, TexturePackingImage> build;
            public bool isNormal;
            public bool isAlbedo;
            public bool existed;
            public long length;
            public DateTime modified;
        }

        [MenuItem("Tools/Mesh Lab/Texture Channel Packer", priority = 80)]
        public static void Open()
        {
            var window = GetWindow<TextureChannelPackerWindow>("Texture Packer");
            window.minSize = new Vector2(620, 650);
        }

        private void OnEnable()
        {
            SubscribeSelection(OnSelectionChanged);
            SubscribeSave(TickLoads);
            if (string.IsNullOrEmpty(outputFolder)) outputFolder = Application.dataPath;
            ReloadSources();
            OnSelectionChanged();
        }

        private void OnDisable()
        {
            UnsubscribeSelection(OnSelectionChanged);
            UnsubscribeSave(TickSave);
            UnsubscribeSave(TickLoads);
            CancelLoads();
            selectionPending = false;
            CancelSaveOnClose();
            ClearPreviews();
            foreach (var source in loaded.Values) DestroyImmediate(source.thumbnail);
            loaded.Clear();
        }

        private void OnSelectionChanged()
        {
            if (!followFbx) return;
            if (saveTask != null) { selectionPending = true; return; }
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path) && Selection.activeGameObject != null)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(Selection.activeGameObject);
                path = AssetDatabase.GetAssetPath(source);
            }
            if (!string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase)) return;
            textureName = ModelBaseName(path);
            outputFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            Repaint();
        }

        private static void SubscribeSelection(Action callback) => Selection.selectionChanged += callback;
        private static void UnsubscribeSelection(Action callback) => Selection.selectionChanged -= callback;
        private static void SubscribeSave(EditorApplication.CallbackFunction callback) => EditorApplication.update += callback;
        private static void UnsubscribeSave(EditorApplication.CallbackFunction callback) => EditorApplication.update -= callback;

        public static string ModelBaseName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) ? name.Substring(3) : name;
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Texture Channel Packer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Drop PNG, JPG or TGA files from Explorer / Finder or the Project window. " +
                "Sources stay on disk and are never copied into the project. Full-image bilinear resize; no cropping.", MessageType.Info);
            using (new EditorGUI.DisabledScope(saveTask != null))
            {
                DrawSettings();
                DrawOutput(albedo, "Albedo", false);
                DrawOutput(mrca, "MRCA", false);
                DrawOutput(normal, "Normal", true);
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
            if (SourcesLoading())
            {
                EditorGUILayout.LabelField("Loading sources… " + pendingSources.Count + " remaining");
                if (GUILayout.Button("Cancel loading")) CancelLoads();
            }
            if (saveTask != null)
            {
                EditorGUILayout.LabelField(importTask == null ? "Resizing and encoding textures in the background…" : "Importing saved textures…");
                using (new EditorGUI.DisabledScope(importTask != null))
                    if (GUILayout.Button("Cancel export")) saveCancellation.Cancel();
            }
            using (new EditorGUI.DisabledScope(saveTask != null || SourcesLoading() || (!albedo.enabled && !mrca.enabled && !normal.enabled)))
                if (GUILayout.Button("Save enabled textures — TGA RLE", GUILayout.Height(30)))
                    SaveEnabled();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSettings()
        {
            EditorGUI.BeginChangeCheck();
            followFbx = EditorGUILayout.Toggle("Use selected FBX name / folder", followFbx);
            if (EditorGUI.EndChangeCheck() && followFbx) OnSelectionChanged();
            textureName = EditorGUILayout.TextField("Base name", textureName);
            prefix = EditorGUILayout.Toggle("Add T_ prefix", prefix);
            using (new EditorGUILayout.HorizontalScope())
            {
                outputFolder = EditorGUILayout.TextField("Output folder", outputFolder);
                if (GUILayout.Button("Browse", GUILayout.Width(65)))
                {
                    string folder = EditorUtility.OpenFolderPanel("Output folder", outputFolder, "");
                    if (!string.IsNullOrEmpty(folder)) outputFolder = folder;
                }
            }
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Resolution", GUILayout.Width(85));
                widthIndex = EditorGUILayout.Popup(widthIndex, SizeLabels);
                square = GUILayout.Toggle(square, square ? "Locked 1:1" : "Unlocked", "Button", GUILayout.Width(100));
                if (square) heightIndex = widthIndex;
                using (new EditorGUI.DisabledScope(square)) heightIndex = EditorGUILayout.Popup(heightIndex, SizeLabels);
            }
            if (EditorGUI.EndChangeCheck()) ClearPreviews();
            if (GUILayout.Button("Reload source files", GUILayout.Width(150))) ReloadSources();
        }

        private void DrawOutput(Output output, string title, bool isNormal)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    output.enabled = EditorGUILayout.ToggleLeft(title, output.enabled, EditorStyles.boldLabel, GUILayout.Width(90));
                    output.suffix = EditorGUILayout.Popup(output.suffix, Suffixes);
                    EditorGUILayout.LabelField(FileName(output), GUILayout.MinWidth(180));
                }
                EditorGUI.BeginChangeCheck();
                if (!isNormal) output.split = EditorGUILayout.Popup("RGB input", output.split ? 1 : 0,
                    new[] { "Whole RGB image", "Separate R / G / B channels" }) == 1;
                if (EditorGUI.EndChangeCheck()) ClearPreviews();
                if (isNormal || !output.split)
                {
                    DrawSlot(output, output.rgb, "RGB", false);
                    EditorGUI.BeginChangeCheck();
                    output.sourceAlpha = EditorGUILayout.Toggle("Use RGB image's alpha", output.sourceAlpha);
                    if (EditorGUI.EndChangeCheck()) ClearPreviews();
                }
                else
                {
                    DrawSlot(output, output.slots[0], "R", true);
                    DrawSlot(output, output.slots[1], "G", true);
                    DrawSlot(output, output.slots[2], "B", true);
                }
                if (!isNormal) DrawSlot(output, output.slots[3], "A (override)", true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(SourcesLoading()))
                        if (GUILayout.Button("Preview")) RunAction(() => BuildPreview(output));
                    using (new EditorGUI.DisabledScope(!HasInput(output) || SourcesLoading()))
                        if (GUILayout.Button("Save TGA RLE")) RunAction(() => SaveOutputs(new[] { output }));
                }
                if (previews.TryGetValue(output, out var preview))
                {
                    Rect rect = GUILayoutUtility.GetRect(160, 160, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawTextureTransparent(rect, preview, ScaleMode.ScaleToFit);
                }
            }
        }

        private void DrawSlot(Output output, Slot slot, string label, bool scalar)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(85));
                Rect rect = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48));
                GUI.Box(rect, GUIContent.none);
                if (loaded.TryGetValue(slot.path, out var source))
                    EditorGUI.DrawTextureTransparent(rect, source.thumbnail, ScaleMode.ScaleToFit);
                HandleDrop(rect, output, slot);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(string.IsNullOrEmpty(slot.path) ? "Drop a texture here" :
                        Path.GetFileName(slot.path), slot.path));
                    if (source != null) EditorGUILayout.LabelField(source.image.Width + " × " + source.image.Height);
                    else if (pendingSources.Contains(slot.path)) EditorGUILayout.LabelField("Loading…");
                    if (scalar)
                    {
                        EditorGUI.BeginChangeCheck();
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            slot.channel = EditorGUILayout.Popup(slot.channel, Channels, GUILayout.Width(55));
                            if (string.IsNullOrEmpty(slot.path)) slot.constant = EditorGUILayout.IntSlider("Default", slot.constant, 0, 255);
                        }
                        if (EditorGUI.EndChangeCheck()) ClearPreviews();
                    }
                }
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string path = EditorUtility.OpenFilePanel("Source texture (PNG / JPG / TGA)", "", "");
                    if (!string.IsNullOrEmpty(path)) Assign(output, slot, path);
                }
                if (GUILayout.Button("×", GUILayout.Width(28)))
                {
                    slot.path = "";
                    ClearPreviews();
                    PruneSources();
                }
            }
        }

        private void HandleDrop(Rect rect, Output output, Slot slot)
        {
            if (!GUI.enabled) return;
            Event evt = Event.current;
            if (!rect.Contains(evt.mousePosition) ||
                (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)) return;
            string path = DragAndDrop.paths.Length == 1 ? DragAndDrop.paths[0] : "";
            bool valid = TexturePackingImage.Supports(path) && File.Exists(path);
            DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (valid && evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                Assign(output, slot, path);
            }
            evt.Use();
        }

        private void Assign(Output output, Slot slot, string path)
        {
            RunAction(() =>
            {
                string fullPath = Path.GetFullPath(path);
                LoadSource(fullPath);
                slot.path = fullPath;
                output.enabled = true;
                ClearPreviews();
                PruneSources();
            });
        }

        private void LoadSource(string path)
        {
            if (!loaded.ContainsKey(path)) pendingSources.Add(path);
        }

        private bool SourcesLoading() => pendingSources.Count != 0;

        private void OnInspectorUpdate()
        {
            if (SourcesLoading() || saveTask != null) Repaint();
        }

        private void TickLoads()
        {
            if (pendingSources.Count == 0) return;
            var completed = new List<string>();
            var failures = new System.Text.StringBuilder(error);
            // Bound concurrent decoders; assigning the same image to several slots schedules it once.
            foreach (string path in pendingSources)
            {
                if (activeLoads.Count >= 2) break;
                if (activeLoads.ContainsKey(path)) continue;
                try { activeLoads.Add(path, new TexturePackingLoad(path)); }
                catch (Exception exception)
                {
                    failures.Append(path).Append(": ").AppendLine(exception.Message);
                    completed.Add(path);
                }
            }
            foreach (var entry in activeLoads)
            {
                try
                {
                    if (!entry.Value.TryComplete(out var image)) continue;
                    AddSource(entry.Key, image);
                    completed.Add(entry.Key);
                }
                catch (Exception exception)
                {
                    failures.Append(entry.Key).Append(": ").AppendLine(exception.Message);
                    completed.Add(entry.Key);
                }
            }
            foreach (string path in completed)
            {
                if (activeLoads.TryGetValue(path, out var load)) load.Dispose();
                activeLoads.Remove(path);
                pendingSources.Remove(path);
            }
            error = failures.ToString();
            if (completed.Count > 0) Repaint();
        }

        private void CancelLoads()
        {
            foreach (var load in activeLoads.Values) load.Dispose();
            activeLoads.Clear();
            pendingSources.Clear();
        }

        private void AddSource(string path, TexturePackingImage image)
        {
            // Retain only a small GPU thumbnail; packing uses the original CPU pixels.
            int w = Math.Min(image.Width, 128), h = Math.Min(image.Height, 128);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) pixels[y * w + x] = image.Sample(x, y, w, h);
            var thumbnail = MakeTexture(new TexturePackingImage(w, h, pixels));
            loaded.Add(path, new LoadedSource { image = image, thumbnail = thumbnail });
        }

        private void ReloadSources()
        {
            error = "";
            status = "";
            CancelLoads();
            ClearPreviews();
            foreach (var source in loaded.Values) DestroyImmediate(source.thumbnail);
            loaded.Clear();
            foreach (var output in new[] { albedo, mrca, normal })
            {
                if (!string.IsNullOrEmpty(output.rgb.path)) LoadSource(output.rgb.path);
                foreach (var slot in output.slots)
                    if (!string.IsNullOrEmpty(slot.path)) LoadSource(slot.path);
            }
        }

        private void PruneSources()
        {
            var used = new HashSet<string>();
            foreach (var output in new[] { albedo, mrca, normal })
            {
                used.Add(output.rgb.path);
                foreach (var slot in output.slots) used.Add(slot.path);
            }
            var remove = new List<string>();
            foreach (var entry in loaded)
                if (!used.Contains(entry.Key)) { DestroyImmediate(entry.Value.thumbnail); remove.Add(entry.Key); }
            foreach (string path in remove) loaded.Remove(path);
            pendingSources.RemoveWhere(path => !used.Contains(path));
            remove.Clear();
            foreach (var entry in activeLoads)
                if (!used.Contains(entry.Key)) { entry.Value.Dispose(); remove.Add(entry.Key); }
            foreach (string path in remove) activeLoads.Remove(path);
        }

        private static bool HasInput(Output output)
        {
            if (!output.split && !string.IsNullOrEmpty(output.rgb.path)) return true;
            for (int i = output.split ? 0 : 3; i < 4; i++)
                if (!string.IsNullOrEmpty(output.slots[i].path)) return true;
            return false;
        }

        private TexturePackingImage Source(Slot slot)
        {
            if (string.IsNullOrEmpty(slot.path)) return null;
            if (!loaded.TryGetValue(slot.path, out var value))
                throw new InvalidOperationException("Source unavailable; reload or replace it: " + slot.path);
            return value.image;
        }

        private TexturePackingImage Build(Output output, int width, int height)
        {
            return PrepareBuild(output, width, height)(CancellationToken.None);
        }

        private Func<CancellationToken, TexturePackingImage> PrepareBuild(Output output, int width, int height)
        {
            var sources = new TexturePackingImage[4];
            var channels = new int[4];
            var defaults = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                defaults[i] = (byte)output.slots[i].constant;
                channels[i] = output.slots[i].channel;
                if (output != normal && (output.split || i == 3)) sources[i] = Source(output.slots[i]);
            }
            TexturePackingImage rgb = output.split ? null : Source(output.rgb);
            bool sourceAlpha = !output.split && output.sourceAlpha;
            return cancellation => TexturePackingImage.Compose(width, height, rgb,
                sources, channels, defaults, sourceAlpha, cancellation);
        }

        private void BuildPreview(Output output)
        {
            int w = Sizes[widthIndex], h = Sizes[heightIndex];
            float scale = Math.Min(1f, 256f / Math.Max(w, h));
            var image = Build(output, Mathf.RoundToInt(w * scale), Mathf.RoundToInt(h * scale));
            var next = MakeTexture(image, output != albedo);
            if (previews.TryGetValue(output, out var previous)) DestroyImmediate(previous);
            previews[output] = next;
        }

        private static Texture2D MakeTexture(TexturePackingImage image, bool linear = true)
        {
            var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false, linear)
            { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                texture.SetPixels32(image.Pixels);
                texture.Apply(false, true);
                return texture;
            }
            catch { DestroyImmediate(texture); throw; }
        }

        private void ClearPreviews()
        {
            foreach (var texture in previews.Values) DestroyImmediate(texture);
            previews.Clear();
        }

        private string FileName(Output output)
        {
            string name = textureName.Trim();
            if (prefix && !name.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) name = "T_" + name;
            return name + Suffixes[output.suffix] + ".tga";
        }

        private void SaveEnabled()
        {
            var outputs = new List<Output>();
            foreach (var output in new[] { albedo, mrca, normal })
                if (output.enabled) outputs.Add(output);
            RunAction(() => SaveOutputs(outputs));
        }

        private void SaveOutputs(IList<Output> outputs)
        {
            if (saveTask != null) throw new InvalidOperationException("An export is already running.");
            if (SourcesLoading()) throw new InvalidOperationException("Wait for source loading to finish.");
            if (string.IsNullOrWhiteSpace(textureName) || textureName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                textureName.Trim() == "." || textureName.Trim() == "..")
                throw new InvalidOperationException("Enter a valid texture base name without a path.");
            if (!Directory.Exists(outputFolder)) throw new DirectoryNotFoundException("Choose an existing output folder.");
            var comparison = TexturePackingFiles.ComparisonForDirectory(outputFolder);
            var comparer = comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var paths = new HashSet<string>(comparer);
            var sourcePaths = new HashSet<string>(comparer);
            foreach (string sourcePath in loaded.Keys) sourcePaths.Add(TexturePackingFiles.ResolveExistingFile(sourcePath));
            var overwrite = new List<string>();
            var requests = new List<SaveRequest>();
            foreach (var output in outputs)
            {
                if (!HasInput(output)) throw new InvalidOperationException("An enabled texture has no source images.");
                string path = Path.Combine(Path.GetFullPath(outputFolder), FileName(output));
                if (!paths.Add(path)) throw new InvalidOperationException("Choose distinct suffixes for each output.");
                if (sourcePaths.Contains(TexturePackingFiles.ResolveExistingFile(path)))
                    throw new InvalidOperationException("An output would overwrite a source texture: " + path);
                if (File.Exists(path)) overwrite.Add(Path.GetFileName(path));
                // Validate every referenced source before writing any output.
                if (!output.split) Source(output.rgb);
                for (int i = output.split ? 0 : 3; i < 4; i++)
                    if (output != normal) Source(output.slots[i]);
                string assetPath = AssetPathWithComparison(path, comparison);
                ValidateImportablePath(assetPath);
                var existing = new FileInfo(path);
                requests.Add(new SaveRequest { path = path, assetPath = assetPath, build = PrepareBuild(output, Sizes[widthIndex], Sizes[heightIndex]),
                    isNormal = output == normal, isAlbedo = output == albedo, existed = existing.Exists,
                    length = existing.Exists ? existing.Length : 0, modified = existing.Exists ? existing.LastWriteTimeUtc : default });
            }
            if (overwrite.Count > 0 && !EditorUtility.DisplayDialog("Overwrite textures?",
                string.Join("\n", overwrite), "Overwrite", "Cancel")) return;
            saveCancellation = new CancellationTokenSource();
            var token = saveCancellation.Token;
            saveTask = Task.Run(() => StageOutputs(requests, token), token);
            SubscribeSave(TickSave);
        }

        private static List<SaveRequest> StageOutputs(List<SaveRequest> requests, CancellationToken token)
        {
            try
            {
                foreach (var request in requests)
                {
                    token.ThrowIfCancellationRequested();
                    var image = request.build(token);
                    request.staged = TexturePackingFiles.Stage(request.path, stream => image.WriteTgaRle(stream, token));
                    request.build = null;
                }
                token.ThrowIfCancellationRequested();
                return requests;
            }
            catch
            {
                DiscardRequests(requests);
                throw;
            }
        }

        private void TickSave()
        {
            if (saveTask == null) return;
            if (importTask != null && !importTask.IsCompleted) return;
            if (!saveTask.IsCompleted)
            {
                return;
            }
            var finished = saveTask;
            try
            {
                if (importTask == null)
                {
                    var requests = finished.GetAwaiter().GetResult();
                    try
                    {
                        saveCancellation.Token.ThrowIfCancellationRequested();
                        importTask = CommitOutputs(requests);
                    }
                    finally { DiscardRequests(requests); }
                    committedRequests = requests;
                    return;
                }
                importTask.GetAwaiter().GetResult();
                status = "Saved " + committedRequests.Count + " texture(s) to " + Path.GetDirectoryName(committedRequests[0].path);
            }
            catch (OperationCanceledException) { status = "Export cancelled; outputs were not changed."; }
            catch (Exception exception)
            {
                error = exception.Message;
                UvtLog.Error("Texture packer: " + exception.Message);
            }
            finally
            {
                if (committedRequests == null || (importTask != null && importTask.IsCompleted))
                {
                    saveTask = null;
                    importTask = null;
                    committedRequests = null;
                    UnsubscribeSave(TickSave);
                    saveCancellation.Dispose();
                    saveCancellation = null;
                    if (selectionPending) { selectionPending = false; OnSelectionChanged(); }
                    Repaint();
                }
            }
        }

        private static Task CommitOutputs(List<SaveRequest> requests)
        {
            var files = new List<TexturePackingFiles.PendingFile>();
            foreach (var request in requests)
                files.Add(new TexturePackingFiles.PendingFile(request.staged, request.path,
                    request.existed, request.length, request.modified));
            var imports = new List<TexturePackingImports.Input>();
            foreach (var request in requests)
            {
                string assetPath = request.assetPath;
                if (assetPath != null) imports.Add(new TexturePackingImports.Input
                    { path = assetPath, normal = request.isNormal, albedo = request.isAlbedo });
            }
            // Installation stays in one editor tick, with imports deferred until rollback can no longer occur.
            using (new AssetDatabase.AssetEditingScope())
            {
                TexturePackingFiles.InstallBatch(files);
                foreach (var request in requests)
                {
                    request.staged = null;
                    UvtLog.Info("Saved texture: " + request.path);
                }
            }
            return TexturePackingImports.Schedule(imports.ToArray());
        }

        private static void DiscardRequests(List<SaveRequest> requests)
        {
            var failures = new List<Exception>();
            foreach (var request in requests)
            {
                try { TexturePackingFiles.Discard(request.staged); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 0) throw new AggregateException("Some texture temporary files could not be removed.", failures);
        }

        private void CancelSaveOnClose()
        {
            if (saveTask == null) return;
            var cancellation = saveCancellation;
            if (importTask != null)
            {
                // Files have committed. The independent importer queue owns their finalization.
                _ = importTask.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
                importTask = null;
                committedRequests = null;
                saveTask = null;
                saveCancellation = null;
                cancellation.Dispose();
                return;
            }
            cancellation.Cancel();
            _ = saveTask.ContinueWith(task =>
            {
                try
                {
                    if (task.Status == TaskStatus.RanToCompletion) DiscardRequests(task.Result);
                    else _ = task.Exception;
                }
                // Observe cleanup failures too; never leave a faulted, discarded continuation.
                catch (Exception exception) { UvtLog.Warn("Texture packer cleanup: " + exception); }
                finally { cancellation.Dispose(); }
            }, TaskScheduler.Default);
            saveTask = null;
            saveCancellation = null;
        }

        private static void ValidateAssetDestination(string path)
        {
            ValidateImportablePath(AssetPath(path));
        }

        private static void ValidateImportablePath(string assetPath)
        {
            if (assetPath == null) return;
            foreach (string segment in assetPath.Split('/'))
                if (segment.StartsWith(".", StringComparison.Ordinal) || segment.EndsWith("~", StringComparison.Ordinal))
                    throw new InvalidOperationException("Unity ignores this output folder; choose an importable folder: " + assetPath);
        }

        private static string AssetPath(string path)
        {
            return AssetPathWithComparison(path, TexturePackingFiles.ComparisonForDirectory(Path.GetDirectoryName(Path.GetFullPath(path))));
        }

        private static string AssetPathWithComparison(string path, StringComparison comparison)
        {
            path = Path.GetFullPath(path);
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(assets, comparison) ?
                "Assets/" + path.Substring(assets.Length).Replace('\\', '/') : null;
        }

        private void RunAction(Action action)
        {
            error = "";
            status = "";
            try { action(); }
            catch (Exception exception)
            {
                error = exception.Message;
                UvtLog.Error("Texture packer: " + exception.Message);
            }
        }
    }
}
