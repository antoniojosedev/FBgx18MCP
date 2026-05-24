using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Wave-3 SOTA: cross_platform_impact analyzer.
    // Confirms: caller bucketing by surface, divergence detection
    // (required_field_mismatch + validation_rule_only_on_one_side),
    // and that the envelope's _meta.confidence + detector lists are populated.
    public class CrossPlatformImpactAnalyzerTests
    {
        private static IndexCacheService BuildIndex(IEnumerable<SearchIndex.IndexEntry> entries)
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(entries);
            return svc;
        }

        private static AnalyzeService BuildAnalyze(IndexCacheService index) =>
            new AnalyzeService(index, objSvc: null, graph: new CallerGraphService(index));

        [Fact]
        public void Classifier_WebPanelCaller_TaggedAsWeb()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry { Name = "AluCadWebPanel", Type = "WebPanel" }
            });
            Assert.Equal("Web", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("AluCadWebPanel", index.GetIndex()));
        }

        [Fact]
        public void Classifier_SDPanelCaller_TaggedAsSmartDevices()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry { Name = "AluListSD", Type = "SDPanel" }
            });
            Assert.Equal("SmartDevices", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("AluListSD", index.GetIndex()));
        }

        [Fact]
        public void Classifier_Procedure_BothSurfaces_ClassifiedAsBoth()
        {
            // ProcX is called by both a WebPanel and an SDPanel — must report Both.
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry {
                    Name = "ProcX", Type = "Procedure",
                    CalledBy = new List<string> { "WPCaller", "SDCaller" }
                },
                new SearchIndex.IndexEntry { Name = "WPCaller", Type = "WebPanel" },
                new SearchIndex.IndexEntry { Name = "SDCaller", Type = "SDPanel" }
            });
            Assert.Equal("Both", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("ProcX", index.GetIndex()));
        }

        [Fact]
        public void CrossPlatformImpact_OnlyWebCallers_EmitsWebOnlyEnvelope()
        {
            // Target Transaction called by two WebPanels and nothing on SD.
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry {
                    Name = "Aluno", Type = "Transaction",
                    CalledBy = new List<string> { "AluCad", "AluList" }
                },
                new SearchIndex.IndexEntry { Name = "AluCad", Type = "WebPanel" },
                new SearchIndex.IndexEntry { Name = "AluList", Type = "WebPanel" }
            });
            var svc = BuildAnalyze(index);

            // Use internal overload so we can pass a null sourceResolver — divergence
            // detectors then degrade gracefully and confidence drops to "low".
            var mi = typeof(AnalyzeService).GetMethod("CrossPlatformImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                null);
            Assert.NotNull(mi);
            string json = (string)mi.Invoke(svc, new object[] { "Aluno", (Func<string, string, string>)null });

            var o = JObject.Parse(json);
            Assert.Equal("Success", o["status"]?.ToString());
            Assert.Equal("Aluno", o["target"]?["name"]?.ToString());
            Assert.Equal(2, o["summary"]?["webCallers"]?.ToObject<int>());
            Assert.Equal(0, o["summary"]?["sdCallers"]?.ToObject<int>());
            Assert.Equal("low", o["_meta"]?["confidence"]?.ToString());
            Assert.Contains("required_field_mismatch", o["_meta"]?["detectorsRun"]?.ToString());
        }

        [Fact]
        public void CrossPlatformImpact_MixedCallers_BucketsByPlatform()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry {
                    Name = "Aluno", Type = "Transaction",
                    CalledBy = new List<string> { "AluCadWeb", "AluCadSD" }
                },
                new SearchIndex.IndexEntry { Name = "AluCadWeb", Type = "WebPanel" },
                new SearchIndex.IndexEntry { Name = "AluCadSD", Type = "SDPanel" }
            });
            var svc = BuildAnalyze(index);

            var mi = typeof(AnalyzeService).GetMethod("CrossPlatformImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                null);
            string json = (string)mi.Invoke(svc, new object[] { "Aluno", (Func<string, string, string>)null });

            var o = JObject.Parse(json);
            Assert.Equal(1, o["summary"]?["webCallers"]?.ToObject<int>());
            Assert.Equal(1, o["summary"]?["sdCallers"]?.ToObject<int>());
            var web = (JArray)o["platforms"]["Web"]["callers"];
            var sd = (JArray)o["platforms"]["SmartDevices"]["callers"];
            Assert.Equal("AluCadWeb", web[0]["name"]?.ToString());
            Assert.Equal("AluCadSD", sd[0]["name"]?.ToString());
        }

        [Fact]
        public void CrossPlatformImpact_SurfaceGatedRule_FlagsValidationDivergence()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry {
                    Name = "Aluno", Type = "Transaction",
                    CalledBy = new List<string> { "AluCadWeb" }
                },
                new SearchIndex.IndexEntry { Name = "AluCadWeb", Type = "WebPanel" }
            });
            var svc = BuildAnalyze(index);

            // Resolver returns the target's Rules with a surface gate.
            Func<string, string, string> resolver = (n, p) =>
            {
                if (n == "Aluno" && p == "Rules")
                    return "Error('Required') if AluCod.IsEmpty() if &surface = \"Web\";";
                return null;
            };

            var mi = typeof(AnalyzeService).GetMethod("CrossPlatformImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                null);
            string json = (string)mi.Invoke(svc, new object[] { "Aluno", resolver });

            var o = JObject.Parse(json);
            var div = (JArray)o["crossPlatformDivergence"];
            Assert.NotEmpty(div);
            Assert.Equal("validation_rule_only_on_one_side", div[0]["kind"]?.ToString());
            Assert.Equal("gated", div[0]["Web"]?.ToString());
        }

        [Fact]
        public void CrossPlatformImpact_RequiredFieldMismatch_DetectedAcrossSurfaces()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry {
                    Name = "Aluno", Type = "Transaction",
                    CalledBy = new List<string> { "AluCadWeb", "AluCadSD" }
                },
                new SearchIndex.IndexEntry { Name = "AluCadWeb", Type = "WebPanel" },
                new SearchIndex.IndexEntry { Name = "AluCadSD", Type = "SDPanel" }
            });
            var svc = BuildAnalyze(index);

            // Target requires AluCod via `Error(...) if AluCod.IsEmpty()`.
            // Web caller writes AluCod without guard (treats it as required);
            // SD caller guards it (treats it as optional). Detector must flag this.
            Func<string, string, string> resolver = (n, p) =>
            {
                if (n == "Aluno" && p == "Rules")
                    return "Error('Required') if AluCod.IsEmpty();";
                if (n == "AluCadWeb" && p == "Events")
                    return "Event 'Save'\n    AluCod = &AluCodVar\nEndEvent";
                if (n == "AluCadSD" && p == "Events")
                    // Guards on the attribute itself before writing — the analyzer
                    // looks for `if [not] AluCod.IsEmpty()` in the same caller part
                    // alongside an `AluCod =` assignment to bucket as "optional".
                    return "Event 'Save'\n    if not AluCod.IsEmpty()\n        AluCod = &AluCodVar\n    endif\nEndEvent";
                return null;
            };

            var mi = typeof(AnalyzeService).GetMethod("CrossPlatformImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                null);
            string json = (string)mi.Invoke(svc, new object[] { "Aluno", resolver });

            var o = JObject.Parse(json);
            var div = (JArray)o["crossPlatformDivergence"];
            bool found = false;
            foreach (var d in div)
            {
                if (d["kind"]?.ToString() == "required_field_mismatch" && d["field"]?.ToString() == "AluCod")
                {
                    Assert.Equal("required", d["Web"]?.ToString());
                    // SD caller has a guard around the assignment but also assigns —
                    // probe shape will be "optional" (any guard wins when no unguarded
                    // write exists in the same caller set).
                    Assert.Contains(d["SmartDevices"]?.ToString(), new[] { "optional", "mixed" });
                    found = true;
                }
            }
            Assert.True(found, "Expected a required_field_mismatch divergence on AluCod.");
        }

        [Fact]
        public void CrossPlatformImpact_PendingDetectors_Surfaced()
        {
            var index = BuildIndex(new[]
            {
                new SearchIndex.IndexEntry { Name = "Aluno", Type = "Transaction" }
            });
            var svc = BuildAnalyze(index);
            var mi = typeof(AnalyzeService).GetMethod("CrossPlatformImpact",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                null);
            string json = (string)mi.Invoke(svc, new object[] { "Aluno", (Func<string, string, string>)null });
            var o = JObject.Parse(json);
            string pending = o["_meta"]?["detectorsPending"]?.ToString() ?? "";
            Assert.Contains("type_coercion_only_on_one_side", pending);
            Assert.Contains("null_handling_divergence", pending);
        }
    }
}
