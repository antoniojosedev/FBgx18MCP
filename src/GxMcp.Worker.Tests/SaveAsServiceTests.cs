using System;
using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Wave 3 — IDE Save As parity. Tests the SaveAsService orchestration via
    /// an in-memory IObjectCloner so no live KB / SDK is required.
    /// </summary>
    public class SaveAsServiceTests
    {
        private sealed class FakeCloner : SaveAsService.IObjectCloner
        {
            public Dictionary<string, SaveAsService.SourceDescriptor> Sources { get; }
                = new Dictionary<string, SaveAsService.SourceDescriptor>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Existing { get; }
                = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, SaveAsService.PatternInstanceDescriptor> Instances { get; }
                = new Dictionary<string, SaveAsService.PatternInstanceDescriptor>(StringComparer.OrdinalIgnoreCase);

            public List<(string type, string name)> Creates { get; } = new List<(string, string)>();
            public List<(string source, string target, string part)> Clones { get; }
                = new List<(string, string, string)>();
            public List<(string name, string pattern)> Applies { get; } = new List<(string, string)>();

            public string FailOnPart { get; set; }
            public bool FailOnCreate { get; set; }

            public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
            {
                SaveAsService.SourceDescriptor d;
                return Sources.TryGetValue(name, out d) ? d : null;
            }

            public bool TargetExists(string newName) => Existing.Contains(newName);

            public string CreateObject(string type, string newName)
            {
                Creates.Add((type, newName));
                if (FailOnCreate)
                    return "{\"status\":\"Error\",\"error\":\"create blew up\"}";
                Existing.Add(newName);
                return "{\"status\":\"Success\"}";
            }

            public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
            {
                Clones.Add((sourceName, newName, partName));
                if (FailOnPart != null &&
                    string.Equals(FailOnPart, partName, StringComparison.OrdinalIgnoreCase))
                    return "{\"status\":\"Error\",\"error\":\"part write blew up\"}";
                return "{\"status\":\"Success\"}";
            }

            public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName)
            {
                SaveAsService.PatternInstanceDescriptor d;
                return Instances.TryGetValue(sourceName, out d) ? d : null;
            }

            public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance)
            {
                Applies.Add((newName, sourceInstance?.PatternKey));
                return "{\"status\":\"Success\"}";
            }
        }

        private static FakeCloner ClonerWith(string sourceName, string type, params string[] parts)
        {
            var c = new FakeCloner();
            c.Sources[sourceName] = new SaveAsService.SourceDescriptor
            {
                Name = sourceName,
                Type = type,
                Parts = parts
            };
            return c;
        }

        [Fact]
        public void HappyPath_SinglePart_ClonesAllPartsAndReturnsNewName()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Rules", "Variables");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Success", json["status"]?.ToString());
            Assert.Equal("ProcA", json["sourceName"]?.ToString());
            Assert.Equal("ProcACopy", json["created"]?["name"]?.ToString());
            Assert.Equal("Procedure", json["created"]?["type"]?.ToString());
            var partsCloned = (JArray)json["created"]?["partsCloned"];
            Assert.NotNull(partsCloned);
            Assert.Equal(3, partsCloned.Count);
            Assert.Equal("Source", partsCloned[0].ToString());
            Assert.Null(json["patternInstance"]);
            Assert.Single(cloner.Creates);
            Assert.Equal(3, cloner.Clones.Count);
        }

        [Fact]
        public void TargetExists_ReturnsTargetExistsCodeWithHint()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            cloner.Existing.Add("ProcACopy");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Error", json["status"]?.ToString());
            Assert.Equal("TargetExists", json["code"]?.ToString());
            Assert.Contains("genexus_delete_object", json["hint"]?.ToString() ?? "");
            Assert.Empty(cloner.Creates);
            Assert.Empty(cloner.Clones);
        }

        [Fact]
        public void TargetExists_WithOverwriteTrue_StillRefusesButHintMentionsFutureRevision()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            cloner.Existing.Add("ProcACopy");
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "ProcA",
                ["newName"] = "ProcACopy",
                ["overwrite"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("TargetExists", json["code"]?.ToString());
            Assert.Contains("reserved", json["hint"]?.ToString() ?? "");
        }

        [Fact]
        public void SourceMissing_ReturnsNotFound()
        {
            var cloner = new FakeCloner(); // no sources registered
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "Nope", ["newName"] = "NopeCopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Error", json["status"]?.ToString());
            Assert.Equal("NotFound", json["code"]?.ToString());
            Assert.Contains("Nope", json["error"]?.ToString() ?? "");
        }

        [Fact]
        public void IncludePatternInstance_OnNonPatternObject_OmitsPatternBlockNoError()
        {
            var cloner = ClonerWith("WebPanelA", "WebPanel", "Source", "WebForm");
            // No WWP instance registered → FindWwpInstance returns null.
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "WebPanelA",
                ["newName"] = "WebPanelACopy",
                ["includePatternInstance"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Success", json["status"]?.ToString());
            Assert.Null(json["patternInstance"]);
            Assert.Empty(cloner.Applies);
        }

        [Fact]
        public void IncludePatternInstance_OnWwpHost_ClonesPatternAndReturnsBlock()
        {
            var cloner = ClonerWith("CustomerH", "Transaction", "Structure", "Rules");
            cloner.Instances["CustomerH"] = new SaveAsService.PatternInstanceDescriptor
            {
                PatternKey = "WorkWithPlus",
                HostName = "CustomerH"
            };
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "CustomerH",
                ["newName"] = "CustomerHCopy",
                ["includePatternInstance"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Success", json["status"]?.ToString());
            Assert.Equal("Success", json["patternInstance"]?["status"]?.ToString());
            Assert.Equal("WorkWithPlus", json["patternInstance"]?["pattern"]?.ToString());
            Assert.Single(cloner.Applies);
            Assert.Equal("CustomerHCopy", cloner.Applies[0].name);
        }

        [Fact]
        public void DryRun_ReturnsPlanAndNeverCallsCloner()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Rules");
            var svc = new SaveAsService(cloner);

            var args = new JObject
            {
                ["name"] = "ProcA",
                ["newName"] = "ProcACopy",
                ["dryRun"] = true
            };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("DryRun", json["status"]?.ToString());
            Assert.Equal("Procedure", json["plan"]?["createType"]?.ToString());
            Assert.Equal("ProcACopy", json["plan"]?["newName"]?.ToString());
            var parts = (JArray)json["plan"]?["partsToClone"];
            Assert.NotNull(parts);
            Assert.Equal(2, parts.Count);

            // Critically: dispatcher / cloner never called.
            Assert.Empty(cloner.Creates);
            Assert.Empty(cloner.Clones);
            Assert.Empty(cloner.Applies);
        }

        [Fact]
        public void PartFailure_ReturnsPartialFailureWithUndoHint()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source", "Rules", "Variables");
            cloner.FailOnPart = "Rules";
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcACopy" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("PartialFailure", json["status"]?.ToString());
            Assert.Equal("clonePart:Rules", json["failedStep"]?.ToString());
            var done = (JArray)json["completedSteps"];
            Assert.Contains(done, t => t.ToString() == "create:ProcACopy");
            Assert.Contains(done, t => t.ToString() == "clonePart:Source");
            Assert.Contains("genexus_undo", json["hint"]?.ToString() ?? "");
        }

        [Fact]
        public void SameSourceAndNewName_RejectedAsUsageError()
        {
            var cloner = ClonerWith("ProcA", "Procedure", "Source");
            var svc = new SaveAsService(cloner);

            var args = new JObject { ["name"] = "ProcA", ["newName"] = "ProcA" };
            var json = JObject.Parse(svc.SaveAs(args));

            Assert.Equal("Error", json["status"]?.ToString());
            Assert.Equal("usage_error", json["code"]?.ToString());
        }
    }
}
