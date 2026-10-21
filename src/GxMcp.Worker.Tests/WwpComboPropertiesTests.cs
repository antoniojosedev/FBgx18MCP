using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // set_combo_properties: how a request is read. The SDK property names come from the
    // WorkWithPlus instance specification (ElementType Attribute); a key the specification does not
    // declare is discarded by PatternInstanceElementAttributes.FromXml on save, so it is refused here.
    public class WwpComboPropertiesTests
    {
        private static JObject Parse(string json, out List<WwpComboProperties.Requested> requested)
            => WwpComboProperties.Parse(JToken.Parse(json), out requested);

        [Theory]
        [InlineData("controlLoadDataDynamically", "LoadDataDynamically", "controlLoadDynamically")]
        [InlineData("controlConditionsDynamicAttsOrVars", "ConditionsDynamicAttsOrVars", "conditionDynAtts")]
        public void TheNamesFromTheFieldReport_AreRefusedAsUnknownToTheSdk_WithTheRealName(string reported, string publicName, string sdkName)
        {
            JObject error = Parse("{\"" + reported + "\": true}", out _);

            Assert.Equal("UnknownComboProperty", (string)error["code"]);
            JObject unknown = (JObject)error["unknown"].Single();
            Assert.Equal(reported, (string)unknown["property"]);
            Assert.Equal(publicName, (string)unknown["didYouMean"]);
            Assert.Equal(sdkName, (string)unknown["sdkName"]);
            Assert.Contains("discarded", (string)error["error"]);
        }

        [Fact]
        public void AValidRequestIsNotPartlyAccepted_WhenAnotherKeyIsUnknown()
        {
            JObject error = Parse("{\"Parameters\": \"a\", \"controlLoadDataDynamically\": true}", out _);

            Assert.Equal("UnknownComboProperty", (string)error["code"]);
            Assert.Single(error["unknown"]);
        }

        [Theory]
        [InlineData("Parameters")]
        [InlineData("parameters")]
        [InlineData("DataProviderParameters")]
        [InlineData("controlInputDPParameters")]
        public void ParametersAliasesResolveToTheSdkProperty(string key)
        {
            Assert.Null(Parse("{\"" + key + "\": \"a\"}", out var requested));
            Assert.Equal("controlInputDPParameters", requested.Single().Definition.SdkName);
        }

        [Theory]
        [InlineData("LoadDataDynamically")]
        [InlineData("Load Data Dynamically")]
        [InlineData("controlLoadDynamically")]
        [InlineData("LOADDYNAMICALLY")]
        public void LoadAliasesResolveToTheSdkProperty(string key)
        {
            Assert.Null(Parse("{\"" + key + "\": true}", out var requested));
            Assert.Equal("controlLoadDynamically", requested.Single().Definition.SdkName);
        }

        [Theory]
        [InlineData("ConditionsDynamicAttsOrVars")]
        [InlineData("Conditions dynamic atts. or vars.")]
        [InlineData("conditionDynAtts")]
        public void ConditionAliasesResolveToTheSdkProperty(string key)
        {
            Assert.Null(Parse("{\"" + key + "\": \"A\"}", out var requested));
            Assert.Equal("conditionDynAtts", requested.Single().Definition.SdkName);
        }

        [Fact]
        public void TwoKeysForOnePropertyAreRefused()
        {
            JObject error = Parse("{\"LoadDataDynamically\": true, \"controlLoadDynamically\": false}", out _);
            Assert.Equal("DuplicateComboProperty", (string)error["code"]);
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{}")]
        [InlineData("[1]")]
        public void MissingOrEmptyPropertiesAreRefused(string json)
        {
            Assert.Equal("MissingComboProperties", (string)WwpComboProperties.Parse(JToken.Parse(json), out _)["code"]);
        }

        [Fact]
        public void NoPropertiesToken_IsRefused()
        {
            Assert.Equal("MissingComboProperties", (string)WwpComboProperties.Parse(null, out _)["code"]);
        }

        [Fact]
        public void ParametersArray_IsJoinedWithCommaSpace_AndTrimmed()
        {
            Assert.Null(Parse("{\"Parameters\": [\" List.Current \", \"&Company\", \"&Client\", \"Domain.Value\", \"Code\"]}", out var requested));
            Assert.Equal("List.Current, &Company, &Client, Domain.Value, Code", requested.Single().Text);
        }

        [Fact]
        public void ParametersString_KeepsCommasInsideCallsAndQuotes()
        {
            Assert.Null(Parse("{\"Parameters\": \"Fn(a, b), 'x, y', &V\"}", out var requested));
            Assert.Equal("Fn(a, b), 'x, y', &V", requested.Single().Text);
        }

        [Theory]
        [InlineData("[\"a\", \"\"]")]
        [InlineData("[\"a\", 3]")]
        [InlineData("\"a,,b\"")]
        [InlineData("\"a, b,\"")]
        [InlineData("[\"Fn(a\"]")]
        [InlineData("[\"a, b\"]")]
        [InlineData("\"it's\"")]
        [InlineData("7")]
        public void InvalidParameterListsAreRefused(string value)
        {
            JObject error = Parse("{\"Parameters\": " + value + "}", out _);
            Assert.Equal("InvalidComboPropertyValue", (string)error["code"]);
            Assert.Equal("Parameters", (string)error["property"]);
        }

        [Fact]
        public void EmptyParameters_ClearTheList()
        {
            Assert.Null(Parse("{\"Parameters\": []}", out var requested));
            Assert.Equal(string.Empty, requested.Single().Text);
            Assert.Null(Parse("{\"Parameters\": \"\"}", out requested));
            Assert.Equal(string.Empty, requested.Single().Text);
        }

        [Theory]
        [InlineData("true", "True")]
        [InlineData("false", "False")]
        [InlineData("\"True\"", "True")]
        [InlineData("\"false\"", "False")]
        public void LoadDataDynamically_AcceptsBooleans(string value, string expected)
        {
            Assert.Null(Parse("{\"LoadDataDynamically\": " + value + "}", out var requested));
            Assert.Equal(expected, requested.Single().Text);
        }

        [Theory]
        [InlineData("\"yes\"")]
        [InlineData("1")]
        [InlineData("null")]
        public void LoadDataDynamically_RefusesNonBooleans(string value)
        {
            JObject error = Parse("{\"LoadDataDynamically\": " + value + "}", out _);
            Assert.Equal("InvalidComboPropertyValue", (string)error["code"]);
        }

        [Fact]
        public void ConditionNames_AreJoinedWithSemicolon_StrippingTheAmpersand()
        {
            Assert.Null(Parse("{\"ConditionsDynamicAttsOrVars\": [\"CompanyId\", \"&ClientId\", \"Var.Item\"]}", out var requested));
            Assert.Equal("CompanyId;ClientId;Var.Item", requested.Single().Text);
            Assert.Null(Parse("{\"ConditionsDynamicAttsOrVars\": \"CompanyId; ClientId\"}", out requested));
            Assert.Equal("CompanyId;ClientId", requested.Single().Text);
        }

        [Theory]
        [InlineData("\"CompanyId,ClientId\"")]
        [InlineData("\"Company Id\"")]
        [InlineData("[\"A\", \"a\"]")]
        [InlineData("[\"1A\"]")]
        [InlineData("[2]")]
        [InlineData("true")]
        public void InvalidConditionNamesAreRefused(string value)
        {
            JObject error = Parse("{\"ConditionsDynamicAttsOrVars\": " + value + "}", out _);
            Assert.Equal("InvalidComboPropertyValue", (string)error["code"]);
        }

        [Fact]
        public void Suggest_FindsTheSupportedNameForNearMissesAndNothingForStrangers()
        {
            Assert.Equal("controlLoadDynamically", WwpComboProperties.Suggest("loadDataDynamicaly")?.SdkName);
            Assert.Equal("controlInputDPParameters", WwpComboProperties.Suggest("dpParameter")?.SdkName);
            Assert.Null(WwpComboProperties.Suggest("backgroundColor"));
            Assert.Null(WwpComboProperties.Suggest("ab"));
        }

        [Fact]
        public void SupportedNames_ListTheSdkNamesAndTheirRequirements()
        {
            JArray supported = WwpComboProperties.SupportedNames();

            Assert.Equal(new[] { "controlInputDPParameters", "controlLoadDynamically", "conditionDynAtts" },
                supported.Select(s => (string)s["sdkName"]));
            Assert.All(supported, s => Assert.Contains("controlInfoDef=Custom", (string)s["requires"]));
        }

        // The VisibleIf strings below are copied from WorkWithPlusInstance.xml (ElementType Attribute).
        private const string LoadVisibleIf = "self::*[@controlInfoDef='Custom' and (@controlType='DVelop Combo' or @controlType='Extended Suggest') and @controlInputDS!='Fixed Values']";
        private const string ConditionsVisibleIf = "self::*[@controlInfoDef='Custom' and (@controlType='DVelop Combo' or @controlType='Extended Suggest') and @controlLoadDynamically='True']";

        private static Dictionary<string, string> Values(string info, string type, string source, string load) => new Dictionary<string, string>
        {
            ["controlInfoDef"] = info, ["controlType"] = type, ["controlInputDS"] = source, ["controlLoadDynamically"] = load
        };

        [Theory]
        [InlineData("Custom", "DVelop Combo", "Data Provider", "False", true)]
        [InlineData("Custom", "Extended Suggest", "Attributes", "False", true)]
        [InlineData("Custom", "DVelop Combo", "Fixed Values", "False", false)]
        [InlineData("Custom", "Dynamic Combo Box", "Data Provider", "False", false)]
        [InlineData("Based on", "DVelop Combo", "Data Provider", "False", false)]
        public void LoadDynamically_IsApplicableOnlyToADvelopComboNotOnFixedValues(string info, string type, string source, string load, bool expected)
        {
            Assert.Equal(expected, WwpComboProperties.IsApplicable(LoadVisibleIf, Values(info, type, source, load), out bool evaluated));
            Assert.True(evaluated);
        }

        [Theory]
        [InlineData("True", true)]
        [InlineData("False", false)]
        public void Conditions_NeedTheLoadFlag(string load, bool expected)
        {
            Assert.Equal(expected, WwpComboProperties.IsApplicable(ConditionsVisibleIf, Values("Custom", "DVelop Combo", "Data Provider", load), out _));
        }

        [Fact]
        public void AnEmptyOrBrokenVisibleIf_IsApplicable_ButNotMarkedEvaluated()
        {
            Assert.True(WwpComboProperties.IsApplicable(null, Values("Custom", "x", "y", "False"), out bool evaluated));
            Assert.False(evaluated);
            Assert.True(WwpComboProperties.IsApplicable("self::*[[", Values("Custom", "x", "y", "False"), out evaluated));
            Assert.False(evaluated);
        }

        [Fact]
        public void EffectiveValuesWithNonXmlNames_DoNotBreakTheEvaluation()
        {
            var values = Values("Custom", "DVelop Combo", "Data Provider", "True");
            values["[ElementName]"] = "x";
            values[""] = "y";

            Assert.True(WwpComboProperties.IsApplicable(ConditionsVisibleIf, values, out bool evaluated));
            Assert.True(evaluated);
        }
    }
}
