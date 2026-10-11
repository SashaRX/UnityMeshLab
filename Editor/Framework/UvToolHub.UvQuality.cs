using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    public partial class UvToolHub
    {
        bool uvQualityExpanded;
        int uvQualityKey, uvQualityRequestKey;
        bool uvQualityHasKey;
        LightmapUvQuality.Report uvQualityReport;
        Task<LightmapUvQuality.Report> uvQualityTask;
        CancellationTokenSource uvQualityCancel;
        Texture2D uvQualityMap;
        string uvQualityError;

        // Capturing Unity objects is main-thread only. Geometry math uses independent arrays on a worker.
        void OnInspectorUpdate()
        {
            if (ctx == null || inspection == null) return;
            int key = UvQualityKey();
            if (!uvQualityHasKey || uvQualityKey != key) {
                CancelUvQuality(); uvQualityKey = key; uvQualityHasKey = true;
                uvQualityReport = null; uvQualityError = null;
                if (viewportItems.Count > 0) {
                    try {
                        var parts = CaptureQualityParts();
                        var reference = ctx.PreviewLod == 0 || toolOwnsUvContent ? null : CaptureQualityLod0();
                        uvQualityCancel = new CancellationTokenSource();
                        var token = uvQualityCancel.Token; uvQualityRequestKey = key;
                        uvQualityTask = Task.Run(() => LightmapUvQuality.Measure(parts, reference, token), token);
                    }
                    catch (Exception error) { uvQualityError = error.Message; }
                }
            }
            if (uvQualityTask != null && uvQualityTask.IsCompleted) {
                if (uvQualityTask.IsFaulted) uvQualityError = uvQualityTask.Exception.GetBaseException().Message;
                else if (!uvQualityTask.IsCanceled && uvQualityRequestKey == key) {
                    uvQualityReport = uvQualityTask.Result;
                    CreateUvQualityMap(uvQualityReport);
                }
                uvQualityTask = null; uvQualityCancel?.Dispose(); uvQualityCancel = null;
            }
            Repaint();
        }

        int UvQualityKey()
        {
            unchecked {
                int key = (ctx.PreviewCacheVersion * 397 + ctx.PreviewUvChannel) * 397 + ctx.PreviewLod;
                key = key * 397 + (toolOwnsUvContent ? 1 : 0);
                for (int i = 0; i < viewportItems.Count; ++i) {
                    var item = viewportItems[i]; var entry = viewportEntries[i];
                    key = key * 397 + (item.mesh ? item.mesh.GetInstanceID() : 0);
                    key = key * 397 + item.matrix.GetHashCode();
                    key = key * 397 + (entry?.reverseTransferJson?.GetHashCode() ?? 0);
                }
                foreach (var entry in ctx.MeshEntries.Where(e => e.include && e.lodIndex == 0)) {
                    var mesh = ctx.DMesh(entry);
                    key = key * 397 + (mesh ? mesh.GetInstanceID() : 0);
                    key = key * 397 + (entry.renderer ? entry.renderer.localToWorldMatrix.GetHashCode() : 0);
                }
                return key;
            }
        }

        LightmapUvQuality.Part[] CaptureQualityParts()
        {
            var parts = new List<LightmapUvQuality.Part>();
            for (int i = 0; i < viewportItems.Count; ++i) {
                var item = viewportItems[i];
                if (!item.mesh) continue;
                parts.Add(CaptureQualityPart(inspection.Readable(item.mesh), viewportEntries[i], item.matrix, ctx.PreviewUvChannel));
            }
            return parts.ToArray();
        }

        LightmapUvQuality.Part[] CaptureQualityLod0()
        {
            var parts = new List<LightmapUvQuality.Part>();
            var transform = ContextWorldToPreview();
            foreach (var entry in ctx.MeshEntries.Where(e => e.include && e.lodIndex == 0)) {
                var mesh = ctx.DMesh(entry);
                if (!mesh) continue;
                parts.Add(CaptureQualityPart(inspection.Readable(mesh), entry,
                    entry.renderer ? transform * entry.renderer.localToWorldMatrix : Matrix4x4.identity, ctx.PreviewUvChannel));
            }
            return parts.ToArray();
        }

        internal static LightmapUvQuality.Part CaptureQualityPart(Mesh mesh, MeshEntry entry, Matrix4x4 transform, int channel)
        {
            var positions = mesh.vertices;
            for (int i = 0; i < positions.Length; ++i) positions[i] = transform.MultiplyPoint3x4(positions[i]);
            var coordinates = new List<Vector2>(); mesh.GetUVs(channel, coordinates);
            var indices = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; ++s)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) indices.AddRange(mesh.GetTriangles(s));
            ReverseUvAudit.ProvenanceEntry provenance = null;
            if (channel == 1 && !string.IsNullOrEmpty(entry?.reverseTransferJson))
                provenance = JsonUtility.FromJson<ReverseUvAudit.ProvenanceEntry>(entry.reverseTransferJson);
            return new LightmapUvQuality.Part { positions = positions, uv = coordinates.ToArray(), triangles = indices.ToArray(),
                key = entry?.meshGroupKey ?? mesh.name, sourceKey = entry?.fbxMesh ? entry.fbxMesh.name : mesh.name, provenance = provenance };
        }

        void DrawUvQualityButton()
        {
            bool current = uvQualityHasKey && uvQualityKey == UvQualityKey();
            var report = current ? uvQualityReport : null;
            string label = report != null ? $"UV quality {report.score}/100" : "UV quality …";
            var color = GUI.backgroundColor;
            if (report != null) GUI.backgroundColor = QualityColor(report.score);
            uvQualityExpanded = GUILayout.Toggle(uvQualityExpanded, new GUIContent(label,
                "Approximate lightmap UV assessment for all displayed meshes. Click for component scores, defects and bbox occupancy."),
                EditorStyles.toolbarButton, GUILayout.Width(125));
            GUI.backgroundColor = color;
        }

        void DrawUvQualityPanel()
        {
            if (!uvQualityExpanded) return;
            var report = uvQualityHasKey && uvQualityKey == UvQualityKey() ? uvQualityReport : null;
            if (report == null) {
                EditorGUILayout.HelpBox(uvQualityError == null ? "Measuring displayed UVs…" : "UV quality unavailable: " + uvQualityError, MessageType.Info);
                return;
            }
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"UV{ctx.PreviewUvChannel} · LOD{ctx.PreviewLod} · {report.score}/100 · approximate", EditorStyles.boldLabel);
            if (GUILayout.Button("Recheck", EditorStyles.miniButton, GUILayout.Width(60))) uvQualityHasKey = false;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (uvQualityMap) {
                var rect = GUILayoutUtility.GetRect(95, 100, GUILayout.Width(95), GUILayout.Height(100));
                GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 76), uvQualityMap, ScaleMode.StretchToFill);
                GUI.Label(new Rect(rect.x, rect.y + 78, rect.width, 20), "UV bbox · 512²", EditorStyles.miniLabel);
            }
            EditorGUILayout.BeginVertical();
            QualityBar("Packing", report.packingScore, $"fill {report.fill:P0} · largest void {report.largestEmptyRectangle:P0}");
            QualityBar("Stretch", report.stretchScore, $"P95 {report.stretchP95:F2}× · max {report.worstStretch:G4}× · area >4× {report.severeArea:P1}");
            QualityBar("Density", report.densityScore, $"variation {report.densityCv:P1}");
            QualityBar("Defects", report.defectScore, $"invalid {report.invalidFaces} · collapsed {report.collapsedFaces} · zero 3D {report.zeroGeometryFaces} · overlap {report.unexpectedOverlapPairs} / allowed {report.allowedOverlapPairs}");
            QualityBar("LOD0 match", report.lodScore, report.lodScore < 0 ? "not measured"
                : $"coverage {report.lodCoverage:P0} · aligned {report.lodAlignedArea:P0} · mean / max UV error {report.lodMeanError:P1} / {report.lodWorstError:P1}");
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"BBox {report.bboxMax.x - report.bboxMin.x:G4} × {report.bboxMax.y - report.bboxMin.y:G4}. Padding / baked atlas resolution: not assessed.", EditorStyles.miniLabel);
            if (!report.complete) EditorGUILayout.HelpBox(report.limitation, MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        static void QualityBar(string name, int score, string detail)
        {
            var rect = GUILayoutUtility.GetRect(100, 19, GUILayout.ExpandWidth(true));
            var color = GUI.color; GUI.color = score < 0 ? Color.gray : QualityColor(score);
            EditorGUI.ProgressBar(rect, Math.Max(0, score) / 100f, score < 0 ? name + " · n/a" : $"{name} {score}/100");
            GUI.color = color;
            GUI.Label(rect, new GUIContent(string.Empty, detail));
            EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
        }
        static Color QualityColor(int score) => score >= 80 ? new Color(.35f, .8f, .45f)
            : score >= 60 ? new Color(1, .75f, .25f) : new Color(1, .35f, .3f);

        void CreateUvQualityMap(LightmapUvQuality.Report report)
        {
            if (uvQualityMap) DestroyImmediate(uvQualityMap);
            uvQualityMap = new Texture2D(report.rasterWidth, report.rasterHeight, TextureFormat.RGBA32, false) {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, name = "UV bbox occupancy"
            };
            var colors = new Color32[report.occupancy.Length];
            for (int i = 0; i < colors.Length; ++i) colors[i] = report.occupancy[i] == 0
                ? new Color32(42, 46, 52, 255) : new Color32(74, 174, 182, 255);
            uvQualityMap.SetPixels32(colors); uvQualityMap.Apply();
        }

        void CancelUvQuality()
        {
            uvQualityCancel?.Cancel(); uvQualityCancel?.Dispose(); uvQualityCancel = null;
            // Observe errors even when a stale worker finishes after cancellation.
            if (uvQualityTask != null) _ = uvQualityTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            uvQualityTask = null;
        }
        void DisposeUvQuality()
        {
            CancelUvQuality(); uvQualityHasKey = false; uvQualityReport = null;
            if (uvQualityMap) DestroyImmediate(uvQualityMap); uvQualityMap = null;
        }
    }
}
