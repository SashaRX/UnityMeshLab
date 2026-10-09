using System;
using System.Globalization;
using System.Net;
using System.Text;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal static class TransferBenchmarkReport
    {
        const int MaxVisualFaces = 20000;
        const string TableCellSeparator = "</td><td>";
        static string Number(double value) => value.ToString("G9", CultureInfo.InvariantCulture);
        static string Escape(string value) => WebUtility.HtmlEncode(value ?? "");

        internal static string Csv(TransferBenchmark.Report report)
        {
            var text = new StringBuilder("case,method,negative_control,ground_truth,median_ms,min_ms,max_ms,deterministic,input_unchanged,misses,normal_fallbacks,clamped_vertices,faces,invalid,degenerate,stretched,mean_anisotropy,worst_anisotropy,overlap_pairs,overlap_complete,oob,rms_reference_texels,max_reference_texels,reference_pass,source_mean_anisotropy,source_overlap_pairs,error\n");
            foreach (var row in report.rows) {
                var q = row.quality;
                object[] cells = { row.name, row.method, row.negativeControl, row.referenceIsGroundTruth, row.medianMilliseconds,
                    row.minimumMilliseconds, row.maximumMilliseconds, row.deterministic, row.inputUnchanged, row.misses, row.fallbackVertices,
                    row.clampedVertices, q?.faces, q?.invalidFaces, q?.degenerateFaces, q?.stretchedFaces, q?.areaWeightedAnisotropy,
                    q?.worstAnisotropy, q?.overlapPairs, q?.overlapScanComplete, q?.outOfBoundsVertices,
                    row.referenceAvailable ? row.rmsReferenceTexels : (double?)null,
                    row.referenceAvailable ? row.maximumReferenceTexels : (double?)null,
                    row.referencePass, row.sourceQuality?.areaWeightedAnisotropy, row.sourceQuality?.overlapPairs, row.error };
                for (int i = 0; i < cells.Length; ++i) {
                    if (i > 0) text.Append(',');
                    text.Append(CsvUtil.Escape(Convert.ToString(cells[i], CultureInfo.InvariantCulture)));
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        internal static string Html(TransferBenchmark.Report report)
        {
            var text = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>UV transfer comparison</title><style>body{font:14px system-ui;background:#181b21;color:#edf0f7;margin:24px}table{border-collapse:collapse;width:100%}td,th{padding:8px;border:1px solid #404550;text-align:left}th{position:sticky;top:0;background:#262c36}a{color:#83cfff}.bad{background:#672f36}.control{background:#373243}img{width:200px;height:200px}code{overflow-wrap:anywhere}</style><h1>UV transfer comparison</h1>");
            text.Append("<p>Complete: ").Append(report.complete).Append("; cases: ").Append(report.completedCases).Append('/').Append(report.requestedCases)
                .Append("; Unity ").Append(Escape(report.unityVersion)).Append("; commit <code>").Append(Escape(report.gitSha)).Append("</code>; dirty: ").Append(report.gitDirty).Append("</p>");
            text.Append("<p>Timing includes transfer and its BVH build, excludes warmup, diagnostics, quality scans and file IO. Source quality uses local geometry; target quality uses the captured world transform. Anisotropy 1 is isotropic. Reference error is in atlas texels; recorded captures are a baseline, not ground truth. Negative controls deliberately contain defects. An incomplete overlap scan is a lower bound. No overall winner is computed.</p>");
            if (!string.IsNullOrEmpty(report.error)) text.Append("<pre>").Append(Escape(report.error)).Append("</pre>");
            text.Append("<table><thead><tr><th>Case / method</th><th>UV2</th><th>Median ms</th><th>Anisotropy mean / worst</th><th>Stretched / degenerate / invalid</th><th>Overlaps / OOB</th><th>Reference max texels</th><th>Repeat / input / misses</th></tr></thead><tbody>");
            foreach (var row in report.rows) AppendHtmlRow(text, row);
            return text.Append("</tbody></table></html>").ToString();
        }

        static void AppendHtmlRow(StringBuilder text, TransferBenchmark.Row row)
        {
            bool bad = !string.IsNullOrEmpty(row.error) || !row.deterministic || !row.inputUnchanged;
            string rowClass = bad ? "bad" : "";
            if (!bad && row.negativeControl) rowClass = "control";
            text.Append("<tr class=\"").Append(rowClass).Append("\"><td>")
                .Append(Escape(row.name)).Append("<br><strong>").Append(Escape(row.method)).Append("</strong><br>").Append(Escape(row.description));
            if (bad) text.Append("<pre>").Append(Escape(row.error)).Append("</pre>");
            text.Append(TableCellSeparator);
            if (!string.IsNullOrEmpty(row.view)) text.Append("<a href=\"").Append(Escape(row.view)).Append("\"><img loading=\"lazy\" alt=\"UV2 triangles\" src=\"").Append(Escape(row.view)).Append("\"></a>");
            text.Append(TableCellSeparator).Append(Number(row.medianMilliseconds)).Append(TableCellSeparator);
            var q = row.quality;
            if (q != null) text.Append(Number(q.areaWeightedAnisotropy)).Append(" / ").Append(Number(q.worstAnisotropy));
            string overlap = "unavailable";
            if (q != null) overlap = q.overlapPairs + (q.overlapScanComplete ? "" : "+ (incomplete)") + " / " + q.outOfBoundsVertices;
            text.Append(TableCellSeparator).Append(q == null ? "unavailable" : q.stretchedFaces + " / " + q.degenerateFaces + " / " + q.invalidFaces)
                .Append(TableCellSeparator).Append(overlap)
                .Append(TableCellSeparator).Append(row.referenceAvailable ? Number(row.maximumReferenceTexels) + (row.referenceIsGroundTruth ? " (truth)" : " (baseline)") : "unavailable (atlas size missing)")
                .Append(TableCellSeparator).Append(row.deterministic).Append(" / ").Append(row.inputUnchanged).Append(" / ").Append(row.misses).Append("</td></tr>");
        }

        internal static string View(TransferBenchmark.Input input, TransferBenchmark.Row row, Vector2[] uv)
        {
            var text = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-20 -20 552 552\"><rect x=\"-20\" y=\"-20\" width=\"552\" height=\"552\" fill=\"#242832\"/><rect width=\"512\" height=\"512\" fill=\"#151820\" stroke=\"#aaa\"/><g stroke=\"#9feaff\" stroke-width=\".65\" fill-opacity=\".32\">");
            var triangles = input.target.triangles;
            int count = Math.Min(triangles.Length / 3, MaxVisualFaces);
            for (int face = 0; face < count; ++face) {
                text.Append("<polygon fill=\"hsl(").Append((face * 137) % 360).Append(",65%,60%)\" points=\"");
                for (int corner = 0; corner < 3; ++corner) {
                    var point = uv[triangles[face * 3 + corner]];
                    text.Append(Number(point.x * 512)).Append(',').Append(Number((1 - point.y) * 512)).Append(' ');
                }
                text.Append("\"><title>face ").Append(face).Append("</title></polygon>");
            }
            text.Append("</g><text x=\"0\" y=\"-6\" font-family=\"sans-serif\" font-size=\"10\" fill=\"white\">")
                .Append(Escape(row.method)).Append("; ").Append(count).Append('/').Append(triangles.Length / 3).Append(" faces shown</text></svg>");
            return text.ToString();
        }
    }
}
