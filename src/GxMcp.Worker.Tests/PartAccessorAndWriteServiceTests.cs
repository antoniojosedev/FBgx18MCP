using GxMcp.Worker;
using GxMcp.Worker.Structure;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PartAccessorAndWriteServiceTests
    {
        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new WriteService(obj);
        }

        [Fact]
        public void ApplySemanticOps_RejectsMissingTarget()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\"}]}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("target", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplySemanticOps_RejectsMissingOps()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"target\":\"Customer\"}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("ops", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplySemanticOps_NoKb_ReportsObjectNotFound()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse(
                "{\"target\":\"Customer\",\"part\":\"Structure\"," +
                "\"ops\":[{\"op\":\"set_attribute\",\"name\":\"X\",\"type\":\"Numeric(8.0)\"}]}");
            string result = ws.ApplySemanticOps(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("not found", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplyJsonPatch_RejectsMissingTarget()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"part\":\"Structure\",\"patch\":[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"x\"}]}");
            string result = ws.ApplyJsonPatch(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("target", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplyJsonPatch_RejectsMissingPart()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"target\":\"Customer\",\"patch\":[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"x\"}]}");
            string result = ws.ApplyJsonPatch(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("part", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplyJsonPatch_RejectsMissingPatch()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse("{\"target\":\"Customer\",\"part\":\"Structure\"}");
            string result = ws.ApplyJsonPatch(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("patch", json["error"]?["message"]?.ToString());
        }

        [Fact]
        public void ApplyJsonPatch_NoKb_ReportsObjectNotFound()
        {
            var ws = BuildIsolatedWriteService();
            var req = JObject.Parse(
                "{\"target\":\"Customer\",\"part\":\"Structure\"," +
                "\"patch\":[{\"op\":\"replace\",\"path\":\"/description\",\"value\":\"x\"}]}");
            string result = ws.ApplyJsonPatch(req);
            var json = JObject.Parse(result);
            Assert.True(json["isError"]?.ToObject<bool>());
            Assert.Equal("usage_error", json["error"]?["code"]?.ToString());
            Assert.Contains("not found", json["error"]?["message"]?.ToString());
        }

        [Theory]
        [InlineData("Events", "Events", true)]
        [InlineData("Events", "Source", false)]
        [InlineData("Events", null, false)]
        [InlineData("Source", "Events", false)]
        [InlineData("Source", "Source", true)]
        [InlineData("Source", null, true)]
        [InlineData("Code", "Events", false)]
        [InlineData("Code", "Source", true)]
        public void MatchesSourcePart_ShouldRespectRequestedPart(string requestedPartName, string sourcePartName, bool expected)
        {
            Assert.Equal(expected, PartAccessor.MatchesSourcePart(requestedPartName, sourcePartName));
        }

        // ── issue #29 — SDPanel (Smart Device Panel) part GUID mapping ─────────────────
        // SDPanel parts are WorkWithDevices virtual projection parts whose GUIDs differ
        // from the Web equivalents. GetPartGuid must return the SD GUIDs, and Source/Events
        // must resolve to SDEvents (the event code), not the generic Web Events/Source GUID.
        [Theory]
        [InlineData("SDPanel", "Source", "144bd5ff-f918-415b-98e6-aca44fed84fa")]
        [InlineData("SDPanel", "Events", "144bd5ff-f918-415b-98e6-aca44fed84fa")]
        [InlineData("SDPanel", "Code", "144bd5ff-f918-415b-98e6-aca44fed84fa")]
        [InlineData("SDPanel", "SDEvents", "144bd5ff-f918-415b-98e6-aca44fed84fa")]
        [InlineData("SDPanel", "Rules", "1b0a32a3-de6d-4be1-a4dd-1b85d3741534")]
        [InlineData("SDPanel", "Variables", "14c4ade7-53f0-4a56-bdfd-843735b66f47")]
        [InlineData("SDPanel", "Layout", "1414ed00-8cc4-4f44-8820-4baf93547173")]
        [InlineData("SDPanel", "SDLayout", "1414ed00-8cc4-4f44-8820-4baf93547173")]
        [InlineData("SDPanel", "Conditions", "163f0d8b-d8ac-4db4-8dd4-de8979f2b5b9")]
        [InlineData("PanelForSD", "Source", "144bd5ff-f918-415b-98e6-aca44fed84fa")]
        public void GetPartGuid_SDPanel_MapsToVirtualPartGuids(string objType, string partName, string expectedGuid)
        {
            Assert.Equal(System.Guid.Parse(expectedGuid), PartAccessor.GetPartGuid(objType, partName));
        }

        [Fact]
        public void GetPartGuid_SDPanel_SourceDiffersFromWebSourceGuid()
        {
            // Regression guard: the SD Events GUID must NOT be the generic Web Events/Source
            // GUID (c44bd5ff-…) — using the Web GUID is exactly what made SDPanel reads
            // fall through to an empty "<Properties />".
            var sd = PartAccessor.GetPartGuid("SDPanel", "Source");
            var web = PartAccessor.GetPartGuid("WebPanel", "Source");
            Assert.NotEqual(web, sd);
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatIdenticalContentAsNoChange()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent", "Event Start\r\nEndEvent"));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatDifferentContentAsChange()
        {
            Assert.False(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent", "Event Start\r\n\tmsg('x')\r\nEndEvent"));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldTreatNullAsEmpty()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite(null, string.Empty));
        }

        [Fact]
        public void IsUnchangedSourceWrite_ShouldIgnoreLineEndingAndTrailingNewlineDifferences()
        {
            Assert.True(WritePolicy.IsUnchangedSourceWrite("Event Start\r\nEndEvent\r\n", "Event Start\nEndEvent"));
        }

        [Theory]
        [InlineData("Events", "Erro", "", true)]
        [InlineData("Events", "Erro, line: 1", "", true)]
        [InlineData("Events", "Error, line: 1", "", true)]
        [InlineData("Source", "Part save failed: Erro", "Erro", true)]
        [InlineData("Code", "", "", true)]
        [InlineData("Rules", "Erro", "", false)]
        [InlineData("Events", "Validation failed", "", false)]
        [InlineData("Events", "Erro", "Detailed SDK message", false)]
        [InlineData("Events", "Erro, line: 1", "Detailed SDK message", false)]
        public void ShouldRetryWithoutPartSave_ShouldOnlyRetryGenericLogicalSourceFailures(string partName, string exceptionMessage, string diagnosticText, bool expected)
        {
            Assert.Equal(expected, WritePolicy.ShouldRetryWithoutPartSave(partName, exceptionMessage, diagnosticText));
        }

        // ── Task 4.2 (v2.3.8) — AddVariable resolver gate ──────────────────────────────
        // The resolver gate executes BEFORE any SDK / KB call, so the unknown-type
        // path is observable without a real KB. The synonym (success) path needs the
        // SDK, so we guard it with the same TypeLoadException / FileNotFoundException
        // pattern used elsewhere (see CallerGraphServiceTests).

        [Fact]
        public void AddVariable_UnknownType_ReturnsUnknownTypeError()
        {
            var ws = BuildIsolatedWriteService();
            string json;
            try
            {
                json = ws.AddVariable("TestProc", "X", "Bogus(99)");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["error"]?["code"]?.ToString());
            Assert.False(string.IsNullOrEmpty(obj["suggestion"]?.ToString()));
            Assert.NotNull(obj["accepted"]);
            Assert.True(obj["accepted"] is JArray arr && arr.Count > 0);
            // Message mentions the offending input and the suggestion.
            Assert.Contains("Bogus", obj["error"]?["message"]?.ToString() ?? "");
        }

        [Fact]
        public void AddVariable_EmptyType_ReturnsUnknownTypeError()
        {
            // Empty/whitespace typeName should also short-circuit to the error envelope
            // rather than falling through to the SDK default (which silently produced
            // a NUMERIC variable in v2.3.7 and earlier).
            var ws = BuildIsolatedWriteService();
            // null typeName is the legacy "no type — use injector default" path; we keep
            // it backward-compatible. So test the explicit-but-bogus case via whitespace.
            string json;
            try
            {
                json = ws.AddVariable("TestProc", "X", "   ");
            }
            catch (System.IO.FileNotFoundException) { return; }
            catch (System.TypeLoadException) { return; }

            // With whitespace, resolver returns Recognized=false because of empty check.
            // But our gate only runs `if (!string.IsNullOrEmpty(typeName))` — whitespace
            // is technically non-empty, so it enters the resolver and trips the gate.
            var obj = JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("UnknownType", obj["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AddVariable_VarChar_PreservesVarCharCanonical()
        {
            // issue #32 item 4: VarChar(120) must resolve to its own canonical VarChar so
            // it persists as eDBType.VARCHAR (not CHARACTER, which forced defensive Trim()
            // against VARCHAR2 columns). The end-to-end SDK path would require a fixture KB;
            // the helper unit tests in VariableTypeResolverTests cover the wider matrix.
            var resolution = GxMcp.Worker.Helpers.VariableTypeResolver.Resolve("VarChar(120)");
            Assert.True(resolution.Recognized);
            // Canonical VarChar (not Character) — this is what maps to eDBType.VARCHAR in
            // VariableInjector.TryParseDbType's alias table, so the var persists as VARCHAR.
            Assert.Equal("VarChar", resolution.CanonicalType);
            Assert.Equal(120, resolution.Length);
        }

        [Fact]
        public void BuildFailureDetails_ShouldDeduplicatePrimaryAndIssueDescriptions()
        {
            var issues = new JArray
            {
                new JObject { ["description"] = "Erro" },
                new JObject { ["description"] = "Object save failed" },
                new JObject { ["description"] = "object save failed" }
            };

            string details = WritePolicy.BuildFailureDetails("Erro", issues);

            Assert.Equal("Erro | Object save failed", details);
        }
    }
}
