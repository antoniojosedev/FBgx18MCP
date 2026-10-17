using System;
using System.Collections.Generic;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // issue #543: `genexus_variable action=add typeName=<SDT>` on an existing object reported
    // a false VariableTypeNotPersisted because the post-save read-back could not parse the
    // EntityKey pair the SDK persists in ATTCUSTOMTYPE (three readers, two element orders).
    // The parser is shared and order-tolerant, and the resolver reports which evidence found
    // the object so a by-name fallback is visible in the receipt instead of silent.
    public class Issue543SdtReferenceResolutionTests
    {
        private const string SdtClassGuid = "2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b";

        [Theory]
        [InlineData("<Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type><Id>42</Id>")]
        [InlineData("<Id>42</Id><Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type>")]
        [InlineData("<Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type>\r\n  <Id>42</Id>")]
        [InlineData("<Id>\n 42 \n</Id>\n<Type>\n 2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b \n</Type>")]
        [InlineData("<Type>2FD3AC3B-6FDD-4A5E-9B4A-27F1EE1A3C1B</Type><Id>42</Id>")]
        [InlineData("<StructureTypeReference><Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type><Id>42</Id></StructureTypeReference>")]
        public void TryParse_AcceptsEitherElementOrderWhitespaceAndGuidCase(string text)
        {
            Assert.True(CustomTypeReferenceParser.TryParse(text, out Guid typeGuid, out int id));
            Assert.Equal(Guid.Parse(SdtClassGuid), typeGuid);
            Assert.Equal(42, id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("MySdt")]
        [InlineData("2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b")]
        [InlineData("<Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type>")]
        [InlineData("<Id>42</Id>")]
        [InlineData("<Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type><Id>abc</Id>")]
        [InlineData("<Type>not-a-guid</Type><Id>42</Id>")]
        public void TryParse_RejectsTextWithoutACompleteReference(string text)
        {
            Assert.False(CustomTypeReferenceParser.TryParse(text, out Guid typeGuid, out int id));
            Assert.Equal(Guid.Empty, typeGuid);
            Assert.Equal(0, id);
        }

        // The ATTCUSTOMTYPE value as the resolver sees it: a Guid property (the token the
        // SDK exposes) and a ToString() (the persisted text), both carrying the same text.
        private sealed class FakeCustomType
        {
            private readonly string _text;
            public FakeCustomType(string text) { _text = text; }
            public string Guid => _text;
            public override string ToString() => _text;
        }

        private sealed class Resolved
        {
            public string Name { get; set; }
        }

        private static Func<string, object> Properties(Dictionary<string, object> values)
        {
            return name => values.TryGetValue(name, out object value) ? value : null;
        }

        private static VariableInjector.BoundTypeLookups<Resolved> Lookups(
            Func<Guid, int, Resolved> fromEntityKey = null, Func<string, Resolved> byTypeName = null)
        {
            return new VariableInjector.BoundTypeLookups<Resolved>
            {
                FromKey = key => key is Guid ? new Resolved { Name = "by-key" } : null,
                FromEntityKey = fromEntityKey ?? ((guid, id) => null),
                ByTypeName = byTypeName ?? (name => null),
                BusinessComponentByName = (name, module) => null
            };
        }

        [Theory]
        [InlineData("<Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type>\r\n<Id>42</Id>")]
        [InlineData("<Id>42</Id><Type>2fd3ac3b-6fdd-4a5e-9b4a-27f1ee1a3c1b</Type>")]
        public void Resolver_PersistedEntityKeyPair_ResolvesAsNativeReference_InEitherOrder(string persisted)
        {
            var sdt = new Resolved { Name = "MySdt" };
            var lookups = Lookups(fromEntityKey: (guid, id) =>
                guid == Guid.Parse(SdtClassGuid) && id == 42 ? sdt : null);
            var properties = Properties(new Dictionary<string, object>
            {
                ["ATTCUSTOMTYPE"] = new FakeCustomType(persisted)
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Same(sdt, bound);
            Assert.Equal(VariableInjector.ResolutionPathNativeReference, resolutionPath);
        }

        [Fact]
        public void Resolver_NoReferenceAndNoDataTypeString_StaysUnresolved_SoTheBindingFailsClosed()
        {
            var sdtGuid = Guid.NewGuid();
            var lookups = Lookups(
                fromEntityKey: (guid, id) => new Resolved { Name = "unexpected" },
                byTypeName: name => new Resolved { Name = "unexpected" });
            var properties = Properties(new Dictionary<string, object>
            {
                ["ATTCUSTOMTYPE"] = new FakeCustomType("")
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Null(bound);
            Assert.Equal(VariableInjector.ResolutionPathNone, resolutionPath);
            // The verifier's verdict for an unresolved binding: VariableTypeNotPersisted.
            Assert.False(VariableInjector.IsNativeObjectBindingParts(sdtGuid, "SDT", null, null, out string failure));
            Assert.Contains("not the requested", failure);
        }

        [Fact]
        public void Resolver_DataTypeStringOnly_ResolvesButIsReportedAsNameEvidence()
        {
            var sdt = new Resolved { Name = "MySdt" };
            var lookups = Lookups(byTypeName: name => name == "MySdt" ? sdt : null);
            var properties = Properties(new Dictionary<string, object>
            {
                ["DataTypeString"] = "MySdt"
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Same(sdt, bound);
            Assert.Equal(VariableInjector.ResolutionPathDataTypeName, resolutionPath);
        }

        [Fact]
        public void Resolver_DataTypeString_IsNotNameEvidenceForANonSdtVariable()
        {
            var lookups = Lookups(byTypeName: name => new Resolved { Name = name });
            var properties = Properties(new Dictionary<string, object>
            {
                ["DataTypeString"] = "Numeric"
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "NUMERIC", null, lookups, out string resolutionPath);

            Assert.Null(bound);
            Assert.Equal(VariableInjector.ResolutionPathNone, resolutionPath);
        }

        [Fact]
        public void Resolver_CustomTypeNameToken_ResolvesByNameAndSaysSo()
        {
            var sdt = new Resolved { Name = "MySdt" };
            var lookups = Lookups(byTypeName: name => name == "MySdt" ? sdt : null);
            var properties = Properties(new Dictionary<string, object>
            {
                ["ATTCUSTOMTYPE"] = new FakeCustomType("MySdt")
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Same(sdt, bound);
            Assert.Equal(VariableInjector.ResolutionPathCustomTypeName, resolutionPath);
        }

        [Fact]
        public void Resolver_NativeKeyWinsOverEveryNameFallback()
        {
            var lookups = Lookups(byTypeName: name => new Resolved { Name = "by-name" });
            var properties = Properties(new Dictionary<string, object>
            {
                ["DataType"] = Guid.NewGuid(),
                ["DataTypeString"] = "MySdt",
                ["ATTCUSTOMTYPE"] = new FakeCustomType("MySdt")
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Equal("by-key", bound.Name);
            Assert.Equal(VariableInjector.ResolutionPathNativeReference, resolutionPath);
        }

        [Fact]
        public void Resolver_ModularBusinessComponent_ResolvesByNameInTheOwnerModule()
        {
            string seenName = null, seenModule = null;
            var bc = new Resolved { Name = "Customer" };
            var lookups = Lookups();
            lookups.BusinessComponentByName = (name, module) =>
            {
                seenName = name; seenModule = module;
                return bc;
            };
            var properties = Properties(new Dictionary<string, object>
            {
                ["DataTypeString"] = "Customer, Sales"
            });

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                properties, "GX_BUSCOMP", "Owner", lookups, out string resolutionPath);

            Assert.Same(bc, bound);
            Assert.Equal("Customer", seenName);
            Assert.Equal("Sales", seenModule);
            Assert.Equal(VariableInjector.ResolutionPathDataTypeName, resolutionPath);
        }

        [Fact]
        public void AllThreePersistedReferenceReaders_ShareTheOrderTolerantParser()
        {
            foreach (var file in new[]
            {
                new[] { "src", "GxMcp.Worker", "Helpers", "VariableInjector.cs" },
                new[] { "src", "GxMcp.Worker", "Helpers", "SdtMemberResolver.cs" },
                new[] { "src", "GxMcp.Worker", "Parsers", "SdtDslParser.cs" }
            })
            {
                string source = RepoSource.WithoutComments(file);
                Assert.Contains("CustomTypeReferenceParser.TryParse(", source);
                Assert.DoesNotContain("<Type>([0-9a-fA-F", source);
                Assert.DoesNotContain("<Id>(\\d+)", source);
            }
        }

        [Fact]
        public void PostSaveReadBack_ReportsTheResolutionPathOnBothOutcomes()
        {
            string source = RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.Variables.cs");
            string body = SourceAssert.MethodBody(source, "private string VerifyObjectReferencesPersisted(");

            Assert.Contains("ResolveBoundTypeObject(variable, persistedPart?.Model, out resolutionPath)", body);
            // Once in the invalid[] entry, once in the verified-binding evidence.
            Assert.Equal(2, SourceAssert.Count(body, "[\"resolutionPath\"] = resolutionPath"));
            Assert.Contains("[\"customType\"] = DescribeCustomType(variable)", body);
            Assert.Contains("bindingEvidence?.Add(", body);
        }
    }
}
