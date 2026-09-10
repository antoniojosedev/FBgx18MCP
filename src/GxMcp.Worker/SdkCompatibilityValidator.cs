using System;
using System.IO;
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

            string expectedVersion = (string)manifest["supportedVersion"];
            string anchor = (string)manifest["anchor"];
            string anchorPath = Path.Combine(sdkPath, anchor ?? string.Empty);
            if (string.IsNullOrWhiteSpace(anchor) || !File.Exists(anchorPath))
                return Fail("GXMCP_SDK_ANCHOR_MISSING", "GXMCP_SDK_ANCHOR_MISSING path=" + (anchor ?? "<missing>"));

            string actualVersion = versionReader(anchorPath);
            if (!string.Equals(expectedVersion, actualVersion, StringComparison.OrdinalIgnoreCase))
                return Fail("GXMCP_SDK_VERSION_MISMATCH", "GXMCP_SDK_VERSION_MISMATCH expectedVersion=" + expectedVersion + " actualVersion=" + actualVersion);

            var assemblies = manifest["assemblies"] as JArray;
            if (assemblies == null || assemblies.Count == 0)
                return Fail("GXMCP_SDK_MANIFEST_INVALID", "GXMCP_SDK_MANIFEST_INVALID manifest=" + manifestPath + " error=assemblies");
            foreach (var token in assemblies)
            {
                string relativePath = (string)token["path"];
                string expectedHash = (string)token["sha256"];
                string filePath = Path.Combine(sdkPath, relativePath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(relativePath) || !File.Exists(filePath))
                    return Fail("GXMCP_SDK_ASSEMBLY_MISSING", "GXMCP_SDK_ASSEMBLY_MISSING path=" + (relativePath ?? "<missing>"));
                string actualHash = Sha256(filePath);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    return Fail("GXMCP_SDK_FINGERPRINT_MISMATCH", "GXMCP_SDK_FINGERPRINT_MISMATCH path=" + relativePath + " expectedSha256=" + expectedHash + " actualSha256=" + actualHash);
            }
            return new SdkCompatibilityResult(true, "GXMCP_SDK_COMPATIBLE", "GXMCP_SDK_COMPATIBLE version=" + expectedVersion + " assemblies=" + assemblies.Count);
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
