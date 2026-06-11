using System;
using System.Threading;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Process-wide gate serializing access to the GeneXus SDK object model.
    ///
    /// INVARIANT: the Artech.* SDK is plain thread-unsafe managed code. STA apartments
    /// do NOT serialize cross-apartment access for non-COM managed objects, and this
    /// worker historically ran five concurrent STA threads against the same KB model
    /// (SdkWorker bridge, Background STA dispatcher, GxMcp-Lite / GxMcp-Enrich /
    /// GxMcp-Delta index threads, KbWatcherThread) plus MTA threadpool fallbacks in
    /// ListService / SearchService. Every region that touches the SDK (KBObject reads,
    /// DesignModel.Objects enumeration / Get / GetKeys, part loads, GetReferences, ...)
    /// must run inside <c>using (SdkGate.Enter()) { ... }</c>.
    ///
    /// Rules to avoid deadlocks:
    ///  - The gate is a Monitor, so it is REENTRANT on the same thread. Nested
    ///    Enter() calls (e.g. a dispatched command that promotes an enrichment which
    ///    calls UpdateEntry) are safe.
    ///  - NEVER hold the gate across an await / Task.Wait on work that itself needs
    ///    the gate on another thread. Keep acquisition scopes tight around the actual
    ///    SDK calls; do JSON shaping, logging and disk IO outside.
    ///  - The gate intentionally does NOT replace KbService._kbLock (which protects
    ///    the _kb handle lifecycle) nor KbWatcherService's write-gate (which lets the
    ///    watcher skip ticks during write transactions). It generalizes them for
    ///    model-level reads/walks.
    /// </summary>
    internal static class SdkGate
    {
        private static readonly object _gate = new object();

        /// <summary>RAII acquire. Blocks until the SDK is free. Reentrant.</summary>
        public static IDisposable Enter()
        {
            Monitor.Enter(_gate);
            return new Releaser();
        }

        /// <summary>
        /// Bounded acquire for callers that prefer to fail fast (e.g. MTA read paths
        /// that can return a typed IndexNotReady/Busy envelope instead of blocking
        /// behind a long build). Returns null when the gate wasn't acquired.
        /// </summary>
        public static IDisposable TryEnter(int timeoutMs)
        {
            if (!Monitor.TryEnter(_gate, timeoutMs)) return null;
            return new Releaser();
        }

        /// <summary>True when the current thread already holds the gate.</summary>
        public static bool IsHeldByCurrentThread => Monitor.IsEntered(_gate);

        private sealed class Releaser : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Monitor.Exit(_gate);
            }
        }
    }
}
