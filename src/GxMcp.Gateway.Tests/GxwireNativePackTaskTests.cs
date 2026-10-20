using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // genexus_wire action=pack_task on the native KB the session has open is answered by the Worker
    // from the SDK-backed index instead of being refused with GXWIRE_REQUIRES_TEXT_SOURCE_TREE. This
    // pins exactly which calls leave the Gateway-owned path; everything else must stay on it.
    public class GxwireNativePackTaskTests
    {
        private static TempDir NativeKb()
        {
            var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "GXLOCK"), "");
            File.WriteAllText(Path.Combine(dir.Path, "GX_KB_Sample.mdf"), "");
            return dir;
        }

        private static TempDir TextTree()
        {
            var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "Sync.prc"), "parm(in:&Id);");
            return dir;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("pack_task")]
        [InlineData("pack-task")]
        [InlineData("PACK_TASK")]
        [InlineData("  pack_task ")]
        public void PackTaskOnTheOpenNativeKb_IsAnsweredByTheWorker(string? action)
        {
            using var kb = NativeKb();
            var args = new JObject { ["task"] = "check the helpers" };
            if (action != null) args["action"] = action;

            Assert.True(GxwireNativePackTask.Applies("genexus_wire", args, kb.Path));
        }

        [Fact]
        public void ExplicitTargets_DoNotChangeTheRouting()
        {
            using var kb = NativeKb();

            Assert.True(GxwireNativePackTask.Applies("genexus_wire",
                new JObject { ["targets"] = new JArray("Sync") }, kb.Path));
        }

        [Fact]
        public void AnExplicitDirThatIsTheOpenKb_StillRoutesToTheWorker_EvenSpelledDifferently()
        {
            using var kb = NativeKb();
            string spelled = kb.Path.ToUpperInvariant() + Path.DirectorySeparatorChar;

            Assert.True(GxwireNativePackTask.Applies("genexus_wire",
                new JObject { ["dir"] = spelled }, kb.Path));
        }

        [Theory]
        [InlineData("status")]
        [InlineData("for")]
        [InlineData("callers")]
        [InlineData("impact")]
        [InlineData("path")]
        [InlineData("slice")]
        [InlineData("safe_delete")]
        [InlineData("index")]
        [InlineData("doctor")]
        public void EveryOtherWireAction_StaysOnTheFileBasedPath(string action)
        {
            using var kb = NativeKb();

            Assert.False(GxwireNativePackTask.Applies("genexus_wire",
                new JObject { ["action"] = action, ["target"] = "Sync" }, kb.Path));
        }

        [Fact]
        public void WithoutAnOpenKb_NothingRoutesToTheWorker()
        {
            Assert.False(GxwireNativePackTask.Applies("genexus_wire", new JObject(), null));
            Assert.False(GxwireNativePackTask.Applies("genexus_wire", new JObject(), "  "));
        }

        [Fact]
        public void ATextTreeKb_StaysOnTheFileBasedPath()
        {
            using var tree = TextTree();

            Assert.False(GxwireNativePackTask.Applies("genexus_wire", new JObject(), tree.Path));
        }

        [Fact]
        public void ADirThatIsAnotherFolder_StaysOnTheFileBasedPath_EvenWithANativeKbOpen()
        {
            using var kb = NativeKb();
            using var export = TextTree();

            Assert.False(GxwireNativePackTask.Applies("genexus_wire",
                new JObject { ["dir"] = export.Path }, kb.Path));
        }

        [Fact]
        public void ADirThatIsAnotherNativeKb_IsNotAnsweredWithTheOpenKbsIndex()
        {
            using var open = NativeKb();
            using var other = NativeKb();

            Assert.False(GxwireNativePackTask.Applies("genexus_wire",
                new JObject { ["dir"] = other.Path }, open.Path));
        }

        [Theory]
        [InlineData("genexus_query")]
        [InlineData("genexus_object_context")]
        [InlineData("")]
        [InlineData(null)]
        public void OnlyGenexusWireIsEverRerouted(string? tool)
        {
            using var kb = NativeKb();

            Assert.False(GxwireNativePackTask.Applies(tool, new JObject(), kb.Path));
        }

        [Fact]
        public void NullArguments_AreTheDefaultPackTask()
        {
            using var kb = NativeKb();

            Assert.True(GxwireNativePackTask.Applies("genexus_wire", null, kb.Path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("pack_task")]
        [InlineData("pack-task")]
        public void ARefusedNativePackTask_SaysHowToReachTheSdkPath(string? action)
        {
            // Reached only when the KB is not open (or dir names some other native KB): the refusal keeps
            // its code and text and now also points at opening the KB instead of at a whole-KB export.
            var args = new JObject();
            if (action != null) args["action"] = action;

            var refusal = GxwireNativePackTask.WithPackTaskHint(
                new JObject { ["status"] = "error", ["error"] = "GXWIRE_REQUIRES_TEXT_SOURCE_TREE", ["hint"] = "original" }, args);

            Assert.Equal("GXWIRE_REQUIRES_TEXT_SOURCE_TREE", refusal["error"]?.ToString());
            Assert.Equal("original", refusal["hint"]?.ToString());
            Assert.Contains("genexus_kb action=open", refusal["packTask"]?.ToString());
            Assert.Contains("targets", refusal["packTask"]?.ToString());
        }

        [Theory]
        [InlineData("callers")]
        [InlineData("status")]
        [InlineData("impact")]
        public void ARefusedFileBasedAction_DoesNotPromisePackTask(string action)
        {
            var refusal = GxwireNativePackTask.WithPackTaskHint(
                new JObject { ["error"] = "GXWIRE_REQUIRES_TEXT_SOURCE_TREE" }, new JObject { ["action"] = action });

            Assert.Null(refusal["packTask"]);
        }

        [Fact]
        public void TheDispatcherWiring_FallsThroughForNativePackTask()
        {
            // DispatchToolCallCoreAsync cannot run without a live Gateway, so its call site is pinned at
            // source level: a Gateway that stops asking Applies() sends pack_task back to the refusal.
            string code = File.ReadAllText(FindSource("GxMcp.Gateway", "Program.ToolDispatch.cs"));

            Assert.Matches(
                @"GxwireNativePackTask\.Applies\(tName,\s*tArgs,\s*_currentKb\.Value\?\.Path\)\s*\?\s*null\s*:\s*await\s+TryDispatchGatewayToolAsync\(",
                code);
        }

        [Theory]
        [InlineData("pack_task", true)]
        [InlineData("for", false)]
        public async System.Threading.Tasks.Task TheNativeKbRefusal_CarriesThePackTaskHintOnlyForPackTask(string action, bool expectHint)
        {
            // The refusal is decided before any gxwire process exists (#593); the seams stand in for gxwire.exe.
            using var kb = NativeKb();
            var result = await Program.ExecuteGxwireToolAsyncForTest(new JObject
            {
                ["action"] = action,
                ["task"] = "Sync",
                ["target"] = "Sync",
                ["dir"] = kb.Path
            }, new Program.GxwireToolSeams
            {
                // CI has no gxwire.exe; the refusal must come before any launch anyway.
                LocateExecutable = () => System.IO.Path.Combine(kb.Path, "gxwire.exe"),
                Launch = (_, _, _) => throw new InvalidOperationException("gxwire must not be launched for a native KB"),
                ResolveCachePath = _ => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gxwire-unused.cache")
            });

            Assert.Equal("GXWIRE_REQUIRES_TEXT_SOURCE_TREE", (string?)result["code"] ?? (string?)result["error"]);
            Assert.Equal(expectHint, result["packTask"] != null);
        }

        private static string FindSource(params string[] segments)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName, "src" }.Concat(segments).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate " + string.Join("/", segments) + " above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void NativePackTask_WaitsForTheIndexLikeEveryOtherIndexRead()
        {
            using var kb = NativeKb();
            using var tree = TextTree();

            Assert.True(Program.IsIndexDependentToolForTest("genexus_wire", new JObject { ["action"] = "pack_task" }, kb.Path));
            Assert.True(Program.IsIndexDependentToolForTest("genexus_wire", null, kb.Path));
            // The file-based actions and a text tree never touch the Worker's index.
            Assert.False(Program.IsIndexDependentToolForTest("genexus_wire", new JObject { ["action"] = "callers" }, kb.Path));
            Assert.False(Program.IsIndexDependentToolForTest("genexus_wire", new JObject { ["action"] = "pack_task" }, tree.Path));
            Assert.False(Program.IsIndexDependentToolForTest("genexus_wire", new JObject { ["action"] = "pack_task" }, null));
            Assert.False(Program.IsIndexDependentToolForTest("genexus_wire", new JObject { ["action"] = "pack_task", ["dir"] = tree.Path }, kb.Path));
        }

        [Fact]
        public void TheRoutedCallReachesTheWorkerWireModuleWithEveryArgumentItNeeds()
        {
            // What the Worker receives is the RPC envelope, not the router's object: its `method` is the
            // router's `module`, and anything the router does not carry in `module`/`params` is lost on
            // the wire. A declared tool that no router claims arrives with an empty method and the
            // Worker answers UnknownMethodOrAction, so the whole path is pinned here, envelope included.
            var request = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["name"] = "genexus_wire",
                    ["arguments"] = new JObject
                    {
                        ["action"] = "pack_task",
                        ["task"] = "check the helpers",
                        ["targets"] = new JArray("Sync", "Mapper"),
                        ["token_budget"] = 900,
                        ["full"] = true
                    }
                }
            };

            object? routed = McpRouter.ConvertToolCall(request);

            Assert.NotNull(routed);
            var command = JObject.FromObject(routed!);
            Assert.Equal("Wire", command["module"]?.ToString());

            JObject rpc = Program.BuildWorkerRpcRequest(command, "request-1");
            Assert.Equal("Wire", rpc["method"]?.ToString());
            var args = (JObject)rpc["params"]!["params"]!;
            Assert.Equal("pack_task", args["action"]?.ToString());
            Assert.Equal("check the helpers", args["task"]?.ToString());
            Assert.Equal(new[] { "Sync", "Mapper" }, ((JArray)args["targets"]!).ToObject<string[]>());
            Assert.Equal(900, args["token_budget"]?.Value<int>());
            Assert.True(args["full"]?.Value<bool>());
        }

        [Fact]
        public void NoOtherRouterClaimsGenexusWire()
        {
            // The router-duplicate guard refuses to start when two routers claim a tool; this pins that
            // exactly one (the OperationsRouter) answers for genexus_wire.
            var claimants = new System.Collections.Generic.List<string>();
            var routerTypes = typeof(Routers.IMcpModuleRouter).Assembly.GetTypes()
                .Where(t => typeof(Routers.IMcpModuleRouter).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface
                    && t.GetConstructor(System.Type.EmptyTypes) != null);
            foreach (var type in routerTypes)
            {
                try
                {
                    var router = (Routers.IMcpModuleRouter)System.Activator.CreateInstance(type)!;
                    if (router.ConvertToolCall("genexus_wire", new JObject()) != null) claimants.Add(type.Name);
                }
                catch { }
            }

            Assert.Equal(new[] { "OperationsRouter" }, claimants.ToArray());
        }
    }
}
