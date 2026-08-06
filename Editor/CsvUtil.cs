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
        /// 2. RFC 4180 quoting — fields containing a comma, quote or newline are
        ///    wrapped in double quotes with embedded quotes doubled.
        /// </summary>
        internal static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            if (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' ||
                s[0] == '\t' || s[0] == '\r' || s[0] == '\n')
                s = "'" + s;

            bool needQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
