using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using GxMcp.TestSupport;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #638: an add_grid_variable write is confirmed only when the projected parent WebForm holds the requested variable
    // as a column of the grid control that renders the WorkWithPlus grid. A WebForm that merely changed, that holds the variable
    // outside that grid, or that the projection left untouched (no Design System) is not a confirmation.
    //
    // The fixture is the WebForm of WebPanel Gx0010 read through genexus_read from the KBTesteBench scratch KB (GeneXus 18):
    // grid control Grid1 is a simplegrid whose columns are item elements bound by attribute="var:N"/"att:N", and a filter
    // outside the grid binds another variable as data attribute="var:6". N is the variable's Id in the object, not its position:
    // genexus_inspect on the same object reports &LinkSelection internalId 5 (the grid column) and &cTrnGroupProbeBaseId 6
    // (the filter), while &Today, declared after them, has Id 1.
    public class WwpGridVariableProjectionTests
    {
        private static readonly string[] Grid1 = { "Grid1" };

        private static string RealWebForm()
            => File.ReadAllText(Path.Combine(TestFixtures.FindRepoRoot(),
                "src", "GxMcp.Worker.Tests", "Fixtures", "Gx0010WebForm.KBTesteBench.xml"));

        // The projection adds the column the way the SDK stores one: an item of the grid bound to the variable's layout id.
        private static string WithItem(string webForm, string gridControlName, string attribute, int index)
        {
            XDocument document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace);
            XElement grid = document.Descendants().Single(e => e.Name.LocalName == "simplegrid"
                && (string)e.Attribute("controlName") == gridControlName);
            var item = new XElement("item", new XAttribute("id", "0b1c2d3e-0000-4000-8000-000000000638"), new XAttribute("attribute", attribute));
            var items = grid.Elements("item").ToList();
            if (index >= items.Count) items.Last().AddAfterSelf(item); else items[index].AddBeforeSelf(item);
            return document.ToString(SaveOptions.DisableFormatting);
        }

        [Fact]
        public void TheRealFixtureHasAVariableColumnInsideTheGridAndAnotherVariableOutsideIt()
        {
            XDocument document = XDocument.Parse(RealWebForm());
            XElement grid = document.Descendants("simplegrid").Single();
            Assert.Equal("Grid1", (string)grid.Attribute("controlName"));
            Assert.Equal(new[] { "var:5", "att:4", "att:6", "att:7" }, grid.Elements("item").Select(i => (string)i.Attribute("attribute")));
            Assert.Contains(document.Descendants("data"), d => (string)d.Attribute("attribute") == "var:6");
        }

        [Fact]
        public void AnAppendedColumnBoundToTheVariableInTheRequestedGridIsConfirmed()
        {
            string before = RealWebForm();
            string after = WithItem(before, "Grid1", "var:12", 4);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.True(result["confirmed"]?.ToObject<bool>(), result.ToString());
            Assert.Equal("Grid1", result["gridControlName"]?.ToString());
            Assert.Equal(4, result["columnIndex"]?.ToObject<int>());
            Assert.Equal(new[] { "var:5", "att:4", "att:6", "att:7", "var:12" }, result["columns"].Select(c => c.ToString()));
            Assert.True(result["otherColumnsPreserved"]?.ToObject<bool>());
        }

        [Fact]
        public void AnInsertedColumnReportsItsIndex()
        {
            string before = RealWebForm();
            string after = WithItem(before, "Grid1", "var:12", 1);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.True(result["confirmed"]?.ToObject<bool>(), result.ToString());
            Assert.Equal(1, result["columnIndex"]?.ToObject<int>());
        }

        [Fact]
        public void AnUnchangedWebFormIsNotAConfirmation()
        {
            // The case reported on WWP 16.2 without a Design System: a later projection leaves the WebForm as it was.
            string before = RealWebForm();

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, before, Grid1, "TstNom", 12);

            Assert.False(result["confirmed"]?.ToObject<bool>());
            Assert.Equal(new[] { "var:5", "att:4", "att:6", "att:7" }, result["columns"].Select(c => c.ToString()));
            Assert.Contains("&TstNom", result["message"]?.ToString());
        }

        [Fact]
        public void AWebFormThatChangedWithoutTheColumnIsNotAConfirmation()
        {
            string before = RealWebForm();
            string after = before.Replace("caption=\"GX_BtnCancel\"", "caption=\"Cancel\"");
            Assert.NotEqual(before, after);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.False(result["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void TheVariableBoundOutsideTheGridIsNotAColumn()
        {
            // var:6 is the filter variable: bound in the form, but not as a column of Grid1.
            string before = RealWebForm();

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, before, Grid1, "cTrnGroupProbeBaseId", 6);

            Assert.False(result["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void ANewBindingOfTheVariableOutsideTheGridIsNotAColumn()
        {
            // The projection bound the variable, but as a data control next to the filter, not as a column of Grid1.
            string before = RealWebForm();
            XDocument document = XDocument.Parse(before, LoadOptions.PreserveWhitespace);
            XElement filter = document.Descendants("data").Single(d => (string)d.Attribute("attribute") == "var:6");
            filter.AddAfterSelf(new XElement("data", new XAttribute("attribute", "var:12"), new XAttribute("labelPosition", "None")));
            string after = document.ToString(SaveOptions.DisableFormatting);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.False(result["confirmed"]?.ToObject<bool>());
            Assert.Equal(new[] { "var:5", "att:4", "att:6", "att:7" }, result["columns"].Select(c => c.ToString()));
        }

        [Fact]
        public void AColumnInAnotherGridOrAGridThatIsNotThereIsNotAConfirmation()
        {
            string before = RealWebForm();
            string after = WithItem(before, "Grid1", "var:12", 4);

            JObject otherGrid = WwpActionService.VerifyProjectedGridVariable(before, after, new[] { "GridOther" }, "TstNom", 12);

            Assert.False(otherGrid["confirmed"]?.ToObject<bool>());
            Assert.Equal(new[] { "Grid1" }, otherGrid["webFormGrids"].Select(g => g.ToString()));
        }

        [Fact]
        public void AnotherVariablesColumnIsNotTheRequestedOne()
        {
            string before = RealWebForm();
            string after = WithItem(before, "Grid1", "var:13", 4);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.False(result["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void AColumnTheGridAlreadyHeldBeforeTheWriteIsNotAConfirmation()
        {
            // var:5 was already a column: its presence after the projection proves nothing about this write.
            string before = RealWebForm();

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, before, Grid1, "LinkSelection", 5);

            Assert.False(result["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void TwoColumnsBoundToTheVariableAreNotAConfirmation()
        {
            string before = RealWebForm();
            string after = WithItem(WithItem(before, "Grid1", "var:12", 4), "Grid1", "var:12", 0);

            JObject result = WwpActionService.VerifyProjectedGridVariable(before, after, Grid1, "TstNom", 12);

            Assert.False(result["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void AVariableWithoutALayoutIdIsMatchedOnlyByItsAmpersandName()
        {
            string before = RealWebForm();

            Assert.True(WwpActionService.VerifyProjectedGridVariable(before, WithItem(before, "Grid1", "&TstNom", 4), Grid1, "TstNom", null)["confirmed"]?.ToObject<bool>());
            Assert.False(WwpActionService.VerifyProjectedGridVariable(before, WithItem(before, "Grid1", "var:12", 4), Grid1, "TstNom", null)["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void AnUnreadableProjectedWebFormIsNotAConfirmation()
        {
            string before = RealWebForm();

            Assert.False(WwpActionService.VerifyProjectedGridVariable(before, null, Grid1, "TstNom", 12)["confirmed"]?.ToObject<bool>());
            Assert.False(WwpActionService.VerifyProjectedGridVariable(before, "<GxMultiForm", Grid1, "TstNom", 12)["confirmed"]?.ToObject<bool>());
        }

        [Fact]
        public void TheWriterChecksTheProjectedColumnAndRollsBackWhenItIsMissing()
        {
            string writer = SourceAssert.NormaliseNewlines(RepoSource.WithoutComments(
                RepoSource.Read("src", "GxMcp.Worker", "Services", "WwpActionService.GridVariable.cs")));
            int validation = writer.IndexOf("JObject previewMutation = ApplyAddGridVariableXml", StringComparison.Ordinal);
            int stale = writer.IndexOf("if (!IsExpectedVersion(expectedVersion, versionToken))", StringComparison.Ordinal);
            int preview = writer.IndexOf("if (dryRun)", StringComparison.Ordinal);
            int locked = writer.IndexOf("lock (WriteService.AcquirePerTargetLock(target))", StringComparison.Ordinal);
            int snapshots = writer.IndexOf("CaptureSnapshots(", StringComparison.Ordinal);
            int native = writer.IndexOf("ApplyNativeAddGridVariable(currentPart", StringComparison.Ordinal);
            int reread = writer.IndexOf("VerifyAddGridVariable(lockedBefore", StringComparison.Ordinal);
            int project = writer.IndexOf("WwpProjectionHelper.TryProjectHostOntoParent(", StringComparison.Ordinal);
            int column = writer.IndexOf("VerifyProjectedGridVariable(parentWebFormBefore, projectedWebForm", StringComparison.Ordinal);
            int notConfirmed = writer.IndexOf("\"WwpProjectionNotConfirmed\"", StringComparison.Ordinal);
            int added = writer.IndexOf("code: \"WwpGridVariableAdded\"", StringComparison.Ordinal);
            int rollback = writer.IndexOf("RestoreSnapshots(currentInstance, currentPart", StringComparison.Ordinal);

            // Refusals (invalid request, stale version) come before the preview, and the preview before any lock or snapshot.
            Assert.True(validation >= 0 && stale > validation && preview > stale && locked > preview && snapshots > locked, "refusal order changed");
            Assert.True(native > snapshots && reread > native && project > reread && column > project && notConfirmed > column && added > notConfirmed,
                "the write must be confirmed by the requested column in the projected grid before it is reported");
            Assert.True(rollback > added, "a failed confirmation must restore the snapshots");
            Assert.DoesNotContain("WwpProjectionVerificationUnavailable", writer);
            Assert.DoesNotContain("did not change after projection", writer);
        }
    }
}
