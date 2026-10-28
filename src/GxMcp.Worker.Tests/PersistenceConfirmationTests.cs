using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #692: a print-block mutation or an object create that returned success could not tell the
    /// caller whether the store held the change, because the read-back went through the instance that was
    /// just mutated. The response now says so explicitly: <c>persistedVerified</c> is true only after a
    /// cache-invalidated reload found the change, and null (never true) otherwise.
    /// </summary>
    public class PersistenceConfirmationTests
    {
        [Fact]
        public void Annotate_ReportsAFreshReloadAsVerified()
        {
            JObject result = PersistenceConfirmation.Annotate(new JObject { ["name"] = "Sample" }, true, null);

            Assert.True((bool)result["saved"]);
            Assert.True((bool)result["persistedVerified"]);
            Assert.Equal("fresh-reload", (string)result["verification"]);
            Assert.Equal("Sample", (string)result["name"]);
        }

        [Fact]
        public void Annotate_ReportsAnUnavailableReloadAsUnconfirmed_NeverAsVerified()
        {
            JObject result = PersistenceConfirmation.Annotate(new JObject(), false, "The SDK object cache could not be invalidated.");

            Assert.True((bool)result["saved"]);
            Assert.Equal(JTokenType.Null, result["persistedVerified"].Type);
            Assert.Equal("unavailable: The SDK object cache could not be invalidated.", (string)result["verification"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Annotate_FallsBackToAGenericReason_WhenTheReloadGaveNone(string reason)
        {
            JObject result = PersistenceConfirmation.Annotate(new JObject(), false, reason);

            Assert.Equal(JTokenType.Null, result["persistedVerified"].Type);
            Assert.StartsWith("unavailable: ", (string)result["verification"]);
            Assert.True(((string)result["verification"]).Length > "unavailable: ".Length);
        }

        [Fact]
        public void Annotate_ToleratesANullResult()
        {
            Assert.Null(PersistenceConfirmation.Annotate(null, true, null));
        }

        [Fact]
        public void EveryPrintBlockMutationReadsBackThroughAReloadedObject_AndReportsTheConfirmation()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");

            // Add and rename reload once and again per retry; delete reloads once: five call sites plus the definition.
            Assert.Equal(6, Regex.Matches(src, @"ReloadForVerification\(").Count);
            Assert.Equal(3, Regex.Matches(src, @"PersistenceConfirmation\.Annotate\(").Count);
            Assert.Contains("_objectService.ReloadSavedObjectFresh(committed, out unavailableReason)", src);

            // The cached resolution is only the fallback inside the helper; no mutation may read back through it directly.
            Assert.Single(Regex.Matches(src,
                @"_objectService\.FindObject\(committed\.Name, committed\.TypeDescriptor\?\.Name\)"));
            Assert.DoesNotContain("var refreshedObj = _objectService.FindObject(", src);
            Assert.DoesNotContain("var retryObj = _objectService.FindObject(", src);
        }

        [Fact]
        public void DeletePrintBlock_DoesNotConfirmAnUnreadableReload()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "LayoutService.cs");
            string deletion = SourceAssert.MethodBody(src, "public string DeletePrintBlock(");
            Assert.Matches(@"LoadVisualContext\(refreshedObj, target, VisualSurface.Report\);\s*if \(refreshed.Error != null\) return refreshed.Error;", deletion);
        }

        [Fact]
        public void TheReloadIsTheObjectServiceFreshByIdentityResolution()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("internal KBObject ReloadSavedObjectFresh(KBObject saved, out string unavailableReason)", src);
            Assert.Contains("fresh = FindObjectFreshByIdentity(saved);", src);
        }

        [Fact]
        public void ObjectCreated_ReportsWhetherTheStoreHoldsTheObject()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Services", "ObjectService.cs");

            Assert.Contains("ReloadSavedObjectFresh(newObj, out string reloadUnavailable)", src);
            Assert.Contains("PersistenceConfirmation.Annotate(responsePayload, reloaded != null, reloadUnavailable)", src);
            // An Attribute already went through the reload for its metadata check; that result is reused, not repeated.
            Assert.Contains("typedMeta?[\"attribute\"]?[\"persistedVerified\"]", src);
        }

        [Fact]
        public void TheReportLayoutHelperLogDoesNotCallAnUnsavedStateStagedForever()
        {
            string src = RepoSource.Read("src", "GxMcp.Worker", "Helpers", "ReportLayoutHelper.cs");

            Assert.DoesNotContain("staged in memory", src);
            Assert.Equal(3, Regex.Matches(src, @"applied to the in-memory layout part \(the caller saves and verifies it\)").Count);
        }
    }
}
