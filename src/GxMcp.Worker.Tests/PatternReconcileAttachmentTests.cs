using Xunit;

namespace GxMcp.Worker.Tests
{
    // Friction 2026-05-28 — DryRun + verify-failed pattern envelopes now
    // attach the PatternChildOrderReconciler report so validate=only callers
    // see which parents the reconciler had to fix or skip. Source-level
    // convention test: exercising the live path needs a WWP host.
    public class PatternReconcileAttachmentTests
    {
        [Fact]
        public void WriteService_AttachReconcileReport_IsWired_ViaConvention()
        {
            // Definition + call sites now live in split partial files (plan 007).
            string servicesDir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "GxMcp.Worker", "Services");
            string writeSrc = System.IO.File.ReadAllText(System.IO.Path.Combine(servicesDir, "WriteService.VisualWrite.cs"))
                + System.IO.File.ReadAllText(System.IO.Path.Combine(servicesDir, "WriteService.PatternWrite.cs"));

            // Helper is defined.
            Assert.Contains("private static void AttachReconcileReport", writeSrc);
            // Both dry-run paths attach the report.
            Assert.Contains("AttachReconcileReport(dryResp, reconcileReport);", writeSrc);
            // Verify-failed envelope attaches it too.
            Assert.Contains("AttachReconcileReport(verifyJobj, reconcileReport);", writeSrc);
            // Output shape includes parentsUpdated + skipsHint.
            Assert.Contains("[\"parentsUpdated\"] = report.ParentsUpdated", writeSrc);
            Assert.Contains("skipsHint", writeSrc);
        }
    }
}
