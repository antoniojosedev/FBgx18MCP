using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal sealed class WorkerSdkCompatibilityProbeResult
    {
        internal string? InstallationPath { get; init; }
        internal string? Version { get; init; }
        internal string? Major { get; init; }
        internal string Status { get; init; } = "unavailable";
        internal string Code { get; init; } = "GXMCP_SDK_VERSION_UNDETECTED";
        internal string Diagnostic { get; init; } = string.Empty;

        internal bool IsCompatible => string.Equals(Status, "compatible", StringComparison.Ordinal);
        internal bool IsRejected => string.Equals(Status, "incompatible", StringComparison.Ordinal);

        internal JObject ToDiagnosticObject()
        {
            return new JObject
            {
                ["installationPath"] = InstallationPath,
                ["version"] = Version,
                ["major"] = Major,
                ["matchedMajor"] = IsCompatible ? Major : null,
                ["status"] = Status,
                ["code"] = Code,
                ["diagnostic"] = Diagnostic,
                ["supportedMajors"] = JArray.FromObject(GeneXusVersionCatalog.SupportedMajors),
                ["catalogSource"] = GeneXusVersionCatalog.CatalogSource
            };
        }
    }

    /// <summary>
    /// Cheap Gateway-side mirror of the Worker SDK major gate. It deliberately checks
    /// only the explicit major catalog: the Worker remains responsible for the full
    /// manifest/fingerprint check. This probe prevents a known unsupported major from
    /// entering the worker respawn loop and gives whoami/doctor a synchronous reason.
    /// </summary>
    internal static class WorkerSdkCompatibilityProbe
    {
        internal static WorkerSdkCompatibilityProbeResult Check(string? installationPath)
        {
            if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Status = "unavailable",
                    Code = "GXMCP_SDK_PATH_MISSING",
                    Diagnostic = "GXMCP_SDK_PATH_MISSING path="
                        + (string.IsNullOrWhiteSpace(installationPath) ? "<missing>" : installationPath)
                };
            }

            string? version = ReadAnchorVersion(installationPath);
            if (string.IsNullOrWhiteSpace(version))
                version = Program.DetectGeneXusVersion(installationPath);

            return Evaluate(installationPath, version);
        }

        internal static WorkerSdkCompatibilityProbeResult Evaluate(string installationPath, string? version)
        {

            string? major = GeneXusVersionCatalog.GetMajor(version);
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(major))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Version = version,
                    Major = major,
                    Status = "unavailable",
                    Code = "GXMCP_SDK_VERSION_UNDETECTED",
                    Diagnostic = "GXMCP_SDK_VERSION_UNDETECTED path=" + installationPath
                };
            }

            if (!GeneXusVersionCatalog.IsSupported(version))
            {
                return new WorkerSdkCompatibilityProbeResult
                {
                    InstallationPath = installationPath,
                    Version = version,
                    Major = major,
                    Status = "incompatible",
                    Code = "GXMCP_SDK_VERSION_MISMATCH",
                    Diagnostic = "GXMCP_SDK_VERSION_MISMATCH expectedMajors="
                        + GeneXusVersionCatalog.SupportedMajorsDisplay
                        + " actualVersion=" + version
                };
            }

            return new WorkerSdkCompatibilityProbeResult
            {
                InstallationPath = installationPath,
                Version = version,
                Major = major,
                Status = "compatible",
                Code = "GXMCP_SDK_COMPATIBLE",
                Diagnostic = "GXMCP_SDK_COMPATIBLE version=" + version
                    + " major=" + major
                    + " supportedMajors=" + GeneXusVersionCatalog.SupportedMajorsDisplay
            };
        }

        private static string? ReadAnchorVersion(string installationPath)
        {
            try
            {
                string anchor = Path.Combine(installationPath, "Artech.Architecture.Common.dll");
                if (!File.Exists(anchor)) return null;
                var info = FileVersionInfo.GetVersionInfo(anchor);
                return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            }
            catch
            {
                return null;
            }
        }
    }
}
