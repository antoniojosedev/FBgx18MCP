using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using FakeWorkWithInstance = Artech.Patterns.WorkWith.WorkWithInstance;

namespace GxMcp.Worker.Tests
{
    public class WorkWithServiceTests
    {
        private const string SampleWorkWithXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"">
          <actions>
            <action name=""Update"" caption=""Update"" />
            <action name=""Delete"" caption=""Delete"" />
          </actions>
        </tab>
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        private const string SampleWorkWithXmlNoActions = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"" />
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        private const string SampleWorkWithXmlWithDefaultsAndNeighbors = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level>
    <selection page=""&lt;default&gt;"">
      <actions>
        <action name=""Export"" caption=""Export"" />
      </actions>
    </selection>
    <view>
      <tabs>
        <tab code=""General"" name=""General"" type=""Tabular"">
          <actions>
            <action name=""Update"" caption=""Update"" />
            <action name=""Delete"" caption=""Delete"" />
          </actions>
        </tab>
        <tab code=""Orders"" name=""Orders"" type=""Grid"" page=""&lt;default&gt;"">
          <actions>
            <action name=""ViewOrder"" caption=""View Order"" />
          </actions>
        </tab>
      </tabs>
    </view>
  </level>
</WorkWithInstance>";

        [Fact]
        public void TryAddActionToXml_AddsActionWithProcedureConditionAndParameters_Success()
        {
            var parameters = new List<string> { "&CustomerId", "'PDF'" };
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Export PDF",
                procObj: null,
                condition: "&IsAdmin = true",
                parameters: parameters,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(newXml);

            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listError);
            Assert.True(listed, listError);
            Assert.Single(tabs);

            var generalTab = (JObject)tabs[0];
            Assert.Equal("General", generalTab["code"]?.ToString());

            var actions = (JArray)generalTab["actions"];
            Assert.Equal(3, actions.Count);

            var exportAct = actions.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "ExportReport", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(exportAct);
            Assert.Equal("Export PDF", exportAct["caption"]?.ToString());
            Assert.Equal("&IsAdmin = true", exportAct["condition"]?.ToString());

            var actParams = (JArray)exportAct["parameters"];
            Assert.Equal(2, actParams.Count);
            Assert.Equal("&CustomerId", actParams[0].ToString());
            Assert.Equal("'PDF'", actParams[1].ToString());
        }

        [Fact]
        public void TryAddActionToXml_DuplicateAction_FailsWithActionAlreadyExists()
        {
            bool addedFirst = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Export PDF",
                procObj: null,
                condition: null,
                parameters: null,
                out string firstXml,
                out string errCode1,
                out string errMsg1);

            Assert.True(addedFirst, errMsg1);

            bool addedSecond = WorkWithSdkAdapter.TryAddActionToXml(
                firstXml,
                tabCode: "General",
                actionName: "ExportReport",
                caption: "Another Export",
                procObj: null,
                condition: null,
                parameters: null,
                out string _,
                out string errCode2,
                out string errMsg2);

            Assert.False(addedSecond);
            Assert.Equal("ActionAlreadyExists", errCode2);
            Assert.Contains("already exists", errMsg2);
        }

        [Fact]
        public void TryAddActionToXml_TabNotFound_FailsWithTabNotFound()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "Invoices",
                actionName: "ExportReport",
                caption: "Export",
                procObj: null,
                condition: null,
                parameters: null,
                out string _,
                out string errCode,
                out string errMsg);

            Assert.False(added);
            Assert.Equal("TabNotFound", errCode);
            Assert.Contains("Invoices", errMsg);
        }

        [Fact]
        public void TryAddActionToXml_CreatesActionsContainerIfMissing()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXmlNoActions,
                tabCode: "General",
                actionName: "PrintInvoice",
                caption: "Print",
                procObj: null,
                condition: null,
                parameters: null,
                out string newXml,
                out string errCode,
                out string errMsg);

            Assert.True(added, errMsg);
            Assert.Null(errCode);

            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listErr);
            Assert.True(listed, listErr);
            var actions = (JArray)tabs[0]["actions"];
            Assert.Single(actions);
            Assert.Equal("PrintInvoice", actions[0]["name"]?.ToString());
        }

        [Fact]
        public void PatternRegistry_WorkWithPatternId_IsWorkWith()
        {
            Assert.Equal(new Guid("78cecefe-be7d-4980-86ce-8d6e91fba04b"), PatternRegistry.WorkWithPatternId);

            var wwManifest = new PatternManifest
            {
                Id = PatternRegistry.WorkWithPatternId,
                Name = "WorkWith",
                Publisher = "GeneXus"
            };
            Assert.True(wwManifest.IsWorkWith);
            Assert.False(wwManifest.IsWorkWithPlus);

            var wwpManifest = new PatternManifest
            {
                Id = PatternRegistry.WorkWithPlusPatternId,
                Name = "WorkWithPlus",
                Publisher = "DVelop"
            };
            Assert.True(wwpManifest.IsWorkWithPlus);
            Assert.False(wwManifest.IsWorkWithPlus);
        }

        [Fact]
        public void BuildActionPlan_IncludesValidationsAndExplicitValidationLimits()
        {
            var parameters = new List<string> { "procUrlTeste(\"General\")", "TestTrnEmpresaId" };
            var plan = WorkWithSdkAdapter.BuildActionPlan(
                instanceName: "WorkWithWebTestTrn",
                tabCode: "General",
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "TestTrnPropria",
                parameters: parameters);

            Assert.NotNull(plan);
            Assert.Equal("WorkWithWebTestTrn", plan["instance"]?.ToString());
            Assert.Equal("General", plan["tab"]?.ToString());
            Assert.Equal("ExecutaTeste", plan["actionName"]?.ToString());
            Assert.Equal("Executa Teste", plan["caption"]?.ToString());
            Assert.Equal("procExecutaTeste", plan["procedure"]?.ToString());
            Assert.Equal("TestTrnPropria", plan["condition"]?.ToString());

            var planParams = plan["parameters"] as JArray;
            Assert.NotNull(planParams);
            Assert.Equal(2, planParams.Count);
            Assert.Equal("procUrlTeste(\"General\")", planParams[0].ToString());
            Assert.Equal("TestTrnEmpresaId", planParams[1].ToString());

            // Issue #476: Plan must declare validations performed
            var validations = plan["validations"] as JObject;
            Assert.NotNull(validations);
            Assert.True(validations["targetObjectResolved"]?.ToObject<bool>());
            Assert.True(validations["workWithInstanceResolved"]?.ToObject<bool>());
            Assert.True(validations["tabExists"]?.ToObject<bool>());
            Assert.True(validations["actionNameFormatValid"]?.ToObject<bool>());
            Assert.True(validations["actionNameUniqueOnTab"]?.ToObject<bool>());
            Assert.True(validations["procedureResolved"]?.ToObject<bool>());
            Assert.True(validations["procedureTypeValid"]?.ToObject<bool>());

            // Issue #476: Plan must explicitly report validation limits (signature arity/types, expressions)
            var limits = plan["validationLimits"] as JObject;
            Assert.NotNull(limits);
            Assert.Contains("parm() rule", limits["signatureAndTypes"]?.ToString());
            Assert.Contains("not compiled", limits["conditionExpression"]?.ToString());
            Assert.Contains("not compiled", limits["parameterExpressions"]?.ToString());
        }

        [Fact]
        public void VerifyActionMatches_AllFieldsMatch_ReturnsTrue()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "TestTrnPropria",
                ["parameters"] = new JArray("procUrlTeste(\"General\")", "TestTrnEmpresaId")
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "TestTrnPropria",
                parameters: new[] { "procUrlTeste(\"General\")", "TestTrnEmpresaId" },
                out string mismatchDetail);

            Assert.True(matches, mismatchDetail);
            Assert.Null(mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_CaptionMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Outro Caption",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "",
                ["parameters"] = new JArray()
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "",
                parameters: Array.Empty<string>(),
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Caption mismatch", mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_ParameterOrderOrCountMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "",
                ["parameters"] = new JArray("ParamB", "ParamA") // Swapped order
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "",
                parameters: new[] { "ParamA", "ParamB" },
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Parameters mismatch", mismatchDetail);
        }

        [Fact]
        public void VerifyActionMatches_ConditionMismatch_ReturnsFalseWithDetail()
        {
            var observed = new JObject
            {
                ["name"] = "ExecutaTeste",
                ["caption"] = "Executa Teste",
                ["procedure"] = "procExecutaTeste",
                ["condition"] = "&IsAdmin",
                ["parameters"] = new JArray()
            };

            bool matches = WorkWithSdkAdapter.VerifyActionMatches(
                observed,
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "&IsOwner",
                parameters: Array.Empty<string>(),
                out string mismatchDetail);

            Assert.False(matches);
            Assert.NotNull(mismatchDetail);
            Assert.Contains("Condition mismatch", mismatchDetail);
        }

        [Fact]
        public void TryAddActionToXml_ControlledFixtureWithDefaultsAndNeighborTabs_PreservesDefaultsAndNeighborStructure()
        {
            var parameters = new List<string> { "TestTrnId" };
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXmlWithDefaultsAndNeighbors,
                tabCode: "General",
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procObj: null,
                condition: "False",
                parameters: parameters,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(newXml);

            // Verify defaults are preserved verbatim on Selection and neighbor tab (not materialized to concrete 10)
            Assert.Contains("&lt;default&gt;", newXml);
            Assert.DoesNotContain("page=\"10\"", newXml);

            // Verify neighbor tab Orders and its action are preserved
            Assert.Contains(@"code=""Orders""", newXml);
            Assert.Contains(@"name=""ViewOrder""", newXml);

            // Verify list actions accurately parses both tabs
            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listError);
            Assert.True(listed, listError);
            Assert.Equal(2, tabs.Count);

            var generalTab = tabs.FirstOrDefault(t => string.Equals(t["code"]?.ToString(), "General")) as JObject;
            Assert.NotNull(generalTab);
            var generalActions = (JArray)generalTab["actions"];
            Assert.Equal(3, generalActions.Count);
            Assert.NotNull(generalActions.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), "ExecutaTeste")));

            var ordersTab = tabs.FirstOrDefault(t => string.Equals(t["code"]?.ToString(), "Orders")) as JObject;
            Assert.NotNull(ordersTab);
            var ordersActions = (JArray)ordersTab["actions"];
            Assert.Single(ordersActions);
            Assert.Equal("ViewOrder", ordersActions[0]["name"]?.ToString());
        }

        [Fact]
        public void TryAddActionToXml_PreservesRawWhitespaceWithoutReformattingUnmutatedElements()
        {
            bool added = WorkWithSdkAdapter.TryAddActionToXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "CustomAction",
                caption: "Custom",
                procObj: null,
                condition: null,
                parameters: null,
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(added, errorMessage);
            Assert.Null(errorCode);

            // Existing tab element and original actions formatting are preserved without line collapsing
            Assert.Contains(@"<tab code=""General"" name=""General"" type=""Tabular"">", newXml);
            Assert.Contains(@"<action name=""Update"" caption=""Update"" />", newXml);
        }

        [Fact]
        public void TryRemoveActionFromXml_RemovesTargetActionAndPreservesNeighbors()
        {
            bool removed = WorkWithSdkAdapter.TryRemoveActionFromXml(
                SampleWorkWithXmlWithDefaultsAndNeighbors,
                tabCode: "General",
                actionName: "Delete",
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(removed, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(newXml);

            // Verify Delete was removed from General
            Assert.DoesNotContain(@"<action name=""Delete""", newXml);

            // Verify Update neighbor action on General was preserved
            Assert.Contains(@"<action name=""Update"" caption=""Update"" />", newXml);

            // Verify Orders neighbor tab and its action ViewOrder were preserved
            Assert.Contains(@"code=""Orders""", newXml);
            Assert.Contains(@"name=""ViewOrder""", newXml);

            // Verify defaults were preserved verbatim
            Assert.Contains("&lt;default&gt;", newXml);

            // Verify list actions accurately parses the post-removal state
            bool listed = WorkWithSdkAdapter.TryListActionsFromXml(newXml, out JArray tabs, out string listError);
            Assert.True(listed, listError);
            var generalTab = tabs.FirstOrDefault(t => string.Equals(t["code"]?.ToString(), "General")) as JObject;
            Assert.NotNull(generalTab);
            var generalActions = (JArray)generalTab["actions"];
            Assert.Single(generalActions);
            Assert.Equal("Update", generalActions[0]["name"]?.ToString());
        }

        [Fact]
        public void TryRemoveActionFromXml_ActionNotFound_ReturnsFalseAndErrorCode()
        {
            bool removed = WorkWithSdkAdapter.TryRemoveActionFromXml(
                SampleWorkWithXml,
                tabCode: "General",
                actionName: "NonExistentAction",
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.False(removed);
            Assert.Null(newXml);
            Assert.Equal("ActionNotFound", errorCode);
            Assert.Contains("NonExistentAction", errorMessage);
        }

        [Fact]
        public void TryRemoveActionFromXml_TabNotFound_ReturnsFalseAndErrorCode()
        {
            bool removed = WorkWithSdkAdapter.TryRemoveActionFromXml(
                SampleWorkWithXml,
                tabCode: "NonExistentTab",
                actionName: "Update",
                out string newXml,
                out string errorCode,
                out string errorMessage);

            Assert.False(removed);
            Assert.Null(newXml);
            Assert.Equal("TabNotFound", errorCode);
            Assert.Contains("NonExistentTab", errorMessage);
        }

        [Fact]
        public void BuildActionPlan_ReflectsArityValidation()
        {
            var plan = WorkWithSdkAdapter.BuildActionPlan(
                instanceName: "WorkWithWebTestTrn",
                tabCode: "General",
                actionName: "ExecutaTeste",
                caption: "Executa Teste",
                procName: "procExecutaTeste",
                condition: "TestTrnPropria",
                parameters: new[] { "P1" },
                procObj: null);

            Assert.NotNull(plan);
            Assert.True(plan["valid"]?.ToObject<bool>());
            var validations = plan["validations"] as JObject;
            Assert.NotNull(validations);
            Assert.True(validations["arityMatches"]?.ToObject<bool>());
        }

        // Issue #559: the real standard WorkWith nodes are `filterAttribute` under
        // `attributes` and `condition` under `conditions`, in the document namespace -
        // the same shape PatternInstancePatchPreflightTests pins. The first version of
        // the XML reader looked for `attribute` and read every populated filter as empty.
        private const string SampleWorkWithXmlWithFilter = @"<?xml version=""1.0"" encoding=""utf-8""?>
<instance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level id=""Root"">
    <selection>
      <filter>
        <attributes>
          <filterAttribute name=""TemExistente"" description=""Tem Existente"" domain=""11111111-1111-1111-1111-111111111111-SimOuNao"" default=""SimOuNao.Nao"" />
        </attributes>
        <conditions>
          <condition value=""DocumentoId.IsEmpty() when &amp;TemExistente = SimOuNao.Nao;"" />
        </conditions>
      </filter>
      <actions>
        <action name=""Export"" caption=""Export"" />
      </actions>
    </selection>
  </level>
</instance>";

        private const string SampleWorkWithXmlWithTwoFilteredLevels = @"<?xml version=""1.0"" encoding=""utf-8""?>
<instance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level id=""Root"">
    <selection>
      <filter>
        <attributes>
          <filterAttribute name=""CustomerName"" description=""Customer Name"" />
        </attributes>
        <conditions />
      </filter>
    </selection>
    <level id=""Orders"">
      <selection>
        <filter>
          <attributes>
            <filterAttribute name=""OrderDate"" description=""Order Date"" />
          </attributes>
          <conditions />
        </filter>
      </selection>
    </level>
  </level>
</instance>";

        [Fact]
        public void TryListFiltersFromXml_RealSchemaShape_ReadsFilterAttributeNodes()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithFilter, "Root", out var filters, out var error, out var errorMessage);

            Assert.True(success, errorMessage);
            Assert.Null(error);
            Assert.NotNull(filters);
            Assert.Equal("Root", filters["level"]?.ToString());

            var attrs = filters["attributes"] as JArray;
            Assert.NotNull(attrs);
            Assert.Single(attrs);
            Assert.Equal("TemExistente", attrs[0]["name"]?.ToString());
            Assert.Equal("Tem Existente", attrs[0]["description"]?.ToString());
            Assert.Equal("SimOuNao", attrs[0]["domain"]?.ToString());
            // Issue #588: the schema's `default` field is read by the XML projection.
            Assert.Equal("SimOuNao.Nao", attrs[0]["default"]?.ToString());

            var conds = filters["conditions"] as JArray;
            Assert.NotNull(conds);
            Assert.Single(conds);
            Assert.Contains("when &TemExistente = SimOuNao.Nao;", conds[0]?.ToString());
        }

        [Fact]
        public void TryListFiltersFromXml_SingleLevel_ResolvesWithoutLevelArgument()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithFilter, null, out var filters, out _, out var errorMessage);

            Assert.True(success, errorMessage);
            Assert.Equal("Root", filters["level"]?.ToString());
        }

        [Fact]
        public void TryListFiltersFromXml_LevelWithoutSelection_ReturnsWorkWithSelectionMissing()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXml, null, out var filters, out var error, out var errorMessage);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("WorkWithSelectionMissing", error);
            Assert.Contains("Root", errorMessage);
        }

        [Fact]
        public void TryListFiltersFromXml_SelectionWithoutFilter_ReturnsWorkWithFilterNotPresent()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithDefaultsAndNeighbors, null, out var filters, out var error, out var errorMessage);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("WorkWithFilterNotPresent", error);
            Assert.Contains("GeneXus IDE", errorMessage);
        }

        [Fact]
        public void TryListFiltersFromXml_UnknownLevel_ReturnsFilterLevelNotFoundWithCandidates()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithFilter, "NonExistentLevel", out var filters, out var error, out var errorMessage);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("FilterLevelNotFound", error);
            Assert.Contains("NonExistentLevel", errorMessage);
            Assert.Contains("Root", errorMessage);
        }

        [Fact]
        public void TryListFiltersFromXml_TwoLevelsWithoutLevel_ReturnsFilterLevelAmbiguous()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithTwoFilteredLevels, null, out var filters, out var error, out var errorMessage);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("FilterLevelAmbiguous", error);
            Assert.Contains("Root", errorMessage);
            Assert.Contains("Orders", errorMessage);
        }

        [Fact]
        public void TryListFiltersFromXml_TwoLevelsWithExplicitLevel_ReturnsThatLevel()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithTwoFilteredLevels, "Orders", out var filters, out _, out var errorMessage);

            Assert.True(success, errorMessage);
            Assert.Equal("Orders", filters["level"]?.ToString());
            var attrs = filters["attributes"] as JArray;
            Assert.Single(attrs);
            Assert.Equal("OrderDate", attrs[0]["name"]?.ToString());
        }

        [Fact]
        public void TryListFiltersFromXml_WithoutDefaultAttribute_ReportsEmptyStringNotNull()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithTwoFilteredLevels, "Orders", out var filters, out _, out var errorMessage);

            Assert.True(success, errorMessage);
            var attr = (JObject)((JArray)filters["attributes"])[0];
            Assert.Equal(JTokenType.String, attr["default"]?.Type);
            Assert.Equal("", attr["default"]?.ToString());
        }

        [Fact]
        public void ReadNativeFilter_ReadsDefaultFromTypedSurfaceAndNormalizesUnsetToEmpty()
        {
            var filter = new Artech.Patterns.WorkWith.FilterElement();
            var withDefault = filter.AddFilterAttribute("OrderDate");
            withDefault.Description = "Order Date";
            withDefault.Default = "Today()";
            filter.AddFilterAttribute("CustomerName");
            filter.AddCondition("OrderDate >= &OrderDate;");

            var level = new WorkWithSdkAdapter.FilterLevelCandidate { Id = "Root", HasSelection = true, Filter = filter };
            var filters = WorkWithSdkAdapter.ReadNativeFilter(level);

            Assert.Equal("Root", filters["level"]?.ToString());
            var attrs = (JArray)filters["attributes"];
            Assert.Equal(2, attrs.Count);
            Assert.Equal("OrderDate", attrs[0]["name"]?.ToString());
            Assert.Equal("Order Date", attrs[0]["description"]?.ToString());
            Assert.Equal("Today()", attrs[0]["default"]?.ToString());
            Assert.Equal("&OrderDate", attrs[0]["variableName"]?.ToString());
            Assert.Equal("CustomerName", attrs[1]["name"]?.ToString());
            Assert.Equal(JTokenType.String, attrs[1]["default"]?.Type);
            Assert.Equal("", attrs[1]["default"]?.ToString());
            Assert.Single((JArray)filters["conditions"]);
        }

        [Fact]
        public void TryResolveNativeFilterSurface_ResolvesDefaultSetterFromTypedSurface()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(WorkWithServiceTests).Assembly;
            try
            {
                bool resolved = WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, needsDescription: true, needsDomain: false, needsDefault: true, needsConditions: true, out var surface, out var missing);

                Assert.True(resolved, missing);
                Assert.NotNull(surface.Default);
                Assert.Equal("Default", surface.Default.Name);
                Assert.True(surface.Default.CanWrite);
                Assert.Equal(typeof(string), surface.Default.PropertyType);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void TryResolveNativeFilterSurface_WithoutDefaultSetter_NamesTheMissingMember()
        {
            // An assembly without the typed WorkWith types stands in for an SDK build whose
            // FilterAttributeElement has no settable string Default.
            WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(object).Assembly;
            try
            {
                bool resolved = WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, needsDescription: false, needsDomain: false, needsDefault: true, needsConditions: false, out var surface, out var missing);

                Assert.False(resolved);
                Assert.Null(surface);
                Assert.Contains("FilterAttributeElement.Default string setter", missing);

                bool resolvedWithoutDefault = WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, needsDescription: false, needsDomain: false, needsDefault: false, needsConditions: false, out _, out var missingWithoutDefault);
                Assert.False(resolvedWithoutDefault);
                Assert.DoesNotContain("FilterAttributeElement.Default string setter", missingWithoutDefault);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void TryAuthorFilterAttributeOnTypedModel_WritesDefaultThroughTypedSetter()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(WorkWithServiceTests).Assembly;
            try
            {
                Assert.True(WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, needsDescription: true, needsDomain: false, needsDefault: true, needsConditions: true, out var surface, out var missing), missing);

                var workWith = new Artech.Patterns.WorkWith.WorkWithInstance();
                var root = workWith.AddLevel("Root");
                var conditions = new List<string> { "OrderDate >= &OrderDate when not &OrderDate.IsEmpty();" };

                bool authored = WorkWithSdkAdapter.TryAuthorFilterAttributeOnTypedModel(surface, workWith, "Root", "OrderDate", "Order Date", null, "Today()", conditions, out var created, out _, out var code, out var message);

                Assert.True(authored, message);
                Assert.Null(code);
                var attribute = Assert.IsType<Artech.Patterns.WorkWith.FilterAttributeElement>(created);
                Assert.Equal("OrderDate", attribute.Name);
                Assert.Equal("Order Date", attribute.Description);
                Assert.Equal("Today()", attribute.Default);
                Assert.Same(attribute, Assert.Single(root.Selection.Filter.Attributes));
                Assert.Equal(conditions[0], Assert.Single(root.Selection.Filter.Conditions).Value);

                // The re-read of the same typed model reports the value the writer set.
                var reread = WorkWithSdkAdapter.ReadNativeFilter(new WorkWithSdkAdapter.FilterLevelCandidate { Id = "Root", HasSelection = true, Filter = root.Selection.Filter });
                Assert.Equal("Today()", ((JArray)reread["attributes"])[0]["default"]?.ToString());
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void TryAuthorFilterAttributeOnTypedModel_OmittedDefault_LeavesTypedDefaultUntouched()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(WorkWithServiceTests).Assembly;
            try
            {
                Assert.True(WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, needsDescription: false, needsDomain: false, needsDefault: false, needsConditions: false, out var surface, out var missing), missing);

                var workWith = new Artech.Patterns.WorkWith.WorkWithInstance();
                var root = workWith.AddLevel("Root");

                bool authored = WorkWithSdkAdapter.TryAuthorFilterAttributeOnTypedModel(surface, workWith, null, "CustomerName", null, null, "   ", new List<string>(), out var created, out _, out _, out var message);

                Assert.True(authored, message);
                var attribute = Assert.IsType<Artech.Patterns.WorkWith.FilterAttributeElement>(created);
                Assert.Null(attribute.Default);
                Assert.Null(attribute.Description);
                Assert.Empty(root.Selection.Filter.Conditions);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void TryAuthorFilterAttributeOnTypedModel_AmbiguousLevel_RefusesBeforeMutating()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(WorkWithServiceTests).Assembly;
            try
            {
                Assert.True(WorkWithSdkAdapter.TryResolveNativeFilterSurface(null, false, false, true, false, out var surface, out var missing), missing);

                var workWith = new Artech.Patterns.WorkWith.WorkWithInstance();
                var root = workWith.AddLevel("Root");
                var orders = workWith.AddLevel("Orders");

                bool authored = WorkWithSdkAdapter.TryAuthorFilterAttributeOnTypedModel(surface, workWith, null, "OrderDate", null, null, "Today()", null, out var created, out _, out var code, out var message);

                Assert.False(authored);
                Assert.Null(created);
                Assert.Equal("FilterLevelAmbiguous", code);
                Assert.Contains("Orders", message);
                Assert.Empty(root.Selection.Filter.Attributes);
                Assert.Empty(orders.Selection.Filter.Attributes);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void ComparedFilterFields_IncludesDefaultOnlyWhenSupplied()
        {
            Assert.Equal(new[] { "name", "conditions" }, WorkWithSdkAdapter.ComparedFilterFields(null, null, null));
            Assert.Equal(new[] { "name", "conditions" }, WorkWithSdkAdapter.ComparedFilterFields(null, null, "  "));
            Assert.Equal(new[] { "name", "default", "conditions" }, WorkWithSdkAdapter.ComparedFilterFields(null, null, "Today()"));
            Assert.Equal(new[] { "name", "description", "domain", "default", "conditions" }, WorkWithSdkAdapter.ComparedFilterFields("Order Date", "DateDomain", "Today()"));
        }

        [Fact]
        public void VerifyFilterAttributeMatches_Default_MatchesSuppliedValueAndReportsMismatch()
        {
            var observed = new JObject { ["name"] = "OrderDate", ["description"] = "Order Date", ["domain"] = "", ["default"] = "Today()" };
            var observedConditions = new JArray();

            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "OrderDate", null, null, "Today()", null, out var detail), detail);

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "OrderDate", null, null, "Now()", null, out detail));
            Assert.Contains("Default mismatch", detail);
            Assert.Contains("expected 'Now()', observed 'Today()'", detail);

            var withoutDefault = new JObject { ["name"] = "OrderDate", ["description"] = "Order Date", ["domain"] = "", ["default"] = "" };
            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(withoutDefault, observedConditions, "OrderDate", null, null, "Today()", null, out detail));
            Assert.Contains("Default mismatch", detail);
        }

        [Fact]
        public void VerifyFilterAttributeMatches_OmittedDefault_NeverComparesAgainstEmptyOrMissing()
        {
            var observedConditions = new JArray();
            var emptyDefault = new JObject { ["name"] = "OrderDate", ["default"] = "" };
            var missingDefault = new JObject { ["name"] = "OrderDate" };
            var populatedDefault = new JObject { ["name"] = "OrderDate", ["default"] = "Today()" };

            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(emptyDefault, observedConditions, "OrderDate", null, null, null, null, out var detail), detail);
            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(missingDefault, observedConditions, "OrderDate", null, null, "", null, out detail), detail);
            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(populatedDefault, observedConditions, "OrderDate", null, null, "   ", null, out detail), detail);
        }

        [Fact]
        public void BuildFilterPlan_IncludesDefaultWhenSuppliedAndDeclaresItStoredNotValidated()
        {
            var plan = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebOrder", "Root", "OrderDate", null, null, "Today()", null);

            Assert.Equal("Today()", plan["filterAttribute"]?["default"]?.ToString());
            Assert.Contains("stored as given", plan["validationLimits"]?["default"]?.ToString());
            Assert.Contains("not compiled", plan["validationLimits"]?["default"]?.ToString());

            var withoutDefault = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebOrder", "Root", "OrderDate", null, null, "   ", null);
            Assert.Null(withoutDefault["filterAttribute"]?["default"]);
        }

        [Fact]
        public void BuildFilterReceipt_WithDefault_ComparesDefaultAndEchoesRequestedValue()
        {
            var observed = new JObject { ["name"] = "OrderDate", ["description"] = "Order Date", ["domain"] = "", ["default"] = "Today()" };
            var observedConditions = new JArray("OrderDate >= &OrderDate;");

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebOrder", "Root", observed, observedConditions, "OrderDate", "Order Date", null, "Today()", new List<string> { "OrderDate >= &OrderDate;" }, WorkWithReadSource.NativeInstance, WorkWithReadSource.NativeInstance);

            Assert.Equal(new[] { "name", "description", "default", "conditions" }, (receipt["verification"]?["fieldsCompared"] as JArray).Select(f => f.ToString()).ToArray());
            Assert.Equal("Today()", receipt["requested"]?["default"]?.ToString());
            Assert.Equal("Today()", receipt["filterAttribute"]?["default"]?.ToString());
            Assert.Equal("in_memory_instance_reread", receipt["verification"]?["observedSource"]?.ToString());
        }

        [Theory]
        [InlineData("CustomerName")]
        [InlineData("&CustomerName")]
        [InlineData("_tem_existente2")]
        public void ValidateFilterRequest_AcceptsIdentifierWithOptionalAmpersand(string name)
        {
            bool valid = WorkWithSdkAdapter.ValidateFilterRequest(name, new List<string> { "CustomerName like &CustomerName;" }, new JArray(), out var code, out var message);

            Assert.True(valid, message);
            Assert.Null(code);
        }

        [Theory]
        [InlineData("Customer Name")]
        [InlineData("1Customer")]
        [InlineData("&&Customer")]
        [InlineData("Customer.Name")]
        [InlineData("")]
        public void ValidateFilterRequest_InvalidIdentifier_IsRefused(string name)
        {
            bool valid = WorkWithSdkAdapter.ValidateFilterRequest(name, null, null, out var code, out _);

            Assert.False(valid);
            Assert.Equal("InvalidFilterAttributeName", code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ValidateFilterRequest_EmptyCondition_IsRefused(string empty)
        {
            bool valid = WorkWithSdkAdapter.ValidateFilterRequest("CustomerName", new List<string> { "CustomerId = 1;", empty }, null, out var code, out var message);

            Assert.False(valid);
            Assert.Equal("FilterConditionEmpty", code);
            Assert.Contains("conditions[1]", message);
        }

        [Fact]
        public void ValidateFilterRequest_DuplicateConditionInRequest_IsRefused()
        {
            var conditions = new List<string> { "CustomerId = 1;", "CustomerId = 2;", "CustomerId = 1;" };

            bool valid = WorkWithSdkAdapter.ValidateFilterRequest("CustomerName", conditions, null, out var code, out var message);

            Assert.False(valid);
            Assert.Equal("FilterConditionDuplicate", code);
            Assert.Contains("conditions[2]", message);
        }

        [Fact]
        public void ValidateFilterRequest_ConditionAlreadyPresentOnFilter_IsRefused()
        {
            var existing = new JArray("DocumentoId.IsEmpty() when &TemExistente = SimOuNao.Nao;");

            bool valid = WorkWithSdkAdapter.ValidateFilterRequest("Outro", new List<string> { "DocumentoId.IsEmpty() when &TemExistente = SimOuNao.Nao;" }, existing, out var code, out _);

            Assert.False(valid);
            Assert.Equal("FilterConditionDuplicate", code);
        }

        [Fact]
        public void BuildFilterPlan_DoesNotInventDescriptionAndReportsNativeRoute()
        {
            var plan = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebCustomer", "Root", "CustomerName", description: null, domain: null, defaultValue: null, conditions: new List<string> { "CustomerName like &CustomerName;" });

            Assert.Equal("native-sdk", plan["route"]?.ToString());
            Assert.Equal("Root", plan["level"]?.ToString());
            var filterAttribute = plan["filterAttribute"] as JObject;
            Assert.Equal("CustomerName", filterAttribute["name"]?.ToString());
            Assert.Null(filterAttribute["description"]);
            Assert.Null(filterAttribute["domain"]);
            Assert.Null(filterAttribute["default"]);
            Assert.Single(plan["conditions"] as JArray);
            Assert.NotNull(plan["validations"]);
            Assert.Contains("not compiled", plan["validationLimits"]?["conditionExpressions"]?.ToString());

            var withDescription = WorkWithSdkAdapter.BuildFilterPlan("WorkWithWebCustomer", "Root", "CustomerName", "Customer Name", "CustomerNameDomain", null, null);
            Assert.Equal("Customer Name", withDescription["filterAttribute"]?["description"]?.ToString());
            Assert.Equal("CustomerNameDomain", withDescription["filterAttribute"]?["domain"]?.ToString());
            Assert.Empty(withDescription["conditions"] as JArray);
        }

        [Fact]
        public void VerifyFilterAttributeMatches_ComparesSuppliedFieldsAndRequestedConditions()
        {
            var observed = new JObject { ["name"] = "customername", ["description"] = "Customer Name", ["domain"] = "CustomerNameDomain" };
            var observedConditions = new JArray("Existing;", "CustomerName like &CustomerName;");
            var requested = new List<string> { "CustomerName like &CustomerName;" };

            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "CustomerName", "Customer Name", "customernamedomain", null, requested, out var detail), detail);
            Assert.True(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "CustomerName", null, null, null, requested, out _));

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "CustomerName", "Other", null, null, requested, out detail));
            Assert.Contains("Description mismatch", detail);

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, observedConditions, "CustomerName", null, "Missing", null, requested, out detail));
            Assert.Contains("Domain mismatch", detail);

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(observed, new JArray("Existing;"), "CustomerName", null, null, null, requested, out detail));
            Assert.Contains("was not found in the verified filter conditions", detail);

            Assert.False(WorkWithSdkAdapter.VerifyFilterAttributeMatches(null, observedConditions, "CustomerName", null, null, null, requested, out detail));
        }

        [Fact]
        public void BuildFilterReceipt_ReportsNativeRouteAndInMemoryRereadScope()
        {
            var observed = new JObject { ["name"] = "CustomerName", ["description"] = "Customer Name", ["domain"] = "" };
            var observedConditions = new JArray("CustomerName like &CustomerName;");

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebCustomer", "Root", observed, observedConditions, "CustomerName", "Customer Name", null, null, new List<string> { "CustomerName like &CustomerName;" }, WorkWithReadSource.NativeInstance, WorkWithReadSource.NativeInstance);

            Assert.Equal("WorkWithWebCustomer", receipt["instance"]?.ToString());
            Assert.Equal("Root", receipt["level"]?.ToString());
            Assert.Equal("native-sdk", receipt["route"]?.ToString());
            Assert.True(receipt["verified"]?.ToObject<bool>());
            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.Contains("post-write in-memory instance re-read", receipt["verificationScope"]?.ToString());
            Assert.Equal("in_memory_instance_reread", receipt["verification"]?["observedSource"]?.ToString());
            Assert.Null(receipt["verification"]?["observedSourceReason"]);
            Assert.Equal("in_memory_instance_reread", receipt["verification"]?["preCheckSource"]?.ToString());
            Assert.Null(receipt["verification"]?["preCheckSourceReason"]);
            // Issue #588: an omitted `default` is not compared, so the pinned list is unchanged.
            Assert.Equal(new[] { "name", "description", "conditions" }, (receipt["verification"]?["fieldsCompared"] as JArray).Select(f => f.ToString()).ToArray());
            Assert.Equal("Customer Name", receipt["filterAttribute"]?["description"]?.ToString());
            Assert.Equal("CustomerName", receipt["requested"]?["name"]?.ToString());
            Assert.Equal("", receipt["requested"]?["default"]?.ToString());
            Assert.Equal("not_guaranteed", receipt["derivedObjectRegeneration"]?.ToString());
            Assert.Null(receipt["generatedObjectsRegenerated"]);
            Assert.Null(receipt["applied"]);
        }

        [Fact]
        public void TryAddFilterAttributeNative_WithoutNativeSdk_RefusesWorkWithSdkUnavailable()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => null;
            try
            {
                bool added = WorkWithSdkAdapter.TryAddFilterAttributeNative(null, "Root", "CustomerName", null, null, null, new List<string> { "CustomerName like &CustomerName;" }, out _, out var code, out var message);

                Assert.False(added);
                Assert.Equal("WorkWithSdkUnavailable", code);
                Assert.Contains("no XML fallback", message);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void AddFilterAttribute_HasNoXmlWriteFallbackSeam()
        {
            // The structural preflight refuses raw-XML filter authoring, so the typed
            // route must not offer the same write through a WriteService fallback.
            var add = typeof(WorkWithSdkAdapter).GetMethod("AddFilterAttribute", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.NotNull(add);
            Assert.DoesNotContain(add.GetParameters(), p => p.ParameterType == typeof(WriteService));

            var xmlWriter = typeof(WorkWithSdkAdapter).GetMethod("TryAddFilterAttributeToXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.Null(xmlWriter);
        }

        // Issue #589: ListFilters/ListActions report the reader that actually produced
        // the result and the receipts copy it. The adapter resolves the SDK type by name,
        // so the test assembly stands in for Artech.Patterns.WorkWith and the XML seam
        // returns a document whose content differs from the native stand-in: every test
        // proves which reader was used, not only which label was reported.

        private sealed class FakeNativeSdkScope : IDisposable
        {
            public FakeNativeSdkScope(Func<object, object> load)
            {
                FakeWorkWithInstance.LoadHook = load;
                WorkWithSdkAdapter.AssemblyResolverOverride = () => typeof(FakeWorkWithInstance).Assembly;
            }

            public void Dispose()
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
                FakeWorkWithInstance.LoadHook = null;
            }
        }

        private sealed class ThrowingLevels : System.Collections.IEnumerable
        {
            public System.Collections.IEnumerator GetEnumerator()
            {
                throw new InvalidOperationException("Levels enumeration failed");
            }
        }

        private static object NativeFilterLevel(string id, string attributeName, string condition)
        {
            return new
            {
                Id = id,
                Selection = new
                {
                    Filter = new
                    {
                        Attributes = new[] { new { Name = attributeName, Description = attributeName + " (native)", DomainName = "", VariableName = "&" + attributeName } },
                        Conditions = new[] { new { Value = condition } }
                    }
                }
            };
        }

        private static FakeWorkWithInstance NativeActionsInstance()
        {
            return new FakeWorkWithInstance
            {
                Levels = new[]
                {
                    new
                    {
                        View = new
                        {
                            Tabs = new[]
                            {
                                new
                                {
                                    Code = "General",
                                    Name = "General",
                                    Type = "Tabular",
                                    Actions = new[] { new { Name = "Update", Caption = "Update (native)", GxobjectName = "", Condition = "", Parameters = new object[0] } }
                                }
                            }
                        }
                    }
                }
            };
        }

        [Fact]
        public void ListFiltersCore_NativeRoute_ReportsInMemoryInstanceReread()
        {
            using (new FakeNativeSdkScope(_ => new FakeWorkWithInstance { Levels = new[] { NativeFilterLevel("Root", "CustomerName", "CustomerName like &CustomerName;") } }))
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out var error, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Null(error);
                Assert.Equal(WorkWithReadSource.NativeInstance, source);
                Assert.Equal("Root", filters["level"]?.ToString());
                Assert.Equal("CustomerName", filters["attributes"]?[0]?["name"]?.ToString());
                Assert.Equal("CustomerName like &CustomerName;", filters["conditions"]?[0]?.ToString());
            }
        }

        [Fact]
        public void ListFiltersCore_NativeLoadThrows_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => throw new InvalidOperationException("WorkWithInstance.Load failed")))
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out _, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeReadFailed, source);
                Assert.Equal("TemExistente", filters["attributes"]?[0]?["name"]?.ToString());
            }
        }

        [Fact]
        public void ListFiltersCore_NativeLevelsTraversalThrows_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => new FakeWorkWithInstance { Levels = new ThrowingLevels() }))
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out _, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeReadFailed, source);
                Assert.Equal("TemExistente", filters["attributes"]?[0]?["name"]?.ToString());
            }
        }

        [Fact]
        public void ListFiltersCore_NativeInstanceWithoutLevels_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => new FakeWorkWithInstance { Levels = null }))
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out _, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeLevelsAbsent, source);
                Assert.Equal("TemExistente", filters["attributes"]?[0]?["name"]?.ToString());
            }
        }

        [Fact]
        public void ListFiltersCore_NativeLoadReturnsNoInstance_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => null))
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out _, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeInstanceAbsent, source);
                Assert.Equal("TemExistente", filters["attributes"]?[0]?["name"]?.ToString());
            }
        }

        [Fact]
        public void ListFiltersCore_NativeSdkUnavailable_FallsBackToXmlProjectionAndSaysWhy()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => null;
            try
            {
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => SampleWorkWithXmlWithFilter, null, out var filters, out var source, out _, out var errorMessage);

                Assert.True(success, errorMessage);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeUnavailable, source);
                Assert.Equal("TemExistente", filters["attributes"]?[0]?["name"]?.ToString());
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Fact]
        public void ListFiltersCore_NativeLevelError_IsNotMaskedByTheXmlProjection()
        {
            using (new FakeNativeSdkScope(_ => new FakeWorkWithInstance
            {
                Levels = new[]
                {
                    NativeFilterLevel("Root", "CustomerName", "CustomerName like &CustomerName;"),
                    NativeFilterLevel("Orders", "OrderDate", "OrderDate = &OrderDate;")
                }
            }))
            {
                bool xmlRead = false;
                bool success = WorkWithSdkAdapter.ListFiltersCore(null, () => { xmlRead = true; return SampleWorkWithXmlWithFilter; }, null, out var filters, out var source, out var error, out _);

                Assert.False(success);
                Assert.Null(filters);
                Assert.Equal("FilterLevelAmbiguous", error);
                Assert.Equal(WorkWithReadSource.NativeInstance, source);
                Assert.False(xmlRead);
            }
        }

        [Fact]
        public void ListFilters_NullInstance_RefusesBeforeReading()
        {
            bool success = WorkWithSdkAdapter.ListFilters(null, null, null, out var filters, out _, out var error, out var errorMessage);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("NullInstance", error);
            Assert.NotEmpty(errorMessage);
        }

        [Fact]
        public void ListActionsCore_NativeRoute_ReportsInMemoryInstanceReread()
        {
            using (new FakeNativeSdkScope(_ => NativeActionsInstance()))
            {
                bool success = WorkWithSdkAdapter.ListActionsCore(null, () => SampleWorkWithXml, out var tabs, out var source, out var error);

                Assert.True(success, error);
                Assert.Equal(WorkWithReadSource.NativeInstance, source);
                Assert.Single(tabs);
                var actions = (JArray)tabs[0]["actions"];
                Assert.Single(actions);
                Assert.Equal("Update (native)", actions[0]["caption"]?.ToString());
            }
        }

        [Fact]
        public void ListActionsCore_NativeLoadThrows_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => throw new InvalidOperationException("WorkWithInstance.Load failed")))
            {
                bool success = WorkWithSdkAdapter.ListActionsCore(null, () => SampleWorkWithXml, out var tabs, out var source, out var error);

                Assert.True(success, error);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeReadFailed, source);
                Assert.Equal(2, ((JArray)tabs[0]["actions"]).Count);
            }
        }

        [Fact]
        public void ListActionsCore_NativeLevelsTraversalThrows_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => new FakeWorkWithInstance { Levels = new ThrowingLevels() }))
            {
                bool success = WorkWithSdkAdapter.ListActionsCore(null, () => SampleWorkWithXml, out var tabs, out var source, out var error);

                Assert.True(success, error);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeReadFailed, source);
                Assert.Equal(2, ((JArray)tabs[0]["actions"]).Count);
            }
        }

        [Fact]
        public void ListActionsCore_NativeLoadReturnsNoInstance_FallsBackToXmlProjectionAndSaysWhy()
        {
            using (new FakeNativeSdkScope(_ => null))
            {
                bool success = WorkWithSdkAdapter.ListActionsCore(null, () => SampleWorkWithXml, out var tabs, out var source, out var error);

                Assert.True(success, error);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeInstanceAbsent, source);
                Assert.Equal(2, ((JArray)tabs[0]["actions"]).Count);
            }
        }

        [Fact]
        public void ListActionsCore_NativeSdkUnavailable_FallsBackToXmlProjectionAndSaysWhy()
        {
            WorkWithSdkAdapter.AssemblyResolverOverride = () => null;
            try
            {
                bool success = WorkWithSdkAdapter.ListActionsCore(null, () => SampleWorkWithXml, out var tabs, out var source, out var error);

                Assert.True(success, error);
                Assert.Equal(WorkWithReadSource.XmlProjectionNativeUnavailable, source);
                Assert.Equal(2, ((JArray)tabs[0]["actions"]).Count);
            }
            finally
            {
                WorkWithSdkAdapter.AssemblyResolverOverride = null;
            }
        }

        [Theory]
        [InlineData("NativeInstance", "in_memory_instance_reread", null)]
        [InlineData("XmlProjectionNativeUnavailable", "xml_projection", "native_sdk_unavailable")]
        [InlineData("XmlProjectionNativeInstanceAbsent", "xml_projection", "native_instance_absent")]
        [InlineData("XmlProjectionNativeLevelsAbsent", "xml_projection", "native_levels_absent")]
        [InlineData("XmlProjectionNativeReadFailed", "xml_projection", "native_read_failed")]
        public void DescribeReadSource_WritesLabelAndReasonOnlyForTheXmlProjection(string sourceName, string label, string reason)
        {
            var source = (WorkWithReadSource)Enum.Parse(typeof(WorkWithReadSource), sourceName);
            var target = new JObject();

            WorkWithSdkAdapter.DescribeReadSource(target, "observedSource", source);

            Assert.Equal(label, target["observedSource"]?.ToString());
            Assert.Equal(reason, target["observedSourceReason"]?.ToString());
        }

        [Fact]
        public void BuildFilterReceipt_XmlProjectionReread_ReportsProjectionSourceAndReason()
        {
            var observed = new JObject { ["name"] = "CustomerName", ["description"] = "CustomerName", ["domain"] = "" };

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebCustomer", "Root", observed, new JArray(), "CustomerName", null, null, null, null, WorkWithReadSource.NativeInstance, WorkWithReadSource.XmlProjectionNativeReadFailed);

            var verification = receipt["verification"] as JObject;
            Assert.NotNull(verification);
            Assert.Equal("xml_projection", verification["observedSource"]?.ToString());
            Assert.Equal("native_read_failed", verification["observedSourceReason"]?.ToString());
            Assert.Equal("in_memory_instance_reread", verification["preCheckSource"]?.ToString());
            Assert.Null(verification["preCheckSourceReason"]);
            Assert.Contains("post-write PatternInstance XML projection re-read", receipt["verificationScope"]?.ToString());
            Assert.DoesNotContain("in-memory instance re-read", receipt["verificationScope"]?.ToString());
            Assert.Equal("native-sdk", receipt["route"]?.ToString());
        }

        [Fact]
        public void BuildFilterReceipt_XmlProjectionPreCheck_ReportsProjectionPreCheckSourceAndReason()
        {
            var observed = new JObject { ["name"] = "CustomerName", ["description"] = "CustomerName", ["domain"] = "" };

            var receipt = WorkWithSdkAdapter.BuildFilterReceipt("WorkWithWebCustomer", "Root", observed, new JArray(), "CustomerName", null, null, null, null, WorkWithReadSource.XmlProjectionNativeLevelsAbsent, WorkWithReadSource.NativeInstance);

            var verification = receipt["verification"] as JObject;
            Assert.NotNull(verification);
            Assert.Equal("in_memory_instance_reread", verification["observedSource"]?.ToString());
            Assert.Null(verification["observedSourceReason"]);
            Assert.Equal("xml_projection", verification["preCheckSource"]?.ToString());
            Assert.Equal("native_levels_absent", verification["preCheckSourceReason"]?.ToString());
            Assert.Contains("post-write in-memory instance re-read", receipt["verificationScope"]?.ToString());
        }
    }
}

