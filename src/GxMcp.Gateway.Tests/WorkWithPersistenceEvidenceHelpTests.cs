using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Issue #590: the <c>genexus_workwith</c> help must declare that <c>verified</c> is
    /// scoped to a reread of the received in-memory instance, name the receipt field
    /// that says so, and point at the restart smoke that proves durability - without
    /// promising an independent persisted reread the tool does not perform.
    /// </summary>
    public class WorkWithPersistenceEvidenceHelpTests
    {
        private static string Help() => ToolHelpCatalog.Get("genexus_workwith") ?? string.Empty;

        [Fact]
        public void Help_DeclaresThePersistenceEvidenceLimit()
        {
            string help = Help();

            Assert.Contains("persistenceEvidence", help, StringComparison.Ordinal);
            Assert.Contains("rereadOf: received_in_memory_instance", help, StringComparison.Ordinal);
            Assert.Contains("independentPersistedReread: false", help, StringComparison.Ordinal);
            Assert.Contains("provesDurability: false", help, StringComparison.Ordinal);
            Assert.Contains("re-opens the persisted definition", help, StringComparison.Ordinal);
            Assert.Contains("Worker restart", help, StringComparison.Ordinal);
            Assert.Contains("GeneXus IDE", help, StringComparison.Ordinal);
        }

        [Fact]
        public void Help_PointsAtTheDurabilitySmoke()
        {
            string help = Help();

            Assert.Contains("scripts/workwith_persistence_smoke.ps1", help, StringComparison.Ordinal);
            Assert.Contains("genexus_worker_reload", help, StringComparison.Ordinal);
            Assert.Contains("disposable", help, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Help_DoesNotPromiseAnIndependentPersistedReread()
        {
            string help = Help();

            // The exact overclaims a receipt reader would act on.
            Assert.DoesNotContain("independent reread", help, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("re-reads the persisted definition", help, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("survives a Worker restart.", help, StringComparison.Ordinal);
        }
    }
}
