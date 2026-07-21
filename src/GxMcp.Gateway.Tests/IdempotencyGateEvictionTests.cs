using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Plan 028: _gates (ConcurrentDictionary<(kbPath,tool,key), SemaphoreSlim>) is
    // populated via GetOrAdd on every GetOrCompute call and, before this fix, never
    // removed. Every distinct client-supplied idempotency key leaked one live
    // SemaphoreSlim forever. GetOrCompute now evicts the gate (via value-matching
    // TryRemove) once it's released and uncontended.
    public class IdempotencyGateEvictionTests
    {
        [Fact]
        public async Task SequentialDistinctKeys_DoNotAccumulateGates()
        {
            var cache = new IdempotencyCache(15, 1000);
            const int N = 50;
            for (int i = 0; i < N; i++)
            {
                await cache.GetOrCompute("kb1", "t", "k" + i, "h" + i,
                    () => Task.FromResult(JObject.Parse("{\"i\":" + i + "}")));
            }

            Assert.True(cache.GateCount < N,
                $"Expected gates to be evicted after each completed compute; found {cache.GateCount} gates for {N} distinct keys.");
        }

        [Fact]
        public async Task SameKeySamePayload_FactoryRunsOnceAndCachesResult()
        {
            var cache = new IdempotencyCache(15, 1000);
            int factoryCalls = 0;
            JObject Factory()
            {
                factoryCalls++;
                return JObject.Parse("{\"answer\":42}");
            }

            var r1 = await cache.GetOrCompute("kb1", "t", "k1", "h1", () => Task.FromResult(Factory()));
            var r2 = await cache.GetOrCompute("kb1", "t", "k1", "h1", () => Task.FromResult(Factory()));

            Assert.Equal(1, factoryCalls);
            Assert.Equal(r1.ToString(), r2.ToString());
        }

        [Fact]
        public async Task ReusedKey_ConflictingPayload_StillThrows()
        {
            var cache = new IdempotencyCache(15, 1000);
            await cache.GetOrCompute("kb1", "t", "k1", "h1",
                () => Task.FromResult(JObject.Parse("{}")));

            await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
                cache.GetOrCompute("kb1", "t", "k1", "h2",
                    () => Task.FromResult(JObject.Parse("{}"))));
        }
    }
}
