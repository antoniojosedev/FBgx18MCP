using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #594: after a read had already logged SUCCESS, the background edge scan
    /// ("[FR#3] Textual call scan failed: NullReferenceException") ran object after
    /// object and starved the SDK thread. Two layers are covered here without a KB:
    /// the edge helpers that threw for every hub with more than eight edges, and the
    /// failure breaker that bounds a scan which keeps failing.
    /// </summary>
    [Collection("LoggerTestSink")]
    public sealed class TextualCallScanRegressionTests : IDisposable
    {
        private readonly List<KeyValuePair<string, string>> _captured = new List<KeyValuePair<string, string>>();

        public TextualCallScanRegressionTests()
        {
            Logger.TestSink = (level, message) =>
            {
                // Other test classes log concurrently; keep only this feature's lines.
                if (message != null && message.StartsWith("[FR#3]", StringComparison.Ordinal))
                    lock (_captured) _captured.Add(new KeyValuePair<string, string>(level, message));
            };
        }

        public void Dispose()
        {
            Logger.TestSink = null;
        }

        // ── Root cause: companion-set lookup never matched, null table dereferenced ──

        [Fact]
        public void AddCallCow_HubEntryBeyondCompanionSetThreshold_DoesNotThrow()
        {
            var entry = new SearchIndex.IndexEntry { Name = "Hub", Type = "Procedure" };
            for (int i = 0; i < 12; i++)
            {
                Assert.True(IndexCacheService.AddCallCow(entry, "Callee" + i));
            }

            Assert.Equal(12, entry.Calls.Count);
            // The fast path must keep value semantics once it is actually in use.
            Assert.False(IndexCacheService.AddCallCow(entry, "callee3"));
            Assert.True(IndexCacheService.AddCallCow(entry, "Callee12"));
            Assert.Equal(13, entry.Calls.Count);
        }

        [Fact]
        public void AddCalledByCow_And_AddTableCow_BeyondThreshold_DoNotThrow()
        {
            var target = new SearchIndex.IndexEntry { Name = "Target", Type = "Procedure" };
            for (int i = 0; i < 10; i++)
            {
                Assert.True(IndexCacheService.AddCalledByCow(target, "Caller" + i));
                Assert.True(IndexCacheService.AddTableCow(target, "Table" + i));
            }

            Assert.Equal(10, target.CalledBy.Count);
            Assert.Equal(10, target.Tables.Count);
            Assert.False(IndexCacheService.AddCalledByCow(target, "CALLER9"));
            Assert.False(IndexCacheService.AddTableCow(target, "table0"));
        }

        [Fact]
        public void AddEdgeCow_UnknownGetter_FallsBackToLinearCheck()
        {
            var entry = new SearchIndex.IndexEntry { Name = "Hub", Type = "Procedure" };
            Func<SearchIndex.IndexEntry, List<string>> get = x => x.Calls;
            Action<SearchIndex.IndexEntry, List<string>> set = (x, l) => x.Calls = l;
            for (int i = 0; i < 12; i++)
            {
                Assert.True(IndexCacheService.AddEdgeCow(entry, get, set, "Callee" + i));
            }

            Assert.False(IndexCacheService.AddEdgeCow(entry, get, set, "callee11"));
            Assert.Equal(12, entry.Calls.Count);
        }

        // ── Breaker: a provider that fails for every object is bounded ──

        [Fact]
        public void TextualScan_ProviderThrowsForEveryObject_CallerReturnsAndScanStopsAtCap()
        {
            const int objectCount = 40;
            var cache = BuildCache(objectCount, out var searchIndex, out var callers, out var target);
            int invocations = 0;
            Func<IEnumerable<string>> throwingProvider = () =>
            {
                invocations++;
                throw new NullReferenceException("Object reference not set to an instance of an object.");
            };

            foreach (var caller in callers)
            {
                bool changed = cache.RunTextualCallScan(caller, searchIndex, throwingProvider);
                Assert.False(changed);
            }

            Assert.Equal(IndexCacheService.TextualScanFailureCap, invocations);
            Assert.True(cache.TextualScanTripped);
            Assert.Equal(objectCount - IndexCacheService.TextualScanFailureCap, cache.TextualScanSkipped);
            Assert.All(callers, caller => Assert.True(caller.Calls == null || caller.Calls.Count == 0));
            Assert.True(target.CalledBy == null || target.CalledBy.Count == 0);

            List<KeyValuePair<string, string>> lines;
            lock (_captured) lines = _captured.ToList();
            Assert.Equal(1, lines.Count(l => l.Key == "WARN"));
            Assert.Equal(IndexCacheService.TextualScanFailureCap - 1, lines.Count(l => l.Key == "DEBUG"));
            Assert.Contains("NullReferenceException", lines.Single(l => l.Key == "WARN").Value);
        }

        [Fact]
        public void TextualScan_HealthyProvider_StillEnrichesCallsAndCalledBy()
        {
            var cache = BuildCache(2, out var searchIndex, out var callers, out var target);
            var caller = callers[0];
            int invocations = 0;
            Func<IEnumerable<string>> provider = () =>
            {
                invocations++;
                return new[] { "Event Start\n  ProcB.Call(&a)\nEndEvent", "parm(&a);" };
            };

            bool changed = cache.RunTextualCallScan(caller, searchIndex, provider);

            Assert.True(changed);
            Assert.Equal(1, invocations);
            Assert.Contains("ProcB", caller.Calls);
            Assert.Contains(caller.Name, target.CalledBy);
            Assert.False(cache.TextualScanTripped);
            Assert.Equal(0, cache.TextualScanConsecutiveFailures);
            lock (_captured) Assert.Empty(_captured);
        }

        [Fact]
        public void TextualScan_SuccessResetsConsecutiveFailures()
        {
            var cache = BuildCache(1, out var searchIndex, out var callers, out _);
            var caller = callers[0];
            Func<IEnumerable<string>> throwing = () => throw new NullReferenceException();
            Func<IEnumerable<string>> healthy = () => new[] { "ProcB.Call(&a)" };

            for (int i = 0; i < IndexCacheService.TextualScanFailureCap - 1; i++)
                Assert.False(cache.RunTextualCallScan(caller, searchIndex, throwing));
            Assert.Equal(IndexCacheService.TextualScanFailureCap - 1, cache.TextualScanConsecutiveFailures);

            Assert.True(cache.RunTextualCallScan(caller, searchIndex, healthy));
            Assert.Equal(0, cache.TextualScanConsecutiveFailures);
            Assert.False(cache.TextualScanTripped);

            // The cap counts consecutive failures only: it takes a full run again to trip.
            for (int i = 0; i < IndexCacheService.TextualScanFailureCap - 1; i++)
                cache.RunTextualCallScan(caller, searchIndex, throwing);
            Assert.False(cache.TextualScanTripped);
            cache.RunTextualCallScan(caller, searchIndex, throwing);
            Assert.True(cache.TextualScanTripped);
        }

        [Fact]
        public void TextualScan_TrippedBreaker_IsReArmedByReindexStart()
        {
            var cache = BuildCache(1, out var searchIndex, out var callers, out var target);
            var caller = callers[0];
            int invocations = 0;
            Func<IEnumerable<string>> throwing = () => { invocations++; throw new NullReferenceException(); };

            for (int i = 0; i < IndexCacheService.TextualScanFailureCap + 3; i++)
                cache.RunTextualCallScan(caller, searchIndex, throwing);
            Assert.True(cache.TextualScanTripped);
            Assert.Equal(IndexCacheService.TextualScanFailureCap, invocations);
            Assert.Equal(3, cache.TextualScanSkipped);

            cache.MarkReindexStarted(1);

            Assert.False(cache.TextualScanTripped);
            Assert.Equal(0, cache.TextualScanSkipped);
            Assert.True(cache.RunTextualCallScan(caller, searchIndex, () => new[] { "ProcB.Call(&a)" }));
            Assert.Contains(caller.Name, target.CalledBy);
        }

        [Fact]
        public void TextualScan_OversizedPartIsSkipped_SmallerPartStillScanned()
        {
            var cache = BuildCache(1, out var searchIndex, out var callers, out var target);
            var caller = callers[0];
            string oversized = new string('x', IndexCacheService.TextualScanMaxSourceChars + 1) + "\nProcB.Call(&a)";

            Assert.False(cache.RunTextualCallScan(caller, searchIndex, () => new[] { oversized }));
            Assert.Equal(0, cache.TextualScanConsecutiveFailures);
            Assert.True(caller.Calls == null || caller.Calls.Count == 0);

            Assert.True(cache.RunTextualCallScan(caller, searchIndex, () => new[] { oversized, "ProcB.Call(&a)" }));
            Assert.Contains(caller.Name, target.CalledBy);
        }

        // ── Background dispatcher yields to foreground SDK work ──

        [Theory]
        [InlineData(false, 0, 0, false, false)]
        [InlineData(true, 0, 0, false, true)]   // a command is running on the SdkWorker thread
        [InlineData(false, 1, 0, false, true)]  // a command waits in the legacy SDK queue
        [InlineData(false, 0, 1, false, true)]  // a command waits in the STA scheduler
        [InlineData(true, 3, 2, true, false)]   // soft reload drains this queue from inside its own command
        public void BackgroundDispatcher_YieldsExactlyWhileForegroundSdkWorkIsPending(
            bool sdkBusy, int legacyQueueDepth, int scheduledDepth, bool shuttingDown, bool expected)
        {
            Assert.Equal(expected, Program.IsForegroundSdkWorkPending(sdkBusy, legacyQueueDepth, scheduledDepth, shuttingDown));
        }

        [Fact]
        public void BackgroundDispatcher_ReportsForegroundWorkWhileSdkCommandIsQueued()
        {
            // The process-level overload reads the real queues; other test classes may hold
            // scheduler items concurrently, so only the positive direction is asserted here.
            var item = new SdkCommandItem { Obj = new JObject(), RawLine = "{}" };
            Assert.True(Program.SdkCommandQueue.TryAdd(item));
            try
            {
                Assert.True(Program.IsForegroundSdkWorkPending());
            }
            finally
            {
                Assert.True(Program.SdkCommandQueue.TryTake(out _));
            }
        }

        // ── Fixture ──

        private static IndexCacheService BuildCache(
            int callerCount,
            out SearchIndex searchIndex,
            out List<SearchIndex.IndexEntry> callers,
            out SearchIndex.IndexEntry target)
        {
            target = new SearchIndex.IndexEntry
            {
                Name = "ProcB",
                Type = "Procedure",
                Guid = Guid.NewGuid().ToString(),
                CalledBy = new List<string>()
            };
            callers = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < callerCount; i++)
            {
                callers.Add(new SearchIndex.IndexEntry
                {
                    Name = "Panel" + i,
                    Type = "WebPanel",
                    Guid = Guid.NewGuid().ToString(),
                    Calls = new List<string>(),
                    CalledBy = new List<string>()
                });
            }

            var cache = new IndexCacheService();
            cache.LoadFromEntries(new[] { target }.Concat(callers));
            searchIndex = cache.GetIndex();
            target = searchIndex.Objects.Values.Single(e => e.Name == "ProcB");
            var byName = searchIndex.Objects.Values.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            callers = callers.Select(c => byName[c.Name]).ToList();
            return cache;
        }
    }
}
