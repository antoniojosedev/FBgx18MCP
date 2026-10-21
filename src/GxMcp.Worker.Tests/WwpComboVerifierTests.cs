using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // set_combo_properties verification: the whole instance is compared with what the write was
    // meant to change, and the projected parent form is checked for what its text can prove.
    public class WwpComboVerifierTests
    {
        private const string Ref = "adbb33c9-0906-4971-833c-998de27e0676-Category";

        private static string Instance(string comboAttributes, string other = "x") =>
            "<instance><transaction><table name='TableMain'>"
            + "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Company' description='" + other + "' />"
            + "<attribute attribute='" + Ref + "' controlType='DVelop Combo' " + comboAttributes + " />"
            + "</table></transaction></instance>";

        private static readonly Dictionary<string, string> Expected = new Dictionary<string, string>
        {
            ["controlInputDPParameters"] = "A, B, C",
            ["controlLoadDynamically"] = "True"
        };

        private static JObject Verify(string before, string after, IReadOnlyDictionary<string, string> effective = null, int ordinal = 0) =>
            WwpComboVerifier.Verify(XDocument.Parse(before), XDocument.Parse(after), Ref, ordinal, Expected, effective);

        [Fact]
        public void ConfirmsTheRequestedValues_WhenTheRestOfTheInstanceIsUntouched_RegardlessOfAttributeOrder()
        {
            string before = Instance("controlInputDPParameters='A'");
            string after = Instance("controlLoadDynamically='True' controlInputDPParameters='A, B, C'");

            JObject result = Verify(before, after);

            Assert.True((bool)result["confirmed"]);
            Assert.True((bool)result["restOfInstanceUnchanged"]);
            Assert.Empty(result["unexpectedChanges"]);
            Assert.Equal("A, B, C", (string)result["properties"]["controlInputDPParameters"]["persistedXml"]);
        }

        [Fact]
        public void RefusesAValueThatWasNotPersisted()
        {
            string before = Instance("controlInputDPParameters='A'");
            string after = Instance("controlLoadDynamically='True' controlInputDPParameters='A'");

            JObject result = Verify(before, after);

            Assert.False((bool)result["confirmed"]);
            Assert.False((bool)result["propertiesConfirmed"]);
            Assert.False((bool)result["properties"]["controlInputDPParameters"]["confirmed"]);
            Assert.True((bool)result["properties"]["controlLoadDynamically"]["confirmed"]);
            Assert.Contains("does not hold the requested combo properties", (string)result["message"]);
        }

        [Fact]
        public void AnOmittedAttribute_IsConfirmedOnlyByTheNativeEffectiveValue()
        {
            string before = Instance("controlLoadDynamically='True' controlInputDPParameters='A'");
            string after = Instance("controlInputDPParameters='A, B, C'");

            Assert.False((bool)Verify(before, after)["confirmed"]);
            Assert.False((bool)Verify(before, after, new Dictionary<string, string>
            {
                ["controlInputDPParameters"] = "A, B, C", ["controlLoadDynamically"] = "False"
            })["confirmed"]);
            Assert.True((bool)Verify(before, after, new Dictionary<string, string>
            {
                ["controlInputDPParameters"] = "A, B, C", ["controlLoadDynamically"] = "True"
            })["confirmed"]);
        }

        [Fact]
        public void AnEmptyExpectedValue_MayBeOmittedByTheSave()
        {
            var expected = new Dictionary<string, string> { ["conditionDynAtts"] = string.Empty };
            string before = Instance("conditionDynAtts='X'");
            string after = Instance(string.Empty);

            JObject result = WwpComboVerifier.Verify(XDocument.Parse(before), XDocument.Parse(after), Ref, 0, expected, null);

            Assert.True((bool)result["confirmed"]);
        }

        [Fact]
        public void ChangesElsewhereInTheInstance_AreReportedAndRefuse()
        {
            string before = Instance("controlInputDPParameters='A'", other: "old");
            string after = Instance("controlLoadDynamically='True' controlInputDPParameters='A, B, C'", other: "new");

            JObject result = Verify(before, after);

            Assert.True((bool)result["propertiesConfirmed"]);
            Assert.False((bool)result["confirmed"]);
            Assert.False((bool)result["restOfInstanceUnchanged"]);
            Assert.Contains("@description: old -> new", string.Join("|", result["unexpectedChanges"].Select(t => (string)t)));
            Assert.Contains("beyond the requested properties", (string)result["message"]);
        }

        [Fact]
        public void AnAddedOrRemovedElement_IsAnUnexpectedChange()
        {
            string before = Instance("controlInputDPParameters='A'");
            string after = before.Replace("</table>", "<attribute attribute='extra' /></table>")
                .Replace("controlInputDPParameters='A'", "controlInputDPParameters='A, B, C' controlLoadDynamically='True'");

            JObject result = Verify(before, after);

            Assert.False((bool)result["confirmed"]);
            Assert.Contains("children", string.Join("|", result["unexpectedChanges"].Select(t => (string)t)));
        }

        [Fact]
        public void TheSdkRenumberingItsOwnChildOrderList_IsNotAChange()
        {
            string before = Instance("controlInputDPParameters='A'").Replace("<table name='TableMain'>", "<table name='TableMain' childrenOrderedList='7;1;a'>");
            string after = Instance("controlInputDPParameters='A, B, C' controlLoadDynamically='True'")
                .Replace("<table name='TableMain'>", "<table name='TableMain' childrenOrderedList='7;2;b'>");

            Assert.True((bool)Verify(before, after)["confirmed"]);
        }

        [Fact]
        public void TheSecondOccurrenceOfARepeatedAttribute_IsVerifiedByItsOrdinal()
        {
            string row(string extra) => "<attribute attribute='" + Ref + "' " + extra + " />";
            string before = "<instance><t>" + row("controlInputDPParameters='A'") + "</t><u>" + row("controlInputDPParameters='A'") + "</u></instance>";
            string after = "<instance><t>" + row("controlInputDPParameters='A'") + "</t><u>"
                + row("controlInputDPParameters='A, B, C' controlLoadDynamically='True'") + "</u></instance>";

            Assert.True((bool)Verify(before, after, null, ordinal: 1)["confirmed"]);
            Assert.False((bool)Verify(before, after, null, ordinal: 0)["confirmed"]);
        }

        [Fact]
        public void TheTargetDisappearingOrMultiplying_IsNotFound()
        {
            string before = Instance("controlInputDPParameters='A'");
            string gone = "<instance><transaction /></instance>";

            JObject result = Verify(before, gone);

            Assert.False((bool)result["confirmed"]);
            Assert.False((bool)result["elementFound"]);
            Assert.Contains("1 before, 0 after", (string)result["message"]);
            Assert.False((bool)Verify(before, before, null, ordinal: 5)["confirmed"]);
        }

        // ── projection ────────────────────────────────────────────────────────

        private const string Form = "<GxMultiForm><Form type='layout'><detail><layout>"
            + "<data attribute='att:1' /><data attribute='att:2' /></layout></detail></Form></GxMultiForm>";

        [Fact]
        public void Projection_WhoseBeforeFormIsNotXml_IsNotConfirmed_EvenWhenItHasNoAttributeControls()
        {
            JObject result = WwpActionService.VerifyComboProjection("<GxMultiForm><broken", "<GxMultiForm><Form type='layout'/></GxMultiForm>");

            Assert.False((bool)result["confirmed"]);
            Assert.Contains("before the projection is not valid XML", (string)result["message"]);
        }

        [Fact]
        public void Projection_ThatKeepsTheFormAndItsAttributeControls_IsConfirmedStructurally()
        {
            JObject result = WwpActionService.VerifyComboProjection(Form, Form.Replace("<layout>", "<layout id='new'>"));

            Assert.True((bool)result["confirmed"]);
            Assert.Equal("structural", (string)result["level"]);
            Assert.True((bool)result["changed"]);
            Assert.Equal(2, (int)result["attributeControlsAfter"]);
        }

        [Fact]
        public void Projection_ThatLosesAnAttributeControl_IsRefused()
        {
            JObject result = WwpActionService.VerifyComboProjection(Form, Form.Replace("<data attribute='att:2' />", string.Empty));

            Assert.False((bool)result["confirmed"]);
            Assert.Contains("attribute controls on the WebForm (2 -> 1)", (string)result["message"]);
        }

        [Fact]
        public void Projection_ThatChangesTheRootOrIsNotXml_IsRefused()
        {
            Assert.False((bool)WwpActionService.VerifyComboProjection(Form, "<Other />")["confirmed"]);
            Assert.False((bool)WwpActionService.VerifyComboProjection(Form, "not xml")["confirmed"]);
            Assert.False((bool)WwpActionService.VerifyComboProjection(Form, null)["confirmed"]);
            Assert.False((bool)WwpActionService.VerifyComboProjection(Form, "  ")["confirmed"]);
        }

        [Fact]
        public void Projection_ThatKeepsEveryControlButChangesTheRootElement_IsRefused()
        {
            JObject result = WwpActionService.VerifyComboProjection(Form, Form.Replace("GxMultiForm", "OtherForm"));

            Assert.False((bool)result["confirmed"]);
            Assert.Contains("changed its root element from 'GxMultiForm' to 'OtherForm'", (string)result["message"]);
        }

        [Fact]
        public void Projection_Unchanged_IsReportedAsUnchangedButConfirmed()
        {
            JObject result = WwpActionService.VerifyComboProjection(Form, Form);

            Assert.True((bool)result["confirmed"]);
            Assert.False((bool)result["changed"]);
        }
    }
}
