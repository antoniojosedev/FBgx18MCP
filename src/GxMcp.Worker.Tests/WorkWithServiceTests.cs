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
    }
}
