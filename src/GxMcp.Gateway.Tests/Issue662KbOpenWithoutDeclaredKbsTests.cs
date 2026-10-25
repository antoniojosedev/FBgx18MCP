using System;
using System.IO;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // https://github.com/lennix1337/Genexus18MCP/issues/662
    // genexus_kb action=open with an explicit path must work when the config declares no
    // Environment.KBs and no default KB; it must never surface the internal
    // "Tool conversion produced no worker command." error.
    [Collection("Gateway route state")]
    public sealed class Issue662KbOpenWithoutDeclaredKbsTests
    {
        [Fact]
        public void NoWorkerCommandError_NamesToolAndActionAndIsActionable()
        {
            var error = Program.BuildNoWorkerCommandError("genexus_kb", new JObject { ["action"] = "open" });

            string message = error["error"]!.ToString();
            Assert.DoesNotContain("Tool conversion produced no worker command", message);
            Assert.Contains("genexus_kb", message);
            Assert.Contains("'open'", message);
            Assert.Equal("TOOL_ROUTE_NOT_FOUND", error["code"]?.ToString());
            Assert.Contains("path=<KB root>", error["hint"]!.ToString());
        }

        [Fact]
        public void NoWorkerCommandError_WithoutAction_StillNamesTheTool()
        {
            var error = Program.BuildNoWorkerCommandError("genexus_doc", null);

            Assert.Contains("Tool 'genexus_doc' produced no Worker command", error["error"]!.ToString());
            Assert.True(string.IsNullOrEmpty(error["action"]?.Value<string>()));
        }

        [Theory]
        [InlineData("strict")]
        [InlineData("legacy")]
        public async Task OpenWithExplicitPath_WithEmptyConfig_OpensTheKb(string resolutionPolicy)
        {
            string directory = Path.Combine(Path.GetTempPath(), "gxmcp-issue662-" + Guid.NewGuid().ToString("N"));
            string kbDirectory = Path.Combine(directory, "uvs");
            Directory.CreateDirectory(kbDirectory);
            File.WriteAllText(Path.Combine(kbDirectory, "uvs.gxw"), "");
            string configPath = Path.Combine(directory, "config.json");
            File.WriteAllText(configPath, "{}");
            var config = new Configuration
            {
                Environment = new EnvironmentConfig { ResolutionPolicy = resolutionPolicy }
            };
            using var state = Program.ConfigureRouteStateForTest(config, configPath, handle =>
            {
                var worker = new WorkerProcess(config, handle);
                worker.SetProcessStateForTest(alive: true, exitConfirmed: false);
                return worker;
            });
            string session = "issue662-" + Guid.NewGuid().ToString("N");

            try
            {
                var response = await Program.ProcessMcpRequest(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "issue662-open",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "genexus_kb",
                        ["arguments"] = new JObject { ["action"] = "open", ["path"] = kbDirectory }
                    }
                }, session);

                string text = response!["result"]!["content"]![0]!["text"]!.ToString();
                Assert.DoesNotContain("Tool conversion produced no worker command", text);
                Assert.False(response["result"]!["isError"]?.Value<bool>() ?? false, text);
                var payload = JObject.Parse(text);
                Assert.Equal("uvs", payload["opened"]?.ToString());
            }
            finally
            {
                Program.ClearSessionSelectedKb(session);
                try { Directory.Delete(directory, true); } catch { }
            }
        }
    }
}
