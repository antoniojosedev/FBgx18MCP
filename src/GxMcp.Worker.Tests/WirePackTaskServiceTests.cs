using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // genexus_wire action=pack_task on a native KB: the pack is built from the SDK-backed index, for
    // exactly the objects asked about, and says what it could not resolve. Every index here is
    // synthetic; no KB, SDK object or file is touched.
    public class WirePackTaskServiceTests
    {
        private static SearchIndex.IndexEntry E(string name, string type, string path = null, string guid = null,
            string[] calls = null, bool enriched = true, string parm = null, string description = null)
            => new SearchIndex.IndexEntry
            {
                Name = name,
                Type = type,
                Guid = guid ?? Guid.NewGuid().ToString(),
                Path = path ?? ("Root Module/" + name),
                Calls = calls != null ? calls.ToList() : new List<string>(),
                IsEnriched = enriched,
                ParmRule = parm,
                Description = description
            };

        private static IndexCacheService BuildIndex(params SearchIndex.IndexEntry[] entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(new SearchIndex.IndexEntry[0]);
            var idx = svc.TryGetLoadedIndex();
            foreach (var e in entries)
            {
                string key = !string.IsNullOrEmpty(e.Guid) ? e.Guid : (e.Type + ":" + e.Name + ":" + e.Path);
                idx.Objects[key] = e;
            }
            return svc;
        }

        private static WirePackTaskService Service(
            IndexCacheService index,
            Func<SearchIndex.IndexEntry, IReadOnlyList<string>> parts = null)
            => new WirePackTaskService(
                index,
                new GraphToolsService(null, null, index, new CallerGraphService(index)),
                parts ?? (e => new[] { "Source", "Rules" }));

        private static JObject Run(WirePackTaskService svc, JObject args) => JObject.Parse(svc.Run(args));

        private static JObject ResultOf(JObject envelope)
        {
            Assert.Equal("ok", envelope["status"]?.ToString());
            return (JObject)envelope["result"];
        }

        private static List<string> Names(JToken array) => array.Select(a => (string)a["name"]).ToList();

        // ---- explicit targets --------------------------------------------------------------

        [Fact]
        public void ExplicitTargets_ResolveToAnchorsWithDirectCallersAndCallees()
        {
            var index = BuildIndex(
                E("Gateway", "Procedure", calls: new[] { "Helper" }),
                E("Helper", "Procedure", parm: "parm(in:&Id);"),
                E("Caller", "WebPanel", calls: new[] { "Helper" }));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Helper") }));

            Assert.Equal("gxwire.pack/v1", result["schema"]?.ToString());
            Assert.Equal("native-sdk", result["engine"]?.ToString());
            Assert.True(result["readOnly"].Value<bool>());
            var anchor = (JObject)result["anchors"].Single();
            Assert.Equal("Helper", anchor["name"]?.ToString());
            Assert.Equal("Procedure", anchor["type"]?.ToString());
            Assert.Equal("targets", anchor["via"]?.ToString());
            Assert.Equal("parm(in:&Id);", anchor["parm_rule"]?.ToString());
            Assert.Equal(new[] { "Caller", "Gateway" }, anchor["callers"].Select(c => (string)c).OrderBy(c => c).ToArray());
            Assert.Equal(2, anchor["callers_total"].Value<int>());
            Assert.Empty(anchor["callees"]);
            Assert.Equal(new[] { "Source", "Rules" }, anchor["parts_listed"].Select(p => (string)p).ToArray());
        }

        [Fact]
        public void TypeQualifiedAndObjectTargets_DisambiguateWithoutAmbiguityReport()
        {
            var index = BuildIndex(
                E("Customer", "Transaction"),
                E("Customer", "WebPanel", path: "Root Module/UI/Customer"));

            var qualified = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("WebPanel:Customer") }));
            Assert.Equal("WebPanel", qualified["anchors"].Single()["type"]?.ToString());
            Assert.Empty(qualified["ambiguous"]);

            var asObject = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray(new JObject { ["name"] = "Customer", ["type"] = "Transaction" })
            }));
            Assert.Equal("Transaction", asObject["anchors"].Single()["type"]?.ToString());
        }

        [Fact]
        public void AmbiguousName_ListsEveryCandidateAndPicksNone()
        {
            var index = BuildIndex(
                E("Customer", "Transaction"),
                E("Customer", "WebPanel", path: "Root Module/UI/Customer"),
                E("Other", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Customer", "Other") }));

            Assert.Equal(new[] { "Other" }, Names(result["anchors"]));
            var ambiguous = (JObject)result["ambiguous"].Single();
            Assert.Equal("Customer", ambiguous["requested"]?.ToString());
            Assert.Equal("AmbiguousName", ambiguous["reason"]?.ToString());
            Assert.Equal(2, ambiguous["candidates"].Count());
            Assert.Contains("Transaction:Customer", ambiguous["hint"]?.ToString());
            Assert.False(result["complete"].Value<bool>());
        }

        [Fact]
        public void UnknownTarget_IsReportedWithNearMatches_AndTheOthersStillResolve()
        {
            var index = BuildIndex(E("SyncInvoice", "Procedure"), E("SyncInvoiceLog", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray("SyncInvoice", "SyncInvoic")
            }));

            Assert.Equal(new[] { "SyncInvoice" }, Names(result["anchors"]));
            var unresolved = (JObject)result["unresolved"].Single();
            Assert.Equal("SyncInvoic", unresolved["requested"]?.ToString());
            Assert.Equal("NotFound", unresolved["reason"]?.ToString());
            Assert.Equal("targets", unresolved["via"]?.ToString());
            Assert.False(result["complete"].Value<bool>());
        }

        [Fact]
        public void UnknownTargetWithNearMatch_SuggestsTheObjectThatContainsTheName()
        {
            var index = BuildIndex(E("InvoiceSyncHelper", "Procedure"), E("Unrelated", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray("InvoiceSyncHelper", "SyncHelper")
            }));

            var suggestions = (JArray)result["unresolved"].Single()["suggestions"];
            Assert.Equal("InvoiceSyncHelper", suggestions.Single()["name"]?.ToString());
        }

        [Fact]
        public void EveryExplicitTargetUnresolved_IsAnErrorThatStillNamesWhatFailed()
        {
            var index = BuildIndex(E("Real", "Procedure"));

            var envelope = Run(Service(index), new JObject { ["targets"] = new JArray("Missing", "AlsoMissing") });

            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("TargetsNotResolved", envelope["error"]?["code"]?.ToString());
            Assert.Equal(2, envelope["unresolved"].Count());
            Assert.Equal("Missing", envelope["unresolved"][0]["requested"]?.ToString());
            // The Gateway trims an error to code/message/hint, so the names must be in the message.
            string message = envelope["error"]?["message"]?.ToString();
            Assert.Contains("'Missing' not found", message);
            Assert.Contains("'AlsoMissing' not found", message);
        }

        [Fact]
        public void EveryTargetAmbiguous_NamesTheTypesInTheErrorMessage()
        {
            var index = BuildIndex(E("Customer", "Transaction"), E("Customer", "WebPanel", path: "Root Module/UI/Customer"));

            var envelope = Run(Service(index), new JObject { ["targets"] = new JArray("Customer") });

            Assert.Equal("TargetsNotResolved", envelope["error"]?["code"]?.ToString());
            Assert.Contains("'Customer' is ambiguous (Transaction/WebPanel)", envelope["error"]?["message"]?.ToString());
            Assert.Contains("Type:Name", envelope["error"]?["hint"]?.ToString());
        }

        [Fact]
        public void ATypoAtTheEndOfTheName_StillSuggestsTheObjectItAimedAt()
        {
            var index = BuildIndex(E("ListInvoices", "Procedure"), E("Unrelated", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray("ListInvoices", "ListInvoicez")
            }));

            var suggestions = (JArray)result["unresolved"].Single()["suggestions"];
            Assert.Equal("ListInvoices", suggestions.Single()["name"]?.ToString());
        }

        [Fact]
        public void TargetsGivenAsASingleString_AreSplitOnCommas()
        {
            var index = BuildIndex(E("First", "Procedure"), E("Second", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = "First, Second" }));

            Assert.Equal(new[] { "First", "Second" }, Names(result["anchors"]));
        }

        [Fact]
        public void TargetsResolvedTwice_ProduceOneAnchor()
        {
            var index = BuildIndex(E("Once", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray("Once", "once", "Procedure:Once"),
                ["task"] = "look at Once"
            }));

            Assert.Equal(new[] { "Once" }, Names(result["anchors"]));
            Assert.Equal("targets", result["anchors"][0]["via"]?.ToString());
        }

        // ---- names taken from the task text ------------------------------------------------

        [Fact]
        public void TaskText_ResolvesWholeIdentifiers_AndReportsTheNameLikeOnesThatMissed()
        {
            var index = BuildIndex(
                E("InvoiceSyncProc", "Procedure"),
                E("Cust_Export", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["task"] = "Verificar os helpers e Patterns de InvoiceSyncProc, Cust_Export e MissingHelperX"
            }));

            Assert.Equal(new[] { "InvoiceSyncProc", "Cust_Export" }, Names(result["anchors"]));
            Assert.All(result["anchors"], a => Assert.Equal("task-text", a["via"]?.ToString()));
            var unresolved = result["unresolved"].Select(u => (string)u["requested"]).ToArray();
            Assert.Equal(new[] { "MissingHelperX" }, unresolved);
            Assert.Contains(result["limitations"], l => (string)l["code"] == "NamesExtractedFromTask");
            Assert.False(result["complete"].Value<bool>());
        }

        [Fact]
        public void TaskText_NeverLooksUpPlainWords()
        {
            var index = BuildIndex(E("RealThing", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["task"] = "Verificar os helpers e Patterns" }));

            Assert.Empty(result["unresolved"]);
            Assert.Empty(result["anchors"]);
            Assert.True(result["empty"].Value<bool>());
            Assert.Contains(result["limitations"], l => (string)l["code"] == "NoObjectMatched");
        }

        [Fact]
        public void TaskText_SkipsStructuralMatches_AndSaysSo()
        {
            var index = BuildIndex(
                E("Balance", "Attribute"),
                E("Billing", "Folder"),
                E("DoBilling", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["task"] = "review Balance and Billing around DoBilling" }));

            Assert.Equal(new[] { "DoBilling" }, Names(result["anchors"]));
            var skipped = result["skipped"].Select(s => (string)s["token"]).OrderBy(t => t).ToArray();
            Assert.Equal(new[] { "Balance", "Billing" }, skipped);
            Assert.All(result["skipped"], s => Assert.Equal("StructuralTypeFromFreeText", s["reason"]?.ToString()));
        }

        [Fact]
        public void ExplicitTarget_IsNeverFilteredByStructuralType()
        {
            var index = BuildIndex(E("Balance", "Attribute"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Balance") }));

            Assert.Equal("Attribute", result["anchors"].Single()["type"]?.ToString());
        }

        [Fact]
        public void TaskText_AmbiguousIdentifier_IsReportedNotGuessed()
        {
            var index = BuildIndex(
                E("ExportOrders", "Procedure"),
                E("ExportOrders", "DataProvider", path: "Root Module/DP/ExportOrders"));

            var result = ResultOf(Run(Service(index), new JObject { ["task"] = "check ExportOrders" }));

            Assert.Empty(result["anchors"]);
            var ambiguous = (JObject)result["ambiguous"].Single();
            Assert.Equal("task-text", ambiguous["via"]?.ToString());
            Assert.Equal(2, ambiguous["candidates"].Count());
        }

        [Fact]
        public void TaskText_ResolvesModuleQualifiedNamesByPath()
        {
            var index = BuildIndex(
                E("Sync", "Procedure", path: "Root Module/Integration/Sync"),
                E("Sync", "Procedure", path: "Root Module/Legacy/Sync"));

            var result = ResultOf(Run(Service(index), new JObject { ["task"] = "open Integration.Sync only" }));

            var anchor = (JObject)result["anchors"].Single();
            Assert.Equal("Root Module/Integration/Sync", anchor["path"]?.ToString());
        }

        // ---- budget and truncation ---------------------------------------------------------

        private static (IndexCacheService Index, JArray Targets) ManyProcedures(int count)
        {
            var entries = Enumerable.Range(0, count)
                .Select(i => E("IntegrationProcedureNumber" + i.ToString("D2"), "Procedure",
                    description: new string('d', 100)))
                .ToArray();
            return (BuildIndex(entries), new JArray(entries.Select(e => e.Name)));
        }

        [Fact]
        public void TokenBudget_StopsAddingAnchorsAndDeclaresEachOmittedObject()
        {
            var (index, targets) = ManyProcedures(8);

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = targets, ["token_budget"] = 450 }));

            int kept = result["anchors"].Count();
            Assert.InRange(kept, 1, 7);
            Assert.True(result["truncated"].Value<bool>());
            var omitted = (JArray)result["omittedTargets"];
            Assert.Equal(8 - kept, omitted.Count);
            Assert.Equal(omitted.Count, result["omitted"].Value<int>());
            Assert.All(omitted, o => Assert.Equal("TokenBudget", o["reason"]?.ToString()));
            Assert.Empty(Names(result["anchors"]).Intersect(Names(omitted)));
            Assert.Contains(result["limitations"], l => (string)l["code"] == "TargetsOmitted");
            Assert.False(result["complete"].Value<bool>());
        }

        [Fact]
        public void TokenBudget_AlwaysKeepsTheFirstAnchor()
        {
            var (index, targets) = ManyProcedures(3);

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = targets, ["token_budget"] = 1 }));

            Assert.Equal("IntegrationProcedureNumber00", result["anchors"][0]["name"]?.ToString());
            Assert.True(result["anchors"].Count() >= 1);
        }

        [Fact]
        public void FullMode_IgnoresTheTokenBudget()
        {
            var (index, targets) = ManyProcedures(8);

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = targets, ["token_budget"] = 450, ["full"] = true
            }));

            Assert.Equal(8, result["anchors"].Count());
            Assert.False(result["truncated"].Value<bool>());
            Assert.Empty(result["omittedTargets"]);
        }

        [Fact]
        public void AnchorCap_DeclaresTheSurplusAsOmitted()
        {
            var (index, targets) = ManyProcedures(14);

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = targets, ["token_budget"] = 20000 }));

            Assert.Equal(12, result["anchors"].Count());
            var omitted = (JArray)result["omittedTargets"];
            Assert.Equal(2, omitted.Count);
            Assert.All(omitted, o => Assert.Equal("TargetsCapped", o["reason"]?.ToString()));
        }

        [Fact]
        public void TimeBudget_StopsBuildingAndDeclaresTheRest()
        {
            var (index, targets) = ManyProcedures(4);
            var svc = Service(index);
            svc.TimeBudgetMs = 0;

            var result = ResultOf(Run(svc, new JObject { ["targets"] = targets, ["token_budget"] = 20000 }));

            Assert.Single(result["anchors"]);
            var omitted = (JArray)result["omittedTargets"];
            Assert.Equal(3, omitted.Count);
            Assert.All(omitted, o => Assert.Equal("TimeBudget", o["reason"]?.ToString()));
        }

        [Fact]
        public void ReferenceLists_AreCappedPerSide_AndFlagTheCut()
        {
            var callers = Enumerable.Range(0, 11).Select(i => E("Client" + i.ToString("D2"), "Procedure", calls: new[] { "Hub" })).ToList();
            var all = new List<SearchIndex.IndexEntry>(callers) { E("Hub", "Procedure") };
            var index = BuildIndex(all.ToArray());

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Hub") }));

            var anchor = (JObject)result["anchors"].Single();
            Assert.Equal(8, anchor["callers"].Count());
            Assert.Equal(11, anchor["callers_total"].Value<int>());
            Assert.True(anchor["callers_truncated"].Value<bool>());
        }

        // ---- relations, trust and consulted sources ----------------------------------------

        [Fact]
        public void StoppingAtDirectRelations_IsNotReportedAsTruncation()
        {
            // Only the direct callers and callees are asked for, so the caller of a caller existing
            // one level up must not make the list look cut.
            var index = BuildIndex(
                E("Outer", "Procedure", calls: new[] { "Middle" }),
                E("Middle", "Procedure", calls: new[] { "Inner" }),
                E("Inner", "Procedure", calls: new[] { "Leaf" }),
                E("Leaf", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Middle") }));

            var anchor = (JObject)result["anchors"].Single();
            Assert.Equal(new[] { "Outer" }, anchor["callers"].Select(c => (string)c).ToArray());
            Assert.Equal(new[] { "Inner" }, anchor["callees"].Select(c => (string)c).ToArray());
            Assert.Null(anchor["callers_truncated"]);
            Assert.Null(anchor["callees_truncated"]);
        }

        [Fact]
        public void Links_ConnectOnlyTheRequestedObjectsThatCallEachOther()
        {
            var index = BuildIndex(
                E("EntryPoint", "Procedure", calls: new[] { "Mapper", "Outsider" }),
                E("Mapper", "Procedure"),
                E("Outsider", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("EntryPoint", "Mapper") }));

            var link = (JObject)result["links"].Single();
            Assert.Equal("EntryPoint", link["from"]?.ToString());
            Assert.Equal("Mapper", link["to"]?.ToString());
            Assert.Equal("calls", link["kind"]?.ToString());
        }

        [Fact]
        public void UnenrichedObject_ReportsPartialReferences_NotAConfirmedZero()
        {
            var index = BuildIndex(E("Lonely", "Procedure", enriched: false));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Lonely") }));

            var anchor = (JObject)result["anchors"].Single();
            Assert.NotEqual("complete", anchor["trust"]["callers"]?.ToString());
            Assert.False(anchor["callers_read"].Value<bool>());
            var limitation = result["limitations"].First(l => (string)l["code"] == "ReferencesPartial" && (string)l["side"] == "callers");
            Assert.Equal("Lonely", limitation["object"]?.ToString());
            Assert.Contains("not a confirmed zero", limitation["message"]?.ToString());
            Assert.False(result["complete"].Value<bool>());
        }

        [Fact]
        public void Consulted_NamesTheSourcesAndStatesNoPartBodyWasRead()
        {
            var index = BuildIndex(E("Subject", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray("Subject") }));

            var consulted = (JObject)result["consulted"];
            var sources = consulted["sources"].Select(s => (string)s).ToArray();
            Assert.Contains("search-index:identity", sources);
            Assert.Contains("search-index:edges", sources);
            Assert.Contains("sdk:availableParts", sources);
            Assert.Empty(consulted["partBodiesRead"]);
            Assert.Equal(1, consulted["objectsResolved"].Value<int>());
            Assert.Contains(result["limitations"], l => (string)l["code"] == "PartBodiesNotRead");
        }

        [Fact]
        public void PartsListingFailure_IsALimitationOfThatObjectOnly()
        {
            var index = BuildIndex(E("Fine", "Procedure"), E("Broken", "Procedure"));
            var svc = Service(index, e =>
            {
                if (e.Name == "Broken") throw new InvalidOperationException("sdk exploded: secret details");
                return new[] { "Source" };
            });

            var result = ResultOf(Run(svc, new JObject { ["targets"] = new JArray("Fine", "Broken") }));

            Assert.Equal(2, result["anchors"].Count());
            Assert.NotNull(result["anchors"][0]["parts_listed"]);
            Assert.Null(result["anchors"][1]["parts_listed"]);
            var limitation = result["limitations"].Single(l => (string)l["code"] == "PartsListingUnavailable");
            Assert.Equal("Broken", limitation["object"]?.ToString());
            Assert.Contains("InvalidOperationException", limitation["message"]?.ToString());
            Assert.DoesNotContain("secret details", limitation["message"]?.ToString());
        }

        [Fact]
        public void PartsListingReturningNull_IsDeclaredAsUnavailable()
        {
            var index = BuildIndex(E("Ghost", "Procedure"));

            var result = ResultOf(Run(Service(index, e => null), new JObject { ["targets"] = new JArray("Ghost") }));

            Assert.Contains(result["limitations"], l => (string)l["code"] == "PartsListingUnavailable");
            Assert.DoesNotContain("sdk:availableParts", result["consulted"]["sources"].Select(s => (string)s));
        }

        [Fact]
        public void WithoutAnSdkReader_PartsAreDeclaredUnavailable_AndTheRestStillWorks()
        {
            var index = BuildIndex(E("Plain", "Procedure"));
            var svc = new WirePackTaskService(index, new GraphToolsService(null, null, index, new CallerGraphService(index)), (Func<SearchIndex.IndexEntry, IReadOnlyList<string>>)null);

            var result = ResultOf(Run(svc, new JObject { ["targets"] = new JArray("Plain") }));

            Assert.Equal("Plain", result["anchors"].Single()["name"]?.ToString());
            Assert.Contains(result["limitations"], l => (string)l["code"] == "PartsListingUnavailable");
        }

        // ---- refusals ----------------------------------------------------------------------

        [Fact]
        public void NeitherTaskNorTargets_IsTaskRequired()
        {
            var envelope = Run(Service(BuildIndex(E("Any", "Procedure"))), new JObject());

            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("TaskRequired", envelope["error"]?["code"]?.ToString());
        }

        [Fact]
        public void NoIndex_IsIndexNotReady()
        {
            var svc = new WirePackTaskService(null, null, (Func<SearchIndex.IndexEntry, IReadOnlyList<string>>)null);

            var envelope = Run(svc, new JObject { ["targets"] = new JArray("Anything") });

            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("IndexNotReady", envelope["error"]?["code"]?.ToString());
        }

        [Theory]
        [InlineData("callers")]
        [InlineData("impact")]
        [InlineData("slice")]
        public void OtherWireActions_AreRefusedWithTheNativeAlternatives(string action)
        {
            var envelope = Run(Service(BuildIndex(E("Any", "Procedure"))), new JObject
            {
                ["action"] = action, ["targets"] = new JArray("Any")
            });

            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("UnsupportedNativeAction", envelope["error"]?["code"]?.ToString());
            Assert.Contains("genexus_object_context", envelope["error"]?["hint"]?.ToString());
        }

        [Theory]
        [InlineData("pack_task")]
        [InlineData("pack-task")]
        [InlineData("PACK_TASK")]
        public void PackTaskSpellings_AreAccepted(string action)
        {
            var index = BuildIndex(E("Known", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject { ["action"] = action, ["targets"] = new JArray("Known") }));

            Assert.Equal("Known", result["anchors"].Single()["name"]?.ToString());
        }

        [Fact]
        public void MalformedNumericArguments_FallBackToDefaults()
        {
            var index = BuildIndex(E("Known", "Procedure"));

            var result = ResultOf(Run(Service(index), new JObject
            {
                ["targets"] = new JArray("Known"), ["token_budget"] = "lots", ["full"] = "yes"
            }));

            Assert.Equal(1200, result["budget"].Value<int>());
            Assert.Equal("Known", result["anchors"].Single()["name"]?.ToString());
        }

        [Fact]
        public void EstimatedTokens_ReflectTheAnswerThatWasBuilt()
        {
            var (index, targets) = ManyProcedures(3);

            var small = ResultOf(Run(Service(index), new JObject { ["targets"] = new JArray(targets[0]) }));
            var larger = ResultOf(Run(Service(index), new JObject { ["targets"] = targets, ["token_budget"] = 20000 }));

            Assert.True(small["est_tokens"].Value<int>() > 0);
            Assert.True(larger["est_tokens"].Value<int>() > small["est_tokens"].Value<int>());
        }

        // ---- wiring ------------------------------------------------------------------------

        [Fact]
        public void TheDispatcherRoutesTheWireModuleToThePackTaskService()
        {
            // The Gateway sends the call with method "Wire" (see GxwireNativePackTaskTests); a Worker without
            // that entry answers UnknownMethodOrAction, which no hermetic test of the service can notice.
            string path = Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "CommandDispatcher.cs");
            string code = File.ReadAllText(path);

            Assert.Matches(@"\[""wire""\]\s*=\s*\([^)]*\)\s*=>\s*_wirePackTaskService\.Run\(", code);
            Assert.Matches(@"_wirePackTaskService\s*=\s*new\s+WirePackTaskService\(", code);
        }

        // ---- read-only by construction -----------------------------------------------------

        [Fact]
        public void TheServiceSourceReachesNoWriteBuildOrExecutionSurface()
        {
            // pack_task is a published read-only action: no Specify, Generate, Build, Rebuild, reorg,
            // execution or write may ever be reachable from it. A source-level guard is the only
            // check that also covers a path no test exercises.
            string path = Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "WirePackTaskService.cs");
            string code = File.ReadAllText(path);
            code = Regex.Replace(code, @"^\s*///.*$|^\s*//.*$", string.Empty, RegexOptions.Multiline);
            code = Regex.Replace(code, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");

            string[] forbidden =
            {
                "WriteService", "PatchService", "BuildService", "MutationEngine", "CompilationPipeline", "LifecycleService",
                "BuildPlanService", "RefactorService", "Process", "File", "Directory", "Specify", "Generate", "Rebuild",
                "Reorg", "Save", "Execute", "Build"
            };
            var hits = forbidden.Where(token => Regex.IsMatch(code, @"\b" + token + @"\w*\b")).ToList();

            Assert.True(hits.Count == 0, "WirePackTaskService reached a forbidden surface: " + string.Join(", ", hits));
        }
    }
}
