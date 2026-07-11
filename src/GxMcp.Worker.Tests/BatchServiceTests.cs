using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // DIR-01: BatchService.MultiEdit used to fall back to a legacy top-level
    // parsed["count"] if the canonical parsed["result"]["count"] was absent.
    // BatchEdit (the only producer MultiEdit calls into) has emitted exclusively
    // via McpResponse.Ok/Err since v2.8.0, so that fallback was dead. These tests
    // pin the canonical-only parsing.
    public class BatchServiceTests
    {
        private static BatchService BuildIsolatedBatchService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            var write = new WriteService(obj);
            var patch = new PatchService(obj, write);
            return new BatchService(kb, write, patch, obj);
        }

        [Fact]
        public void MultiEdit_NoKbOpen_ReadsCanonicalCountFromResult()
        {
            var batch = BuildIsolatedBatchService();
            var items = new JArray
            {
                new JObject
                {
                    ["name"] = "NoSuchObject",
                    ["changes"] = new JArray
                    {
                        new JObject { ["part"] = "Source", ["mode"] = "patch", ["content"] = "x", ["operation"] = "Replace" }
                    }
                }
            };

            string result = batch.MultiEdit(items);
            var json = JObject.Parse(result);

            Assert.Equal("ok", json["status"]?.ToString());
            // Each item in `items` is one call to BatchEdit; BatchEdit's own
            // McpResponse.Ok wraps its per-change count under result.count even
            // when the inner patch attempt itself failed (ObjectNotFound) —
            // BatchEdit still counts the attempted change.
            Assert.Equal(1, json["result"]?["totalChanges"]?.ToObject<int>());
        }

        [Fact]
        public void BatchEdit_NoKbOpen_EmitsCanonicalEnvelopeOnly()
        {
            var batch = BuildIsolatedBatchService();
            var changes = new JArray
            {
                new JObject { ["part"] = "Source", ["mode"] = "patch", ["content"] = "x", ["operation"] = "Replace" }
            };

            string result = batch.BatchEdit("NoSuchObject", changes);
            var json = JObject.Parse(result);

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal(1, json["result"]?["count"]?.ToObject<int>());
            // No legacy top-level "count" — canonical shape only.
            Assert.Null(json["count"]);
        }
    }
}
