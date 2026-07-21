using System;
using System.Collections.Generic;
using System.IO;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class GeneratedDiffServiceTests
    {
        private sealed class FakeGitShell : IGitShell
        {
            public bool RepoFlag { get; set; }
            public Dictionary<string, string> HeadByPath { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool IsGitRepo(string workingDir) => RepoFlag;
            public bool TryShowHead(string workingDir, string repoRelativePath, out string content, out string error)
            {
                error = null;
                string norm = repoRelativePath.Replace("\\", "/");
                if (HeadByPath.TryGetValue(norm, out content)) return true;
                content = null;
                error = "not in HEAD";
                return false;
            }
        }

        [Fact]
        public void Diff_MissingTarget_ReturnsError()
        {
            var svc = new GeneratedDiffService(kbService: null, git: new FakeGitShell());
            var json = JObject.Parse(svc.Diff(null, "last-build"));
            Assert.Equal("error", (string)json["status"]!);
        }

        [Fact]
        public void Diff_InvalidAgainst_ReturnsError()
        {
            var svc = new GeneratedDiffService(kbService: null, git: new FakeGitShell());
            var json = JObject.Parse(svc.Diff("Foo", "yesterday"));
            Assert.Equal("error", (string)json["status"]!);
            Assert.Equal("InvalidAgainst", (string)json["error"]?["code"]!);
        }

        [Fact]
        public void UnifiedDiff_NoChange_ReturnsEmpty()
        {
            var (diff, added, removed) = GeneratedDiffService.UnifiedDiff(
                "line1\nline2\n", "line1\nline2\n", "x.cs");
            Assert.Equal(0, added);
            Assert.Equal(0, removed);
            Assert.Equal(string.Empty, diff);
        }

        [Fact]
        public void UnifiedDiff_CountsAddedAndRemoved()
        {
            var (diff, added, removed) = GeneratedDiffService.UnifiedDiff(
                "a\nb\nc\n", "a\nX\nc\nd\n", "x.cs");
            Assert.Equal(2, added);   // X, d
            Assert.Equal(1, removed); // b
            Assert.Contains("--- a/x.cs", diff);
            Assert.Contains("+++ b/x.cs", diff);
            Assert.Contains("-b", diff);
            Assert.Contains("+X", diff);
            Assert.Contains("+d", diff);
        }

        [Fact]
        public void FindGeneratedFiles_LocatesUnderCSharpModelWeb()
        {
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "CSharpModel", "Web");
            Directory.CreateDirectory(web);
            string objName = "Foo";
            File.WriteAllText(Path.Combine(web, objName + ".cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(web, objName + ".aspx"), "<%@ %>");
            File.WriteAllText(Path.Combine(web, "Other.cs"), "class Other {}");
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, objName);
                Assert.Equal(2, files.Count);
            }
            finally
            {
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }

        [Fact]
        public void FindGeneratedFiles_SingleWalk_ExcludesOverMatchesAndUnrelatedFiles()
        {
            // Plan 039: FindGeneratedFiles walks each root once and filters extensions
            // in memory. "Foo.*" is a broad glob, so this proves the precise-filename
            // filter still excludes target.cs.bak and targetX.cs style over-matches.
            string tempKb = Path.Combine(Path.GetTempPath(), "gxmcp-test-" + Guid.NewGuid().ToString("N"));
            string web = Path.Combine(tempKb, "CSharpModel", "Web");
            Directory.CreateDirectory(web);
            string objName = "Foo";
            File.WriteAllText(Path.Combine(web, objName + ".cs"), "class Foo {}");
            File.WriteAllText(Path.Combine(web, objName + ".aspx"), "<%@ %>");
            File.WriteAllText(Path.Combine(web, objName + ".cs.bak"), "stale");
            File.WriteAllText(Path.Combine(web, "FooBar.cs"), "class FooBar {}");
            try
            {
                var files = GeneratedDiffService.FindGeneratedFiles(tempKb, objName);
                Assert.Equal(2, files.Count);
                Assert.Contains(files, f => f.EndsWith("Foo.cs", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(files, f => f.EndsWith("Foo.aspx", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(files, f => f.EndsWith("Foo.cs.bak", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(files, f => f.EndsWith("FooBar.cs", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try { Directory.Delete(tempKb, true); } catch { }
            }
        }
    }
}
