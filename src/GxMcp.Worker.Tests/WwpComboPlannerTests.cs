using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // set_combo_properties planning against a fake of the SDK element: what is predicted, what is
    // refused before anything is written, and the order the native commands are issued in.
    public class WwpComboPlannerTests
    {
        private const string LoadVisibleIf = "self::*[@controlInfoDef='Custom' and (@controlType='DVelop Combo' or @controlType='Extended Suggest') and @controlInputDS!='Fixed Values']";
        private const string ParametersVisibleIf = "self::*[@controlInfoDef='Custom' and (@controlType='Dynamic Combo Box' or @controlType='Dynamic List Box' or @controlType='DVelop Combo' or @controlType='Extended Suggest') and @controlInputDS='Data Provider']";
        private const string ConditionsVisibleIf = "self::*[@controlInfoDef='Custom' and (@controlType='DVelop Combo' or @controlType='Extended Suggest') and @controlLoadDynamically='True']";

        internal sealed class FakeCombo : IWwpComboElement
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>
            {
                ["attribute"] = "adbb33c9-0906-4971-833c-998de27e0676-Category",
                ["controlInfoDef"] = "Custom",
                ["controlType"] = "DVelop Combo",
                ["controlInputDS"] = "Data Provider",
                ["controlInputDPParameters"] = "A, B",
                ["controlLoadDynamically"] = "False",
                ["conditionDynAtts"] = ""
            };
            internal readonly HashSet<string> Declared = new HashSet<string> { "controlInputDPParameters", "controlLoadDynamically", "conditionDynAtts" };
            internal readonly HashSet<string> ReadOnlyProperties = new HashSet<string>();
            internal readonly HashSet<string> Inapplicable = new HashSet<string>();
            internal readonly List<string> Applied = new List<string>();
            internal string RefuseApply;
            internal Func<string, string, string> ConvertError = (sdk, text) => null;
            internal Func<string, string, string> Render = (sdk, text) => text;

            public string Path => "/instance/transaction[1]/table/attribute[@attribute=\"" + Reference + "\"]";
            public string Reference => Values["attribute"];
            public bool HasProperty(string sdkName) => Declared.Contains(sdkName);
            public string VisibleIf(string sdkName) =>
                sdkName == "controlInputDPParameters" ? ParametersVisibleIf : sdkName == "controlLoadDynamically" ? LoadVisibleIf : ConditionsVisibleIf;
            public bool IsReadOnly(string sdkName) => ReadOnlyProperties.Contains(sdkName);
            public bool IsApplicable(string sdkName) => !Inapplicable.Contains(sdkName);
            public string Effective(string sdkName) => Values.TryGetValue(sdkName, out string value) ? value : null;
            public IReadOnlyDictionary<string, string> EffectiveValues() => Values;

            public bool TryConvert(string sdkName, string text, out object typed, out string storage, out string error)
            {
                typed = null;
                storage = null;
                error = ConvertError(sdkName, text);
                if (error != null) return false;
                typed = "typed:" + text;
                storage = Render(sdkName, text);
                return true;
            }

            public bool Apply(string sdkName, object typed, string expectedStorage)
            {
                Applied.Add(sdkName + "=" + expectedStorage);
                if (RefuseApply == sdkName) return false;
                Values[sdkName] = expectedStorage;
                return true;
            }
        }

        private static List<WwpComboProperties.Requested> Request(string json)
        {
            Assert.Null(WwpComboProperties.Parse(JToken.Parse(json), out var requested));
            return requested;
        }

        private static readonly string[] Candidates = { "Company", "Client", "Code" };

        [Fact]
        public void Plan_PredictsTheStoredValueAndWhatChanges()
        {
            var element = new FakeCombo();

            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"Parameters\": [\"A\", \"B\", \"Category\"], \"LoadDataDynamically\": true, \"ConditionsDynamicAttsOrVars\": [\"Company\", \"Client\"]}"),
                Candidates);

            Assert.Null(plan.Error);
            Assert.True(plan.AnyChange);
            Assert.Equal("A, B, Category", plan.Expected()["controlInputDPParameters"]);
            Assert.Equal("True", plan.Expected()["controlLoadDynamically"]);
            Assert.Equal("Company;Client", plan.Expected()["conditionDynAtts"]);
            JObject report = plan.ToReport(element.Path, element.Reference);
            JObject parameters = (JObject)report["properties"]["Parameters"];
            Assert.Equal("A, B", (string)parameters["before"]);
            Assert.Equal("A, B, Category", (string)parameters["after"]);
            Assert.Equal("controlInputDPParameters", (string)parameters["sdkName"]);
            Assert.True((bool)parameters["changed"]);
            Assert.Empty(report["unknownToSdk"]);
        }

        [Fact]
        public void Plan_ReportsWhenTheSdkRendersAValueDifferentlyFromTheRequest()
        {
            var element = new FakeCombo { Render = (sdk, text) => sdk == "controlInputDPParameters" ? text.Replace(" ", string.Empty) : text };

            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"Parameters\": \"A, B, C\", \"LoadDataDynamically\": true}"), Candidates);

            JObject properties = (JObject)plan.ToReport(element.Path, element.Reference)["properties"];
            Assert.True((bool)properties["Parameters"]["normalizedBySdk"]);
            Assert.Equal("A,B,C", (string)properties["Parameters"]["after"]);
            Assert.Equal("A, B, C", (string)properties["Parameters"]["requested"]);
            Assert.False((bool)properties["LoadDataDynamically"]["normalizedBySdk"]);
        }

        [Fact]
        public void Plan_UnchangedValues_ReportNoChange()
        {
            var element = new FakeCombo();

            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"Parameters\": \"A, B\"}"), Candidates);

            Assert.Null(plan.Error);
            Assert.False(plan.AnyChange);
            Assert.False((bool)plan.ToReport(element.Path, element.Reference)["properties"]["Parameters"]["changed"]);
        }

        [Fact]
        public void Plan_PropertyTheElementDoesNotDeclare_IsRefusedAsDiscardedOnSave()
        {
            var element = new FakeCombo();
            element.Declared.Remove("conditionDynAtts");

            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"ConditionsDynamicAttsOrVars\": [\"Company\"]}"), Candidates);

            Assert.Equal("ComboPropertyUnknownToSdk", (string)plan.Error["code"]);
            Assert.True((bool)plan.Error["discardedOnSave"]);
            Assert.Equal("conditionDynAtts", (string)plan.Error["sdkName"]);
        }

        [Fact]
        public void Plan_ReadOnlyAndInapplicableProperties_AreRefused()
        {
            var readOnly = new FakeCombo();
            readOnly.ReadOnlyProperties.Add("controlInputDPParameters");
            Assert.Equal("ComboPropertyReadOnly",
                (string)WwpComboPlanner.Plan(readOnly, Request("{\"Parameters\": \"A\"}"), Candidates).Error["code"]);

            var inapplicable = new FakeCombo();
            inapplicable.Inapplicable.Add("controlLoadDynamically");
            Assert.Equal("ComboPropertyNotApplicable",
                (string)WwpComboPlanner.Plan(inapplicable, Request("{\"LoadDataDynamically\": true}"), Candidates).Error["code"]);
        }

        [Fact]
        public void Plan_ValueTheSdkConverterRejects_IsRefusedWithTheSdkMessage()
        {
            var element = new FakeCombo { ConvertError = (sdk, text) => sdk == "controlInputDPParameters" ? "FormatException: bad" : null };

            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"Parameters\": \"A\"}"), Candidates);

            Assert.Equal("ComboPropertyRejectedBySdk", (string)plan.Error["code"]);
            Assert.Contains("FormatException: bad", (string)plan.Error["error"]);
        }

        [Theory]
        [InlineData("Based on", "DVelop Combo", "Data Provider")]
        [InlineData("Custom", "Edit", "Data Provider")]
        [InlineData("Custom", "DVelop Combo", "Fixed Values")]
        public void Plan_AControlThatCannotUseLoadDynamically_IsRefusedWithItsCurrentConfiguration(string info, string type, string source)
        {
            var element = new FakeCombo();
            element.Values["controlInfoDef"] = info;
            element.Values["controlType"] = type;
            element.Values["controlInputDS"] = source;

            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"LoadDataDynamically\": true}"), Candidates);

            Assert.Equal("ComboPropertyNotApplicable", (string)plan.Error["code"]);
            Assert.Equal(type, (string)plan.Error["current"]["controlType"]);
            // `current` is what is stored now, not what the request would set.
            Assert.Equal("False", (string)plan.Error["current"]["controlLoadDynamically"]);
            Assert.Equal(source, (string)plan.Error["current"]["controlInputDS"]);
            Assert.Contains("does not change them", (string)plan.Error["error"]);
        }

        [Fact]
        public void Plan_ConditionsAreApplicableWhenTheSameRequestTurnsTheLoadFlagOn()
        {
            var element = new FakeCombo();

            WwpComboPlan together = WwpComboPlanner.Plan(element,
                Request("{\"ConditionsDynamicAttsOrVars\": [\"Company\"], \"LoadDataDynamically\": true}"), Candidates);
            WwpComboPlan alone = WwpComboPlanner.Plan(element, Request("{\"ConditionsDynamicAttsOrVars\": [\"Company\"]}"), Candidates);

            Assert.Null(together.Error);
            Assert.Equal("ComboPropertyNotApplicable", (string)alone.Error["code"]);
            Assert.Equal("conditionDynAtts", (string)alone.Error["sdkName"]);
        }

        [Fact]
        public void Plan_ClearingAPropertyTheControlCannotUse_IsAllowed()
        {
            var element = new FakeCombo();
            element.Values["controlType"] = "Edit";
            element.Values["conditionDynAtts"] = "Company";
            element.Values["controlLoadDynamically"] = "True";

            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"ConditionsDynamicAttsOrVars\": [], \"LoadDataDynamically\": false}"), Candidates);

            Assert.Null(plan.Error);
            Assert.True(plan.AnyChange);
        }

        [Fact]
        public void Plan_ConditionNamesThatAreNotOnTheForm_AreRefusedWithTheCandidates()
        {
            var element = new FakeCombo();

            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"LoadDataDynamically\": true, \"ConditionsDynamicAttsOrVars\": [\"Company\", \"Missing\"]}"), Candidates);

            Assert.Equal("ComboConditionNameNotFound", (string)plan.Error["code"]);
            Assert.Equal(new[] { "Missing" }, plan.Error["unresolved"].Select(t => (string)t));
            Assert.Equal(new[] { "Client", "Code", "Company" }, plan.Error["candidates"].Select(t => (string)t));
        }

        [Fact]
        public void Plan_ConditionNames_MatchCaseInsensitively_AndAcceptSdtItems()
        {
            var element = new FakeCombo();

            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"LoadDataDynamically\": true, \"ConditionsDynamicAttsOrVars\": [\"company\", \"Client.Id\"]}"), Candidates);

            Assert.Null(plan.Error);
        }

        [Fact]
        public void Apply_IssuesTheChangedPropertiesInDependencyOrder_NotInRequestOrder()
        {
            var element = new FakeCombo();
            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"ConditionsDynamicAttsOrVars\": [\"Company\"], \"LoadDataDynamically\": true, \"Parameters\": \"A, B, Category\"}"), Candidates);

            Assert.Null(WwpComboPlanner.Apply(element, plan));

            Assert.Equal(new[] { "controlInputDPParameters=A, B, Category", "controlLoadDynamically=True", "conditionDynAtts=Company" }, element.Applied);
        }

        [Fact]
        public void Apply_SkipsUnchangedProperties()
        {
            var element = new FakeCombo();
            WwpComboPlan plan = WwpComboPlanner.Plan(element, Request("{\"Parameters\": \"A, B\", \"LoadDataDynamically\": true}"), Candidates);

            Assert.Null(WwpComboPlanner.Apply(element, plan));

            Assert.Equal(new[] { "controlLoadDynamically=True" }, element.Applied);
        }

        [Fact]
        public void Apply_StopsAtTheFirstPropertyTheSdkRefuses_AndNamesIt()
        {
            var element = new FakeCombo { RefuseApply = "controlLoadDynamically" };
            WwpComboPlan plan = WwpComboPlanner.Plan(element,
                Request("{\"LoadDataDynamically\": true, \"ConditionsDynamicAttsOrVars\": [\"Company\"]}"), Candidates);

            Assert.Equal("controlLoadDynamically", WwpComboPlanner.Apply(element, plan));

            Assert.Equal(new[] { "controlLoadDynamically=True" }, element.Applied);
        }

        [Fact]
        public void ApplyOrder_CoversEveryDefinition()
        {
            Assert.Equal(WwpComboProperties.All.Select(d => d.SdkName).OrderBy(n => n),
                WwpComboProperties.ApplyOrder.OrderBy(n => n));
        }

        private const string Instance = @"<instance>
  <transaction>
    <table name='TableMain'>
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Company' />
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Client' />
      <variable name='&amp;Filter' />
      <gridVariable name='Row' />
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Category' controlType='DVelop Combo' />
    </table>
  </transaction>
  <WPRoot>
    <table name='Other'>
      <attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Elsewhere' />
    </table>
  </WPRoot>
</instance>";

        [Fact]
        public void CandidateNames_AreTheAttributesAndVariablesOfTheSameFormSection_WithoutTheTarget()
        {
            XDocument document = XDocument.Parse(Instance);
            XElement target = WwpComboPlanner.FindAttributeElements(document, "Category").Single();

            List<string> names = WwpComboPlanner.CandidateNames(target);

            Assert.Equal(new[] { "Client", "Company", "Filter", "Row" }, names.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void FindAttributeElements_MatchesByNameOrByReference_AndReportsDuplicates()
        {
            XDocument document = XDocument.Parse(Instance);

            Assert.Single(WwpComboPlanner.FindAttributeElements(document, "Category"));
            Assert.Single(WwpComboPlanner.FindAttributeElements(document, "adbb33c9-0906-4971-833c-998de27e0676-Category"));
            Assert.Empty(WwpComboPlanner.FindAttributeElements(document, "Cat"));
            document.Descendants("table").First().Add(new XElement("attribute",
                new XAttribute("attribute", "adbb33c9-0906-4971-833c-998de27e0676-Category")));
            Assert.Equal(2, WwpComboPlanner.FindAttributeElements(document, "Category").Count);
        }

        [Theory]
        [InlineData("adbb33c9-0906-4971-833c-998de27e0676-Category", "Category")]
        [InlineData("Category", "Category")]
        [InlineData("", "")]
        public void NameFromReference_DropsTheTypeGuid(string reference, string expected)
        {
            Assert.Equal(expected, WwpComboPlanner.NameFromReference(reference));
        }
    }
}
