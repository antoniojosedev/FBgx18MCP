using System;
using System.IO;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class TestService
    {
        private readonly KbService _kbService;
        private readonly BuildService _buildService;

        public TestService(KbService kbService, BuildService buildService)
        {
            _kbService = kbService;
            _buildService = buildService;
        }

        // Security: `target` is LLM-controlled and flows into an MSBuild XML
        // document. Unescaped, an attacker could inject arbitrary MSBuild
        // tasks (e.g. <Exec Command="calc"/>) and achieve RCE under the
        // worker's privileges. Validate first, then XML-escape on the way in.
        // The same goes for the KB path — it's derived from the SDK but we
        // still escape to be defensive in depth.
        internal static bool IsSafeTestTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (target.Length > 256) return false;
            // GeneXus object names can include letters, digits, underscore, dot,
            // and we allow comma/semicolon for "Build With These Only" style
            // lists. Anything else (especially `<`, `>`, `"`, `'`, `&`) is
            // disallowed at the entrypoint so it can't reach the XML emitter.
            foreach (var c in target)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '_' || c == '.'
                       || c == ',' || c == ';' || c == ' ' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        public string RunTest(string target)
        {
            try
            {
                if (!IsSafeTestTarget(target))
                {
                    return "{\"status\":\"Error\",\"code\":\"InvalidTarget\"," +
                        "\"error\":\"target contains characters outside the allowed set " +
                        "(letters, digits, '_', '.', ',', ';', ' ', '-').\"}";
                }

                var kb = _kbService.GetKB();
                Logger.Info(string.Format("Preparing to run test: {0}", target));

                // MSBuild based execution is more resilient as it handles the full environment
                string kbPath = _buildService.GetKBPath();
                string gxPath = Environment.GetEnvironmentVariable("GX_PROGRAM_DIR") ?? @"C:\Program Files (x86)\GeneXus\GeneXus18";
                string msbuildPath = Path.Combine(gxPath, "MSBuild.exe");

                string tempFile = Path.Combine(Path.GetTempPath(), "RunGXTest_" + Guid.NewGuid().ToString() + ".msbuild");
                // Defense-in-depth: SecurityElement.Escape on every interpolated
                // value, even after IsSafeTestTarget. This means a future widening
                // of the allowlist can't reintroduce XML injection.
                string safeGx = System.Security.SecurityElement.Escape(gxPath);
                string safeKb = System.Security.SecurityElement.Escape(kbPath);
                string safeTarget = System.Security.SecurityElement.Escape(target);
                string msbuildContent = string.Format(
                    "<Project DefaultTargets='Run' xmlns='http://schemas.microsoft.com/developer/msbuild/2003'>" +
                    "    <Import Project='{0}\\Genexus.Tasks.targets' />" +
                    "    <Import Project='{0}\\Abstracta.GXtest.Tasks.targets' />" +
                    "    <Target Name='Run'>" +
                    "        <OpenKnowledgeBase Book='{1}' />" +
                    "        <ExecuteTests Objects='{2}' />" +
                    "    </Target>" +
                    "</Project>", safeGx, safeKb, safeTarget);

                File.WriteAllText(tempFile, msbuildContent);
                Logger.Info(string.Format("Running GXtest via MSBuild for {0}...", target));

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = msbuildPath,
                    Arguments = string.Format("/nologo /verbosity:normal \"{0}\"", tempFile),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    try { File.Delete(tempFile); } catch { }

                    bool success = stdout.Contains("Tests execution succeeded") || stdout.Contains("Passed: 1");
                    
                    return "{\"status\":\"" + (success ? "Success" : "Failed") + "\", \"output\":\"" + 
                        CommandDispatcher.EscapeJsonString(stdout) + "\", \"errors\":\"" + 
                        CommandDispatcher.EscapeJsonString(stderr) + "\"}";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format("Test execution error: {0}", ex.Message));
                return "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
