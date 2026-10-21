using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly GxwireTool Gxwire = new GxwireTool(
            Log,
            ResolveKbPath,
            () => _currentKb.Value?.Path,
            () => _activeConfig?.Environment?.KBPath,
            BuildDiagnosticContext);

        internal static Task<JObject> ExecuteGxwireToolAsync(JObject? tArgs, string sessionId, bool sessionContextEnabled) =>
            Gxwire.ExecuteGxwireToolAsync(tArgs, sessionId, sessionContextEnabled);

        internal static Task<JObject> ExecuteGxwireToolAsyncForTest(JObject? tArgs) =>
            Gxwire.ExecuteGxwireToolAsync(tArgs, "test-session", false, GxwireTool.GxwireToolSeams.Default);

        internal static Task<JObject> ExecuteGxwireToolAsyncForTest(JObject? tArgs, GxwireTool.GxwireToolSeams seams) =>
            Gxwire.ExecuteGxwireToolAsync(tArgs, "test-session", false, seams);
    }
}
