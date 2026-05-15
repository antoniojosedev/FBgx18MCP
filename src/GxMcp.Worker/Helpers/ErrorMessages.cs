using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    // v2.3.8 (Task 7.1) — friction report 2026-05-15 #15. The GeneXus SDK
    // emits user-facing diagnostics in PT-BR when the OS locale is pt-BR.
    // For tool callers (LLMs operating in EN), the language flip is noise
    // and sometimes causes them to misclassify the error. Translate maps
    // the canonical patterns we've seen in friction reports to EN; unknown
    // messages pass through verbatim. Callers that need the original (e.g.
    // for support escalation) should embed it as _meta.sourceMessage.
    public static class ErrorMessages
    {
        // Ordered: longer/more specific patterns first so partial overlaps
        // (e.g. "não é um valor válido" inside a larger message) don't get
        // half-translated by a generic rule. Seeded from real PT-BR strings
        // observed in docs/mcp-friction-report-*.md + dev_log.md + worker logs.
        private static readonly (Regex Pattern, string Replacement)[] Table = new (Regex, string)[]
        {
            // Validation envelope (most common — drives ~half of agent confusion in PT-BR locales).
            (new Regex(@"A validação de Web Panel '([^']+)' falhou\.?", RegexOptions.Compiled),
             "Web Panel '$1' validation failed."),
            (new Regex(@"A validação de Transaction '([^']+)' falhou\.?", RegexOptions.Compiled),
             "Transaction '$1' validation failed."),
            (new Regex(@"A validação de Procedure '([^']+)' falhou\.?", RegexOptions.Compiled),
             "Procedure '$1' validation failed."),
            (new Regex(@"A validação de Structured Data Type '([^']+)' falhou\.?", RegexOptions.Compiled),
             "Structured Data Type '$1' validation failed."),
            (new Regex(@"A validação de ([\w ]+?) '([^']+)' falhou\.?", RegexOptions.Compiled),
             "$1 '$2' validation failed."),
            // Per-property / per-control diagnostics (Detailed Messages payload).
            (new Regex(@"Referência de controle inválida", RegexOptions.Compiled),
             "Invalid control reference"),
            (new Regex(@"([\w]+) é propriedade inválida", RegexOptions.Compiled),
             "$1 is an invalid property"),
            (new Regex(@"propriedade inválida", RegexOptions.Compiled),
             "invalid property"),
            (new Regex(@"'Vazio'", RegexOptions.Compiled),
             "'Empty'"),
            (new Regex(@"não é um valor válido", RegexOptions.Compiled),
             "is not a valid value"),
            // Reorg / build pipeline diagnostics.
            (new Regex(@"GAM não será reorganizado", RegexOptions.Compiled),
             "GAM will not be reorganized"),
            (new Regex(@"não será reorganizado", RegexOptions.Compiled),
             "will not be reorganized"),
            (new Regex(@"O ambiente de destino está configurado para não reorganizar", RegexOptions.Compiled),
             "Target environment is configured to skip reorganization"),
            (new Regex(@"Reorganização cancelada", RegexOptions.Compiled),
             "Reorganization cancelled"),
            // Generic prefixes — must come LAST so the specific patterns above win.
            (new Regex(@"^Detailed Messages:?", RegexOptions.Compiled),
             "Detailed messages:"),
            (new Regex(@"A operação falhou", RegexOptions.Compiled),
             "Operation failed"),
            (new Regex(@"^Erro ao\b", RegexOptions.Compiled),
             "Error while"),
            (new Regex(@"Não foi possível", RegexOptions.Compiled),
             "Could not"),
            (new Regex(@"nenhum objeto", RegexOptions.Compiled),
             "no object"),
        };

        public static string Translate(string ptbr)
        {
            if (ptbr == null) return null;
            if (ptbr.Length == 0) return ptbr;

            var s = ptbr;
            foreach (var (rx, repl) in Table) s = rx.Replace(s, repl);

            // Punctuation normalization: collapse runs of dots and trim trailing whitespace.
            s = Regex.Replace(s, @"\.\.+", ".");
            return s.TrimEnd();
        }

        public static (string En, string Source) TranslateWithSource(string ptbr)
        {
            return (Translate(ptbr), ptbr);
        }
    }
}
