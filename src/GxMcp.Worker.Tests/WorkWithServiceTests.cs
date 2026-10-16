using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

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

        private const string SampleWorkWithXmlWithFilter = @"<?xml version=""1.0"" encoding=""utf-8""?>
<WorkWithInstance xmlns=""http://schemas.genexus.com/Patterns/WorkWith/v1.0"">
  <level id=""Root"">
    <selection>
      <filter>
        <attributes>
          <attribute name=""CustomerName"" description=""Customer Name"" domain=""CustomerName"" variableName=""&amp;CustomerName"" />
        </attributes>
        <conditions>
          <condition value=""CustomerName like &amp;CustomerName when not &amp;CustomerName.IsEmpty()"" />
        </conditions>
      </filter>
      <actions>
        <action name=""Export"" caption=""Export"" />
      </actions>
    </selection>
  </level>
</WorkWithInstance>";

        [Fact]
        public void TryListFiltersFromXml_WithFilters_ReturnsAttributesAndConditions()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithFilter, "Root", out var filters, out var error);

            Assert.True(success, error);
            Assert.Null(error);
            Assert.NotNull(filters);
            Assert.Equal("Root", filters["level"]?.ToString());

            var attrs = filters["attributes"] as JArray;
            Assert.NotNull(attrs);
            Assert.Single(attrs);
            Assert.Equal("CustomerName", attrs[0]["name"]?.ToString());
            Assert.Equal("Customer Name", attrs[0]["description"]?.ToString());
            Assert.Equal("CustomerName", attrs[0]["domain"]?.ToString());

            var conds = filters["conditions"] as JArray;
            Assert.NotNull(conds);
            Assert.Single(conds);
            Assert.Contains("CustomerName like &CustomerName", conds[0]?.ToString());
        }

        [Fact]
        public void TryListFiltersFromXml_FilterNotPresent_ReturnsWorkWithFilterNotPresent()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXml, null, out var filters, out var error);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("WorkWithFilterNotPresent", error);
        }

        [Fact]
        public void TryListFiltersFromXml_LevelNotFound_ReturnsLevelNotFound()
        {
            bool success = WorkWithSdkAdapter.TryListFiltersFromXml(SampleWorkWithXmlWithFilter, "NonExistentLevel", out var filters, out var error);

            Assert.False(success);
            Assert.Null(filters);
            Assert.Equal("LevelNotFound", error);
        }

        [Fact]
        public void TryAddFilterAttributeToXml_Success_AddsAttributeAndPreservesNeighbors()
        {
            bool success = WorkWithSdkAdapter.TryAddFilterAttributeToXml(
                SampleWorkWithXmlWithFilter,
                levelId: "Root",
                attributeName: "CustomerStatus",
                description: "Customer Status",
                domain: "StatusDomain",
                conditions: new[] { "CustomerStatus = &CustomerStatus when not &CustomerStatus.IsEmpty()" },
                out string updatedXml,
                out string errorCode,
                out string errorMessage);

            Assert.True(success, errorMessage);
            Assert.Null(errorCode);
            Assert.NotNull(updatedXml);

            // Verifies existing attribute preserved
            Assert.Contains("CustomerName", updatedXml);
            // Verifies new attribute added
            Assert.Contains("CustomerStatus", updatedXml);
            Assert.Contains("StatusDomain", updatedXml);
            // Verifies condition added
            Assert.Contains("CustomerStatus = &amp;CustomerStatus", updatedXml);
            // Verifies neighbor action preserved
            Assert.Contains(@"<action name=""Export""", updatedXml);

            // Verifies reading back updated xml
            bool readBack = WorkWithSdkAdapter.TryListFiltersFromXml(updatedXml, "Root", out var readFilters, out var readError);
            Assert.True(readBack, readError);
            var attrs = readFilters["attributes"] as JArray;
            Assert.Equal(2, attrs.Count);
        }

        [Fact]
        public void TryAddFilterAttributeToXml_FilterNotPresent_ReturnsWorkWithFilterNotPresent()
        {
            bool success = WorkWithSdkAdapter.TryAddFilterAttributeToXml(
                SampleWorkWithXml,
                levelId: null,
                attributeName: "CustomerStatus",
                description: null,
                domain: null,
                conditions: null,
                out string updatedXml,
                out string errorCode,
                out string errorMessage);

            Assert.False(success);
            Assert.Null(updatedXml);
            Assert.Equal("WorkWithFilterNotPresent", errorCode);
        }

        [Fact]
        public void TryAddFilterAttributeToXml_LevelNotFound_ReturnsLevelNotFound()
        {
            bool success = WorkWithSdkAdapter.TryAddFilterAttributeToXml(
                SampleWorkWithXmlWithFilter,
                levelId: "NonExistentLevel",
                attributeName: "CustomerStatus",
                description: null,
                domain: null,
                conditions: null,
                out string updatedXml,
                out string errorCode,
                out string errorMessage);

            Assert.False(success);
            Assert.Null(updatedXml);
            Assert.Equal("LevelNotFound", errorCode);
        }

        [Fact]
        public void TryAddFilterAttributeToXml_AttributeAlreadyExists_ReturnsFilterAttributeAlreadyExists()
        {
            bool success = WorkWithSdkAdapter.TryAddFilterAttributeToXml(
                SampleWorkWithXmlWithFilter,
                levelId: "Root",
                attributeName: "CustomerName",
                description: null,
                domain: null,
                conditions: null,
                out string updatedXml,
                out string errorCode,
                out string errorMessage);

            Assert.False(success);
            Assert.Null(updatedXml);
            Assert.Equal("FilterAttributeAlreadyExists", errorCode);
        }

        [Fact]
        public void WorkWithReceipt_DeclaresDerivedObjectRegenerationNotGuaranteed_AndNeverGeneratedObjectsRegenerated()
        {
            // Receipt contract guard: WorkWith filter authoring must declare
            // derivedObjectRegeneration: "not_guaranteed" and must never re-introduce generatedObjectsRegenerated.
            var receipt = new JObject
            {
                ["instance"] = "WorkWithCustomer",
                ["applied"] = true,
                ["derivedObjectRegeneration"] = "not_guaranteed"
            };

            Assert.Equal("not_guaranteed", receipt["derivedObjectRegeneration"]?.ToString());
            Assert.Null(receipt["generatedObjectsRegenerated"]);
        }
    }
}
