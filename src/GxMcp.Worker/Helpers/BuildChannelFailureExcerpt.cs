using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Turns the lines the GeneXus build wrote on its <c>&gt;R</c> output channel into a short, bounded
    /// description of why a native build failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The in-process build forwards the SDK's output protocol to <c>BuildService.HandleLine</c>:
    /// <c>&gt;S</c>/<c>&gt;E</c> section markers, <c>&gt;L</c> log lines, <c>&gt;O</c> diagnostics and
    /// <c>&gt;R</c> runtime messages. Only the diagnostics were itemized, so a failure that only
    /// produced <c>&gt;R</c> lines (for example the compilation launcher that could not be started)
    /// ended as a generic "diagnostics were not captured" error although the output held the cause.
    /// </para>
    /// <para>
    /// Detection relies on the channel prefix, never on message text, so it does not depend on the
    /// locale of the installation. The one text check is the launcher name <c>gxexec</c>, which is a
    /// program name and not translated.
    /// </para>
    /// </remarks>
    internal static class BuildChannelFailureExcerpt
    {
        internal const int MaxLines = 5;
        internal const int MaxLineChars = 300;

        private const string Ellipsis = "...";

        internal const string GxExecHint =
            "gxexec (the GeneXus .NET Framework compilation launcher) could not be started: its directory, "
            + "normally <GeneXus install>\\gxnet, must be resolvable from the Worker process PATH. The Worker "
            + "prepends it when it exists, so confirm gxnet\\GXExec.exe is present in the installation that "
            + "GX_PROGRAM_DIR points to.";

        // ">R<message>" at the start of a line. The prefix is the output channel; what follows is free text.
        private static readonly Regex ChannelLine = new Regex(@"^[ \t]*>R(?<text>.*)$", RegexOptions.Compiled);

        // ">RO <step>" is the late-phase step marker BuildService.ExtractPhaseFailure already knows; keep such a
        // line as emitted so stripping ">R" does not leave a dangling "O <step>".
        private static readonly Regex StepMarker = new Regex(@"^O\s", RegexOptions.Compiled);

        // The bare launcher name only: an absolute path to GXExec.exe failing is not a PATH problem.
        private static readonly Regex BareGxExec = new Regex(
            @"(?<![\\/\w.\-])gxexec(?:\.exe)?(?!\w)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Describes the failure from the <c>&gt;R</c> lines in <paramref name="output"/>: at most
        /// <see cref="MaxLines"/> distinct lines of at most <see cref="MaxLineChars"/> characters,
        /// with a count of what was left out. Returns null when the output has no such line, so the
        /// caller keeps its generic message.
        /// </summary>
        /// <param name="output">The captured build output, one line per entry.</param>
        /// <param name="hint">A remediation hint when the failing command is the bare <c>gxexec</c>; otherwise null.</param>
        internal static string Describe(string output, out string hint)
        {
            hint = null;
            if (string.IsNullOrEmpty(output)) return null;

            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool launcherNotResolved = false;

            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    Match match = ChannelLine.Match(line);
                    if (!match.Success) continue;

                    string text = match.Groups["text"].Value;
                    string shown = StepMarker.IsMatch(text) ? line.Trim() : text.Trim();
                    if (shown.Length == 0) continue;

                    if (BareGxExec.IsMatch(shown)) launcherNotResolved = true;
                    shown = Truncate(shown);
                    if (seen.Add(shown)) distinct.Add(shown);
                }
            }

            if (distinct.Count == 0) return null;

            int shownCount = Math.Min(MaxLines, distinct.Count);
            string excerpt = string.Join(" | ", distinct.GetRange(0, shownCount));
            int omitted = distinct.Count - shownCount;
            if (omitted > 0)
                excerpt += " (+" + omitted + " more in the full build log)";
            if (!excerpt.EndsWith(".", StringComparison.Ordinal))
                excerpt += ".";

            if (launcherNotResolved) hint = GxExecHint;
            return excerpt;
        }

        private static string Truncate(string text)
        {
            if (text.Length <= MaxLineChars) return text;
            return text.Substring(0, MaxLineChars - Ellipsis.Length) + Ellipsis;
        }
    }
}
