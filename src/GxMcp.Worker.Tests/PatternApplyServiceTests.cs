using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // PatternApplyService is the W2 surface for IDE 'Right-click → Apply Pattern'.
    // The live SDK path requires Artech.Packages.Patterns.dll + a WorkWithPlus
    // license + an open KB; the unit suite covers everything reachable through
    // the IPatternEngineAdapter seam, calling the real service via InternalsVisibleTo.
    //
    //  - pattern unavailable (no license / dll missing) → "pattern_unavailable"
    //  - unknown pattern key (not a GUID, not in registry) → "pattern_unavailable"
    //  - object not found → reuses McpResponse.Error not-found shape
    //  - happy-path first apply → status=Success, wasFirstApply=true
    //  - reapply with existing instance → status=Success, wasFirstApply=false
    //  - reapply when no instance exists → falls back to first-apply
    //  - engine throws → surfaced as Error envelope (not bubbled)
    //
    // Real end-to-end apply on a live KB is gated on Skip="no WWP license".
    public class PatternApplyServiceTests
    {
        private const string ObjName = "SomeTransaction";
        private static readonly Guid WWP = PatternApplyService.WorkWithPlusPatternId;

        private class FakeEngine : IPatternEngineAdapter
        {
            public object DefinitionToReturn { get; set; } = new object();
            public object ExistingInstance { get; set; }
            public Func<JObject, PatternApplyResult> ApplyImpl { get; set; }
            public Func<JObject, PatternApplyResult> ReapplyImpl { get; set; }

            public int ApplyCalls;
            public int ReapplyCalls;

            public object GetPatternDefinition(Guid patternId) => DefinitionToReturn;
            public object GetPatternInstance(KBObject parent, Guid patternId) => ExistingInstance;

            public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
            {
                ApplyCalls++;
                return ApplyImpl != null
                    ? ApplyImpl(settings)
                    : new PatternApplyResult { GeneratedObjects = new List<string> { "Generated1", "Generated2" } };
            }

            public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
            {
                ReapplyCalls++;
                return ReapplyImpl != null
                    ? ReapplyImpl(settings)
                    : new PatternApplyResult { GeneratedObjects = new List<string> { "Regenerated" } };
            }
        }

        // Builds a service whose object resolver returns the supplied KBObject (or null).
        // We pass null for the KBObject in tests; the fake engine never dereferences it
        // and PatternApplyService.ApplyPatternToObject tolerates null via objectNameForResponse.
        private static PatternApplyService MakeService(IPatternEngineAdapter engine, KBObject objToReturn)
        {
            return new PatternApplyService(null, engine, name => objToReturn);
        }

        [Fact]
        public void ApplyPattern_NoLicense_ReturnsPatternUnavailable()
        {
            var engine = new FakeEngine { DefinitionToReturn = null };
            // Object IS resolved (non-null) so we hit the engine probe path. But
            // we can't easily build a KBObject, so call the internal pipeline directly.
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Equal("WorkWithPlus", obj["patternKey"]?.ToString());
            Assert.Contains("license", obj["message"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void ApplyPattern_UnknownKey_ReturnsPatternUnavailable()
        {
            // The public ApplyPattern parses the key before resolving objects, so
            // unknown keys short-circuit even with a null _objectService.
            var engine = new FakeEngine();
            var svc = new PatternApplyService(null, engine, name => null);

            string json = svc.ApplyPattern(ObjName, "NotARealPatternKey");
            var obj = JObject.Parse(json);

            Assert.Equal("pattern_unavailable", obj["status"]?.ToString());
            Assert.Equal("NotARealPatternKey", obj["patternKey"]?.ToString());
        }

        [Fact]
        public void ApplyPattern_ObjectNotFound_ReturnsError()
        {
            // findObjectOverride returns null and _objectService is null → fallback
            // McpResponse.Error("Object not found") branch (no SearchIndex needed).
            var engine = new FakeEngine();
            var svc = new PatternApplyService(null, engine, name => null);

            string json = svc.ApplyPattern(ObjName, "WorkWithPlus");
            var obj = JObject.Parse(json);

            Assert.Equal("Error", obj["status"]?.ToString());
            Assert.Contains("not found", obj["error"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, engine.ApplyCalls);
        }

        [Fact]
        public void ApplyPattern_HappyPath_FirstApply_CallsApplyOnce()
        {
            var engine = new FakeEngine
            {
                ExistingInstance = null,
                ApplyImpl = _ => new PatternApplyResult { GeneratedObjects = new List<string> { "WWAlpha", "WWBeta" } }
            };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", new JObject { ["foo"] = "bar" }, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.True(obj["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(1, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);

            var generated = (JArray)obj["generatedObjects"];
            Assert.Equal(2, generated.Count);
            Assert.Contains("WWAlpha", generated.ToObject<List<string>>());
        }

        [Fact]
        public void ApplyPattern_ExistingInstance_TriggersReapply()
        {
            var engine = new FakeEngine { ExistingInstance = new object() };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.False(obj["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(0, engine.ApplyCalls);
            Assert.Equal(1, engine.ReapplyCalls);
        }

        [Fact]
        public void Reapply_WithExistingInstance_TakesReapplyPath()
        {
            var engine = new FakeEngine { ExistingInstance = new object() };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: true, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.False(obj["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(1, engine.ReapplyCalls);
        }

        [Fact]
        public void Reapply_WithoutExistingInstance_FallsBackToFirstApply()
        {
            var engine = new FakeEngine { ExistingInstance = null };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: true, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("Success", obj["status"]?.ToString());
            Assert.True(obj["wasFirstApply"]?.ToObject<bool>());
            Assert.Equal(1, engine.ApplyCalls);
            Assert.Equal(0, engine.ReapplyCalls);
        }

        [Fact]
        public void ApplyPattern_EngineThrows_SurfacesAsErrorEnvelope()
        {
            var engine = new FakeEngine
            {
                ApplyImpl = _ => throw new InvalidOperationException("boom in SDK")
            };
            var svc = MakeService(engine, null);

            string json = svc.ApplyPatternToObject(null, WWP, "WorkWithPlus", null, reapply: false, objectNameForResponse: ObjName);
            var obj = JObject.Parse(json);

            Assert.Equal("Error", obj["status"]?.ToString());
            Assert.Contains("boom", obj["error"]?.ToString() ?? "");
        }

        [Fact(Skip = "no WWP license — exercised by integration smoke against a live KB")]
        public void Integration_FirstApply_WWP_OnRealTransaction_GeneratesObjects()
        {
            // Live happy-path: open KB, find a non-WWP transaction, apply
            // WorkWithPlus through the real ReflectionPatternEngineAdapter.
            // Requires a WorkWithPlus-licensed install + GXMCP_TEST_KB env var.
        }
    }
}
