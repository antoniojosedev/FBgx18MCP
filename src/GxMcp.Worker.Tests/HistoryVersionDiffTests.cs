using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #710 — read-only comparison of native GeneXus revisions. The SDK-bound
    /// rendering is validated live; these tests pin the pure parts: the load order that
    /// makes a non-current revision's parts loadable, and the diff envelope.
    /// </summary>
    public class HistoryVersionDiffTests
    {
        private static JObject Diff(string part, string representation, string before, string after, int toVersionId = 2)
        {
            var from = new RevisionDiffSide(new JObject { ["versionId"] = 1, ["representation"] = representation }, before);
            var to = new RevisionDiffSide(new JObject { ["versionId"] = toVersionId, ["representation"] = representation }, after);
            return JObject.Parse(RevisionDiffEnvelope.Build("Obj", part, from, to, 3));
        }

        [Fact]
        public void LoadAfterPriming_LoadsTheCurrentPartsBeforeTheRevision()
        {
            var calls = new List<string>();

            string loaded = RevisionContentReader.LoadAfterPrimingCurrentParts(
                () => calls.Add("prime-current"),
                () => { calls.Add("load-revision"); return "revision"; });

            Assert.Equal("revision", loaded);
            Assert.Equal(new[] { "prime-current", "load-revision" }, calls);
        }

        [Fact]
        public void LoadAfterPriming_RejectsMissingDelegates()
        {
            Assert.Throws<ArgumentNullException>(() => RevisionContentReader.LoadAfterPrimingCurrentParts<string>(null, () => "x"));
            Assert.Throws<ArgumentNullException>(() => RevisionContentReader.LoadAfterPrimingCurrentParts<string>(() => { }, null));
        }

        [Fact]
        public void DiffEnvelope_ReportsSourceChangesBetweenRevisions()
        {
            var json = Diff("Source", "source",
                "&Msg = 'revision one'\nmsg(&Msg)\n",
                "&Msg = 'revision two'\nmsg(&Msg)\nmsg('added line')\n",
                toVersionId: 3);

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("VersionDiffComputed", json["code"]?.ToString());
            var result = json["result"]!;
            Assert.Equal("Source", result["part"]?.ToString());
            Assert.Equal(1, (int)result["from"]!["versionId"]!);
            Assert.Equal(3, (int)result["to"]!["versionId"]!);
            Assert.False((bool)result["identical"]!);
            Assert.Equal("none", result["normalization"]?.ToString());
            string diff = result["diff"]!.ToString();
            Assert.Contains("-&Msg = 'revision one'", diff);
            Assert.Contains("+&Msg = 'revision two'", diff);
            Assert.Contains("+msg('added line')", diff);
        }

        [Fact]
        public void DiffEnvelope_IdenticalContentHasNoDiff()
        {
            var result = Diff("Rules", "source", "parm(in:&A);\r\n", "parm(in:&A);\n")["result"]!;

            Assert.True((bool)result["identical"]!);
            Assert.Equal(string.Empty, result["diff"]!.ToString());
        }

        [Fact]
        public void DiffEnvelope_IndentsSingleLinePartXmlSoTheDiffIsPerElement()
        {
            var result = Diff("Structure", RevisionDiffEnvelope.PartXml,
                "<Part><Level><Attribute Name=\"A\" /></Level></Part>",
                "<Part><Level><Attribute Name=\"A\" /><Attribute Name=\"B\" /></Level></Part>")["result"]!;

            Assert.Equal("xml-indented", result["normalization"]?.ToString());
            string diff = result["diff"]!.ToString();
            Assert.Contains("+    <Attribute Name=\"B\" />", diff);
            Assert.DoesNotContain("-<Part>", diff);
        }

        [Fact]
        public void DiffEnvelope_MasksRegeneratedGuidIdsOnVisualXml()
        {
            // Two reads of the same WebForm revision: the SDK assigned new ids.
            string first = "<GxMultiForm><Form id=\"1\"><layout id=\"b989b6d7-5d14-4c05-b504-3dc12f28c63c\">"
                + "<table id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\" controlName=\"MainTable\" /></layout></Form></GxMultiForm>";
            string second = "<GxMultiForm><Form id=\"1\"><layout id=\"7aad317f-2ee5-4206-a9be-745392cd8dc3\">"
                + "<table id=\"ca56da13-7e19-49b9-9947-67697ae77214\" controlName=\"MainTable\" /></layout></Form></GxMultiForm>";

            var result = Diff("WebForm", RevisionDiffEnvelope.VisualXml, first, second, toVersionId: 1)["result"]!;

            Assert.True((bool)result["identical"]!);
            Assert.Equal("xml-indented,guid-ids-masked", result["normalization"]?.ToString());
            Assert.Equal(4, (int)result["maskedGuidIds"]!);
        }

        [Fact]
        public void DiffEnvelope_VisualXmlStillShowsAuthoredChanges()
        {
            string before = "<GxMultiForm><table id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\" controlName=\"MainTable\" /></GxMultiForm>";
            string after = "<GxMultiForm><table id=\"ca56da13-7e19-49b9-9947-67697ae77214\" controlName=\"MainTable\">"
                + "<row id=\"982ae776-ad52-4997-9831-42bb34e94d9b\"><cell><textblock controlName=\"TxtRev710\" caption=\"Revision 710\" /></cell></row>"
                + "</table></GxMultiForm>";

            var result = Diff("WebForm", RevisionDiffEnvelope.VisualXml, before, after)["result"]!;

            Assert.False((bool)result["identical"]!);
            string diff = result["diff"]!.ToString();
            Assert.Contains("+        <textblock controlName=\"TxtRev710\" caption=\"Revision 710\" />", diff);
            Assert.DoesNotContain("8df76cc1", diff);
            Assert.DoesNotContain("ca56da13", diff);
        }

        [Fact]
        public void DiffEnvelope_PartXmlKeepsIdsBecauseOnlyVisualReadsRegenerateThem()
        {
            var result = Diff("Other", RevisionDiffEnvelope.PartXml,
                "<Part id=\"8df76cc1-1724-4592-9d58-2f25b904c00f\" />",
                "<Part id=\"ca56da13-7e19-49b9-9947-67697ae77214\" />")["result"]!;

            Assert.False((bool)result["identical"]!);
            Assert.Equal("xml-indented", result["normalization"]?.ToString());
            Assert.Equal(0, (int)result["maskedGuidIds"]!);
        }

        [Fact]
        public void DiffEnvelope_MalformedXmlIsComparedVerbatim()
        {
            var result = Diff("Other", RevisionDiffEnvelope.PartXml, "<Part>", "<Part/>")["result"]!;

            Assert.Equal("none", result["normalization"]?.ToString());
            Assert.False((bool)result["identical"]!);
        }

        [Fact]
        public void DiffEnvelope_RefusesALineTableAboveTheBound()
        {
            var json = Diff("Source", "source", new string('\n', 4999), "x" + new string('\n', 4999));

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("VersionDiffTooLarge", json["error"]?["code"]?.ToString());
            Assert.Null(json["diff"]);
            Assert.True(5000L * 5000L > RevisionDiffEnvelope.MaxCells);
        }
    }
}
