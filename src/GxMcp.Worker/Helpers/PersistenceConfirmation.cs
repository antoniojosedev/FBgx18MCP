using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// What a successful object save tells the caller about durability (issue #692).
    /// A save that returned and a read-back through the in-memory instance that was just
    /// mutated prove only that the SDK accepted the change; they cannot show what the
    /// store holds. The only claim worth making is "a cache-invalidated, identity-based
    /// reload found the change", and anything less is reported as unconfirmed rather than
    /// as saved-and-verified.
    /// </summary>
    public static class PersistenceConfirmation
    {
        public const string FreshReload = "fresh-reload";

        /// <summary>
        /// Adds <c>saved</c>, <c>persistedVerified</c> and <c>verification</c> to
        /// <paramref name="result"/>. <c>persistedVerified</c> is <c>true</c> only when the
        /// change was read back from a freshly reloaded object; otherwise it is <c>null</c>
        /// (never <c>false</c>: a reload that could not run says nothing about the store) and
        /// <c>verification</c> says why.
        /// </summary>
        public static JObject Annotate(JObject result, bool freshReloadConfirmed, string unavailableReason)
        {
            if (result == null) return null;
            result["saved"] = true;
            if (freshReloadConfirmed)
            {
                result["persistedVerified"] = true;
                result["verification"] = FreshReload;
            }
            else
            {
                result["persistedVerified"] = JValue.CreateNull();
                result["verification"] = "unavailable: " + (string.IsNullOrWhiteSpace(unavailableReason)
                    ? "no fresh SDK instance was returned after Save"
                    : unavailableReason);
            }
            return result;
        }
    }
}
