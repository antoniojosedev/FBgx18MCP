using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class MemoryServiceTests
    {
        private static string NewKb()
        {
            string tmpKb = Path.Combine(Path.GetTempPath(), "gxmcp_mem_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpKb);
            return tmpKb;
        }

        [Fact]
        public void SaveCore_CreatesLiveRecord()
        {
            string kb = NewKb();
            try
            {
                var json = JObject.Parse(MemoryService.SaveCore(kb, "Customer.Email must be unique", "Customer", "Transaction", new[] { "validation" }));
                Assert.Equal("ok", (string)json["status"]!);
                Assert.Equal("MemorySaved", (string)json["code"]!);

                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal("Customer.Email must be unique", (string)live[0]["fact"]!);
                Assert.Equal("Customer", (string)live[0]["objectName"]!);
                Assert.Equal(0, (int)live[0]["hits"]!);
                Assert.False((bool)live[0]["tombstone"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void SaveCore_MissingFact_ReturnsError()
        {
            string kb = NewKb();
            try
            {
                var json = JObject.Parse(MemoryService.SaveCore(kb, "  ", "Customer", "Transaction", null));
                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("MissingFact", (string)json["error"]!["code"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void SaveCore_Dedup_BumpsHitsInsteadOfDuplicating()
        {
            string kb = NewKb();
            try
            {
                MemoryService.SaveCore(kb, "Email is unique", "Customer", "Transaction", new[] { "a" });
                // Same normalized fact (case + whitespace differ), same object → bump.
                var json = JObject.Parse(MemoryService.SaveCore(kb, "  email   IS Unique ", "customer", "Transaction", new[] { "b" }));

                Assert.Equal("MemoryUpdated", (string)json["code"]!);
                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal(1, (int)live[0]["hits"]!);
                // tags union
                var tags = ((JArray)live[0]["tags"]!).Select(t => (string)t!).ToArray();
                Assert.Contains("a", tags);
                Assert.Contains("b", tags);
                // original fact preserved
                Assert.Equal("Email is unique", (string)live[0]["fact"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void RecallCore_ByObjectName_Type_Tag_And_NoFilter()
        {
            string kb = NewKb();
            try
            {
                MemoryService.SaveCore(kb, "fact A", "Customer", "Transaction", new[] { "x" });
                MemoryService.SaveCore(kb, "fact B", "Invoice", "Procedure", new[] { "y" });

                var byName = JObject.Parse(MemoryService.RecallCore(kb, "customer", null, null));
                Assert.Equal(1, (int)byName["result"]!["count"]!);
                Assert.Equal("fact A", (string)((JArray)byName["result"]!["memories"]!)[0]["fact"]!);

                var byType = JObject.Parse(MemoryService.RecallCore(kb, null, "Procedure", null));
                Assert.Equal(1, (int)byType["result"]!["count"]!);
                Assert.Equal("fact B", (string)((JArray)byType["result"]!["memories"]!)[0]["fact"]!);

                var byTag = JObject.Parse(MemoryService.RecallCore(kb, null, null, new[] { "X" }));
                Assert.Equal(1, (int)byTag["result"]!["count"]!);

                var all = JObject.Parse(MemoryService.RecallCore(kb, null, null, null));
                Assert.Equal(2, (int)all["result"]!["count"]!);
                Assert.Equal(2, (int)all["result"]!["total"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ForgetCore_TombstonesRecord()
        {
            string kb = NewKb();
            try
            {
                var saved = JObject.Parse(MemoryService.SaveCore(kb, "forget me", "Customer", "", null));
                string id = (string)saved["result"]!["id"]!;
                Assert.Single(MemoryService.LoadLive(kb));

                var json = JObject.Parse(MemoryService.ForgetCore(kb, id));
                Assert.Equal("MemoryForgotten", (string)json["code"]!);
                Assert.Empty(MemoryService.LoadLive(kb));
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ForgetCore_MissingId_ReturnsError()
        {
            string kb = NewKb();
            try
            {
                var json = JObject.Parse(MemoryService.ForgetCore(kb, ""));
                Assert.Equal("error", (string)json["status"]!);
                Assert.Equal("MissingId", (string)json["error"]!["code"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void LoadLive_KeepsLatestPerId()
        {
            string kb = NewKb();
            try
            {
                // Two bumps of the same fact → 3 lines, 1 live record, latest hits=2.
                MemoryService.SaveCore(kb, "same fact", "Obj", "Transaction", null);
                MemoryService.SaveCore(kb, "same fact", "Obj", "Transaction", null);
                MemoryService.SaveCore(kb, "same fact", "Obj", "Transaction", null);

                string path = Path.Combine(kb, ".gx", "memory", "memory.jsonl");
                Assert.Equal(3, File.ReadAllLines(path).Length);

                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal(2, (int)live[0]["hits"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ListCore_ReturnsAllLiveNewestFirst()
        {
            string kb = NewKb();
            try
            {
                MemoryService.SaveCore(kb, "fact 1", "A", "Transaction", null);
                MemoryService.SaveCore(kb, "fact 2", "B", "Transaction", null);

                var json = JObject.Parse(MemoryService.ListCore(kb));
                Assert.Equal("MemoryListed", (string)json["code"]!);
                Assert.Equal(2, (int)json["result"]!["total"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        // ---- Phase 2 — TakeFreshRelevant / AttachRelevantMemory --------------
        // The dedup set (_surfacedIds) is process-global (by design — session ==
        // worker process lifetime), so every test below uses a unique object
        // name/GUID to avoid cross-test collisions when xunit runs tests in
        // parallel within the same test process.

        [Fact]
        public void TakeFreshRelevant_MatchesByObjectName()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "fact for " + objName, objName, "Transaction", null);

                var result = MemoryService.TakeFreshRelevant(kb, objName, "SomeOtherType");
                Assert.Single(result);
                Assert.Equal("fact for " + objName, (string)result[0]["fact"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void TakeFreshRelevant_MatchesByObjectType()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            string objType = "Type_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "type-scoped fact", objName, objType, null);

                var result = MemoryService.TakeFreshRelevant(kb, "SomeOtherName", objType);
                Assert.Single(result);
                Assert.Equal("type-scoped fact", (string)result[0]["fact"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void TakeFreshRelevant_RespectsCap()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    MemoryService.SaveCore(kb, $"fact {i} for {objName}", objName, "Transaction", null);
                }

                var result = MemoryService.TakeFreshRelevant(kb, objName, "Transaction", max: 5);
                Assert.Equal(5, result.Count);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void TakeFreshRelevant_DedupsAcrossCalls_SecondCallReturnsEmpty()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "one-shot fact for " + objName, objName, "Transaction", null);

                var first = MemoryService.TakeFreshRelevant(kb, objName, "Transaction");
                Assert.Single(first);

                var second = MemoryService.TakeFreshRelevant(kb, objName, "Transaction");
                Assert.Empty(second);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void AttachRelevantMemory_AddsBlockWhenMatchExists()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "attach-test fact for " + objName, objName, "Transaction", null);

                string baseJson = new JObject { ["status"] = "ok", ["name"] = objName }.ToString();
                string attached = MemoryService.AttachRelevantMemory(kb, baseJson, objName, "Transaction");

                var parsed = JObject.Parse(attached);
                Assert.NotNull(parsed["relevantMemory"]);
                Assert.Equal(1, (int)parsed["relevantMemory"]!["count"]!);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void AttachRelevantMemory_OmitsBlockWhenNoMatch()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                string baseJson = new JObject { ["status"] = "ok", ["name"] = objName }.ToString();
                string attached = MemoryService.AttachRelevantMemory(kb, baseJson, objName, "Transaction");

                var parsed = JObject.Parse(attached);
                Assert.Null(parsed["relevantMemory"]);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        // ---- Phase 3 — Consolidate / Promote ----------------------------------

        // SaveCore already dedups an identical normalized fact against the same
        // (objectName, objectType) by bumping hits instead of writing a second live
        // record — so to exercise consolidate's own exact-duplicate merge (two
        // distinct ids that happen to normalize the same, e.g. left over from
        // different sessions/agents), append a second raw line directly.
        private static void AppendRawLine(string kb, string id, string fact, string objectName, string objectType, string[] tags, int hits = 0)
        {
            string path = Path.Combine(kb, ".gx", "memory", "memory.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry = new JObject
            {
                ["id"] = id,
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["objectName"] = objectName,
                ["objectType"] = objectType,
                ["tags"] = new JArray((tags ?? Array.Empty<string>()).Select(t => (JToken)t)),
                ["fact"] = fact,
                ["source"] = "explicit",
                ["hits"] = hits,
                ["supersedes"] = new JArray(),
                ["tombstone"] = false
            };
            File.AppendAllText(path, entry.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
        }

        [Fact]
        public void ConsolidateCore_MergesExactDuplicateFactsWithinScope()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                AppendRawLine(kb, Guid.NewGuid().ToString("N"), "The customer email is unique", objName, "Transaction", new[] { "a" }, hits: 2);
                AppendRawLine(kb, Guid.NewGuid().ToString("N"), "the CUSTOMER   email is unique", objName, "Transaction", new[] { "b" }, hits: 1);

                Assert.Equal(2, MemoryService.LoadLive(kb).Count);

                var json = JObject.Parse(MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: false));
                Assert.Equal("MemoryConsolidated", (string)json["code"]!);
                Assert.Equal(2, (int)json["result"]!["liveBefore"]!);
                Assert.Equal(1, (int)json["result"]!["liveAfter"]!);

                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal(3, (int)live[0]["hits"]!);
                var tags = ((JArray)live[0]["tags"]!).Select(t => (string)t!).ToArray();
                Assert.Contains("a", tags);
                Assert.Contains("b", tags);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ConsolidateCore_SubstringSupersetMerge_AbsorbsShorterFact()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                var shortSaved = JObject.Parse(MemoryService.SaveCore(kb, "Email is unique", objName, "Transaction", null));
                string shortId = (string)shortSaved["result"]!["id"]!;
                AppendRawLine(kb, Guid.NewGuid().ToString("N"), "Email is unique and case-insensitive", objName, "Transaction", null);

                var json = JObject.Parse(MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: false));
                Assert.Equal("MemoryConsolidated", (string)json["code"]!);

                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal("Email is unique and case-insensitive", (string)live[0]["fact"]!);
                var supersedes = ((JArray)live[0]["supersedes"]!).Select(t => (string)t!).ToArray();
                Assert.Contains(shortId, supersedes);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ConsolidateCore_DoesNotAbsorbShortGenericFact()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                // "on" is a raw substring of the longer fact but far too short/generic to
                // be a real duplicate — it must survive consolidation, not be silently
                // absorbed (that would permanently destroy an unrelated memory).
                MemoryService.SaveCore(kb, "on", objName, "Transaction", null);
                AppendRawLine(kb, Guid.NewGuid().ToString("N"), "This behavior depends on the GAM configuration flag", objName, "Transaction", null);

                var json = JObject.Parse(MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: false));
                Assert.Equal("MemoryConsolidated", (string)json["code"]!);

                var live = MemoryService.LoadLive(kb);
                Assert.Equal(2, live.Count);
                Assert.Contains("on", live.Select(r => (string)r["fact"]!));
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ConsolidateCore_LeavesNoTempFile_AndValidJsonl()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "duplicate fact one", objName, "Transaction", null);
                AppendRawLine(kb, Guid.NewGuid().ToString("N"), "duplicate fact one", objName, "Transaction", null);

                MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: false);

                string dir = Path.Combine(kb, ".gx", "memory");
                Assert.False(File.Exists(Path.Combine(dir, "memory.jsonl.tmp")));
                foreach (var line in File.ReadAllLines(Path.Combine(dir, "memory.jsonl")))
                    JObject.Parse(line);   // every surviving line stays valid JSON
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ConsolidateCore_DryRun_DoesNotModifyFile()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "duplicate fact one", objName, "Transaction", null);
                MemoryService.SaveCore(kb, "duplicate fact one", objName, "Procedure", null);

                string path = Path.Combine(kb, ".gx", "memory", "memory.jsonl");
                int linesBefore = File.ReadAllLines(path).Length;

                var json = JObject.Parse(MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: true));
                Assert.Equal("MemoryConsolidationPreview", (string)json["code"]!);
                Assert.NotNull(json["result"]!["merges"]);

                int linesAfter = File.ReadAllLines(path).Length;
                Assert.Equal(linesBefore, linesAfter);
                Assert.Equal(2, MemoryService.LoadLive(kb).Count);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void ConsolidateCore_Compaction_ShrinksFileToOneLinePerLiveRecord()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                // 3 bumps of the same fact → 3 lines, 1 live record (already exercises append growth).
                MemoryService.SaveCore(kb, "fact to compact", objName, "Transaction", null);
                MemoryService.SaveCore(kb, "fact to compact", objName, "Transaction", null);
                MemoryService.SaveCore(kb, "fact to compact", objName, "Transaction", null);
                // A second, unrelated live fact on the same object.
                MemoryService.SaveCore(kb, "another fact", objName, "Transaction", null);

                string path = Path.Combine(kb, ".gx", "memory", "memory.jsonl");
                Assert.Equal(4, File.ReadAllLines(path).Length);
                Assert.Equal(2, MemoryService.LoadLive(kb).Count);

                MemoryService.ConsolidateCore(kb, objName, null, null, dryRun: false);

                int rawLinesAfter = File.ReadAllLines(path).Length;
                Assert.Equal(2, rawLinesAfter);
                Assert.Equal(2, MemoryService.LoadLive(kb).Count);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void Promote_SavesWithFrictionSourceAndTag()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            var memoryService = new MemoryService(null);
            try
            {
                var json = JObject.Parse(memoryService.Promote("agent hit a wall doing X", objName, "Transaction", null, kbPathOverride: kb));
                Assert.Equal("MemorySaved", (string)json["code"]!);

                var live = MemoryService.LoadLive(kb);
                Assert.Single(live);
                Assert.Equal("promoted-from-friction", (string)live[0]["source"]!);
                var tags = ((JArray)live[0]["tags"]!).Select(t => (string)t!).ToArray();
                Assert.Contains("friction", tags);
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }

        [Fact]
        public void AttachRelevantMemory_LeavesErrorEnvelopeUnchanged()
        {
            string kb = NewKb();
            string objName = "Obj_" + Guid.NewGuid().ToString("N");
            try
            {
                MemoryService.SaveCore(kb, "should not attach to error for " + objName, objName, "Transaction", null);

                string errorJson = new JObject { ["status"] = "error", ["error"] = new JObject { ["code"] = "SomeError" } }.ToString();
                string result = MemoryService.AttachRelevantMemory(kb, errorJson, objName, "Transaction");

                Assert.Equal(JObject.Parse(errorJson).ToString(), JObject.Parse(result).ToString());
            }
            finally { try { Directory.Delete(kb, true); } catch { } }
        }
    }
}
