using System;
using System.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #349: structural layout authoring on a PatternInstance.
    ///
    /// <para>
    /// The reporter could set attributes but not add elements. A
    /// <c>&lt;textBlock&gt;</c> added inside an existing table was refused as
    /// <c>PatternStructureChangeUnsupported</c> even though it touched nothing but
    /// itself, and a move between containers was refused as
    /// <c>PatternMetadataChangeUnsupported</c> on <c>@childrenOrderedList</c>.
    /// </para>
    ///
    /// <para>
    /// The second rejection was the caller doing the right thing by the wrong rule: the
    /// SDK rebuilds <c>childrenOrderedList</c> from the children on save, so a
    /// hand-written value is both unnecessary and fatal. The gate now accepts the
    /// structural edit on its own terms and the message explains that.
    /// </para>
    /// </summary>
    public class PatternStructuralLayoutEditTests
    {
        private const string Header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<instance xmlns=\"\">";

        private static string Doc(params string[] children) =>
            Header + string.Concat(children) + "</instance>";

        // ── the reporter's case 1: add an element inside an existing table ──────────

        private const string Before = Header
            + "<transaction name=\"T1\"><table name=\"TableMain\">"
            + "<gridLevel name=\"Level1\" />"
            + "</table></transaction></instance>";

        private static string WithExtraChild(string xml) => Header
            + "<transaction name=\"T1\"><table name=\"TableMain\">"
            + "<gridLevel name=\"Level1\" />"
            + xml
            + "</table></transaction></instance>";

        [Fact]
        public void Adding_A_TextBlock_Inside_A_Table_Is_Permitted()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock controlName=\"AvisoSaldo\" caption=\"Aviso\" />"));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)change["operation"]);
            Assert.Equal("textBlock", (string)change["element"]);
            Assert.Equal("AvisoSaldo", (string)change["identity"]);
            Assert.Equal(1, (int)change["index"]);
        }

        // issue #408: a variable shown as a combo box is a <data> element, as PopupLayoutBuilder emits it.
        private const string ComboData = "<data attribute=\"&amp;Status\" labelCaption=\"Status\" class=\"Attribute\""
            + " PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"&lt;Properties&gt;&lt;Property&gt;&lt;Name&gt;ControlType&lt;/Name&gt;&lt;Value&gt;Combo Box&lt;/Value&gt;&lt;/Property&gt;&lt;/Properties&gt;\" />";

        [Fact]
        public void Adding_A_Combo_Data_Element_Bound_To_A_Variable_Is_Permitted()
        {
            var plan = PatternXmlEditPlan.Create(Before, WithExtraChild(ComboData));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("data", (string)change["element"]);
            Assert.Equal("&Status", (string)change["identity"]);
        }

        [Fact]
        public void A_Data_Element_With_Children_Or_Unknown_Attributes_Is_Still_Refused()
        {
            var withChild = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<data attribute=\"&amp;Status\"><x /></data>"));
            var withUnknown = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<data attribute=\"&amp;Status\" bogus=\"1\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", withChild.ErrorCode);
            Assert.Equal("PatternStructureChangeUnsupported", withUnknown.ErrorCode);
            Assert.Contains("bogus", withUnknown.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Change_Records_That_Ordering_Is_Not_Authored()
        {
            // The single most important thing the caller can be told: they must NOT also
            // update childrenOrderedList, because that is what rejected their move.
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock controlName=\"AvisoSaldo\" caption=\"Aviso\" />"));

            var change = Assert.Single(plan.Changes);
            Assert.Contains("childrenOrderedList", (string)change["ordering"], StringComparison.Ordinal);
            Assert.Contains("SDK rebuilds", (string)change["ordering"], StringComparison.Ordinal);
        }

        [Fact]
        public void Insertion_At_The_First_Position_Is_Permitted_And_Reported()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock controlName=\"First\" />")
                    .Replace("<gridLevel name=\"Level1\" /><textBlock controlName=\"First\" />",
                             "<textBlock controlName=\"First\" /><gridLevel name=\"Level1\" />"));

            Assert.Null(plan.ErrorCode);
            Assert.Equal(0, (int)plan.Changes[0]["index"]);
        }

        [Fact]
        public void An_Element_Without_Its_Identity_Attribute_Is_Named_In_The_Rejection()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock caption=\"Aviso\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("controlName", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Disallowed_Attribute_Is_Named_Rather_Than_Rejected_Opaquely()
        {
            // "Child structure changed at /instance/..." was true and useless.
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock controlName=\"AvisoSaldo\" wibble=\"x\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("wibble", plan.Error, StringComparison.Ordinal);
            Assert.Contains("Permitted", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Nested_Addition_Is_Rejected()
        {
            // A container arriving with children has a generated layout this preflight
            // cannot predict, and a wrong guess is a corrupt pattern rather than a
            // rejected edit.
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<table name=\"Nested\"><gridLevel name=\"L\" /></table>"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("nested additions", plan.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_Unknown_Element_Kind_Is_Rejected_And_Named()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<wibbleThing name=\"X\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("wibbleThing", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_Elements_Added_At_Once_Are_Rejected()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<textBlock controlName=\"A\" /><textBlock controlName=\"B\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
        }

        [Fact]
        public void Rewriting_An_Existing_Child_While_Adding_One_Is_Rejected()
        {
            // Otherwise this is a replacement dressed as an insertion.
            var plan = PatternXmlEditPlan.Create(Before,
                WithExtraChild("<gridLevel name=\"RENAMED\" />"));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
        }

        // ── removal ────────────────────────────────────────────────────────────────

        [Fact]
        public void Removing_A_TextBlock_Is_Permitted()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableMain\">"
                + "<textBlock controlName=\"AvisoSaldo\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableMain\" /></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Remove", (string)change["operation"]);
            Assert.Equal("textBlock", (string)change["element"]);
            Assert.Equal("AvisoSaldo", (string)change["identity"]);
        }

        [Fact]
        public void Removing_One_Of_Several_Children_Is_Permitted()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableMain\">"
                + "<textBlock controlName=\"A\" /><image name=\"DropMe\" /><userControl name=\"U\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableMain\">"
                + "<textBlock controlName=\"A\" /><userControl name=\"U\" />"
                + "</table></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Remove", (string)change["operation"]);
            Assert.Equal("image", (string)change["element"]);
            Assert.Equal(1, (int)change["index"]);
        }

        [Fact]
        public void Removing_A_Kind_Outside_The_Table_Is_Rejected()
        {
            // A container node such as gridLevel is not on the table, so removing one
            // stays a structural edit the IDE owns.
            var plan = PatternXmlEditPlan.Create(Before,
                Header + "<transaction name=\"T1\"><table name=\"TableMain\" /></transaction></instance>");

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("gridLevel", plan.Error, StringComparison.Ordinal);
        }

        // ── the reporter's case 2: move between containers ─────────────────────────

        private const string TwoContainers = Header
            + "<transaction name=\"T1\">"
            + "<table name=\"TableRightHeader\"><actionGroup name=\"Actions\" /></table>"
            + "<table name=\"TableActions\" />"
            + "</transaction></instance>";

        [Fact]
        public void Moving_An_ActionGroup_Between_Containers_Is_Reported_As_One_Move()
        {
            var after = Header
                + "<transaction name=\"T1\">"
                + "<table name=\"TableRightHeader\" />"
                + "<table name=\"TableActions\"><actionGroup name=\"Actions\" /></table>"
                + "</transaction></instance>";

            var plan = PatternXmlEditPlan.Create(TwoContainers, after);

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Move", (string)change["operation"]);
            Assert.Equal("actionGroup", (string)change["element"]);
            Assert.Equal("Actions", (string)change["identity"]);
            Assert.NotNull(change["fromPath"]);
        }

        [Fact]
        public void Editing_ChildrenOrderedList_Tells_The_Caller_To_Stop_Editing_It()
        {
            // This is the rejection the reporter hit, and the message they called useless
            // because it pointed at an action that does not exist.
            var after = Header
                + "<transaction name=\"T1\">"
                + "<table name=\"TableRightHeader\" childrenOrderedList=\"1;18;Actions\" />"
                + "<table name=\"TableActions\"><actionGroup name=\"Actions\" /></table>"
                + "</transaction></instance>";

            var plan = PatternXmlEditPlan.Create(TwoContainers, after);

            Assert.Equal("PatternMetadataChangeUnsupported", plan.ErrorCode);
            Assert.Contains("Do not edit childrenOrderedList", plan.Error, StringComparison.Ordinal);
            Assert.Contains("rebuilds it from the child elements", plan.Error, StringComparison.Ordinal);
            // And it must not send the caller hunting for a nonexistent authoring action.
            Assert.DoesNotContain("appropriate SDK pattern authoring action", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void An_Unrelated_Protected_Attribute_Still_Reports_Itself_Generically()
        {
            var after = Header
                + "<transaction name=\"T1\">"
                + "<table name=\"TableRightHeader\" defaultType=\"X\" />"
                + "<table name=\"TableActions\" />"
                + "</transaction></instance>";

            var plan = PatternXmlEditPlan.Create(TwoContainers, after);

            Assert.Equal("PatternMetadataChangeUnsupported", plan.ErrorCode);
            Assert.Contains("maintained by the SDK", plan.Error, StringComparison.Ordinal);
        }

        // ── guards that must survive ───────────────────────────────────────────────

        [Fact]
        public void A_Document_Level_Change_Is_Still_Refused()
        {
            var plan = PatternXmlEditPlan.Create(Before,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><other />");

            Assert.NotNull(plan.ErrorCode);
        }

        [Fact]
        public void Malformed_Xml_Is_Still_Refused_With_Its_Own_Code()
        {
            var plan = PatternXmlEditPlan.Create(Before, "<not-closed>");

            Assert.Equal("PatternInvalidXml", plan.ErrorCode);
        }

        [Fact]
        public void Every_Permitted_Kind_Has_An_Identity_Attribute_In_Its_Allow_List()
        {
            // An identity the ordering cannot name is an element that cannot participate
            // in a childrenOrderedList, so permitting one without its identity would let
            // through exactly the node the SDK cannot address.
            foreach (var kind in new[]
            {
                "textBlock", "variable", "attribute", "userAction",
                "image", "userControl", "actionGroup", "errorViewer",
            })
            {
                Assert.True(StructuralLayoutRules.IsPermittedKind(kind), kind + " is not permitted");
                var identity = StructuralLayoutRules.RequiredIdentityAttribute(kind);
                var allowed = StructuralLayoutRules.AllowedAttributes(kind).ToList();
                Assert.True(allowed.Contains(identity),
                    kind + " does not allow its own identity attribute " + identity
                    + " (allows: " + string.Join(", ", allowed) + ")");
            }
        }

        [Fact]
        public void An_Element_Kind_Outside_The_Table_Is_Not_Permitted()
        {
            foreach (var kind in new[] { "wibble", "gridLevel", "transaction", "table", "" })
                Assert.False(StructuralLayoutRules.IsPermittedKind(kind),
                    kind + " should not be a permitted structural-edit kind");
        }

        [Fact]
        public void Grid_Columns_Stay_Gated_Behind_The_Grid_Authoring_Mode()
        {
            // A regression this change nearly introduced: listing gridVariable in the
            // structural table would have permitted a grid column insertion on ANY
            // container and WITHOUT allowGridStructure, because the unconditional
            // structural handler is consulted before the grid gate can apply.
            var before = "<instance><table name='T'><textBlock controlName='A' /></table></instance>";
            var after = "<instance><table name='T'><textBlock controlName='A' />"
                      + "<gridVariable name='V' variable='x-V' /></table></instance>";

            Assert.False(StructuralLayoutRules.IsPermittedKind("gridVariable"),
                "gridVariable is governed by TryAllowGridVariableInsertion, not by the structural table");
            Assert.False(StructuralLayoutRules.IsPermittedKind("gridAttribute"));

            var plan = PatternXmlEditPlan.Create(before, after);
            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("gridVariable", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void Adding_A_TextBlock_With_ThemeClass_Is_Permitted()
        {
            var after = WithExtraChild("<textBlock controlName=\"TxtValorSinal\" caption=\"Valor (+)\" themeClass=\"TextBlock\" />");
            var plan = PatternXmlEditPlan.Create(Before, after);
            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)change["operation"]);
            Assert.Equal("textBlock", (string)change["element"]);
            Assert.Equal("TxtValorSinal", (string)change["identity"]);
        }

        [Fact]
        public void Adding_A_UserAction_In_Form_Table_Is_Permitted()
        {
            var after = WithExtraChild("<userAction name=\"BtnCustom\" caption=\"Click Me\" />");
            var plan = PatternXmlEditPlan.Create(Before, after);
            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)change["operation"]);
            Assert.Equal("userAction", (string)change["element"]);
            Assert.Equal("BtnCustom", (string)change["identity"]);
        }

        [Fact]
        public void Reordering_Attributes_In_TableAttributes_Is_Permitted()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"CliCod\" caption=\"Código\" />"
                + "<attribute attribute=\"CliNom\" caption=\"Nome\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"CliNom\" caption=\"Nome\" />"
                + "<attribute attribute=\"CliCod\" caption=\"Código\" />"
                + "</table></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);
            Assert.Null(plan.ErrorCode);
            Assert.Equal(2, plan.Changes.Count);
            Assert.All(plan.Changes, c => Assert.Equal("Reorder", (string)c["operation"]));
        }

        [Fact]
        public void Removing_Multiple_Attributes_In_Table_Is_Permitted()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"A\" />"
                + "<attribute attribute=\"B\" />"
                + "<attribute attribute=\"C\" />"
                + "<attribute attribute=\"D\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"A\" />"
                + "<attribute attribute=\"D\" />"
                + "</table></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);
            Assert.Null(plan.ErrorCode);
            Assert.Equal(2, plan.Changes.Count);
            Assert.All(plan.Changes, c => Assert.Equal("Remove", (string)c["operation"]));
        }

        [Fact]
        public void Adding_Prompt_With_Parameters_Inside_Attribute_Is_Permitted()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"FroTip\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<attribute attribute=\"FroTip\">"
                + "<prompt prompt=\"1db606f2-af09-4cf9-a3b5-b481519d28f6-TFroTip\">"
                + "<parameters><parameter name=\"TrnMode.Insert\" /><parameter name=\"nullvalue(FroTip)\" /></parameters>"
                + "</prompt>"
                + "</attribute>"
                + "</table></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);
            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Insert", (string)change["operation"]);
            Assert.Equal("prompt", (string)change["element"]);
        }

        // ── issue #560: the DVelop Combo insert prompt, a <prompt> child of an <attribute> ──

        private const string PromptTarget = "1db606f2-af09-4cf9-a3b5-b481519d28f6-TFroTip";
        private const string InsertParameter = "<parameter name=\"TrnMode.Insert\" />";
        private const string NullValueParameter = "<parameter name=\"nullvalue(FroTip)\" />";

        private static string AttributeWith(string children) => Header
            + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
            + "<attribute attribute=\"FroTip\">" + children + "</attribute>"
            + "</table></transaction></instance>";

        private static string PromptWith(string parameters, string target = PromptTarget) =>
            "<prompt prompt=\"" + target + "\"><parameters>" + parameters + "</parameters></prompt>";

        [Fact]
        public void Adding_A_Parameter_To_An_Existing_Prompt_Is_Permitted()
        {
            // The reporter's second attempt: the prompt already exists and only a
            // <parameter> is added. The attribute keeps one child on both sides, so the
            // size-differs handler never ran and the positional recursion rejected it at
            // <parameters> as "1 child element added. Added: parameter".
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter)),
                AttributeWith(PromptWith(InsertParameter + NullValueParameter)));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Update", (string)change["operation"]);
            Assert.Equal("prompt", (string)change["element"]);
            Assert.Equal(PromptTarget, (string)change["prompt"]);
            Assert.EndsWith("attribute/prompt", (string)change["path"], StringComparison.Ordinal);
        }

        [Fact]
        public void Removing_A_Parameter_From_An_Existing_Prompt_Is_Permitted()
        {
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter + NullValueParameter)),
                AttributeWith(PromptWith(InsertParameter)));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Update", (string)change["operation"]);
            Assert.Equal("prompt", (string)change["element"]);
        }

        [Fact]
        public void Removing_The_Prompt_Is_Permitted()
        {
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter + NullValueParameter)),
                AttributeWith(""));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Remove", (string)change["operation"]);
            Assert.Equal("prompt", (string)change["element"]);
        }

        [Fact]
        public void Changing_The_Prompt_Target_Emits_Update()
        {
            const string other = "1db606f2-af09-4cf9-a3b5-b481519d28f6-TOther";
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter)),
                AttributeWith(PromptWith(InsertParameter, other)));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.Equal("Update", (string)change["operation"]);
            Assert.Equal(other, (string)change["prompt"]);
        }

        [Fact]
        public void A_Prompt_With_An_Unknown_Attribute_Is_Rejected()
        {
            // Whether the prompt is new or replaced in place, an attribute outside the
            // allow-list is refused and named - the in-place case must not fall through
            // to the generic property-edit path, which would accept it.
            var inserted = PatternXmlEditPlan.Create(
                AttributeWith(""),
                AttributeWith("<prompt prompt=\"" + PromptTarget + "\" foo=\"1\" />"));
            var replaced = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter)),
                AttributeWith("<prompt prompt=\"" + PromptTarget + "\" foo=\"1\">"
                    + "<parameters>" + InsertParameter + "</parameters></prompt>"));

            Assert.Equal("PatternStructureChangeUnsupported", inserted.ErrorCode);
            Assert.Contains("foo", inserted.Error, StringComparison.Ordinal);
            Assert.Equal("PatternStructureChangeUnsupported", replaced.ErrorCode);
            Assert.Contains("foo", replaced.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Prompt_Outside_An_Attribute_Is_Rejected()
        {
            var before = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<textBlock controlName=\"Note\" />"
                + "</table></transaction></instance>";
            var after = Header
                + "<transaction name=\"T1\"><table name=\"TableAttributes\">"
                + "<textBlock controlName=\"Note\">" + PromptWith(InsertParameter) + "</textBlock>"
                + "</table></transaction></instance>";

            var plan = PatternXmlEditPlan.Create(before, after);

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("prompt", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_Prompt_With_An_Empty_Target_Is_Rejected()
        {
            var missing = PatternXmlEditPlan.Create(
                AttributeWith(""),
                AttributeWith("<prompt><parameters>" + InsertParameter + "</parameters></prompt>"));
            var blanked = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter)),
                AttributeWith(PromptWith(InsertParameter, "")));

            Assert.Equal("PatternStructureChangeUnsupported", missing.ErrorCode);
            Assert.Contains("target", missing.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("PatternStructureChangeUnsupported", blanked.ErrorCode);
            Assert.Contains("target", blanked.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_Parameter_With_Children_Is_Rejected()
        {
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(PromptWith(InsertParameter)),
                AttributeWith(PromptWith(InsertParameter + "<parameter name=\"x\"><y /></parameter>")));

            Assert.Equal("PatternStructureChangeUnsupported", plan.ErrorCode);
            Assert.Contains("leaf <parameter>", plan.Error, StringComparison.Ordinal);
        }

        [Fact]
        public void An_Unchanged_Prompt_Does_Not_Block_An_Unrelated_Property_Edit()
        {
            // A prompt the IDE wrote may carry an attribute this gate does not know. As
            // long as the prompt itself is untouched, that must not reject an edit to the
            // attribute's own properties.
            const string idePrompt = "<prompt prompt=\"" + PromptTarget + "\" foo=\"1\" />";
            var plan = PatternXmlEditPlan.Create(
                AttributeWith(idePrompt),
                AttributeWith(idePrompt).Replace("<attribute attribute=\"FroTip\">",
                    "<attribute attribute=\"FroTip\" caption=\"Tipo\">"));

            Assert.Null(plan.ErrorCode);
            var change = Assert.Single(plan.Changes);
            Assert.EndsWith("@caption", (string)change["path"], StringComparison.Ordinal);
        }
    }
}
