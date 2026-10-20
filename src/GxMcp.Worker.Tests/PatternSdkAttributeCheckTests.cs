using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The raw PatternInstance edit used to accept attributes the pattern specification does not
    // declare in its dry-run, then lose them on save (PatternVerificationMismatch / rejectedByPersist).
    public class PatternSdkAttributeCheckTests
    {
        private sealed class FakeSpec : IPatternSpecView
        {
            private readonly Dictionary<string, string[]> _attributes = new Dictionary<string, string[]>
            {
                ["Instance"] = new[] { "childrenOrderedList", "type" },
                ["Transaction"] = new[] { "childrenOrderedList", "TrnTemplate" },
                ["TrnTable"] = new[] { "childrenOrderedList", "name", "type" },
                ["TrnTableAttribute"] = new[] { "attribute", "description", "controlType", "controlLoadDynamically", "conditionDynAtts", "controlInputDPParameters", "defaultControlLoadDynamically" }
            };
            private readonly Dictionary<string, string> _children = new Dictionary<string, string>
            {
                ["Instance/transaction"] = "Transaction",
                ["Transaction/table"] = "TrnTable",
                ["TrnTable/attribute"] = "TrnTableAttribute"
            };

            internal readonly HashSet<string> Unlisted = new HashSet<string>();

            public string RootType() => "Instance";
            public string ChildType(string parentType, string childName) =>
                _children.TryGetValue(parentType + "/" + childName, out string type) ? type : null;
            public IReadOnlyCollection<string> AttributeNames(string typeName) =>
                !Unlisted.Contains(typeName) && _attributes.TryGetValue(typeName, out string[] names) ? new HashSet<string>(names) : null;
        }

        private static XDocument Doc(string combo) => XDocument.Parse(
            "<instance type='WebPanel'><transaction TrnTemplate='T'><table name='TableMain' type='Responsive'>"
            + "<attribute attribute='adbb33c9-0906-4971-833c-998de27e0676-Category' " + combo + " />"
            + "</table></transaction></instance>");

        [Fact]
        public void DeclaredAttributes_AreNotReported()
        {
            Assert.Empty(PatternSdkAttributeCheck.Find(
                Doc("controlType='DVelop Combo' controlLoadDynamically='True' conditionDynAtts='A;B' controlInputDPParameters='A'"), new FakeSpec()));
        }

        [Fact]
        public void TheNamesFromTheFieldReport_AreReported_WithTheDeclaredNameTheyMean()
        {
            var found = PatternSdkAttributeCheck.Find(
                Doc("controlLoadDataDynamically='True' controlConditionsDynamicAttsOrVars='A;B'"), new FakeSpec(),
                name => WwpComboProperties.Suggest(name)?.SdkName);

            Assert.Equal(new[] { "controlLoadDataDynamically", "controlConditionsDynamicAttsOrVars" }, found.Select(f => f.Attribute));
            Assert.All(found, f =>
            {
                Assert.Equal("attribute", f.Element);
                Assert.Equal("/instance/transaction[1]/table[1]/attribute[1]", f.ElementPath);
            });
            Assert.Equal("controlLoadDynamically", found[0].DidYouMean);
            Assert.Equal("conditionDynAtts", found[1].DidYouMean);
        }

        [Fact]
        public void ANameInTheWrongCase_IsReported_AndSuggestedInTheDeclaredCase()
        {
            var found = PatternSdkAttributeCheck.Find(Doc("ControlType='x'"), new FakeSpec());

            Assert.Equal("ControlType", found.Single().Attribute);
            Assert.Equal("controlType", found.Single().DidYouMean);
        }

        [Fact]
        public void ADefaultTwinInTheWrongCase_IsSuggestedAsTheDeclaredTwin_NotAsTheAttributeItShadows()
        {
            var found = PatternSdkAttributeCheck.Find(Doc("DefaultControlLoadDynamically='True'"), new FakeSpec());

            Assert.Equal("defaultControlLoadDynamically", found.Single().DidYouMean);
        }

        [Fact]
        public void AnUnrelatedName_IsReportedWithoutASuggestion()
        {
            var found = PatternSdkAttributeCheck.Find(Doc("zzzzzz='1'"), new FakeSpec());

            Assert.Null(found.Single().DidYouMean);
        }

        [Fact]
        public void TheCustomPropertiesPayloadAndNamespaces_AreNeverReported()
        {
            var document = XDocument.Parse(
                "<instance xmlns:x='urn:x' x:note='1'><transaction PATTERN_ELEMENT_CUSTOM_PROPERTIES='&lt;Properties /&gt;' /></instance>");

            Assert.Empty(PatternSdkAttributeCheck.Find(document, new FakeSpec()));
        }

        [Fact]
        public void ElementsTheSpecificationDoesNotDescribe_AreNotJudged()
        {
            var document = XDocument.Parse("<instance><unknownContainer bogus='1'><attribute bogus='2' /></unknownContainer></instance>");

            Assert.Empty(PatternSdkAttributeCheck.Find(document, new FakeSpec()));
        }

        [Fact]
        public void UndeclaredAttributes_AreReportedOnEveryLevelOfTheTree()
        {
            var document = XDocument.Parse("<instance bogus='0'><transaction bogus='1'><table bogus='2' /></transaction></instance>");

            var found = PatternSdkAttributeCheck.Find(document, new FakeSpec());

            Assert.Equal(new[] { "instance", "transaction", "table" }, found.Select(f => f.Element));
        }

        [Fact]
        public void ATypeWhoseAttributesAreUnknown_IsNotJudged_ButItsChildrenStillAre()
        {
            var spec = new FakeSpec { Unlisted = { "Transaction" } };
            var document = XDocument.Parse("<instance><transaction bogus='1'><table bogus='2' /></transaction></instance>");

            var found = PatternSdkAttributeCheck.Find(document, spec);

            Assert.Equal("table", found.Single().Element);
        }

        [Fact]
        public void RepeatedSiblings_GetTheirOccurrenceInThePath()
        {
            var document = XDocument.Parse("<instance><transaction><table name='a'><attribute ok='x' /><attribute bad='y' /></table></transaction></instance>");

            var found = PatternSdkAttributeCheck.Find(document, new FakeSpec());

            Assert.Equal(new[] { "ok", "bad" }, found.Select(f => f.Attribute));
            Assert.Equal("/instance/transaction[1]/table[1]/attribute[2]", found[1].ElementPath);
        }

        [Fact]
        public void NoDocumentOrSpecification_FindsNothing()
        {
            Assert.Empty(PatternSdkAttributeCheck.Find(null, new FakeSpec()));
            Assert.Empty(PatternSdkAttributeCheck.Find(Doc(string.Empty), null));
        }

        [Fact]
        public void Describe_GivesTheRefusalTextAndTheFirstElementsVerifyDiff()
        {
            var found = PatternSdkAttributeCheck.Find(
                Doc("controlLoadDataDynamically='True' controlConditionsDynamicAttsOrVars='A'"), new FakeSpec(),
                name => WwpComboProperties.Suggest(name)?.SdkName);

            JObject description = PatternSdkAttributeCheck.Describe(found, "WorkWithPlus");

            Assert.Contains("The WorkWithPlus specification does not declare 2 attribute(s)", (string)description["message"]);
            Assert.Contains("@controlLoadDataDynamically (the specification declares 'controlLoadDynamically')", (string)description["message"]);
            Assert.Contains("PatternVerificationMismatch", (string)description["message"]);
            Assert.Contains("No change was applied", (string)description["message"]);
            Assert.Equal("attribute", (string)description["verifyDiff"]["element"]);
            Assert.Equal(new[] { "controlLoadDataDynamically", "controlConditionsDynamicAttsOrVars" },
                description["verifyDiff"]["rejectedAttributes"].Select(t => (string)t));
        }

        [Fact]
        public void Describe_CapsTheListedLines()
        {
            var many = Enumerable.Range(0, 12).Select(i => new DiscardedAttribute
            {
                ElementPath = "/instance/a[" + i + "]", Element = "a", Attribute = "x" + i
            }).ToList();

            string message = (string)PatternSdkAttributeCheck.Describe(many, "P")["message"];

            Assert.Contains("12 attribute(s)", message);
            Assert.Contains("; ...", message);
            Assert.DoesNotContain("x11", message);
        }

        // The refusal has to come before anything is saved, in the preview and in the real write.
        [Fact]
        public void TheRawWritePath_RunsTheCheckBeforeTheDryRunReturnAndBeforeTheSave()
        {
            string source = File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(), "src", "GxMcp.Worker", "Services", "WriteService.PatternWrite.cs"));

            int check = source.IndexOf("RejectAttributesOutsideSdkSpecification(obj, target, partName, plan.Xml", StringComparison.Ordinal);
            int refusal = source.IndexOf("if (undeclaredAttributes != null) return undeclaredAttributes;", check, StringComparison.Ordinal);
            int dryRun = source.IndexOf("code: \"WriteDryRun\"", check, StringComparison.Ordinal);
            int envelope = source.IndexOf("BuildPatternPartEnvelope(obj, partName, normalizedInput", StringComparison.Ordinal);

            Assert.True(check > 0, "the raw PatternInstance write must check attribute names against the pattern specification");
            Assert.True(refusal > check && refusal < dryRun, "the refusal must be returned before the preview answers");
            Assert.True(dryRun > check, "the preview must be refused by the same check");
            Assert.True(envelope > dryRun, "the real write must be refused before the part is touched");
            Assert.Contains("code: \"PatternAttributeNotInSdkSpecification\"", source);
            string method = source.Substring(source.IndexOf("private string RejectAttributesOutsideSdkSpecification", StringComparison.Ordinal));
            Assert.Matches(new System.Text.RegularExpressions.Regex(@"SDK attribute-name check skipped: "" \+ ex\.Message\);\s+return null;"), method);
            Assert.Contains("if (specificationChecked) ((JArray)dryRunResult[\"verified\"]).Add(\"sdkAttributeNames\");", source);
        }
    }
}
