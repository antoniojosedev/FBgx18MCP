using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class BackgroundJobRegistry
    {
        private readonly int _retentionSeconds;
        private readonly ConcurrentDictionary<string, JobEntry> _jobs = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _seenBySession = new();

        public BackgroundJobRegistry(int retentionSeconds = 600) => _retentionSeconds = retentionSeconds;

        public JobEntry Start(string session, string kind, int estimatedSeconds)
        {
            var job = new JobEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Session = session,
                Kind = kind,
                Status = "running",
                StartedAt = DateTime.UtcNow,
                EstimatedSeconds = estimatedSeconds
            };
            _jobs[job.Id] = job;
            return job;
        }

        public void Complete(string jobId, bool success, string? summary, JObject? result = null)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return;
            job.Status = success ? "succeeded" : "failed";
            job.CompletedAt = DateTime.UtcNow;
            job.Summary = summary;
            job.Result = result;
        }

        public JobEntry? Get(string jobId) => _jobs.TryGetValue(jobId, out var j) ? j : null;

        public IReadOnlyList<JobEntry> SnapshotForSession(string session)
        {
            var seen = _seenBySession.GetOrAdd(session, _ => new HashSet<string>());
            lock (seen)
            {
                return _jobs.Values
                    .Where(j => j.Session == session)
                    .Where(j => j.Status == "running" || !seen.Contains(j.Id))
                    .ToList();
            }
        }

        public void MarkSeen(string session, IEnumerable<string> jobIds)
        {
            var seen = _seenBySession.GetOrAdd(session, _ => new HashSet<string>());
            lock (seen)
            {
                foreach (var id in jobIds)
                {
                    if (_jobs.TryGetValue(id, out var j) && j.Status != "running")
                        seen.Add(id);
                }
            }
        }

        public void SweepExpired()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_retentionSeconds);
            foreach (var kvp in _jobs)
            {
                if (kvp.Value.CompletedAt != null && kvp.Value.CompletedAt < cutoff)
                    _jobs.TryRemove(kvp.Key, out _);
            }
        }

        public int Count => _jobs.Count;
    }

    public sealed class JobEntry
    {
        public string Id { get; set; } = "";
        public string Session { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "running";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int EstimatedSeconds { get; set; }
        public string? Summary { get; set; }
        public JObject? Result { get; set; }
    }
}
