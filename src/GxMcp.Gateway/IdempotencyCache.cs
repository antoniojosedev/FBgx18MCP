using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    public sealed class IdempotencyCache
    {
        // PERFORMANCE (G-A2): SemaphoreSlim.WaitAsync used to block indefinitely. If a worker
        // hung inside factory(), every subsequent caller for the same key starved until the
        // TTL fired (up to 65 minutes). Bound the wait at 30 seconds and fall back to running
        // factory() without the gate — best-effort idempotency beats a deadlock.
        private static readonly TimeSpan GateAcquisitionTimeout = TimeSpan.FromSeconds(30);

        private readonly TimeSpan _ttl;
        private readonly int _capacity;
        private readonly ConcurrentDictionary<string, KbBucket> _buckets = new ConcurrentDictionary<string, KbBucket>();
        private readonly ConcurrentDictionary<(string, string, string), SemaphoreSlim> _gates =
            new ConcurrentDictionary<(string, string, string), SemaphoreSlim>();

        public IdempotencyCache(int ttlMinutes, int capacity)
        {
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
            _capacity = capacity;
        }

        // Plan 028: test-only visibility into gate accumulation (InternalsVisibleTo
        // GxMcp.Gateway.Tests is already configured in the csproj).
        internal int GateCount => _gates.Count;

        public bool TryGet(string kbPath, string tool, string key,
                           string payloadHash, out JObject? cached)
        {
            cached = null;
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            return bucket.TryGet(tool, key, payloadHash, out cached);
        }

        public void Put(string kbPath, string tool, string key,
                        string payloadHash, JObject result)
        {
            var bucket = _buckets.GetOrAdd(kbPath, _ => new KbBucket(_capacity, _ttl));
            bucket.Put(tool, key, payloadHash, result);
        }

        public async Task<JObject> GetOrCompute(
            string kbPath, string tool, string key, string payloadHash,
            Func<Task<JObject>> factory)
        {
            if (TryGet(kbPath, tool, key, payloadHash, out var cached))
                return cached!;

            var gate = _gates.GetOrAdd((kbPath, tool, key), _ => new SemaphoreSlim(1, 1));
            bool gateAcquired = await gate.WaitAsync(GateAcquisitionTimeout).ConfigureAwait(false);
            if (!gateAcquired)
            {
                // PERFORMANCE (G-A2): gate held by a stuck factory. Log once and execute without
                // the gate so this caller still gets a response. We deliberately do NOT Put the
                // result, otherwise two parallel factories could race on the cache.
                try { Program.Log($"[Gateway] idempotency gate timeout tool={tool} key={key}"); } catch { }
                try
                {
                    return await factory().ConfigureAwait(false);
                }
                catch (ErrorNotCacheable ex)
                {
                    return ex.Result;
                }
            }

            try
            {
                if (TryGet(kbPath, tool, key, payloadHash, out cached))
                    return cached!;
                try
                {
                    var result = await factory().ConfigureAwait(false);
                    Put(kbPath, tool, key, payloadHash, result);
                    return result;
                }
                catch (ErrorNotCacheable ex)
                {
                    return ex.Result;
                }
            }
            finally
            {
                gate.Release();
                // Plan 028: once released and uncontended, evict the gate so _gates
                // doesn't grow without bound for every distinct client-supplied key.
                // CurrentCount == 1 means fully released and idle (no waiters holding
                // it back down to 0). Value-matching TryRemove only removes the
                // instance we actually hold, so a concurrent GetOrAdd that already
                // returned this same instance is unaffected, and a replacement
                // instance installed by a racing caller is never clobbered.
                if (gate.CurrentCount == 1 &&
                    _gates.TryRemove(new KeyValuePair<(string, string, string), SemaphoreSlim>((kbPath, tool, key), gate)))
                {
                    gate.Dispose();
                }
            }
        }

        // PERFORMANCE (G-M4): KbBucket now shards its state across N independent LRU slots.
        // Each shard has its own lock, dictionary, linked-list, and capacity slice. Strict
        // global LRU becomes per-shard LRU, which is acceptable for an idempotency cache
        // (semantics: "don't re-run the same key twice in the TTL window"). Hot-key contention
        // drops by 1/N because two threads hitting different shards never block each other.
        private sealed class KbBucket
        {
            private const int ShardCount = 16; // power of two for cheap hash masking
            private readonly Shard[] _shards;
            private readonly TimeSpan _ttl;

            public KbBucket(int capacity, TimeSpan ttl)
            {
                _ttl = ttl;
                _shards = new Shard[ShardCount];
                int perShard = Math.Max(1, (capacity + ShardCount - 1) / ShardCount);
                for (int i = 0; i < ShardCount; i++) _shards[i] = new Shard(perShard, ttl);
            }

            private Shard PickShard(string tool, string key)
            {
                // Stable, low-overhead hash; deterministic across processes is not required.
                int h = unchecked((tool?.GetHashCode() ?? 0) * 397) ^ (key?.GetHashCode() ?? 0);
                return _shards[(h & int.MaxValue) % ShardCount];
            }

            public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                => PickShard(tool, key).TryGet(tool, key, payloadHash, out cached);

            public void Put(string tool, string key, string payloadHash, JObject result)
                => PickShard(tool, key).Put(tool, key, payloadHash, result);

            private sealed class Shard
            {
                private readonly int _capacity;
                private readonly TimeSpan _ttl;
                private readonly LinkedList<(string Tool, string Key)> _lru = new LinkedList<(string Tool, string Key)>();
                private readonly Dictionary<(string, string), Entry> _map = new Dictionary<(string, string), Entry>();
                private readonly object _lock = new object();

                public Shard(int capacity, TimeSpan ttl) { _capacity = capacity; _ttl = ttl; }

                public bool TryGet(string tool, string key, string payloadHash, out JObject? cached)
                {
                    cached = null;
                    lock (_lock)
                    {
                        if (!_map.TryGetValue((tool, key), out var entry)) return false;
                        if (DateTime.UtcNow - entry.LastAccessedAt > _ttl)
                        {
                            _map.Remove((tool, key));
                            _lru.Remove(entry.Node);
                            return false;
                        }
                        if (entry.PayloadHash != payloadHash)
                            throw new IdempotencyConflictException(
                                $"idempotency key '{key}' reused with different payload");
                        entry.LastAccessedAt = DateTime.UtcNow;
                        _lru.Remove(entry.Node);
                        _lru.AddFirst(entry.Node);
                        cached = entry.Result;
                        return true;
                    }
                }

                public void Put(string tool, string key, string payloadHash, JObject result)
                {
                    lock (_lock)
                    {
                        if (_map.TryGetValue((tool, key), out var existing))
                        {
                            _lru.Remove(existing.Node);
                            _map.Remove((tool, key));
                        }
                        while (_map.Count >= _capacity)
                        {
                            var oldest = _lru.Last!;
                            _lru.RemoveLast();
                            _map.Remove(oldest.Value);
                        }
                        var node = new LinkedListNode<(string, string)>((tool, key));
                        _lru.AddFirst(node);
                        _map[(tool, key)] = new Entry
                        {
                            PayloadHash = payloadHash,
                            Result = result,
                            LastAccessedAt = DateTime.UtcNow,
                            Node = node
                        };
                    }
                }
            }

            private sealed class Entry
            {
                public string PayloadHash = "";
                public JObject Result = new JObject();
                public DateTime LastAccessedAt;
                public LinkedListNode<(string, string)> Node = null!;
            }
        }
    }
}
