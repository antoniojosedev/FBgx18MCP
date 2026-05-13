using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public static class WritePolicy
    {
        private static readonly Regex GenericLineErrorRegex = new Regex(
            @"^(erro|error)\s*,\s*line\s*:\s*\d+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsUnchangedSourceWrite(string existingSource, string incomingSource) =>
            string.Equals(
                NormalizeSourceForComparison(existingSource),
                NormalizeSourceForComparison(incomingSource),
                StringComparison.Ordinal);

        public static string NormalizeSourceForComparison(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }

            return text.Replace("\r\n", "\n")
                       .Replace("\r", "\n")
                       .TrimEnd('\n');
        }

        public static bool IsLogicalSourcePart(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName))
            {
                return false;
            }

            return partName.Equals("Source", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Events", StringComparison.OrdinalIgnoreCase) ||
                   partName.Equals("Code", StringComparison.OrdinalIgnoreCase);
        }

        public static string BuildFailureDetails(string primaryMessage, JArray issues)
        {
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(primaryMessage))
            {
                details.Add(primaryMessage.Trim());
            }

            if (issues != null)
            {
                foreach (var issue in issues.OfType<JObject>())
                {
                    string description = issue["description"]?.ToString();
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    if (details.Any(d => string.Equals(d, description, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    details.Add(description.Trim());
                }
            }

            return string.Join(" | ", details);
        }

        // Returns true when a message is the SDK's uninformative "Erro" / "Error" / "Erro, line: 1" fallback.
        // Used to decide whether to upgrade an exception message with the part's GetSdkMessages() or diagnostics.
        public static bool IsBareGenericError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return true;
            string trimmed = message.Trim();
            if (string.Equals(trimmed, "Erro", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "Error", StringComparison.OrdinalIgnoreCase)) return true;
            if (GenericLineErrorRegex.IsMatch(trimmed)) return true;
            return false;
        }

        // Picks the most informative message available. When the SDK raises a bare "Erro" but
        // part.GetSdkMessages() or SdkDiagnosticsHelper.GetDiagnostics returned the real text
        // (e.g. "src0059: Esperando 'EndFor'..."), prefer that text. Otherwise return the
        // original exception message unchanged.
        public static string PreferDetailedMessage(string exceptionMessage, string sdkMessages, JArray issues)
        {
            string baseMessage = string.IsNullOrWhiteSpace(exceptionMessage) ? string.Empty : exceptionMessage.Trim();
            if (!IsBareGenericError(baseMessage)) return baseMessage;

            if (!string.IsNullOrWhiteSpace(sdkMessages))
            {
                string trimmed = sdkMessages.Trim();
                if (!IsBareGenericError(trimmed)) return trimmed;
            }

            if (issues != null)
            {
                foreach (var issue in issues.OfType<JObject>())
                {
                    string description = issue["description"]?.ToString();
                    if (string.IsNullOrWhiteSpace(description)) continue;
                    if (IsBareGenericError(description)) continue;
                    return description.Trim();
                }
            }

            return baseMessage;
        }

        public static bool ShouldRetryWithoutPartSave(string partName, string exceptionMessage, string diagnosticText)
        {
            if (!IsLogicalSourcePart(partName))
            {
                return false;
            }

            string normalizedException = exceptionMessage?.Trim() ?? string.Empty;
            string normalizedDiagnostic = diagnosticText?.Trim() ?? string.Empty;

            bool genericFailure = string.IsNullOrWhiteSpace(exceptionMessage) ||
                                  string.Equals(normalizedException, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(normalizedException, "Error", StringComparison.OrdinalIgnoreCase) ||
                                  GenericLineErrorRegex.IsMatch(normalizedException) ||
                                  exceptionMessage.IndexOf("Part save failed: Erro", StringComparison.OrdinalIgnoreCase) >= 0;
            bool genericDiagnostics = string.IsNullOrWhiteSpace(diagnosticText) ||
                                      string.Equals(normalizedDiagnostic, "Erro", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(normalizedDiagnostic, "Error", StringComparison.OrdinalIgnoreCase) ||
                                      GenericLineErrorRegex.IsMatch(normalizedDiagnostic);
            return genericFailure && genericDiagnostics;
        }
    }
}
