using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Compatibility;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #587: `genexus_workwith add_filter_attribute` stored `domain` as given.
    // The pattern schema declares it as reference(Domain) and GeneXus 18 exposes the
    // typed `FilterAttributeElement.Domain` setter only (`DomainName` is a read-only
    // projection), so the Domain is now resolved against the KB and applied through
    // the typed reference. These tests are hermetic: the KB lookup is a fake and the
    // SDK element is a plain object with the same property shape.
    public class WorkWithFilterDomainTests
    {
        private static readonly Guid SimOuNaoGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid CustomerNameAttributeGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid ProcedureGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");

        private sealed class FakeDomainObject
        {
            public string Name;
        }

        private sealed class FakeAttributeObject
        {
            public string Name;
        }

        /// Fake KB: records every lookup and resolves by name or GUID within a type.
        private sealed class FakeKb
        {
            public readonly List<KeyValuePair<string, string>> Calls = new List<KeyValuePair<string, string>>();
            private readonly List<WorkWithSdkAdapter.FilterKbObjectRef> _objects = new List<WorkWithSdkAdapter.FilterKbObjectRef>();

            public FakeKb WithDomain(string name, Guid guid)
            {
                _objects.Add(new WorkWithSdkAdapter.FilterKbObjectRef { Name = name, Guid = guid, Type = "Domain", Instance = new FakeDomainObject { Name = name } });
                return this;
            }

            public FakeKb WithAttribute(string name, Guid guid)
            {
                _objects.Add(new WorkWithSdkAdapter.FilterKbObjectRef { Name = name, Guid = guid, Type = "Attribute", Instance = new FakeAttributeObject { Name = name } });
                return this;
            }

            public FakeKb WithProcedure(string name, Guid guid)
            {
                _objects.Add(new WorkWithSdkAdapter.FilterKbObjectRef { Name = name, Guid = guid, Type = "Procedure", Instance = new object() });
                return this;
            }

            public WorkWithSdkAdapter.FilterKbObjectRef Lookup(string nameOrGuid, string typeName)
            {
                Calls.Add(new KeyValuePair<string, string>(nameOrGuid, typeName));
                bool byGuid = Guid.TryParse(nameOrGuid, out var guid);
                return _objects.FirstOrDefault(o =>
                    string.Equals(o.Type, typeName, StringComparison.OrdinalIgnoreCase)
                    && (byGuid ? o.Guid == guid : string.Equals(o.Name, nameOrGuid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private static WorkWithSdkAdapter.FilterDomainResolution ResolvedSimOuNao(string requested = "SimOuNao", string resolvedBy = "name")
        {
            return new WorkWithSdkAdapter.FilterDomainResolution
            {
                Requested = requested,
                Name = "SimOuNao",
                Guid = SimOuNaoGuid,
                ResolvedBy = resolvedBy,
                Instance = new FakeDomainObject { Name = "SimOuNao" }
            };
        }

        // ---- resolution ------------------------------------------------------------

        [Fact]
        public void TryResolveFilterDomain_ByName_ResolvesAgainstKbDomains()
        {
            var kb = new FakeKb().WithDomain("SimOuNao", SimOuNaoGuid);

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain("simounao", kb.Lookup, out var resolution, out var code, out var message);

            Assert.True(ok, message);
            Assert.Null(code);
            Assert.Equal("SimOuNao", resolution.Name);
            Assert.Equal(SimOuNaoGuid, resolution.Guid);
            Assert.Equal("name", resolution.ResolvedBy);
            Assert.Equal("simounao", resolution.Requested);
            Assert.IsType<FakeDomainObject>(resolution.Instance);
            Assert.Equal(new[] { new KeyValuePair<string, string>("simounao", "Domain") }, kb.Calls);
        }

        [Fact]
        public void TryResolveFilterDomain_ByGuid_ResolvesAndReportsGuidRoute()
        {
            var kb = new FakeKb().WithDomain("SimOuNao", SimOuNaoGuid);

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain(SimOuNaoGuid.ToString(), kb.Lookup, out var resolution, out _, out var message);

            Assert.True(ok, message);
            Assert.Equal("SimOuNao", resolution.Name);
            Assert.Equal("guid", resolution.ResolvedBy);
            Assert.Single(kb.Calls);
            Assert.Equal("Domain", kb.Calls[0].Value);
        }

        [Fact]
        public void TryResolveFilterDomain_CanonicalGuidNameForm_TriesGuidThenName()
        {
            var kb = new FakeKb().WithDomain("SimOuNao", SimOuNaoGuid);

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain("44444444-4444-4444-4444-444444444444-SimOuNao", kb.Lookup, out var resolution, out _, out var message);

            Assert.True(ok, message);
            Assert.Equal("SimOuNao", resolution.Name);
            Assert.Equal("name", resolution.ResolvedBy);
            Assert.Equal(2, kb.Calls.Count);
            Assert.Equal("44444444-4444-4444-4444-444444444444", kb.Calls[0].Key);
            Assert.Equal("SimOuNao", kb.Calls[1].Key);
        }

        [Fact]
        public void TryResolveFilterDomain_NotFound_ReturnsFilterDomainNotFoundNamingWhatWasTried()
        {
            var kb = new FakeKb().WithDomain("SimOuNao", SimOuNaoGuid);

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain("DomainThatDoesNotExist", kb.Lookup, out var resolution, out var code, out var message);

            Assert.False(ok);
            Assert.Null(resolution);
            Assert.Equal("FilterDomainNotFound", code);
            Assert.Contains("DomainThatDoesNotExist", message);
            Assert.Contains("name 'DomainThatDoesNotExist'", message);
        }

        [Fact]
        public void TryResolveFilterDomain_GuidOfNonDomainObject_IsNotFound()
        {
            // The lookup is type-filtered: a GUID that belongs to a Procedure never
            // resolves as a Domain even though the object exists.
            var kb = new FakeKb().WithProcedure("procPrint", ProcedureGuid);

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain(ProcedureGuid.ToString(), kb.Lookup, out _, out var code, out var message);

            Assert.False(ok);
            Assert.Equal("FilterDomainNotFound", code);
            Assert.Contains("GUID '" + ProcedureGuid + "'", message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryResolveFilterDomain_NoDomainRequested_IsNoOp(string domain)
        {
            var kb = new FakeKb();

            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain(domain, kb.Lookup, out var resolution, out var code, out _);

            Assert.True(ok);
            Assert.Null(resolution);
            Assert.Null(code);
            Assert.Empty(kb.Calls);
        }

        [Fact]
        public void TryResolveFilterDomain_WithoutLookup_FailsClosed()
        {
            bool ok = WorkWithSdkAdapter.TryResolveFilterDomain("SimOuNao", null, out var resolution, out var code, out _);

            Assert.False(ok);
            Assert.Null(resolution);
            Assert.Equal("FilterDomainNotFound", code);
        }

        // ---- attribute-vs-variable classification and the advisory -----------------------

        [Theory]
        [InlineData("CustomerName")]
        [InlineData("&CustomerName")]
        [InlineData("  &CustomerName ")]
        public void ResolveFilterAttributeTarget_AcceptsAmpersandAndLooksUpThePlainName(string attribute)
        {
            var kb = new FakeKb().WithAttribute("CustomerName", CustomerNameAttributeGuid);

            var target = WorkWithSdkAdapter.ResolveFilterAttributeTarget(attribute, kb.Lookup);

            Assert.NotNull(target);
            Assert.Equal("CustomerName", target.Name);
            Assert.Equal(new[] { new KeyValuePair<string, string>("CustomerName", "Attribute") }, kb.Calls);
        }

        [Theory]
        [InlineData("TemExistente")]
        [InlineData("&TemExistente")]
        public void ResolveFilterAttributeTarget_NameThatIsNotAnAttribute_IsAVariable(string attribute)
        {
            var kb = new FakeKb().WithAttribute("CustomerName", CustomerNameAttributeGuid);

            Assert.Null(WorkWithSdkAdapter.ResolveFilterAttributeTarget(attribute, kb.Lookup));
        }

        [Fact]
        public void BuildFilterDomainAdvisory_VariableTarget_ReportsAppliesToVariableWithoutWarning()
        {
            var advisory = WorkWithSdkAdapter.BuildFilterDomainAdvisory("TemExistente", null, ResolvedSimOuNao());

            Assert.NotNull(advisory);
            Assert.True(advisory["appliesToVariable"]?.ToObject<bool>());
            Assert.Equal("Variable", advisory["filterAttributeResolvedAs"]?.ToString());
            Assert.Equal("SimOuNao", advisory["domain"]?.ToString());
            Assert.Equal(SimOuNaoGuid.ToString(), advisory["domainGuid"]?.ToString());
            Assert.Equal("TemExistente", advisory["filterAttribute"]?.ToString());
            Assert.Null(advisory["warning"]);
        }

        [Fact]
        public void BuildFilterDomainAdvisory_AttributeTarget_WarnsButIsNotAnError()
        {
            var attributeTarget = new WorkWithSdkAdapter.FilterKbObjectRef { Name = "CustomerName", Guid = CustomerNameAttributeGuid, Type = "Attribute" };

            var advisory = WorkWithSdkAdapter.BuildFilterDomainAdvisory("&CustomerName", attributeTarget, ResolvedSimOuNao());

            Assert.False(advisory["appliesToVariable"]?.ToObject<bool>());
            Assert.Equal("Attribute", advisory["filterAttributeResolvedAs"]?.ToString());
            string warning = advisory["warning"]?.ToString();
            Assert.False(string.IsNullOrEmpty(warning));
            Assert.Contains("SimOuNao", warning);
            Assert.Contains("CustomerName", warning);
            Assert.Contains("only to variables", warning);
            // Advisory, not refusal: no error code travels with it.
            Assert.Null(advisory["code"]);
        }

        [Fact]
        public void BuildFilterDomainAdvisory_WithoutDomain_IsAbsent()
        {
            Assert.Null(WorkWithSdkAdapter.BuildFilterDomainAdvisory("CustomerName", null, null));
        }

        [Fact]
        public void BuildFilterDomainAdvisory_MirrorsSignatureAdvisoryShape()
        {
            // add_action's signatureAdvisory is: inspected facts + one boolean verdict +
            // `warning` only when the verdict is negative. The domain advisory must read
            // the same way so an agent checks `advisory.warning` on both.
            var negative = WorkWithSdkAdapter.BuildFilterDomainAdvisory("CustomerName",
                new WorkWithSdkAdapter.FilterKbObjectRef { Name = "CustomerName", Type = "Attribute" }, ResolvedSimOuNao());
            var positive = WorkWithSdkAdapter.BuildFilterDomainAdvisory("TemExistente", null, ResolvedSimOuNao());

            Assert.Equal(JTokenType.Boolean, negative["appliesToVariable"].Type);
            Assert.Equal(JTokenType.String, negative["warning"].Type);
            Assert.Null(positive["warning"]);
            var factKeys = positive.Properties().Select(p => p.Name).ToList();
            Assert.Equal(new[] { "domain", "domainGuid", "filterAttribute", "filterAttributeResolvedAs", "appliesToVariable" }, factKeys);
        }

        // ---- plan (dryRun) and receipt ---------------------------------------------------

        [Fact]
        public void BuildFilterPlan_WithResolvedDomain_ReportsResolutionAndAdvisory()
        {
            var resolution = ResolvedSimOuNao(requested: SimOuNaoGuid.ToString(), resolvedBy: "guid");
            var advisory = WorkWithSdkAdapter.BuildFilterDomainAdvisory("CustomerName",
                new WorkWithSdkAdapter.FilterKbObjectRef { Name = "CustomerName", Type = "Attribute" }, resolution);

            var plan = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebCustomer", "Root", "CustomerName", null, SimOuNaoGuid.ToString(),
                null, new List<string> { "CustomerName like &CustomerName;" }, resolution, advisory);

            // The plan shows the resolved name, not the GUID the caller typed.
            Assert.Equal("SimOuNao", plan["filterAttribute"]?["domain"]?.ToString());
            Assert.True(plan["validations"]?["domainResolved"]?.ToObject<bool>());
            Assert.False(plan["validations"]?["domainAppliesToVariable"]?.ToObject<bool>());
            Assert.NotNull(plan["domainAdvisory"]);
            Assert.Contains("only to variables", plan["domainAdvisory"]?["warning"]?.ToString());
            Assert.Equal(SimOuNaoGuid.ToString(), plan["domainResolution"]?["requested"]?.ToString());
            Assert.Equal("guid", plan["domainResolution"]?["resolvedBy"]?.ToString());
            Assert.Contains("typed FilterAttributeElement.Domain", plan["domainResolution"]?["applyPolicy"]?.ToString());
            Assert.DoesNotContain("not resolved", plan["validationLimits"]?["domain"]?.ToString());
            Assert.Contains("resolved against KB Domains", plan["validationLimits"]?["domain"]?.ToString());
        }

        [Fact]
        public void BuildFilterPlan_VariableTarget_HasNoWarningAndDeclaresDomainApplies()
        {
            var resolution = ResolvedSimOuNao();
            var advisory = WorkWithSdkAdapter.BuildFilterDomainAdvisory("TemExistente", null, resolution);

            var plan = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebCustomer", "Root", "TemExistente", null, "SimOuNao", null, null, resolution, advisory);

            Assert.True(plan["validations"]?["domainAppliesToVariable"]?.ToObject<bool>());
            Assert.Null(plan["domainAdvisory"]?["warning"]);
        }

        [Fact]
        public void BuildFilterPlan_WithoutDomain_OmitsDomainKeys()
        {
            var plan = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebCustomer", "Root", "CustomerName", null, null, null, null);

            Assert.Null(plan["domainResolution"]);
            Assert.Null(plan["domainAdvisory"]);
            Assert.Null(plan["validations"]?["domainResolved"]);
            Assert.Null(plan["validations"]?["domainAppliesToVariable"]);
        }

        [Fact]
        public void BuildFilterReceipt_CarriesDomainResolutionAdvisoryAndPersistedFormat()
        {
            var observed = new JObject { ["name"] = "CustomerName", ["description"] = "Customer Name", ["domain"] = "SimOuNao" };
            var resolution = ResolvedSimOuNao(requested: SimOuNaoGuid.ToString(), resolvedBy: "guid");
            var advisory = WorkWithSdkAdapter.BuildFilterDomainAdvisory("CustomerName",
                new WorkWithSdkAdapter.FilterKbObjectRef { Name = "CustomerName", Type = "Attribute" }, resolution);

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebCustomer", "Root", observed, new JArray(), "CustomerName", "Customer Name",
                SimOuNaoGuid.ToString(), null, null, WorkWithReadSource.NativeInstance, WorkWithReadSource.NativeInstance, resolution, advisory, domainAppliedThrough: "Domain");

            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.True(receipt["verified"]?.ToObject<bool>());
            Assert.Equal(SimOuNaoGuid.ToString(), receipt["requested"]?["domain"]?.ToString());
            Assert.Equal("SimOuNao", receipt["filterAttribute"]?["domain"]?.ToString());
            Assert.Equal("Domain", receipt["domainResolution"]?["appliedThrough"]?.ToString());
            Assert.Contains("{guid}-Name", receipt["domainResolution"]?["persistedFormat"]?.ToString());
            Assert.Contains("domain", (receipt["verification"]?["fieldsCompared"] as JArray).Select(f => f.ToString()));
            Assert.False(receipt["domainAdvisory"]?["appliesToVariable"]?.ToObject<bool>());
            Assert.NotNull(receipt["domainAdvisory"]?["warning"]);
        }

        [Fact]
        public void BuildFilterReceipt_StringFallback_DeclaresPlainNamePersistence()
        {
            var observed = new JObject { ["name"] = "TemExistente", ["domain"] = "SimOuNao" };

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebCustomer", "Root", observed, new JArray(), "TemExistente", null,
                "SimOuNao", null, null, WorkWithReadSource.NativeInstance, WorkWithReadSource.NativeInstance, ResolvedSimOuNao(), null, domainAppliedThrough: "DomainName");

            Assert.Equal("DomainName", receipt["domainResolution"]?["appliedThrough"]?.ToString());
            Assert.Contains("plain Domain name", receipt["domainResolution"]?["persistedFormat"]?.ToString());
            Assert.Null(receipt["domainAdvisory"]);
        }

        // ---- the SDK surface: typed Domain reference first, string setter as fallback ----

        /// Mirrors GeneXus 18 U16 FilterAttributeElement: Domain is a writable typed
        /// reference, DomainName a read-only projection of it.
        private sealed class TypedAttributeElement
        {
            public FakeDomainObject Domain { get; set; }
            public string DomainName => Domain?.Name;
            public string Description { get; set; }
        }

        /// Hypothetical SDK shape with a string setter only.
        private sealed class StringAttributeElement
        {
            public string DomainName { get; set; }
        }

        /// Shape with both: the typed reference must still win.
        private sealed class BothSettersElement
        {
            public FakeDomainObject Domain { get; set; }
            public string DomainName { get; set; }
        }

        private sealed class NoDomainElement
        {
            public string Description { get; set; }
        }

        [Fact]
        public void SelectDomainSetters_GeneXus18Shape_UsesTypedDomainAndNoStringSetter()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(TypedAttributeElement), out var typed, out var asString);

            Assert.NotNull(typed);
            Assert.Equal("Domain", typed.Name);
            Assert.Equal(typeof(FakeDomainObject), typed.PropertyType);
            Assert.Null(asString);
        }

        [Fact]
        public void SelectDomainSetters_StringOnlyShape_FallsBackToDomainName()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(StringAttributeElement), out var typed, out var asString);

            Assert.Null(typed);
            Assert.NotNull(asString);
            Assert.Equal("DomainName", asString.Name);
        }

        [Fact]
        public void SelectDomainSetters_NoDomainMembers_ResolvesNothing()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(NoDomainElement), out var typed, out var asString);

            Assert.Null(typed);
            Assert.Null(asString);
        }

        [Fact]
        public void TryApplyFilterDomain_TypedReference_SetsTheResolvedDomainInstance()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(TypedAttributeElement), out var typed, out var asString);
            var element = new TypedAttributeElement();
            var resolution = ResolvedSimOuNao();

            bool ok = WorkWithSdkAdapter.TryApplyFilterDomain(typed, asString, element, resolution, out var appliedThrough, out var failure);

            Assert.True(ok, failure);
            Assert.Equal("Domain", appliedThrough);
            Assert.Same(resolution.Instance, element.Domain);
            // What list_filters re-reads: the SDK's DomainName projection of the typed reference.
            Assert.Equal("SimOuNao", element.DomainName);
        }

        [Fact]
        public void TryApplyFilterDomain_PrefersTypedReferenceOverStringSetter()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(BothSettersElement), out var typed, out var asString);
            Assert.NotNull(typed);
            Assert.NotNull(asString);
            var element = new BothSettersElement();

            bool ok = WorkWithSdkAdapter.TryApplyFilterDomain(typed, asString, element, ResolvedSimOuNao(), out var appliedThrough, out _);

            Assert.True(ok);
            Assert.Equal("Domain", appliedThrough);
            Assert.NotNull(element.Domain);
            Assert.Null(element.DomainName);
        }

        [Fact]
        public void TryApplyFilterDomain_StringFallback_WritesTheResolvedName()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(StringAttributeElement), out var typed, out var asString);
            var element = new StringAttributeElement();

            bool ok = WorkWithSdkAdapter.TryApplyFilterDomain(typed, asString, element, ResolvedSimOuNao(requested: SimOuNaoGuid.ToString(), resolvedBy: "guid"), out var appliedThrough, out _);

            Assert.True(ok);
            Assert.Equal("DomainName", appliedThrough);
            Assert.Equal("SimOuNao", element.DomainName);
        }

        [Fact]
        public void TryApplyFilterDomain_TypedReferenceRejectsForeignInstance_FailsWithoutStringSetter()
        {
            WorkWithSdkAdapter.SelectDomainSetters(typeof(TypedAttributeElement), out var typed, out var asString);
            var element = new TypedAttributeElement();
            var foreign = new WorkWithSdkAdapter.FilterDomainResolution { Requested = "X", Name = "X", Guid = Guid.NewGuid(), ResolvedBy = "name", Instance = new object() };

            bool ok = WorkWithSdkAdapter.TryApplyFilterDomain(typed, asString, element, foreign, out var appliedThrough, out var failure);

            Assert.False(ok);
            Assert.Null(appliedThrough);
            Assert.Null(element.Domain);
            Assert.Contains("FilterAttributeElement.Domain", failure);
        }

        [Fact]
        public void TryApplyFilterDomain_NoDomainRequested_IsNoOp()
        {
            var element = new TypedAttributeElement();

            bool ok = WorkWithSdkAdapter.TryApplyFilterDomain(null, null, element, null, out var appliedThrough, out var failure);

            Assert.True(ok);
            Assert.Null(appliedThrough);
            Assert.Null(failure);
        }

        [Fact]
        public void TryAddFilterAttributeNative_WithoutNativeSdk_StillRefusesWhenDomainRequested()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => null;
            try
            {
                bool added = WorkWithSdkAdapter.TryAddFilterAttributeNative(null, "Root", "TemExistente", null, ResolvedSimOuNao(), null, null, out var appliedThrough, out var code, out var message);

                Assert.False(added);
                Assert.Null(appliedThrough);
                Assert.Equal("WorkWithSdkUnavailable", code);
                Assert.Contains("no XML fallback", message);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void VerifyFilterAttributeMatches_UsesTheResolvedDomainName()
        {
            // The re-read reports the plain name; a request made by GUID must be verified
            // against the resolved name, which is what AddFilterAttribute now passes.
            var observed = new JObject { ["name"] = "TemExistente", ["domain"] = "SimOuNao" };

            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, new JArray(), "TemExistente", null, "SimOuNao", null, null, out _));
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, new JArray(), "TemExistente", null, SimOuNaoGuid.ToString(), null, null, out var detail));
            Assert.Contains("Domain mismatch", detail);
        }
    }
}
