using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>Why an independent re-read of an object by identity was or was not obtained.</summary>
    internal enum FreshReloadOutcome
    {
        /// <summary>The lookup returned a different instance of the same object.</summary>
        Independent,
        /// <summary>The SDK object cache offers no invalidation, so nothing was read.</summary>
        InvalidationUnavailable,
        /// <summary>The identity lookup found nothing after the caches were dropped.</summary>
        NotResolved,
        /// <summary>The identity lookup returned another object.</summary>
        DifferentObject,
        /// <summary>
        /// The SDK handed back the very instance being verified. Some object types
        /// (UserControl, Image, ThemeClass, DesignSystem) stay resident in the model and
        /// keep their instance through <c>Invalidate</c>; reading it back proves nothing about the store.
        /// </summary>
        SameInstanceRetained
    }

    /// <summary>
    /// The sequence behind "give me an independent copy of this persisted object": invalidate, release
    /// the retained instance, look the identity up again and refuse anything that is not a different
    /// instance of the same object. Kept free of SDK types so every outcome can be exercised without
    /// a Knowledge Base; <c>ObjectService.FindObjectFreshByIdentity</c> supplies the SDK delegates.
    /// </summary>
    internal static class FreshObjectReload
    {
        internal const string DiagnosticCode = "FreshReadUnavailable";

        /// <param name="expected">The instance whose persisted state must be read back.</param>
        /// <param name="guidOf">Reads the object GUID from an instance.</param>
        /// <param name="invalidate">Invalidates the SDK cache entry; false when the SDK cannot.</param>
        /// <param name="release">
        /// Drops what still holds <paramref name="expected"/> (managed read caches and the SDK's retained
        /// instance). Best effort: a failure here leaves the guard below to refuse a non-independent result.
        /// </param>
        /// <param name="resolve">Looks the object up again by identity.</param>
        internal static T Reload<T>(
            T expected,
            Func<T, Guid> guidOf,
            Func<T, bool> invalidate,
            Action<T> release,
            Func<T> resolve,
            out FreshReloadOutcome outcome) where T : class
        {
            if (expected == null)
            {
                outcome = FreshReloadOutcome.NotResolved;
                return null;
            }

            if (!invalidate(expected))
            {
                outcome = FreshReloadOutcome.InvalidationUnavailable;
                return null;
            }

            // Invalidate alone leaves a resident type's instance in place. Releasing it is what makes
            // the lookup below build a new one, so it must happen between the two.
            try { release(expected); }
            catch { /* the checks below refuse a result that is still not independent */ }

            T fresh = resolve();
            if (fresh == null)
            {
                outcome = FreshReloadOutcome.NotResolved;
                return null;
            }
            if (guidOf(fresh) != guidOf(expected))
            {
                outcome = FreshReloadOutcome.DifferentObject;
                return null;
            }
            if (ReferenceEquals(fresh, expected))
            {
                outcome = FreshReloadOutcome.SameInstanceRetained;
                return null;
            }

            outcome = FreshReloadOutcome.Independent;
            return fresh;
        }

        /// <summary>The diagnostic left for callers that turn a null reload into an error.</summary>
        internal static JObject Describe(FreshReloadOutcome outcome, string typeName)
        {
            string type = string.IsNullOrWhiteSpace(typeName) ? "object" : typeName;
            string reason;
            string message;
            string hint;
            switch (outcome)
            {
                case FreshReloadOutcome.InvalidationUnavailable:
                    reason = "InvalidationUnavailable";
                    message = "The SDK object cache could not be invalidated; a verification read was not attempted.";
                    hint = "Retry after the Worker has exclusive access to the KB, or use a separate Worker for independent verification.";
                    break;
                case FreshReloadOutcome.NotResolved:
                    reason = "IdentityNotResolved";
                    message = "The native identity resolver found no " + type + " after the SDK cache was invalidated.";
                    hint = "Confirm the object still exists (genexus_query) before deciding whether to retry.";
                    break;
                case FreshReloadOutcome.DifferentObject:
                    reason = "IdentityMismatch";
                    message = "The native identity resolver returned a different object than the " + type + " being verified.";
                    hint = "Re-read the object by name and compare identities before deciding whether to retry.";
                    break;
                case FreshReloadOutcome.SameInstanceRetained:
                    reason = "SameInstanceRetained";
                    message = "The SDK kept returning the same in-memory " + type
                        + " instance after its cache entry was invalidated and evicted, so an independent read of the persisted object is not available.";
                    hint = "This object type stays resident in the SDK model; the persisted state cannot be verified through this route.";
                    break;
                default:
                    reason = "Independent";
                    message = "The object was read back from an independent instance.";
                    hint = null;
                    break;
            }

            var diagnostic = new JObject
            {
                ["code"] = outcome == FreshReloadOutcome.Independent ? "Independent" : DiagnosticCode,
                ["reason"] = reason,
                ["message"] = message
            };
            if (hint != null) diagnostic["hint"] = hint;
            return diagnostic;
        }
    }
}
