using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 50 — KB-level GAM / security audit.
    ///
    /// Scans the KB's environment property dumps for known-insecure settings
    /// without depending on the SDK GAM API (which isn't reliably reachable
    /// from inside the worker for all KB shapes). The scan is best-effort:
    /// when no env files are found it still returns a structured response
    /// so the caller can act on the absence.
    /// </summary>
    public class SecurityAuditService
    {
        private readonly KbService _kbService;

        public SecurityAuditService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string AuditGam()
        {
            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { /* keep null */ }

            var findings = new JArray();
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
            {
                findings.Add(Finding("info", "KbPathUnknown",
                    "No KB is currently open; cannot audit GAM settings.",
                    "Open a KB first via genexus_kb action=open, then re-run."));
                return Envelope(findings).ToString();
            }

            string envDir = Path.Combine(kbPath, "Environments");
            if (!Directory.Exists(envDir))
            {
                findings.Add(Finding("info", "NoEnvironments",
                    "No Environments directory found in KB.",
                    "The KB has no targets configured. Configure a target environment in the IDE."));
                return Envelope(findings).ToString();
            }

            try
            {
                // Walk all .xml under Environments — env property dumps live there
                // under property-store buckets the SDK manages.
                var files = Directory.EnumerateFiles(envDir, "*.xml", SearchOption.AllDirectories).Take(200);
                foreach (var f in files)
                {
                    string text;
                    try { text = File.ReadAllText(f); } catch { continue; }

                    // Each finding records the offending file so the caller can navigate.
                    if (Regex.IsMatch(text, "IntegratedSecurityLevel\\s*=\\s*[\"']?\\s*(None|0)", RegexOptions.IgnoreCase))
                        findings.Add(Finding("critical", "IntegratedSecurityNone",
                            "IntegratedSecurityLevel=None in " + Path.GetFileName(f) + " — GAM is disabled, KB has no enforced authentication.",
                            "Set IntegratedSecurityLevel to Authentication or Authorization in environment properties."));

                    if (Regex.IsMatch(text, "USE_ENCRYPTION\\s*=\\s*[\"']?\\s*NONE", RegexOptions.IgnoreCase))
                        findings.Add(Finding("warn", "EncryptionDisabled",
                            "USE_ENCRYPTION=NONE in " + Path.GetFileName(f) + " — KB-level encryption is off.",
                            "Enable session encryption in environment properties."));

                    var expiryMatch = Regex.Match(text, "GAM_DEFAULT_TOKEN_EXPIRES\\s*=\\s*[\"']?(\\d+)", RegexOptions.IgnoreCase);
                    if (expiryMatch.Success && int.TryParse(expiryMatch.Groups[1].Value, out int seconds) && seconds > 86400)
                        findings.Add(Finding("info", "TokenExpiryLong",
                            "GAM_DEFAULT_TOKEN_EXPIRES=" + seconds + "s (>24h) in " + Path.GetFileName(f) + ".",
                            "Consider shortening to ≤24h to limit blast radius of stolen tokens."));

                    // Cheap hardcoded-secret heuristic: JWT-shaped or RSA-prefixed strings in env xml values.
                    if (Regex.IsMatch(text, "eyJ[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}\\.[A-Za-z0-9_-]{20,}"))
                        findings.Add(Finding("critical", "JwtInEnvProps",
                            "JWT-shaped token literal found in " + Path.GetFileName(f) + ".",
                            "Move secrets to environment variables or a vault; never commit literal tokens to env props."));

                    if (text.Contains("-----BEGIN RSA PRIVATE KEY-----") || text.Contains("-----BEGIN PRIVATE KEY-----"))
                        findings.Add(Finding("critical", "PrivateKeyInEnvProps",
                            "PEM-formatted private key found in " + Path.GetFileName(f) + ".",
                            "Move the key to a secret manager; never commit private keys to the KB."));
                }
            }
            catch (Exception ex)
            {
                findings.Add(Finding("warn", "AuditError",
                    "Audit walk failed: " + ex.Message, "Inspect the KB manually if findings are missing."));
            }

            return Envelope(findings).ToString();
        }

        private static JObject Finding(string severity, string code, string message, string remediation)
        {
            return new JObject
            {
                ["severity"] = severity,
                ["code"] = code,
                ["message"] = message,
                ["remediation"] = remediation
            };
        }

        private static JObject Envelope(JArray findings)
        {
            string worst = "info";
            foreach (var f in findings)
            {
                string s = f["severity"]?.ToString();
                if (s == "critical") { worst = "critical"; break; }
                if (s == "warn" && worst != "critical") worst = "warn";
            }
            return new JObject
            {
                ["status"] = "Success",
                ["findingsCount"] = findings.Count,
                ["worstSeverity"] = findings.Count == 0 ? "ok" : worst,
                ["findings"] = findings
            };
        }
    }
}
