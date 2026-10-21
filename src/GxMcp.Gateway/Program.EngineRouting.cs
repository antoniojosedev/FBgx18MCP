using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static readonly EngineRoutedOperations EngineOperations = new EngineRoutedOperations(
            () => ActiveConfig,
            Log,
            (payload, isError, toolName, toolArgs) => BuildToolResultContent(payload, isError, toolName, toolArgs));

        /// <summary>
        /// Called from the tool dispatch pipeline before the SDK index gate;
        /// see <see cref="EngineRoutedOperations.TryDispatchAsync"/>.
        /// </summary>
        internal static Task<JObject?> TryDispatchEngineRoutedOperationAsync(
            string toolName,
            string? lifecycleAction,
            JObject? toolArgs,
            string? kbPath,
            string? kbAlias,
            CancellationToken cancellationToken) =>
            EngineOperations.TryDispatchAsync(toolName, lifecycleAction, toolArgs, kbPath, kbAlias, cancellationToken);
    }
}
