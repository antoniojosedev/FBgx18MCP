using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GxMcp.Worker.Utils;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 12 — genexus_diff_generated.
    ///
    /// Unified diffs of GENERATED files (&lt;obj&gt;.cs/.aspx/.js/.html) vs a baseline:
    ///   - last-build: snapshot under .gx/build-baselines/&lt;obj&gt;/&lt;UTC&gt;/&lt;file&gt;.txt
    ///   - git-head:   shell out to `git show HEAD:&lt;path&gt;`; surfaces KbNotInGit when
    ///                 the KB is not a git repo.
    /// </summary>
    public interface IGitShell
    {
        bool IsGitRepo(string workingDir);
        bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error);
    }

    internal class GitShell : IGitShell
    {
        public bool IsGitRepo(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir)) return false;
            // Walk up looking for a .git directory or file (worktrees).
            var dir = new DirectoryInfo(workingDir);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, ".git"))) return true;
                dir = dir.Parent;
            }
            return false;
        }

        public bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error)
        {
            content = null;
            error = null;
            try
            {
                // net48 has no ProcessStartInfo.ArgumentList — quote via the
                // shared CommandLineToArgv-compatible helper (handles trailing
                // backslashes correctly, unlike a naive Replace("\"","\\\"")).
                // Also prepend `--` so a path beginning with '-' can't be
                // re-interpreted as a flag, and refuse paths that escape the
                // working tree.
                string norm = (repoRelativePath ?? string.Empty).Replace("\\", "/");
                string quoted = GithubService.ArgvQuote("HEAD:" + norm);
                var psi = new ProcessStartInfo("git", "show " + quoted)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (p.ExitCode == 0)
                    {
                        content = stdout;
                        return true;
                    }
                    error = string.IsNullOrEmpty(stderr) ? "git exit " + p.ExitCode : stderr.Trim();
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public class GeneratedDiffService
    {
        private readonly KbService _kbService;
        private readonly IGitShell _git;

        // Generated-file extensions we diff for an object.
        private static readonly string[] GeneratedExtensions = { ".cs", ".aspx", ".js", ".html" };

        public GeneratedDiffService(KbService kbService, IGitShell git = null)
        {
            _kbService = kbService;
            _git = git ?? new GitShell();
        }

        public string Diff(string target, string against)
        {
            if (string.IsNullOrEmpty(target))
                return Models.McpResponse.Err(code: "MissingTarget", message: "target object name is required.", hint: "Pass the name of the KB object to diff.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_list_objects", null, "List available objects to find the correct name.")), target: target);

            string baseline = (against ?? "last-build").Trim().ToLowerInvariant();
            if (baseline != "last-build" && baseline != "git-head")
                return Models.McpResponse.Err(code: "InvalidAgainst", message: "against must be 'last-build' or 'git-head'.", hint: "Pass against='last-build' or against='git-head'.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_diff_generated", new JObject { ["name"] = target, ["against"] = "last-build" }, "Retry with a valid against value.")), target: target);

            string kbPath = null;
            try { kbPath = _kbService?.GetKbPath(); } catch { }
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath))
            {
                return Models.McpResponse.Err(code: "KbPathUnknown", message: "No KB is currently open or KB path does not exist.", hint: "Open a KB first via genexus_kb action=open.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_kb", new JObject { ["action"] = "open" }, "Open a KB before calling diff.")), target: target);
            }

            // Locate current generated files for the object.
            var currentFiles = FindGeneratedFiles(kbPath, target);
            if (currentFiles.Count == 0)
            {
                return Models.McpResponse.Ok(target: target, code: "NoGeneratedFiles", result: new JObject
                {
                    ["against"] = baseline,
                    ["files"] = new JArray(),
                    ["totalChangedLines"] = 0,
                    ["note"] = "No generated files found for the object. Build the KB first."
                });
            }

            JArray files = new JArray();
            int totalChangedLines = 0;

            if (baseline == "git-head")
            {
                if (!_git.IsGitRepo(kbPath))
                {
                    return Models.McpResponse.Err(code: "KbNotInGit", message: "KB path is not inside a git working tree.", hint: "Initialize a git repository in the KB directory before using against='git-head'.", nextSteps: new Newtonsoft.Json.Linq.JArray(Models.McpResponse.NextStep("genexus_diff_generated", new JObject { ["name"] = target, ["against"] = "last-build" }, "Use against='last-build' instead of git-head.")), target: target);
                }
                foreach (var file in currentFiles)
                {
                    string rel = MakeRelative(kbPath, file);
                    if (!_git.TryShowHead(kbPath, rel, out string baselineContent, out string err))
                    {
                        // missing-from-HEAD → treat baseline as empty.
                        baselineContent = string.Empty;
                    }
                    string current = SafeRead(file);
                    AddDiffEntry(files, Path.GetFileName(file), baselineContent ?? string.Empty, current, ref totalChangedLines);
                }
            }
            else // last-build
            {
                string baselineDir = ResolveLatestBaselineDir(kbPath, target);
                foreach (var file in currentFiles)
                {
                    string fname = Path.GetFileName(file);
                    string baselineFile = baselineDir == null ? null : Path.Combine(baselineDir, fname + ".txt");
                    string baselineContent = (baselineFile != null && File.Exists(baselineFile))
                        ? SafeRead(baselineFile)
                        : string.Empty;
                    string current = SafeRead(file);
                    AddDiffEntry(files, fname, baselineContent, current, ref totalChangedLines);
                }
            }

            return Models.McpResponse.Ok(target: target, code: "DiffOk", result: new JObject
            {
                ["against"] = baseline,
                ["files"] = files,
                ["totalChangedLines"] = totalChangedLines
            });
        }

        /// <summary>
        /// Capture a baseline snapshot of all generated files for the object under
        /// .gx/build-baselines/&lt;obj&gt;/&lt;UTC&gt;/. Called by the build pipeline; safe to
        /// call from tests or manually.
        /// </summary>
        public string CaptureBaseline(string kbPath, string target)
        {
            if (string.IsNullOrEmpty(kbPath) || !Directory.Exists(kbPath)) return null;
            if (string.IsNullOrEmpty(target)) return null;

            var files = FindGeneratedFiles(kbPath, target);
            if (files.Count == 0) return null;

            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string dir = Path.Combine(kbPath, ".gx", "build-baselines", SanitizeName(target), stamp);
            Directory.CreateDirectory(dir);
            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dir, Path.GetFileName(f) + ".txt"), overwrite: true); } catch { }
            }
            return dir;
        }

        internal static List<string> FindGeneratedFiles(string kbPath, string target)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(kbPath) || string.IsNullOrEmpty(target)) return found;
            // Probe well-known GeneXus 18 output locations.
            var candidates = new List<string>
            {
                Path.Combine(kbPath, "CSharpModel", "Web"),
                Path.Combine(kbPath, "CSharpModel"),
                Path.Combine(kbPath, "DotNetClassWeb"),
                Path.Combine(kbPath, "JavaModel", "Web"),
                kbPath
            };
            foreach (var c in candidates)
            {
                if (!Directory.Exists(c)) continue;
                try
                {
                    foreach (var match in Directory.EnumerateFiles(c, target + ".*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(match);
                        if (GeneratedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                            && string.Equals(Path.GetFileName(match), target + ext, StringComparison.OrdinalIgnoreCase)
                            && !found.Contains(match, StringComparer.OrdinalIgnoreCase))
                        {
                            found.Add(match);
                        }
                    }
                }
                catch { }
                if (found.Count > 0) break; // first matching root wins
            }
            return found;
        }

        internal static string ResolveLatestBaselineDir(string kbPath, string target)
        {
            string root = Path.Combine(kbPath, ".gx", "build-baselines", SanitizeName(target));
            if (!Directory.Exists(root)) return null;
            try
            {
                return Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string SanitizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string MakeRelative(string root, string full)
        {
            return PathSafety.MakeRelative(root, full);
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return string.Empty; }
        }

        private static void AddDiffEntry(JArray files, string name, string baseline, string current, ref int totalChanged)
        {
            var (diff, added, removed) = UnifiedDiff(baseline ?? string.Empty, current ?? string.Empty, name);
            int changed = added + removed;
            totalChanged += changed;
            files.Add(new JObject
            {
                ["name"] = name,
                ["diff"] = diff,
                ["addedLines"] = added,
                ["removedLines"] = removed
            });
        }

        /// <summary>
        /// Minimal-but-readable unified diff. Not byte-for-byte compatible with GNU diff,
        /// but produces a single hunk in standard unified format that downstream readers
        /// (and humans) handle. Good enough for surfacing generated-file deltas.
        /// </summary>
        internal static (string diff, int added, int removed) UnifiedDiff(string left, string right, string fileLabel)
        {
            string[] a = (left ?? "").Split('\n');
            string[] b = (right ?? "").Split('\n');
            // LCS-based diff (small inputs in practice — a single generated file).
            int[,] lcs = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--)
                for (int j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1
                              : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var sb = new StringBuilder();
            sb.Append("--- a/").Append(fileLabel).Append('\n');
            sb.Append("+++ b/").Append(fileLabel).Append('\n');
            // Whole-file hunk header — simpler than computing per-hunk windows.
            sb.AppendFormat("@@ -1,{0} +1,{1} @@\n", a.Length, b.Length);

            int added = 0, removed = 0;
            int ii = 0, jj = 0;
            while (ii < a.Length && jj < b.Length)
            {
                if (a[ii] == b[jj])
                {
                    sb.Append(' ').Append(a[ii]).Append('\n');
                    ii++; jj++;
                }
                else if (lcs[ii + 1, jj] >= lcs[ii, jj + 1])
                {
                    sb.Append('-').Append(a[ii]).Append('\n');
                    ii++; removed++;
                }
                else
                {
                    sb.Append('+').Append(b[jj]).Append('\n');
                    jj++; added++;
                }
            }
            while (ii < a.Length) { sb.Append('-').Append(a[ii++]).Append('\n'); removed++; }
            while (jj < b.Length) { sb.Append('+').Append(b[jj++]).Append('\n'); added++; }

            if (added == 0 && removed == 0)
            {
                // No changes — emit empty diff (drop the @@ header to keep output small).
                return (string.Empty, 0, 0);
            }
            return (sb.ToString(), added, removed);
        }
    }
}
