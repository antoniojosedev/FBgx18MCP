using System.Threading;
using Artech.Architecture.Common.Services;
using SdkServices = Artech.Architecture.Common.Services.Services;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// issue #32 item 2 (generalized): GeneXus SDK services register lazily in the
    /// GxServiceManager, and after a worker respawn (e.g. the developer touched the GeneXus
    /// IDE and the process restarted) a service can briefly lag behind — a single
    /// <c>TryGetService&lt;T&gt;()</c> then returns null and the tool hard-fails, telling the
    /// user to <c>genexus_worker_reload mode=hard</c> by hand.
    ///
    /// This resolves with a bounded retry (self-heals a late registration) plus a forcing
    /// <c>GetService&lt;T&gt;()</c> fallback — <c>TryGetService</c> never triggers a load and
    /// returns null when unregistered, whereas <c>GetService</c> forces resolution/throws.
    /// Returns null only when the service is genuinely unavailable after the retries.
    /// The retry cost is paid ONLY on the failure path (each attempt short-circuits the
    /// moment the service resolves), so success stays first-call fast.
    /// </summary>
    public static class SdkServiceResolver
    {
        public static T Resolve<T>(int attempts = 5, int delayMs = 200) where T : class, IGxService
        {
            for (int i = 0; i < attempts; i++)
            {
                try { var s = SdkServices.TryGetService<T>(); if (s != null) return s; } catch { /* not registered yet */ }
                try { var s = SdkServices.GetService<T>(); if (s != null) return s; } catch { /* forcing variant may throw */ }
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
            return null;
        }
    }
}
