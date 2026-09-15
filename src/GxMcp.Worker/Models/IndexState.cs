using System;

namespace GxMcp.Worker.Models
{
    public class IndexState
    {
        public string Status { get; set; } = "Cold";
        // Availability and freshness are deliberately separate. A restored snapshot
        // can serve reads while it is still stale relative to the open KB.
        public string Freshness { get; set; } = "stale";
        public DateTime? LastIndexedAt { get; set; }
        public DateTime? LastSuccessfulScanAt { get; set; }
        public int TotalObjects { get; set; }
        public double? Progress { get; set; }    // 0..1, only when Reindexing
        public int? EtaMs { get; set; }          // only when Reindexing
        public DateTime? LitePassCompletedUtc { get; set; }
        public DateTime? EnrichmentStartedUtc { get; set; }
    }
}
