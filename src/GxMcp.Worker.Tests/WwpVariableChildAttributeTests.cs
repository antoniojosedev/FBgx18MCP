using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The `variable` child of add_table and add_tab. The engine stores an attribute only when its value differs from the pattern's own
    // default (WorkWithPlusInstance.xml): on a real package a VarChar(40), a Numeric(4) with 0 decimals and a controlEmptyItem of false
    // came back without those attributes, so the re-read check of a write that stated them failed and the whole write was rolled back.
    // The plan and the native write read one list of attributes, so what the plan expects is what the write sets, and a value the write
    // would refuse is refused by the dryRun first.
    public class WwpVariableChildAttributeTests
    {
        private static Dictionary<string, object> Attributes(JObject control)
            => WwpActionService.VariableAttributes(control, (string)control["basicType"] ?? "VarChar").ToDictionary(p => p.Key, p => p.Value);

        private static JObject Variable(string basicType, object length = null, object decimals = null) => new JObject
        {
            ["type"] = "variable", ["name"] = "v", ["basicType"] = basicType,
            ["length"] = length == null ? null : JToken.FromObject(length),
            ["decimals"] = decimals == null ? null : JToken.FromObject(decimals)
        };

        [Theory]
        [InlineData("VarChar", 40, null, "basicCLength")]
        [InlineData("Character", 40, null, "basicCLength")]
        [InlineData("Numeric", 4, null, "basicLength")]
        [InlineData("Numeric", 4, 0, "basicDecimals")]
        [InlineData("LongVarChar", 2097152, null, "basicLVCLength")]
        public void AValueThatIsThePatternDefault_IsNotExpectedBack(string basicType, object length, object decimals, string attribute)
        {
            Dictionary<string, object> attributes = Attributes(Variable(basicType, length, decimals));

            Assert.DoesNotContain(attribute, attributes.Keys);
        }

        [Theory]
        [InlineData("VarChar", 41, null, "basicCLength", 41)]
        [InlineData("Character", 39, null, "basicCLength", 39)]
        [InlineData("Numeric", 9, 2, "basicLength", 9)]
        [InlineData("Numeric", 9, 2, "basicDecimals", 2)]
        [InlineData("LongVarChar", 100, null, "basicLVCLength", 100)]
        public void AValueThatDiffersFromTheDefault_IsExpectedBackAsANumber(string basicType, object length, object decimals, string attribute, int expected)
        {
            Dictionary<string, object> attributes = Attributes(Variable(basicType, length, decimals));

            Assert.Equal(expected, Assert.IsType<int>(attributes[attribute]));
        }

        [Fact]
        public void NumericIsTheDefaultBasicType_OtherTypesAreExpectedBack()
        {
            Assert.DoesNotContain("basicType", Attributes(Variable("Numeric", 8, 2)).Keys);
            Assert.Equal("VarChar", Attributes(Variable("VarChar", 10))["basicType"]);
            Assert.Equal("Character", Attributes(Variable("Character", 10))["basicType"]);
        }

        [Fact]
        public void TheControlDefaultsAreNotExpectedBack_AndTheEmptyItemFlagStaysABool()
        {
            JObject defaults = Variable("VarChar", 50);
            defaults["controlType"] = "<default>";
            defaults["controlEmptyItem"] = false;
            defaults["controlEmptyItemText"] = "GX_EmptyItemText";
            Dictionary<string, object> kept = Attributes(defaults);
            Assert.DoesNotContain("controlType", kept.Keys);
            Assert.DoesNotContain("controlEmptyItem", kept.Keys);
            Assert.DoesNotContain("controlEmptyItemText", kept.Keys);

            JObject custom = Variable("VarChar", 50);
            custom["controlType"] = "Combo Box";
            custom["controlEmptyItem"] = true;
            custom["controlEmptyItemText"] = "Choose";
            Dictionary<string, object> stated = Attributes(custom);
            Assert.Equal("Combo Box", stated["controlType"]);
            // A bool in the pattern: the text "True" is stored as a string and the element fails its cast when read back.
            Assert.True(Assert.IsType<bool>(stated["controlEmptyItem"]));
            Assert.Equal("Choose", stated["controlEmptyItemText"]);
        }

        [Fact]
        public void AnExplicitNull_IsTheSameAsLeavingTheValueOut()
        {
            var control = new JObject
            {
                ["type"] = "variable", ["name"] = "v", ["basicType"] = "VarChar", ["length"] = null, ["decimals"] = null, ["description"] = null,
                ["controlType"] = null, ["controlValues"] = null, ["controlEmptyItem"] = null, ["controlEmptyItemText"] = null
            };

            Assert.Equal(new[] { "basicType" }, Attributes(control).Keys);
        }

        [Theory]
        [InlineData("length", "x", "length must be an integer.")]
        [InlineData("length", "", "length must be an integer.")]
        [InlineData("decimals", "y", "decimals must be an integer.")]
        [InlineData("controlEmptyItem", "maybe", "controlEmptyItem must be true or false.")]
        public void WhatTheWriteWouldRefuse_IsRefusedWhileThePlanIsBuilt(string property, string value, string message)
        {
            JObject control = Variable("VarChar", 10);
            control[property] = value;

            // WwpTabException is private to the service; the plan turns it into the error envelope the other tests read.
            Exception refused = Assert.ThrowsAny<Exception>(() => WwpActionService.VariableAttributes(control, "VarChar"));

            Assert.Equal("WwpTabException", refused.GetType().Name);
            Assert.Equal("InvalidWwpChild", refused.GetType()
                .GetField("Code", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(refused));
            Assert.Equal(message, refused.Message);
        }

        // ---- the dryRun plan -------------------------------------------------------------------------------------------------

        private const string TransactionForm =
            "<instance><transaction><table name='TableMain'><table name='TableContent'>" +
            "<table name='TableAttributes'/></table></table></transaction></instance>";

        private static JObject AddTab(XDocument document, params JToken[] children) => WwpActionService.ApplyTabXml(document, "add_tab", new JObject
        {
            ["controlName"] = "TabX", ["title"] = "X", ["children"] = new JArray(children)
        });

        [Fact]
        public void TheTabPlanCarriesNoPairForADefaultAndKeepsTheOthers()
        {
            var document = XDocument.Parse(TransactionForm);

            JObject result = AddTab(document,
                new JObject { ["type"] = "variable", ["name"] = "vText", ["basicType"] = "VarChar", ["length"] = 40 },
                new JObject { ["type"] = "variable", ["name"] = "vOther", ["basicType"] = "VarChar", ["length"] = 41 },
                new JObject { ["type"] = "variable", ["name"] = "vNum", ["basicType"] = "Numeric", ["length"] = 4, ["decimals"] = 0 });

            Assert.Null(result["error"]);
            List<XElement> variables = document.Descendants("tab").Single().Element("table").Elements("variable").ToList();
            XElement text = variables.Single(v => (string)v.Attribute("name") == "vText");
            Assert.Null(text.Attribute("basicCLength"));
            Assert.Null(text.Attribute("defaultBasicCLength"));
            Assert.Equal("VarChar", (string)text.Attribute("basicType"));
            XElement other = variables.Single(v => (string)v.Attribute("name") == "vOther");
            Assert.Equal("41", (string)other.Attribute("basicCLength"));
            Assert.Equal("41", (string)other.Attribute("defaultBasicCLength"));
            XElement number = variables.Single(v => (string)v.Attribute("name") == "vNum");
            Assert.Null(number.Attribute("basicLength"));
            Assert.Null(number.Attribute("basicDecimals"));
            Assert.Null(number.Attribute("basicType"));
        }

        [Fact]
        public void TheTabPlanWritesTheEmptyItemFlagTheWayThePatternSpellsABool()
        {
            var document = XDocument.Parse(TransactionForm);

            AddTab(document, new JObject
            {
                ["type"] = "variable", ["name"] = "vCombo", ["basicType"] = "VarChar", ["length"] = 20,
                ["controlType"] = "Combo Box", ["controlEmptyItem"] = true
            });

            XElement variable = document.Descendants("variable").Single();
            Assert.Equal("True", (string)variable.Attribute("controlEmptyItem"));
            Assert.Equal("True", (string)variable.Attribute("defaultControlEmptyItem"));
        }

        [Fact]
        public void ChildrenWithoutAnObject_AreTreatedAsNoChildren()
        {
            foreach (JToken entry in new JToken[] { JValue.CreateNull(), "x", 3 })
            {
                var document = XDocument.Parse(TransactionForm);

                JObject result = AddTab(document, entry);

                Assert.Null(result["error"]);
                XElement tab = document.Descendants("tab").Single();
                Assert.Empty(tab.Element("table").Elements());
            }
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("two")]
        public void ATableChildWithBadColumns_IsRefusedByThePlan(string columns)
        {
            var document = XDocument.Parse(TransactionForm);

            JObject result = AddTab(document, new JObject { ["type"] = "table", ["name"] = "T1", ["columns"] = columns });

            Assert.Equal("InvalidWwpChild", (string)result["code"]);
            Assert.Equal("A table child's columns must be an integer of at least 1.", (string)result["error"]);
            Assert.Empty(document.Descendants("tab"));
        }

        [Fact]
        public void ATableChildWithNullColumns_KeepsTheTableUnnumbered()
        {
            var document = XDocument.Parse(TransactionForm);

            JObject result = AddTab(document, new JObject { ["type"] = "table", ["name"] = "T1", ["columns"] = null });

            Assert.Null(result["error"]);
            Assert.Null(document.Descendants("table").Single(t => (string)t.Attribute("name") == "T1").Attribute("numberOfColumns"));
        }

        [Fact]
        public void AVariableLengthTheWriteWouldRefuse_IsRefusedByThePlan()
        {
            var document = XDocument.Parse(TransactionForm);

            JObject result = AddTab(document, new JObject { ["type"] = "variable", ["name"] = "v", ["basicType"] = "VarChar", ["length"] = "x" });

            Assert.Equal("InvalidWwpChild", (string)result["code"]);
            Assert.Equal("length must be an integer.", (string)result["error"]);
        }
    }
}
