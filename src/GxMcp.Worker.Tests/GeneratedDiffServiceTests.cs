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
            Assert.Equal("Error", (string)json["status"]!);
        }

        [Fact]
        public void Diff_InvalidAgainst_ReturnsError()
        {
            var svc = new GeneratedDiffService(kbService: null, git: new FakeGitShell());
            var json = JObject.Parse(svc.Diff("Foo", "yesterday"));
            Assert.Equal("Error", (string)json["status"]!);
            Assert.Equal("InvalidAgainst", (string)json["error"]!);
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
    }
}
