using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// issue #26 (Humberto DSO case): a GeneXus Design System object stores its source in
    /// TWO separate SDK parts — Tokens (GUID 75e52d99-…) holding <c>tokens Name { … }</c>
    /// and Styles (GUID c6b14574-…) holding <c>styles Name { … }</c>. Agents naturally submit
    /// the whole thing as one <c>part="Source"</c> blob (`tokens {…}` followed by `styles {…}`),
    /// which the MCP used to dump entirely into the Tokens part, leaving Styles empty.
    ///
    /// This splitter separates that combined blob back into its two top-level blocks so each
    /// can be written to (or read from) its own part. It relies on the DSL invariant that
    /// <c>tokens</c> and <c>styles</c> each appear once as a top-level block whose keyword
    /// starts a line (column ~0). Content is preserved verbatim — no re-indentation.
    /// </summary>
    public static class DesignSystemSourceSplitter
    {
        /// <summary>
        /// Splits a combined Design System source into its tokens and styles blocks.
        /// Returns true when at least one top-level <c>tokens</c>/<c>styles</c> block was found;
        /// false (with both outputs null) when neither keyword starts a top-level line, in which
        /// case the caller should fall back to writing the blob to a single part unchanged.
        /// </summary>
        public static bool TrySplit(string combined, out string tokensSrc, out string stylesSrc)
        {
            tokensSrc = null;
            stylesSrc = null;
            if (string.IsNullOrEmpty(combined)) return false;

            // Normalise CRLF handling without mutating the content we hand back: we locate the
            // block boundaries on the original string via line scanning.
            string[] lines = combined.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            int tokensLine = -1;
            int stylesLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].TrimStart();
                if (tokensLine < 0 && StartsWithKeyword(t, "tokens")) tokensLine = i;
                else if (stylesLine < 0 && StartsWithKeyword(t, "styles")) stylesLine = i;
                if (tokensLine >= 0 && stylesLine >= 0) break;
            }

            if (tokensLine < 0 && stylesLine < 0) return false;

            // Determine block ranges. Tokens runs from its keyword to just before styles
            // (or to EOF when there is no styles block). Styles runs from its keyword to EOF.
            if (tokensLine >= 0)
            {
                int tokensEnd = (stylesLine >= 0 && stylesLine > tokensLine) ? stylesLine : lines.Length;
                tokensSrc = Join(lines, tokensLine, tokensEnd).Trim();
                if (tokensSrc.Length == 0) tokensSrc = null;
            }
            if (stylesLine >= 0)
            {
                int stylesStart = stylesLine;
                int stylesEnd = lines.Length;
                stylesSrc = Join(lines, stylesStart, stylesEnd).Trim();
                if (stylesSrc.Length == 0) stylesSrc = null;
            }

            return tokensSrc != null || stylesSrc != null;
        }

        // A top-level block keyword is the word at the start of a (trimmed) line, followed by
        // whitespace, '{', or end-of-line — e.g. "tokens Foo {" or "styles {". Guards against
        // matching identifiers like "tokensList".
        private static bool StartsWithKeyword(string trimmed, string keyword)
        {
            if (trimmed.Length < keyword.Length) return false;
            if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.Length == keyword.Length) return true;
            char next = trimmed[keyword.Length];
            return char.IsWhiteSpace(next) || next == '{';
        }

        private static string Join(string[] lines, int start, int endExclusive)
        {
            return string.Join("\n", lines, start, endExclusive - start);
        }
    }
}
