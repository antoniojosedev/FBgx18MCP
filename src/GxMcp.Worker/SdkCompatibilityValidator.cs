using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker
{
    public sealed class SdkCompatibilityResult
    {
        public bool IsCompatible { get; private set; }
        public string Code { get; private set; }
        public string Diagnostic { get; private set; }

        internal SdkCompatibilityResult(bool compatible, string code, string diagnostic)
        {
            IsCompatible = compatible;
            Code = code;
            Diagnostic = diagnostic;
        }
    }

    public static class SdkCompatibilityValidator
    {
        public static SdkCompatibilityResult Validate(string sdkPath, string manifestPath)
        {
            return Validate(sdkPath, manifestPath, FileVersionInfoFor);
        }

        public static SdkCompatibilityResult Validate(string sdkPath, string manifestPath, Func<string, string> versionReader)
        {
            if (string.IsNullOrWhiteSpace(sdkPath) || !Directory.Exists(sdkPath))
                return Fail("GXMCP_SDK_PATH_MISSING", "GXMCP_SDK_PATH_MISSING path=<missing>");
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return Fail("GXMCP_SDK_MANIFEST_MISSING", "GXMCP_SDK_MANIFEST_MISSING manifest=" + (manifestPath ?? "<missing>"));

            JObject manifest;
            try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=" + ex.GetType().Name); }

            string expectedVersion = (string)manifest["supportedVersion"] ?? string.Empty;
            string anchor = (string)manifest["anchor"] ?? string.Empty;
            string anchorPath = Path.Combine(sdkPath, anchor ?? string.Empty);
            if (string.IsNullOrWhiteSpace(anchor) || !File.Exists(anchorPath))
                return Fail("GXMCP_SDK_ANCHOR_MISSING", "GXMCP_SDK_ANCHOR_MISSING path=" + (anchor ?? "<missing>"));

            string actualVersion = versionReader(anchorPath) ?? string.Empty;
            IReadOnlyList<string> supportedMajors = ResolveSupportedMajors(manifestPath, manifest, expectedVersion);
            string? actualMajor = GetMajor(actualVersion);
            bool exactVersion = string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase);
            if (actualMajor == null || !supportedMajors.Contains(actualMajor, StringComparer.Ordinal))
            {
                string expectedMajors = supportedMajors.Count == 0 ? "<none>" : string.Join(",", supportedMajors);
                return Fail("GXMCP_SDK_VERSION_MISMATCH", "GXMCP_SDK_VERSION_MISMATCH expectedVersion=" + expectedVersion
                    + " expectedMajors=" + expectedMajors + " actualVersion=" + actualVersion);
            }

            var assemblies = manifest["assemblies"] as JArray;
            if (assemblies == null || assemblies.Count == 0)
                return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=assemblies");
            var fingerprintDrift = new List<string>();
            foreach (var token in assemblies)
            {
                string relativePath = (string)token["path"];
                string expectedHash = (string)token["sha256"];
                string filePath = Path.Combine(sdkPath, relativePath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(relativePath) || !File.Exists(filePath))
                    return Fail("GXMCP_SDK_ASSEMBLY_MISSING", "GXMCP_SDK_ASSEMBLY_MISSING path=" + (relativePath ?? "<missing>"));
                string actualHash = Sha256(filePath);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    fingerprintDrift.Add("GXMCP_SDK_FINGERPRINT_DRIFT path=" + relativePath + " expectedSha256=" + expectedHash + " actualSha256=" + actualHash);
            }
            string versionDiagnostic = exactVersion ? expectedVersion : expectedVersion + " actualVersion=" + actualVersion + " (compatible major; patch/build drift)";
            string diagnostic = "GXMCP_SDK_COMPATIBLE version=" + versionDiagnostic
                + " major=" + actualMajor
                + " supportedMajors=" + string.Join(",", supportedMajors)
                + " assemblies=" + assemblies.Count;
            if (fingerprintDrift.Count > 0) diagnostic += "\n" + string.Join("\n", fingerprintDrift);
            return new SdkCompatibilityResult(true, "GXMCP_SDK_COMPATIBLE", diagnostic);
        }

        private static IReadOnlyList<string> ResolveSupportedMajors(string manifestPath, JObject manifest, string expectedVersion)
        {
            string directory;
            try { directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty; }
            catch { directory = Path.GetDirectoryName(manifestPath) ?? string.Empty; }

            // The catalog is the source of truth in both layouts used by the
            // repository: config/<manifest> in a checkout and worker/<manifest>
            // plus worker/config/gx-versions.json in a published artifact.
            string[] catalogCandidates =
            {
                Path.Combine(directory, "gx-versions.json"),
                Path.Combine(directory, "config", "gx-versions.json")
            };
            foreach (string candidate in catalogCandidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    JObject catalog = JObject.Parse(File.ReadAllText(candidate));
                    IReadOnlyList<string> majors = ReadMajors(catalog["supportedMajors"] as JArray);
                    if (majors.Count > 0) return majors;
                }
                catch
                {
                    // Preserve compatibility with a standalone/older manifest;
                    // the manifest-major fallback below remains fail-closed.
                }
            }

            IReadOnlyList<string> manifestMajors = ReadMajors(manifest["supportedMajors"] as JArray);
            if (manifestMajors.Count > 0) return manifestMajors;

            string? expectedMajor = GetMajor(expectedVersion);
            return expectedMajor == null ? Array.Empty<string>() : new[] { expectedMajor };
        }

        private static IReadOnlyList<string> ReadMajors(JArray? entries)
        {
            if (entries == null || entries.Count == 0) return Array.Empty<string>();

            var majors = new List<string>();
            foreach (JToken entry in entries)
            {
                string? major = entry is JObject obj ? obj["major"]?.ToString() : entry.ToString();
                if (!string.IsNullOrWhiteSpace(major) && !majors.Contains(major, StringComparer.Ordinal))
                    majors.Add(major);
            }
            return majors;
        }

        private static string? GetMajor(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return null;
            string first = version.Split('.')[0];
            return int.TryParse(first, out int major) && major > 0 ? major.ToString() : null;
        }

        private static SdkCompatibilityResult Fail(string code, string diagnostic)
        {
            return new SdkCompatibilityResult(false, code, diagnostic);
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string FileVersionInfoFor(string path)
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return info.ProductVersion ?? string.Empty;
        }
    }
}
