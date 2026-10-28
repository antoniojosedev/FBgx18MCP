using System;
using System.Linq;
using System.Security;
using System.Xml.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #711: a Dynamic Combo Box's Conditions (ControlWhere) on a layout WebForm.
    ///
    /// <para>
    /// Measured live on a GX18 scratch KB: the control's SDK property (what the IDE's Conditions
    /// shows) reads ControlWhere from <c>PATTERN_ELEMENT_CUSTOM_PROPERTIES</c>; a write there as plain
    /// text is parsed and stored as a <c>TokenDataList</c>, so the raw comparison failed a correct
    /// write and rolled it back. <c>WebUserControlProperties</c> is stored verbatim and the SDK property
    /// does not read it, so a patch there verified while the IDE kept the old expression.
    /// </para>
    /// </summary>
    public class ControlConditionCarriersTests
    {
        // Token types as the GX18 SDK wrote them for "Cw711Flag.IsNull() or Cw711Flag = false;".
        private static readonly (int Type, string Word, int Id)[] StoredTokens =
        {
            (2, "Cw711Flag", 69), (30, ".", 28), (1, "IsNull", 0), (0, "(", 0), (4, ")", 0),
            (25, " ", 0), (9, "or", 0), (25, " ", 0), (2, "Cw711Flag", 69), (25, " ", 0),
            (10, "=", 0), (25, " ", 0), (41, "false", 0), (18, ";", 0), (25, "\r\n", 0)
        };

        private const string Requested = "Cw711Flag.IsNull() or Cw711Flag = false;";

        private static string TokenList(params (int Type, string Word, int Id)[] tokens)
        {
            var root = new XElement("TokenDataList",
                tokens.Select(t => new XElement("TokenData",
                    new XElement("Token", t.Type), new XElement("Word", t.Word), new XElement("Id", t.Id))));
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static string Bag(string controlWhere, string controlType = "Dynamic Combo Box")
        {
            var root = new XElement("Properties",
                new XElement("Property", new XElement("Name", "ControlType"), new XElement("Value", controlType)),
                new XElement("Property", new XElement("Name", "ControlItemValues"), new XElement("Value", "att:67")),
                new XElement("Property", new XElement("Name", "ControlWhere"), new XElement("Value", controlWhere)));
            return root.ToString(SaveOptions.DisableFormatting);
        }

        private static string Form(string pecpWhere, string wucpWhere = null, string extra = "")
        {
            string wucp = wucpWhere == null
                ? string.Empty
                : " WebUserControlProperties=\"" + SecurityElement.Escape(
                    "<Properties><Property><Name>ControlWhere</Name><Value>" + SecurityElement.Escape(wucpWhere)
                    + "</Value></Property></Properties>") + "\"";
            return "<GxMultiForm><Form type=\"layout\"><layout><table controlName=\"MainTable\"" + extra + "><row><cell>"
                + "<data attribute=\"var:5\"" + wucp
                + " PATTERN_ELEMENT_CUSTOM_PROPERTIES=\"" + SecurityElement.Escape(Bag(pecpWhere)) + "\" />"
                + "</cell></row></table></layout></Form></GxMultiForm>";
        }

        // ------------------------------------------------ the SDK's canonical form

        [Fact]
        public void A_Plain_Text_Condition_Equals_The_Token_List_The_Sdk_Stored_For_It()
        {
            Assert.True(CustomPropertiesPayload.SemanticallyEqual(Bag(Requested), Bag(TokenList(StoredTokens))));
        }

        [Fact]
        public void Spacing_Tokens_Are_Read_As_Spaces()
        {
            // A default XML parse drops a whitespace-only <Word>, gluing "A or B" into "AorB".
            Assert.True(ControlExpressionText.TryDecodeTokenList(TokenList(StoredTokens), out string text));
            Assert.Equal(Requested, ControlExpressionText.Normalize(text));
        }

        [Fact]
        public void A_Different_Expression_Is_Still_A_Difference()
        {
            // The tolerance is canonical form, not content: the issue's old expression must not
            // verify a request for the new one.
            var old = TokenList((2, "Cw711Flag", 69), (25, " ", 0), (10, "=", 0), (25, " ", 0), (40, "true", 0), (18, ";", 0));
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(Bag(Requested), Bag(old)));
        }

        [Fact]
        public void Spacing_Inside_A_String_Literal_Is_Content()
        {
            var stored = TokenList((2, "Name", 1), (25, " ", 0), (10, "=", 0), (25, " ", 0), (5, "\"a  b\"", 0), (18, ";", 0));
            Assert.True(CustomPropertiesPayload.SemanticallyEqual(Bag("Name = \"a  b\";"), Bag(stored)));
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(Bag("Name = \"a b\";"), Bag(stored)));
        }

        [Fact]
        public void Two_Plain_Values_Keep_The_Old_Strict_Comparison()
        {
            // Only a token list earns the expression comparison; other values stay as they were.
            Assert.False(CustomPropertiesPayload.SemanticallyEqual(Bag("A  =  1;"), Bag("A = 1;")));
        }

        [Fact]
        public void The_Visual_Write_Verifier_Accepts_The_Canonicalized_Form()
        {
            Assert.True(XmlEquivalence.AreEquivalent(
                Form(TokenList(StoredTokens)), Form(Requested), XmlEquivalenceOptions.Layout, out string diff), diff);
        }

        [Fact]
        public void A_WebForm_Patch_Defaults_To_The_Xml_Layout_Equivalence()
        {
            Assert.Equal("semantic", TextPersistenceVerifier.ResolveMode(null, "WebForm"));
            Assert.Equal("semantic", TextPersistenceVerifier.ResolveMode(null, "Layout"));

            var result = TextPersistenceVerifier.Evaluate(Form(Requested), Form(TokenList(StoredTokens)), null, "WebForm");

            Assert.True(result.Matches);
            Assert.True(result.XmlLayoutEquivalent);
            Assert.Contains("xml-layout-equivalence", result.NormalizationApplied.Select(t => (string)t));
        }

        [Fact]
        public void A_WebForm_Patch_Still_Fails_On_A_Lost_Expression()
        {
            var old = TokenList((2, "Cw711Flag", 69), (25, " ", 0), (10, "=", 0), (25, " ", 0), (40, "true", 0), (18, ";", 0));
            var result = TextPersistenceVerifier.Evaluate(Form(Requested), Form(old), null, "WebForm");

            Assert.False(result.Matches);
            Assert.False(result.XmlLayoutEquivalent);
        }

        [Fact]
        public void An_Explicit_Exact_Mode_Stays_Textual()
        {
            var result = TextPersistenceVerifier.Evaluate(Form(Requested), Form(TokenList(StoredTokens)), "exact", "WebForm");
            Assert.False(result.Matches);
        }

        [Fact]
        public void The_Patch_Receipt_Verifies_A_Replacement_The_Sdk_Restored_In_Canonical_Form()
        {
            string requested = Form(Requested);
            string persisted = Form(TokenList(StoredTokens));
            var verification = TextPersistenceVerifier.Evaluate(requested, persisted, null, "WebForm");
            var payload = new JObject();

            bool verified = PatchPersistenceReceipt.AttachVerification(
                payload, verification, requestedReplacement: Requested, originalContext: "Cw711Flag = true;",
                savedSource: requested, persistedSource: persisted, verifyMode: "semantic", partName: "WebForm", matchCount: 1);

            Assert.True(verified);
            Assert.True((bool)payload["replacementPresent"]);
        }

        // ------------------------------------------------ the two property bags

        [Fact]
        public void Changing_Only_WebUserControlProperties_Is_A_Divergence()
        {
            // The issue's first write: it verified and the IDE kept the old Conditions.
            string current = Form(TokenList(StoredTokens), "Cw711Flag.IsNull() or Cw711Flag = false;");
            string requested = Form(TokenList(StoredTokens), "Cw711Flag = true;");

            var d = Assert.Single(ControlConditionCarriers.FindIntroduced(current, requested));
            Assert.Equal("ControlWhere", d.Property);
            Assert.True(d.WebUserControlChanged);
            Assert.False(d.CustomPropertiesChanged);
            Assert.Equal("Cw711Flag = true;", d.WebUserControlExpression);
            Assert.Equal(Requested, d.CustomPropertiesExpression);
        }

        [Fact]
        public void Changing_Only_The_Custom_Properties_Over_A_Stale_Copy_Is_A_Divergence()
        {
            string current = Form("Cw711Flag = true;", "Cw711Flag = true;");
            string requested = Form(Requested, "Cw711Flag = true;");

            var d = Assert.Single(ControlConditionCarriers.FindIntroduced(current, requested));
            Assert.True(d.CustomPropertiesChanged);
            Assert.False(d.WebUserControlChanged);
        }

        [Fact]
        public void Writing_The_Same_Expression_To_Both_Bags_Is_Consistent()
        {
            string current = Form("Cw711Flag = true;", "Cw711Flag = true;");
            string requested = Form(Requested, Requested);
            Assert.Empty(ControlConditionCarriers.FindIntroduced(current, requested));
            // And the token-list form of one side is the same expression as the text of the other.
            Assert.Empty(ControlConditionCarriers.FindIntroduced(current, Form(TokenList(StoredTokens), Requested)));
        }

        [Fact]
        public void A_Control_With_One_Bag_Is_Not_Checked()
        {
            Assert.Empty(ControlConditionCarriers.FindIntroduced(Form("Cw711Flag = true;"), Form(Requested)));
        }

        [Fact]
        public void An_Untouched_Divergence_Does_Not_Block_An_Unrelated_Edit()
        {
            // The IDE leaves WebUserControlProperties stale on its own; refusing every edit of such
            // a form would block work the request has nothing to do with.
            string current = Form(Requested, "Cw711Flag = true;");
            string requested = Form(Requested, "Cw711Flag = true;", extra: " class=\"Table\"");
            Assert.Empty(ControlConditionCarriers.FindIntroduced(current, requested));
        }

        [Fact]
        public void A_New_Control_With_Divergent_Bags_Is_Reported()
        {
            Assert.Single(ControlConditionCarriers.FindIntroduced(
                "<GxMultiForm><Form type=\"layout\"><layout /></Form></GxMultiForm>",
                Form(Requested, "Cw711Flag = true;")));
        }

        [Fact]
        public void The_Refusal_Names_Both_Values_And_The_Bag_The_Ide_Reads()
        {
            string current = Form(TokenList(StoredTokens), Requested);
            string requested = Form(TokenList(StoredTokens), "Cw711Flag = true;");

            string raw = WriteService.RefuseDivergentConditionCarriers("Wp", "WebForm", current, requested, dryRun: false);

            Assert.NotNull(raw);
            var json = JObject.Parse(raw);
            Assert.Equal("ControlConditionCarriersDiverge", (string)json["error"]["code"]);
            Assert.Contains("PATTERN_ELEMENT_CUSTOM_PROPERTIES", (string)json["error"]["hint"], StringComparison.Ordinal);
            Assert.False((bool)json["persisted"]);
            Assert.False((bool)json["savePathExercised"]);
            Assert.Equal("PATTERN_ELEMENT_CUSTOM_PROPERTIES", (string)json["canonicalCarrier"]);
            var item = (JObject)Assert.Single((JArray)json["divergences"]);
            Assert.Equal("Cw711Flag = true;", (string)item["WebUserControlProperties"]);
            Assert.Equal(Requested, (string)item["PATTERN_ELEMENT_CUSTOM_PROPERTIES"]);
            Assert.Equal("WebUserControlProperties", (string)Assert.Single((JArray)item["changedByRequest"]));
        }

        [Fact]
        public void A_Consistent_Write_Is_Not_Refused()
        {
            Assert.Null(WriteService.RefuseDivergentConditionCarriers(
                "Wp", "WebForm", Form("Cw711Flag = true;", "Cw711Flag = true;"), Form(Requested, Requested), dryRun: true));
        }
    }

    /// <summary>
    /// The refusal has to run where every WebForm write passes, before the save, and in the
    /// patch preview. A behavioural test cannot reach those sites without a live KB.
    /// </summary>
    public class ControlConditionCarriersCallSiteTests
    {
        [Fact]
        public void The_Visual_Write_Refuses_Before_The_Transaction()
        {
            string source = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.VisualWrite.cs"));
            string body = SourceAssert.MethodBody(source, "private string WriteVisualPart(");

            int guard = body.IndexOf("RefuseDivergentConditionCarriers(target, partName, currentXml, normalizedInput, dryRun)", StringComparison.Ordinal);
            int dryRunReturn = body.IndexOf("code: \"WriteDryRun\"", StringComparison.Ordinal);
            int transaction = body.IndexOf("kb.BeginTransaction()", StringComparison.Ordinal);

            Assert.True(guard >= 0, "WriteVisualPart must call the condition-carrier refusal");
            Assert.True(guard < dryRunReturn, "the dry run must refuse what the write would refuse");
            Assert.True(guard < transaction, "the refusal must come before anything is saved");
            Assert.True(body.Contains("if (carrierRefusal != null) return carrierRefusal;"),
                "the refusal must be returned, not just computed");
        }

        [Fact]
        public void The_Patch_Preview_Refuses_Too()
        {
            string source = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "PatchService.cs"));
            int call = source.IndexOf("WriteService.RefuseDivergentConditionCarriers(", StringComparison.Ordinal);
            Assert.True(call >= 0, "the patch dry run must run the same refusal on the patched document");
            string args = source.Substring(call, Math.Min(200, source.Length - call));
            Assert.Contains("workSource, updatedSource, dryRun: true)", args, StringComparison.Ordinal);
            Assert.True(source.IndexOf("if (carrierRefusal != null) return carrierRefusal;", call, StringComparison.Ordinal) > call,
                "the preview must return the refusal");
            int preview = source.IndexOf("\"Dry-run succeeded. Write skipped.\"", StringComparison.Ordinal);
            Assert.True(call < preview, "the refusal must come before the preview reports success");
        }

        [Fact]
        public void The_Committed_Visual_Write_Drops_The_Typed_Property_Cache()
        {
            string source = SourceAssert.NormaliseNewlines(
                RepoSource.WithoutComments("src", "GxMcp.Worker", "Services", "WriteService.VisualWrite.cs"));
            string body = SourceAssert.MethodBody(source, "private string WriteVisualPart(");

            int commit = body.IndexOf("transaction.Commit();", StringComparison.Ordinal);
            int invalidate = body.IndexOf("PropertyService.InvalidatePropertyCache(obj);", StringComparison.Ordinal);
            int verify = body.IndexOf("XmlEquivalence.AreEquivalent(persistedXml, normalizedInput", StringComparison.Ordinal);

            Assert.True(commit >= 0, "WriteVisualPart must commit the transaction");
            Assert.True(invalidate > commit,
                "a committed layout write must drop the cached genexus_properties envelope, or Conditions reads stale");
            Assert.True(invalidate < verify, "the cache must be dropped on every committed exit, verified or not");
        }
    }
}
