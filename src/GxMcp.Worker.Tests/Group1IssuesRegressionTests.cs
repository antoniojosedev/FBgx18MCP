using System;
using System.Collections.Generic;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class Group1IssuesRegressionTests
    {
        [Fact]
        public void Issue541_AtomicCreateService_ParsesAndIncludesDescription()
        {
            var payload = new JObject
            {
                ["name"] = "TestProc",
                ["type"] = "Procedure",
                ["description"] = "Test Procedure Description",
                ["dryRun"] = true
            };

            var spec = AtomicCreateService.ParseAndValidate(payload);
            Assert.Equal("Test Procedure Description", spec.Description);
        }

        [Fact]
        public void Issue541_AttributeTypeApplier_ParsesLengthAndDecimals()
        {
            var spec = AttributeTypeApplier.Parse("Numeric(12,2)");
            Assert.Equal("Numeric", spec.CanonicalType);
            Assert.Equal(12, spec.Length);
            Assert.Equal(2, spec.Decimals);

            var specVarChar = AttributeTypeApplier.Parse("VarChar(120)");
            Assert.Equal("VarChar", specVarChar.CanonicalType);
            Assert.Equal(120, specVarChar.Length);
        }

        [Fact]
        public void Issue542_MatchesPlan_ReturnsDetailedMismatchWhenFieldsDiffer()
        {
            var before = new JArray();
            var requested = new JArray
            {
                new JObject
                {
                    ["name"] = "Member1",
                    ["type"] = "VarChar(40)",
                    ["length"] = 40
                }
            };

            var persistedMismatchedType = new JArray
            {
                new JObject
                {
                    ["name"] = "Member1",
                    ["type"] = "Numeric",
                    ["length"] = 40
                }
            };

            bool match = SDTService.MatchesPlan(before, requested, persistedMismatchedType, false, out string detail);
            Assert.False(match);
            Assert.NotNull(detail);
            Assert.Contains("type mismatch", detail);
            Assert.Contains("Member1", detail);

            var persistedMatched = new JArray
            {
                new JObject
                {
                    ["name"] = "Member1",
                    ["type"] = "VarChar",
                    ["length"] = 40
                }
            };

            bool matchOk = SDTService.MatchesPlan(before, requested, persistedMatched, false, out string detailOk);
            Assert.True(matchOk);
            Assert.Null(detailOk);
        }

        [Fact]
        public void Issue542_MatchesPlan_ReturnsDetailedMismatchWhenCountDiffers()
        {
            var before = new JArray();
            var requested = new JArray
            {
                new JObject { ["name"] = "M1", ["type"] = "Numeric" },
                new JObject { ["name"] = "M2", ["type"] = "VarChar" }
            };
            var persisted = new JArray
            {
                new JObject { ["name"] = "M1", ["type"] = "Numeric" }
            };

            bool match = SDTService.MatchesPlan(before, requested, persisted, false, out string detail);
            Assert.False(match);
            Assert.Contains("member count mismatch", detail);
        }

        // Issue #544 WebForm scenario: the SDK drops the leaf control id and stores the
        // variable reference by its numeric id.
        private const string Issue544WebFormRequested =
            "<Form><table id=\"MainTable\" controlName=\"MainTable\"><row><cell><textblock id=\"TxtTeste\" controlName=\"TxtTeste\" caption=\"Teste MCP\" /></cell></row><row><cell><data attribute=\"&amp;nome\" /></cell></row></table></Form>";
        private const string Issue544WebFormPersisted =
            "<Form><table id=\"MainTable\" controlName=\"MainTable\"><row><cell><textblock controlName=\"TxtTeste\" caption=\"Teste MCP\" /></cell></row><row><cell><data attribute=\"var:5\" /></cell></row></table></Form>";

        // Issue #544 Report scenario: rebinding AttributeReference renames Name/ControlName.
        private const string Issue544ReportRequested =
            "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\"><Control TypeName=\"ReportAttribute\" Name=\"&amp;bar\" ControlName=\"&amp;bar\" AttributeReference=\"&amp;bar2\" Top=\"10\" /></PrintBlock></Report>";
        private const string Issue544ReportPersisted =
            "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\"><Control TypeName=\"ReportAttribute\" Name=\"&amp;bar2\" ControlName=\"&amp;bar2\" AttributeReference=\"&amp;bar2\" Top=\"10\" /></PrintBlock></Report>";

        [Fact]
        public void Issue544_WebFormLayout_StrippedLeafIdAndResolvedVariableRef_IsEquivalent()
        {
            bool equal = XmlEquivalence.AreEquivalent(Issue544WebFormPersisted, Issue544WebFormRequested, XmlEquivalenceOptions.Layout, out string diff);
            Assert.True(equal, diff);
        }

        [Fact]
        public void Issue544_WebFormLayout_RegeneratedControlId_IsEquivalent()
        {
            string requested = "<Form><table id=\"MainTable\" controlName=\"MainTable\" /></Form>";
            string persisted = "<Form><table id=\"T_1\" controlName=\"MainTable\" /></Form>";

            bool equal = XmlEquivalence.AreEquivalent(persisted, requested, XmlEquivalenceOptions.Layout, out string diff);
            Assert.True(equal, diff);
        }

        [Fact]
        public void Issue544_StrictComparison_StillReportsStrippedControlId()
        {
            string requested = "<Control id=\"ctrl_1\" ControlName=\"BtnSubmit\" Caption=\"Submit\" />";
            string persisted = "<Control ControlName=\"BtnSubmit\" Caption=\"Submit\" />";

            Assert.False(XmlEquivalence.AreEquivalent(persisted, requested, out string diff));
            Assert.Contains("rejectedByPersist=[id]", diff);
        }

        [Fact]
        public void Issue544_VariableRef_AmpersandNameVsNumericId_IsEquivalent()
        {
            string requested = "<data attribute=\"&amp;nome\" />";
            string persisted = "<data attribute=\"var:5\" />";

            bool equal = XmlEquivalence.AreEquivalent(persisted, requested, out string diff);
            Assert.True(equal, diff);
        }

        [Fact]
        public void Issue544_WebFormLayout_CaptionDifference_IsStillReported()
        {
            string persisted = Issue544WebFormPersisted.Replace("Teste MCP", "Outro");

            Assert.False(XmlEquivalence.AreEquivalent(persisted, Issue544WebFormRequested, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains("caption", diff);
        }

        [Fact]
        public void Issue544_WebFormLayout_MissingControl_IsStillReported()
        {
            string persisted = "<Form><table id=\"MainTable\" controlName=\"MainTable\"><row><cell><textblock controlName=\"TxtTeste\" caption=\"Teste MCP\" /></cell></row></table></Form>";

            Assert.False(XmlEquivalence.AreEquivalent(persisted, Issue544WebFormRequested, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains("Child count", diff);
        }

        [Fact]
        public void Issue544_ReportLayout_ControlNameFollowsAttributeReference_IsEquivalent()
        {
            bool equal = XmlEquivalence.AreEquivalent(Issue544ReportPersisted, Issue544ReportRequested, XmlEquivalenceOptions.Layout, out string diff);
            Assert.True(equal, diff);
        }

        [Fact]
        public void Issue544_ReportLayout_SupersetPersisted_ControlNameFollowsAttributeReference_IsEquivalent()
        {
            // The projection re-emits SDK defaults the caller never sent, so the
            // requested-subset path is the one that must match by reference.
            string persisted = Issue544ReportPersisted
                .Replace("ControlName=\"Header\">", "ControlName=\"Header\" Height=\"20\">")
                .Replace("Top=\"10\" />", "Top=\"10\" Left=\"5\" BorderStyle=\"None\" />");

            bool equal = XmlEquivalence.AreEquivalent(persisted, Issue544ReportRequested, XmlEquivalenceOptions.Layout, out string diff);
            Assert.True(equal, diff);
        }

        [Fact]
        public void Issue544_ReportLayout_UnappliedAttributeReference_IsStillReported()
        {
            string persisted = Issue544ReportRequested.Replace("AttributeReference=\"&amp;bar2\"", "AttributeReference=\"&amp;bar\"");

            Assert.False(XmlEquivalence.AreEquivalent(persisted, Issue544ReportRequested, XmlEquivalenceOptions.Layout, out string diff));
            Assert.Contains("AttributeReference", diff);
        }

        [Fact]
        public void Issue544_ReportLayout_ControlNameUnrelatedToReference_IsStillReported()
        {
            string requested = "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\"><Control TypeName=\"ReportAttribute\" Name=\"Foo\" ControlName=\"Foo\" AttributeReference=\"&amp;bar2\" Top=\"10\" /></PrintBlock></Report>";
            string persistedSameShape = "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\"><Control TypeName=\"ReportAttribute\" Name=\"Baz\" ControlName=\"Baz\" AttributeReference=\"&amp;bar2\" Top=\"10\" /></PrintBlock></Report>";
            string persistedSuperset = "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\" Height=\"20\"><Control TypeName=\"ReportAttribute\" Name=\"Baz\" ControlName=\"Baz\" AttributeReference=\"&amp;bar2\" Top=\"10\" /></PrintBlock></Report>";

            Assert.False(XmlEquivalence.AreEquivalent(persistedSameShape, requested, XmlEquivalenceOptions.Layout, out _));
            Assert.False(XmlEquivalence.AreEquivalent(persistedSuperset, requested, XmlEquivalenceOptions.Layout, out _));
        }

        [Fact]
        public void Issue544_ReportLayout_MissingControl_IsStillReported()
        {
            string persisted = "<Report><PrintBlock Name=\"Header\" ControlName=\"Header\" Height=\"20\" /></Report>";

            Assert.False(XmlEquivalence.AreEquivalent(persisted, Issue544ReportRequested, XmlEquivalenceOptions.Layout, out _));
        }

        [Fact]
        public void Issue544_XmlEquivalence_ToleratesControlNameMatchingReferencedAttribute()
        {
            // SDK renamed ControlName to match the referenced Attribute
            string xmlRequested = "<Item ControlName=\"Item1\" Attribute=\"CliCod\" Top=\"10\" />";
            string xmlPersisted = "<Item ControlName=\"CliCod\" Attribute=\"CliCod\" Top=\"10\" />";

            bool equal = XmlEquivalence.AreEquivalent(xmlPersisted, xmlRequested, out string diff);
            Assert.True(equal, $"Expected elements to be equal when ControlName matches referenced attribute, but got diff: {diff}");
        }

        [Fact]
        public void Issue547_PatchPersistenceReceipt_DistinguishesSavedFromUnverified()
        {
            var payloadSaved = new JObject();
            PatchPersistenceReceipt.MarkNotPersisted(payloadSaved, saved: true, verifyError: "whitespace difference", normalizedMatches: true);

            Assert.Equal("WriteNotPersisted", payloadSaved["code"]?.ToString());
            Assert.Equal("normalization", payloadSaved["mismatchKind"]?.ToString());
            Assert.Contains("only by formatting or normalization", payloadSaved["message"]?.ToString());
            Assert.True(payloadSaved["saveAttempted"]?.ToObject<bool>());
            Assert.True(payloadSaved["sdkSaveCompleted"]?.ToObject<bool>());

            var payloadUnsaved = new JObject();
            PatchPersistenceReceipt.MarkNotPersisted(payloadUnsaved, saved: false, verifyError: "syntax rejected");
            Assert.Equal("WriteNotPersisted", payloadUnsaved["code"]?.ToString());
            Assert.Contains("does not contain the requested patched content", payloadUnsaved["message"]?.ToString());
            Assert.False(payloadUnsaved["saveAttempted"]?.ToObject<bool>());
        }
    }
}
