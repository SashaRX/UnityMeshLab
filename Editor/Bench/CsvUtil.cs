namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Shared CSV field escaping for the tool's report writers
    /// (BenchmarkRecorder, BenchmarkSweep, FbxMetricsExporter).
    /// </summary>
    internal static class CsvUtil
    {
        /// <summary>
        /// Escapes a single CSV field.
        ///
        /// Two separate concerns, applied in this order:
        /// 1. Formula neutralisation — spreadsheet applications evaluate a cell
        ///    whose text begins with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c> or a
        ///    leading tab/CR/LF as a formula. Our fields carry user-controlled
        ///    data (asset names, file paths), so such values get an apostrophe
        ///    prefix that forces them to stay text.
        /// 2. Line-break flattening — RFC 4180 allows a newline inside a quoted
        ///    field, but our own reader (BenchmarkSweep.AggregateRun) splits the
        ///    report with File.ReadAllLines before parsing, so an embedded CR/LF
        ///    would split one logical record across physical lines and corrupt
        ///    every column after it. CR, LF and TAB become spaces so one
        ///    physical line always equals one logical record.
        /// 3. RFC 4180 quoting — fields containing a comma or a quote are
        ///    wrapped in double quotes with embedded quotes doubled.
        /// </summary>
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            // Tested against the original string: the neutralising apostrophe is
            // still needed for a field that starts with a control character
            // followed by a formula.
            if (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' ||
                s[0] == '\t' || s[0] == '\r' || s[0] == '\n')
                s = "'" + s;

            if (s.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
                s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            bool needQuote = s.IndexOfAny(new[] { ',', '"' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
