using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GxMcp.TestSupport;
using GxMcp.Worker.Compatibility;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #590: the WorkWith write receipts verify by rereading the typed instance
    // from the same in-memory object that was just saved. That reread is real, but it
    // is not an independent read of the persisted definition and not a restart proof.
    // The receipt now says so in `persistenceEvidence`; these tests pin the declared
    // scope so a later edit cannot quietly promote `verified` into a persistence claim.
    public class WorkWithPersistenceEvidenceTests
    {
        private static string[] AdapterFile => new[] { "src", "GxMcp.Worker", "Compatibility", "WorkWithSdkAdapter.cs" };

        [Fact]
        public void BuildPersistenceEvidence_DeclaresRereadOfReceivedInstanceAndNoDurabilityProof()
        {
            var evidence = WorkWithSdkAdapter.BuildPersistenceEvidence();

            Assert.Equal("received_in_memory_instance", evidence["rereadOf"]?.ToString());
            Assert.False(evidence["independentPersistedReread"]?.ToObject<bool>());
            Assert.False(evidence["provesDurability"]?.ToObject<bool>());

            string limit = evidence["limit"]?.ToString() ?? string.Empty;
            Assert.Contains("not an independent read of the persisted definition", limit);
            Assert.Contains("Worker restart", limit);
            Assert.Contains("GeneXus IDE", limit);
            Assert.Contains("genexus_worker_reload", evidence["durabilitySmoke"]?.ToString());
        }

        [Fact]
        public void BuildPersistenceEvidence_NamesAnExistingSmokeScript()
        {
            string smoke = WorkWithSdkAdapter.PersistenceDurabilitySmoke;
            var match = Regex.Match(smoke, @"^scripts/([A-Za-z0-9_\-]+\.ps1)");
            Assert.True(match.Success, "durabilitySmoke must start with the repository-relative script path: " + smoke);

            // A receipt that points at a script nobody can run is not a recipe.
            Assert.True(System.IO.File.Exists(RepoSource.PathOf("scripts", match.Groups[1].Value)), smoke);
        }

        [Fact]
        public void BuildFilterReceipt_CarriesPersistenceEvidence_WithoutChangingVerified()
        {
            var observed = new JObject { ["name"] = "CustomerName", ["description"] = "Customer Name", ["domain"] = "" };
            var observedConditions = new JArray("CustomerName like &CustomerName;");
            var receipt = InvokeReceiptBuilder("BuildFilterReceipt", new Dictionary<string, object>
            {
                ["instanceName"] = "WorkWithWebCustomer",
                ["level"] = "Root",
                ["observedAttribute"] = observed,
                ["observedConditions"] = observedConditions,
                ["attributeName"] = "CustomerName",
                ["description"] = "Customer Name",
                ["domain"] = null,
                ["conditions"] = new List<string> { "CustomerName like &CustomerName;" }
            });

            // The declared limit is additive: the existing fields keep their meaning.
            Assert.True(receipt["verified"]?.ToObject<bool>());
            Assert.True(receipt["saved"]?.ToObject<bool>());
            Assert.True(receipt["persisted"]?.ToObject<bool>());

            var evidence = receipt["persistenceEvidence"] as JObject;
            Assert.NotNull(evidence);
            Assert.Equal(WorkWithSdkAdapter.BuildPersistenceEvidence().ToString(), evidence.ToString());
        }

        [Fact]
        public void EveryWriteReceipt_DeclaresPersistenceEvidence()
        {
            string source = AdapterSource();
            const string marker = "[\"persistenceEvidence\"] = BuildPersistenceEvidence()";

            // The persisting actions build their receipts in these members.
            Assert.Contains(marker, SourceAssert.MethodBody(source, "public static bool AddAction("));
            Assert.Contains(marker, SourceAssert.MethodBody(source, "public static bool RemoveAction("));
            Assert.Contains(marker, SourceAssert.MethodBody(source, "internal static JObject BuildFilterReceipt("));
            Assert.Contains(marker, SourceAssert.MethodBody(source, "internal static JObject BuildRemoveFilterReceipt("));

            // The dryRun plans are previews: nothing was saved, so they carry no
            // persistence statement at all.
            Assert.DoesNotContain(marker, SourceAssert.MethodBody(source, "internal static JObject BuildActionPlan("));
            Assert.DoesNotContain(marker, SourceAssert.MethodBody(source, "internal static JObject BuildFilterPlan("));
        }

        [Fact]
        public void RemoveActionReceipt_DeclaresItsVerificationScope()
        {
            // Issue #590 noted that remove_action reported `verified` without saying
            // what had been observed. The receipt now names the scope like the other two.
            string body = SourceAssert.MethodBody(AdapterSource(), "public static bool RemoveAction(");

            Assert.Contains("[\"verificationScope\"]", body);
            Assert.Contains("received in-memory instance", body);
        }

        /// The adapter's text with comments blanked, as the shared member extractor expects.
        private static string AdapterSource()
        {
            return RepoSource.WithoutComments(RepoSource.Read(AdapterFile));
        }

        /// Binds the builder's parameters by name so a sibling change that appends
        /// parameters (for example a read-source enum) keeps this test compiling and
        /// meaningful: an unknown parameter receives its default value.
        private static JObject InvokeReceiptBuilder(string name, IDictionary<string, object> namedArgs)
        {
            var method = typeof(WorkWithSdkAdapter).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            var args = method.GetParameters().Select(p =>
            {
                if (namedArgs.TryGetValue(p.Name, out var value)) return value;
                return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
            }).ToArray();
            return (JObject)method.Invoke(null, args);
        }
    }
}
