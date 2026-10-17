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

        [Fact]
        public void Issue544_XmlEquivalence_ToleratesStrippedControlIdAttribute()
        {
            string xmlRequested = "<Control id=\"ctrl_1\" ControlName=\"BtnSubmit\" Caption=\"Submit\" />";
            string xmlPersisted = "<Control ControlName=\"BtnSubmit\" Caption=\"Submit\" />";

            bool equal = XmlEquivalence.AreEquivalent(xmlPersisted, xmlRequested, out string diff);
            Assert.True(equal, $"Expected elements to be equal despite stripped id, but got diff: {diff}");
        }

        [Fact]
        public void Issue544_XmlEquivalence_ToleratesAmpersandEntityEncodingInVariableRef()
        {
            string xmlLeft = "<Control ControlName=\"Tot\" attribute=\"var:15\" />";
            string xmlRight = "<Control ControlName=\"Tot\" attribute=\"&amp;Total\" />";

            bool equal = XmlEquivalence.AreEquivalent(xmlLeft, xmlRight, out string diff);
            Assert.True(equal, $"Expected elements to be equal with &amp; entity encoding, but got diff: {diff}");
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
