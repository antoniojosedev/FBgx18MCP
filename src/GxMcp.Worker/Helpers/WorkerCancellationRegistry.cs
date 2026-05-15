using System;
using System.Collections.Concurrent;
using System.Threading;

namespace GxMcp.Worker.Helpers
{
    // v2.3.8 (closing the deferred gap from Task 7.2) — worker-side cancellation
    // registry. The gateway sends long-running commands carrying a `cancelToken`
    // (typically the BackgroundJobRegistry job_id). The thread-safe command
    // dispatcher registers a CTS keyed by that token, passes the CT down to
    // services (SourceSearchService, AnalyzeService, ...), and removes the
    // entry when the command completes. The gateway can then send a parallel
    // `{"module":"Control","action":"Cancel","cancelToken":"…"}` command
    // (also thread-safe) which looks up the CTS and signals it. Without this
    // registry, the worker had no way to honour a cancel mid-call.
    public static class WorkerCancellationRegistry
    {
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        public static IDisposable Register(string token, out CancellationToken ct)
        {
            if (string.IsNullOrEmpty(token))
            {
                ct = CancellationToken.None;
                return new NoopDisposable();
            }
            var cts = _tokens.GetOrAdd(token, _ => new CancellationTokenSource());
            ct = cts.Token;
            return new Scope(token);
        }

        public static bool Cancel(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            if (!_tokens.TryGetValue(token, out var cts)) return false;
            try { cts.Cancel(); return true; }
            catch (ObjectDisposedException) { return false; }
        }

        public static int ActiveCount => _tokens.Count;

        // Test seam: reset state between tests.
        internal static void Reset()
        {
            foreach (var kvp in _tokens)
            {
                try { kvp.Value.Dispose(); } catch { }
            }
            _tokens.Clear();
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _token;
            private bool _disposed;
            public Scope(string token) { _token = token; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_tokens.TryRemove(_token, out var cts))
                {
                    try { cts.Dispose(); } catch { }
                }
            }
        }

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
