using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #638: add_grid_variable and the variables[] of `list` read the variables the owning object declares. The
    // earlier code asked each SDK Variable for a Guid to build a "GUID-Name" reference; the SDK's Variable has none (only an
    // integer Id and its name), so every declared variable was dropped, `list` returned variables: [] and add_grid_variable
    // answered GridVariableIdentityNotFound for a variable that was plainly declared. WorkWithPlus keys a grid variable by
    // name and types it, so what is read from the declaration is the name and the type.
    public class WwpGridVariableTypingTests
    {
        // Shaped like Artech.Genexus.Common.Variable: Name, Type (the eDBType name), Length, Decimals, IsCollection,
        // IsStandard - and no Guid.
        private sealed class SdkLikeVariable
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
            public bool IsCollection { get; set; }
            public bool IsStandard { get; set; }
        }

        private static SdkLikeVariable Var(string name, string type, int length = 0, int decimals = 0,
            bool isCollection = false, bool isStandard = false)
            => new SdkLikeVariable { Name = name, Type = type, Length = length, Decimals = decimals, IsCollection = isCollection, IsStandard = isStandard };

        [Fact]
        public void ADeclaredVariableWithoutAGuid_IsStillDeclaredAndTyped()
        {
            List<WwpActionService.DeclaredVariable> declared = WwpActionService.DistinctDeclared(new object[]
            {
                Var("tstcod", "NUMERIC", 6, 0),
                Var("tstnom", "CHARACTER", 30)
            });

            Assert.Equal(new[] { "tstcod", "tstnom" }, declared.Select(v => v.Name));
            JArray listed = WwpActionService.ListedVariables(declared);
            Assert.Equal(2, listed.Count);
            Assert.Equal("Character", (string)listed[1]["basicType"]);
            Assert.Equal(30, (int)listed[1]["length"]);
            Assert.True((bool)listed[1]["gridColumn"]);
            Assert.Equal("Numeric", (string)listed[0]["basicType"]);
            Assert.Equal(0, (int)listed[0]["decimals"]);
            Assert.Null(listed[0]["variableReference"]);
        }

        [Theory]
        [InlineData("NUMERIC", "Numeric")]
        [InlineData("INT", "Numeric")]
        [InlineData("CHARACTER", "Character")]
        [InlineData("VARCHAR", "VarChar")]
        [InlineData("LONGVARCHAR", "LongVarChar")]
        [InlineData("DATE", "Date")]
        [InlineData("DATETIME", "DateTime")]
        [InlineData("Boolean", "Boolean")]
        [InlineData("GUID", "GUID")]
        [InlineData("BITMAP", "Image")]
        [InlineData("VIDEO", "Video")]
        [InlineData("AUDIO", "Audio")]
        [InlineData("BINARYFILE", "Blob")]
        [InlineData("GX_SDT", null)]
        [InlineData("GX_BUSCOMP", null)]
        [InlineData("GX_EXTERNAL_OBJECT", null)]
        [InlineData("", null)]
        public void SdkTypesMapToTheWorkWithPlusBasicTypes(string dbType, string expected)
            => Assert.Equal(expected, WwpActionService.BasicTypeOf(dbType));

        [Fact]
        public void OnlySizedTypesReportALength()
        {
            JArray listed = WwpActionService.ListedVariables(WwpActionService.DistinctDeclared(new object[]
            {
                Var("started", "DATE", 8),
                Var("flag", "Boolean", 1),
                Var("note", "LONGVARCHAR", 2097152)
            }));

            Assert.Null(listed[0]["length"]);
            Assert.Null(listed[1]["length"]);
            Assert.Equal(2097152, (int)listed[2]["length"]);
            Assert.Null(listed[2]["decimals"]);
        }

        [Fact]
        public void AStructuredOrCollectionVariableIsListedButNotUsableAsAColumn()
        {
            JArray listed = WwpActionService.ListedVariables(WwpActionService.DistinctDeclared(new object[]
            {
                Var("lines", "GX_SDT"),
                Var("names", "CHARACTER", 20, isCollection: true)
            }));

            Assert.False((bool)listed[0]["gridColumn"]);
            Assert.Null(listed[0]["basicType"]);
            Assert.False((bool)listed[1]["gridColumn"]);
            Assert.True((bool)listed[1]["isCollection"]);
        }

        [Fact]
        public void StandardVariablesAreNotListedButTheFirstDeclarationOfAnameWins()
        {
            List<WwpActionService.DeclaredVariable> declared = WwpActionService.DistinctDeclared(new object[]
            {
                Var("Pgmname", "CHARACTER", 40, isStandard: true),
                Var("Status", "CHARACTER", 10),
                Var("status", "NUMERIC", 4),
                Var("&Amount", "NUMERIC", 12, 2),
                Var("", "CHARACTER", 1),
                null
            });

            Assert.Equal(new[] { "Pgmname", "Status", "Amount" }, declared.Select(v => v.Name));
            JArray listed = WwpActionService.ListedVariables(declared);
            Assert.Equal(new[] { "Status", "Amount" }, listed.Select(v => (string)v["name"]));
            Assert.Equal("Character", (string)listed[0]["basicType"]);
            Assert.Equal(2, (int)listed[1]["decimals"]);
        }

        [Fact]
        public void ThereIsNoVariableReferenceToDiscover()
        {
            JObject json = WwpActionService.DistinctDeclared(new object[] { Var("tstnom", "CHARACTER", 30) })[0].ToJson();

            Assert.Equal(new[] { "basicType", "gridColumn", "length", "name", "type" },
                json.Properties().Select(p => p.Name).OrderBy(n => n, System.StringComparer.Ordinal));
        }
    }
}
