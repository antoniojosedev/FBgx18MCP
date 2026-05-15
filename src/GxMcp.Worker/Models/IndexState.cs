using System;

namespace GxMcp.Worker.Models
{
    public class IndexState
    {
        public string Status { get; set; }       // "Cold" | "Reindexing" | "Ready"
        public DateTime? LastIndexedAt { get; set; }
        public int TotalObjects { get; set; }
        public double? Progress { get; set; }    // 0..1, only when Reindexing
        public int? EtaMs { get; set; }          // only when Reindexing
    }
}
