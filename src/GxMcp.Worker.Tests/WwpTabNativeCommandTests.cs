using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;
using Element = GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element;

namespace GxMcp.Worker.Tests
{
    // Issue #552: the native add_tab path against the same fake element tree used by
    // WwpEmptyNativeCommandTests. It proves the command sequence, not the WorkWithPlus
    // package: whether the DVelop pattern accepts a synthesized tabs/tab in a Transaction
    // form is still unverified on a real KB.
    public class WwpTabNativeCommandTests
    {
        private static TabPart TransactionForm(out Element content, out Element attributes)
        {
            var part = new TabPart();
            Element transaction = part.RootElement.Children.CreateChildElement("transaction");
            part.RootElement.Children.Items.Add(transaction);
            Element main = transaction.Children.CreateChildElement("table");
            main.Attributes.SetPropertyValue("name", "TableMain");
            transaction.Children.Items.Add(main);
            content = main.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            main.Children.Items.Add(content);
            attributes = content.Children.CreateChildElement("table");
            attributes.Attributes.SetPropertyValue("name", "TableAttributes");
            content.Children.Items.Add(attributes);
            foreach (string name in new[] { "A", "B" })
            {
                Element attribute = attributes.Children.CreateChildElement("attribute");
                attribute.Attributes.SetPropertyValue("attribute", "adbb33c9-0906-4971-833c-998de27e0676-" + name);
                attributes.Children.Items.Add(attribute);
            }
            return part;
        }

        private static JObject AddTab(params JObject[] children) => new JObject
        {
            ["controlName"] = "TabX", ["title"] = "X", ["children"] = new JArray(children)
        };

        [Fact]
        public void NativeAddTab_SynthesizesTabsInsideTheUpdateAndMovesTheAttribute()
        {
            TabPart part = TransactionForm(out Element content, out Element attributes);
            Element existing = attributes.Children.Items[0];

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab",
                AddTab(new JObject { ["type"] = "attribute", ["attribute"] = "A", ["description"] = "Renamed" }));

            Assert.Null(result["error"]);
            Assert.Equal(1, part.Updates);
            Assert.False(part.TabsPresentWhenUpdateStarted, "the tabs container must be created inside ExecuteUpdate");
            Element tabs = content.Children.Items.Single(e => e.Type == "tabs");
            Element tab = tabs.Children.Items.Single();
            Assert.Equal("tab", tab.Type);
            Assert.Equal("TabX", tab.Attributes.GetPropertyValueString("ControlName"));
            Element table = tab.Children.Items.Single();
            Assert.Equal("Responsive", table.Attributes.GetPropertyValueString("type"));
            Assert.Same(existing, table.Children.Items.Single());
            Assert.Equal("Renamed", existing.Attributes.GetPropertyValueString("description"));
            Assert.Equal(new[] { "adbb33c9-0906-4971-833c-998de27e0676-B" },
                attributes.Children.Items.Select(e => e.Attributes.GetPropertyValueString("attribute")));
        }

        [Fact]
        public void NativeAddTab_RefusesAnArbitraryTable()
        {
            var part = new TabPart();
            Element selection = part.RootElement.Children.CreateChildElement("selection");
            part.RootElement.Children.Items.Add(selection);
            Element filters = selection.Children.CreateChildElement("table");
            filters.Attributes.SetPropertyValue("name", "TableFilters");
            selection.Children.Items.Add(filters);

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab());

            Assert.Equal("TabsContainerNotFound", (string)result["code"]);
            Assert.Equal(0, part.Updates);
            Assert.Empty(filters.Children.Items);
        }

        [Fact]
        public void NativeAddTab_AttributeRequestedTwice_IsRefused()
        {
            TabPart part = TransactionForm(out _, out _);
            var ex = Assert.ThrowsAny<Exception>(() => WwpActionService.ApplyNativeTabMutation(part, "add_tab",
                AddTab(new JObject { ["type"] = "attribute", ["attribute"] = "A" }, new JObject { ["type"] = "attribute", ["attribute"] = "A" })));
            Assert.Contains("requested more than once", (ex.InnerException ?? ex).Message);
        }

        // Issues #552/#595, proved on a real WorkWithPlus package (16.2, Transaction host): CreateChildElement answers a
        // `tab` that already holds its single mandatory `table`, a second table is refused, and the form's tab container
        // is the element named `tabs` whatever its type (TabsTransaction).
        [Fact]
        public void NativeAddTab_ConfiguresTheTableTheEngineCreatesInsteadOfAddingASecondOne()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab(
                new JObject { ["type"] = "textBlock", ["controlName"] = "help", ["caption"] = "Help" }));

            Assert.Null(result["error"]);
            Element tab = content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single();
            Element table = tab.Children.Items.Single();
            Assert.Equal("Responsive", table.Attributes.GetPropertyValueString("type"));
            Assert.Equal("help", table.Children.Items.Single().Attributes.GetPropertyValueString("controlName"));
        }

        [Fact]
        public void NativeAddTab_WithoutChildren_KeepsTheEngineTable()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            Assert.Null(WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab())["error"]);

            Element tab = content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single();
            Element table = tab.Children.Items.Single();
            Assert.Equal("table", table.Type);
            Assert.Empty(table.Children.Items);
            Assert.Null(table.Attributes.GetPropertyValueString("type"));
        }

        [Fact]
        public void NativeAddTab_AConfiguredTableChildTakesTheEngineTableAndTypesItsColumns()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab(
                new JObject { ["type"] = "table", ["name"] = "tblInTab", ["columns"] = 2 }));

            Assert.Null(result["error"]);
            Element table = content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single().Children.Items.Single();
            Assert.Equal("tblInTab", table.Attributes.GetPropertyValueString("name"));
            // numberOfColumns is a number in the pattern: stored as text it fails its cast when the table is read back.
            Assert.IsType<int>(table.Attributes.GetPropertyValue("numberOfColumns"));
            Assert.Equal(2, table.Attributes.GetPropertyValue("numberOfColumns"));
        }

        [Fact]
        public void NativeAddTab_MoreThanOneTableChild_IsRefusedBeforeAnythingIsCreated()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            var ex = Assert.ThrowsAny<Exception>(() => WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab(
                new JObject { ["type"] = "table", ["name"] = "one" }, new JObject { ["type"] = "table", ["name"] = "two" })));

            Assert.Contains("single table", (ex.InnerException ?? ex).Message);
        }

        [Theory]
        [InlineData("remove_tab")]
        [InlineData("move_tab")]
        public void NativeTabOperations_FindTheTabsContainerByItsElementName(string operation)
        {
            var part = new TabPart();
            Element named = new Element("TabsTransaction", name: "tabs") { Parent = part.RootElement };
            part.RootElement.Children.Items.Add(named);
            foreach (string control in new[] { "One", "Two" })
            {
                Element tab = named.Children.CreateChildElement("tab");
                tab.Attributes.SetPropertyValue("ControlName", control);
                named.Children.Items.Add(tab);
            }

            JObject result = WwpActionService.ApplyNativeTabMutation(part, operation,
                new JObject { ["controlName"] = "Two", ["position"] = 0 });

            Assert.Null(result["error"]);
            if (operation == "remove_tab")
                Assert.Equal(new[] { "One" }, named.Children.Items.Select(e => e.Attributes.GetPropertyValueString("ControlName")));
            else
                Assert.Equal(new[] { "Two", "One" }, named.Children.Items.Select(e => e.Attributes.GetPropertyValueString("ControlName")));
        }

        [Fact]
        public void NativeAddTab_VariableChildrenAreTypedAndNumericIsLeftToTheDefault()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab(
                new JObject { ["type"] = "variable", ["name"] = "vText", ["basicType"] = "VarChar", ["length"] = 10 },
                new JObject { ["type"] = "variable", ["name"] = "vAmount", ["basicType"] = "Numeric", ["length"] = 8, ["decimals"] = 2 }));

            Assert.Null(result["error"]);
            List<Element> variables = content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single()
                .Children.Items.Single().Children.Items.ToList();
            Element text = variables.Single(v => v.Attributes.GetPropertyValueString("name") == "vText");
            Assert.Equal("VarChar", text.Attributes.GetPropertyValueString("basicType"));
            Assert.Equal(10, text.Attributes.GetPropertyValue("basicCLength"));
            Element amount = variables.Single(v => v.Attributes.GetPropertyValueString("name") == "vAmount");
            // Numeric is the pattern's default: it is not stored, so it is not set either; its size is basicLength.
            Assert.Null(amount.Attributes.GetPropertyValue("basicType"));
            Assert.Equal(8, amount.Attributes.GetPropertyValue("basicLength"));
            Assert.Equal(2, amount.Attributes.GetPropertyValue("basicDecimals"));
            Assert.Null(amount.Attributes.GetPropertyValue("basicCLength"));
        }

        private static List<Element> NativeTabVariables(JObject[] children, out JObject result)
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);

            result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", AddTab(children));

            return content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single()
                .Children.Items.Single().Children.Items.ToList();
        }

        // The engine drops an attribute equal to the pattern's default, so a write that states one must not set it either: the
        // re-read check expects exactly what was set (live proof: VarChar(40) and Numeric(4) came back without their sizes).
        [Fact]
        public void NativeAddTab_AValueThatIsThePatternDefaultIsNotSet()
        {
            List<Element> variables = NativeTabVariables(new[]
            {
                new JObject { ["type"] = "variable", ["name"] = "vText", ["basicType"] = "VarChar", ["length"] = 40 },
                new JObject { ["type"] = "variable", ["name"] = "vNum", ["basicType"] = "Numeric", ["length"] = 4, ["decimals"] = 0 },
                new JObject { ["type"] = "variable", ["name"] = "vOther", ["basicType"] = "VarChar", ["length"] = 41 }
            }, out JObject result);

            Assert.Null(result["error"]);
            Element text = variables.Single(v => v.Attributes.GetPropertyValueString("name") == "vText");
            Assert.Null(text.Attributes.GetPropertyValue("basicCLength"));
            Assert.Null(text.Attributes.GetPropertyValue("defaultBasicCLength"));
            Assert.Equal("VarChar", text.Attributes.GetPropertyValueString("basicType"));
            Element number = variables.Single(v => v.Attributes.GetPropertyValueString("name") == "vNum");
            Assert.Null(number.Attributes.GetPropertyValue("basicLength"));
            Assert.Null(number.Attributes.GetPropertyValue("basicDecimals"));
            Element other = variables.Single(v => v.Attributes.GetPropertyValueString("name") == "vOther");
            Assert.Equal(41, other.Attributes.GetPropertyValue("basicCLength"));
            Assert.Equal(41, other.Attributes.GetPropertyValue("defaultBasicCLength"));
        }

        // controlEmptyItem is a bool in the pattern: the text "True" is stored as a string and the element fails its cast when it is
        // read back ("Specified cast is not valid"), the same defect the numbers had.
        [Fact]
        public void NativeAddTab_TheEmptyItemFlagIsStoredAsABool()
        {
            List<Element> variables = NativeTabVariables(new[]
            {
                new JObject
                {
                    ["type"] = "variable", ["name"] = "vCombo", ["basicType"] = "VarChar", ["length"] = 20,
                    ["controlType"] = "Combo Box", ["controlEmptyItem"] = true, ["controlEmptyItemText"] = "Choose"
                }
            }, out JObject result);

            Assert.Null(result["error"]);
            Element combo = variables.Single();
            Assert.True(Assert.IsType<bool>(combo.Attributes.GetPropertyValue("controlEmptyItem")));
            Assert.True(Assert.IsType<bool>(combo.Attributes.GetPropertyValue("defaultControlEmptyItem")));
            Assert.Equal("Combo Box", combo.Attributes.GetPropertyValueString("controlType"));
            Assert.Equal("Choose", combo.Attributes.GetPropertyValueString("controlEmptyItemText"));
        }

        [Fact]
        public void NativeAddTab_AnExplicitNullIsTheSameAsLeavingTheValueOut()
        {
            List<Element> variables = NativeTabVariables(new[]
            {
                new JObject
                {
                    ["type"] = "variable", ["name"] = "vNull", ["basicType"] = "VarChar", ["length"] = null, ["decimals"] = null,
                    ["description"] = null, ["controlEmptyItem"] = null
                }
            }, out JObject result);

            Assert.Null(result["error"]);
            Element element = variables.Single();
            Assert.Null(element.Attributes.GetPropertyValue("basicCLength"));
            Assert.Null(element.Attributes.GetPropertyValue("description"));
            Assert.Null(element.Attributes.GetPropertyValue("controlEmptyItem"));
        }

        [Fact]
        public void NativeAddTab_ChildrenWithoutAnObject_KeepTheEngineTable()
        {
            var part = new TabPart(engineCreatesTabTable: true);
            Element content = part.RootElement.Children.CreateChildElement("table");
            content.Attributes.SetPropertyValue("name", "TableContent");
            part.RootElement.Children.Items.Add(content);
            var children = new JArray { JValue.CreateNull(), "x" };

            JObject result = WwpActionService.ApplyNativeTabMutation(part, "add_tab", new JObject
            {
                ["controlName"] = "TabX", ["title"] = "X", ["children"] = children
            });

            Assert.Null(result["error"]);
            Element table = content.Children.Items.Single(e => e.Type == "tabs").Children.Items.Single().Children.Items.Single();
            Assert.Equal("table", table.Type);
            Assert.Empty(table.Children.Items);
        }

        internal sealed class TabPart
        {
            public TabPart(bool engineCreatesTabTable = false)
            {
                RootElement = new Element("instance") { EngineCreatesTabTable = engineCreatesTabTable };
            }

            public Element RootElement { get; }
            public int Updates { get; private set; }
            public bool TabsPresentWhenUpdateStarted { get; private set; }
            public void ExecuteUpdate(string label, Action mutation)
            {
                Updates++;
                TabsPresentWhenUpdateStarted = ContainsTabs(RootElement);
                mutation();
            }
            private static bool ContainsTabs(Element element) =>
                element.Type == "tabs" || element.Children.Items.Any(ContainsTabs);
        }
    }
}

namespace Artech.Packages.Patterns.Objects
{
    internal sealed class InsertElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        private readonly int _index;
        public InsertElementCommand(object parent, object child, int index)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
            _index = index;
        }
        public bool IsSafeToExecute() => true;
        public void Execute() => _parent.Children.Items.Insert(Math.Min(_index, _parent.Children.Items.Count), _child);
    }

    // Children.MoveElement of the SDK: a position outside the list is ignored.
    internal sealed class MoveElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        private readonly int _newPosition;
        public MoveElementCommand(object parent, object child, int oldPosition, int newPosition)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
            _newPosition = newPosition;
        }
        public void Execute()
        {
            if (_newPosition < 0 || _newPosition > _parent.Children.Items.Count - 1) return;
            _parent.Children.Items.Remove(_child);
            _parent.Children.Items.Insert(_newPosition, _child);
        }
    }

    internal sealed class RemoveElementCommand
    {
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _parent;
        private readonly GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element _child;
        public RemoveElementCommand(object parent, object child, int index)
        {
            _parent = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)parent;
            _child = (GxMcp.Worker.Tests.WwpEmptyNativeCommandTests.Element)child;
        }
        public bool IsSafeToExecute() => true;
        public void Execute() => _parent.Children.Items.Remove(_child);
    }
}
