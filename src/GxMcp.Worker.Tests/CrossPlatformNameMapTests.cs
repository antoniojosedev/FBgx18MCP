using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 029: TryFindEntry/ClassifyCallerPlatform resolve via a name->entry map
    // instead of a linear scan of index.Objects. Covers the module-scoped-key case
    // that the bare-name prefix probe misses (the whole point of the fix), plus a
    // regression check that bare-key resolution and Both-surface classification
    // still behave as before.
    public class CrossPlatformNameMapTests
    {
        [Fact]
        public void ClassifyCallerPlatform_BareKeyIndex_ResolvesUnchanged()
        {
            var index = new SearchIndex();
            index.Objects["WebPanel:AluCadWebPanel"] = new SearchIndex.IndexEntry { Name = "AluCadWebPanel", Type = "WebPanel" };

            Assert.Equal("Web", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("AluCadWebPanel", index));
        }

        [Fact]
        public void ClassifyCallerPlatform_ModuleScopedKey_ResolvesViaNameMap()
        {
            // Modular KB: storage key is "Type:module.qualified.Name" but entry.Name
            // is the bare name — the 14-prefix probe ("WebPanel:Foo") misses entirely.
            var index = new SearchIndex();
            index.Objects["WebPanel:my.mod.AluCadWebPanel"] = new SearchIndex.IndexEntry { Name = "AluCadWebPanel", Type = "WebPanel" };

            Assert.Equal("Web", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("AluCadWebPanel", index));
        }

        [Fact]
        public void Analyze_ModuleScopedTarget_ResolvesAndClassifiesCallers()
        {
            var index = new SearchIndex();
            index.Objects["Transaction:my.mod.Aluno"] = new SearchIndex.IndexEntry
            {
                Name = "Aluno",
                Type = "Transaction",
                CalledBy = new List<string> { "AluCadWeb", "AluCadSD" }
            };
            index.Objects["WebPanel:my.mod.AluCadWeb"] = new SearchIndex.IndexEntry { Name = "AluCadWeb", Type = "WebPanel" };
            index.Objects["SDPanel:my.mod.AluCadSD"] = new SearchIndex.IndexEntry { Name = "AluCadSD", Type = "SDPanel" };

            var result = CrossPlatformImpactAnalyzer.Analyze("Aluno", "Transaction", index, targetRulesSource: null, callerSourceResolver: null);

            Assert.Single(result.WebCallers);
            Assert.Single(result.SdCallers);
            Assert.Equal("AluCadWeb", result.WebCallers[0]["name"]?.ToString());
            Assert.Equal("AluCadSD", result.SdCallers[0]["name"]?.ToString());
        }

        [Fact]
        public void ClassifyCallerPlatform_ProcedureReachableFromBothSurfaces_ClassifiedAsBoth()
        {
            var index = new SearchIndex();
            index.Objects["Procedure:ProcX"] = new SearchIndex.IndexEntry
            {
                Name = "ProcX",
                Type = "Procedure",
                CalledBy = new List<string> { "WPCaller", "SDCaller" }
            };
            index.Objects["WebPanel:WPCaller"] = new SearchIndex.IndexEntry { Name = "WPCaller", Type = "WebPanel" };
            index.Objects["SDPanel:SDCaller"] = new SearchIndex.IndexEntry { Name = "SDCaller", Type = "SDPanel" };

            Assert.Equal("Both", CrossPlatformImpactAnalyzer.ClassifyCallerPlatform("ProcX", index));
        }
    }
}
