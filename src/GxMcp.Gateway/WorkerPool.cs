using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GxMcp.Gateway
{
    public sealed class WorkerPoolFullException : Exception
    {
        public IReadOnlyList<KbHandle> OpenKbs { get; }
        public WorkerPoolFullException(IReadOnlyList<KbHandle> openKbs)
            : base($"WorkerPool full ({openKbs.Count} KBs open). Close one with genexus_kb action=close before opening another.")
        {
            OpenKbs = openKbs;
        }
    }

    public sealed class WorkerPool
    {
        private readonly Configuration _config;
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _spawnLock = new object();

        public event Action<string>? OnRpcResponse;
        public event Action<KbHandle>? OnWorkerExited;

        public WorkerPool(Configuration config) { _config = config; }

        private sealed class Entry
        {
            public KbHandle Handle = null!;
            public WorkerProcess? Worker;
            public DateTime LastActivityUtc = DateTime.UtcNow;
        }

        public IReadOnlyList<KbHandle> ListOpen() =>
            _entries.Values
                .Where(e => e.Worker != null)
                .Select(e => e.Handle)
                .ToArray();

        public bool IsAtCapacity()
        {
            int max = _config.Server?.MaxOpenKbs ?? 3;
            return _entries.Count >= max;
        }

        public WorkerProcess? TryGet(string alias)
        {
            if (_entries.TryGetValue(alias.ToLowerInvariant(), out var entry))
            {
                return entry.Worker;
            }

            return null;
        }

        public async Task<WorkerProcess> AcquireAsync(KbHandle handle, CancellationToken ct)
        {
            var entry = _entries.GetOrAdd(handle.NormalizedAlias, _ => new Entry { Handle = handle });
            if (entry.Worker != null)
            {
                entry.LastActivityUtc = DateTime.UtcNow;
                return entry.Worker;
            }

            // Serialise spawn so two concurrent acquires for the same KB don't race.
            // (Lock is cross-KB-coarse, but spawn is fast — the worker process IO is async.)
            await Task.Yield();
            lock (_spawnLock)
            {
                if (entry.Worker != null) return entry.Worker;

                int max = _config.Server?.MaxOpenKbs ?? 3;
                if (_entries.Count > max)
                {
                    var victim = SelectVictim();
                    if (victim != null)
                    {
                        EvictEntry(victim);
                    }

                    if (_entries.Count > max)
                    {
                        _entries.TryRemove(handle.NormalizedAlias, out _);
                        throw new WorkerPoolFullException(ListOpen());
                    }
                }

                var worker = new WorkerProcess(_config, handle);
                worker.OnRpcResponse += json => OnRpcResponse?.Invoke(json);
                var capturedHandle = handle;
                worker.OnWorkerExited += () =>
                {
                    OnWorkerExited?.Invoke(capturedHandle);
                    _entries.TryRemove(capturedHandle.NormalizedAlias, out _);
                };
                worker.Start();
                entry.Worker = worker;
                entry.LastActivityUtc = DateTime.UtcNow;
                return worker;
            }
        }

        public bool Close(string alias)
        {
            if (_entries.TryRemove(alias.ToLowerInvariant(), out var entry))
            {
                try { entry.Worker?.Stop(); } catch { }
                return true;
            }

            return false;
        }

        public void StopAll()
        {
            foreach (var e in _entries.Values)
            {
                try { e.Worker?.Stop(); } catch { }
            }
            _entries.Clear();
        }

        private Entry? SelectVictim()
        {
            return _entries.Values
                .Where(e => e.Worker != null)
                .OrderBy(e => e.LastActivityUtc)
                .FirstOrDefault();
        }

        private void EvictEntry(Entry entry)
        {
            try { entry.Worker?.Stop(); } catch { }
            _entries.TryRemove(entry.Handle.NormalizedAlias, out _);
        }

        internal void RegisterForTest(KbHandle h, DateTime? lastActivity = null)
        {
            _entries[h.NormalizedAlias] = new Entry
            {
                Handle = h,
                LastActivityUtc = lastActivity ?? DateTime.UtcNow
            };
        }

        internal KbHandle? SelectVictimForTest()
        {
            return _entries.Values.OrderBy(e => e.LastActivityUtc).FirstOrDefault()?.Handle;
        }
    }
}
