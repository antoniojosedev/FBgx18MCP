using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        // Proactively kick off the KB search index on first MCP initialize so the
        // first `genexus_query` doesn't pay the full cold-start cost. Worker side
        // short-circuits to "AlreadyIndexed" if cache is warm, so this is cheap on
        // warm starts. When a real cold-start kicks in, an upfront
        // notifications/message tells the agent that search/analyze return partial
        // results while indexing runs in the background — read/edit/build are
        // immediate regardless.
        private static void TriggerIndexBootstrapOnce()
        {
            if (Interlocked.CompareExchange(ref _indexBootstrapStarted, 1, 0) != 0) return;

            Log("[IndexBootstrap] firing on initialize");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null) { Log("[IndexBootstrap] worker pool null"); return; }

                    var indexCommand = new JObject
                    {
                        ["module"] = "KB",
                        ["action"] = "BulkIndex",
                        ["client"] = "mcp"
                    };

                    var resp = await SendWorkerCommandAsync(
                        indexCommand,
                        30000,
                        "Index bootstrap timeout",
                        wr => wr,
                        (_, correlationId) => new JObject(),
                        toolName: "gateway_index_bootstrap",
                        trackOperation: false);

                    // BulkIndex now returns the canonical envelope ({status:"ok", code, result}).
                    // The fresh-vs-warm signal lives in `code`; fall back to the legacy top-level
                    // `status` for any pre-canonical worker still in the pool.
                    var result = resp?["result"] as JObject;
                    string? status = result?["code"]?.ToString()
                        ?? result?["status"]?.ToString();
                    Log($"[IndexBootstrap] worker reply code={status ?? "<null>"}");

                    // The default lite-index path returns "LiteStarted"; the legacy full path
                    // returns "Started". Either means a fresh cold-start index just kicked off,
                    // so the agent should see the one-time background-indexing notice.
                    // ("AlreadyIndexed" / "AlreadyInProgress" / "DeltaStarted" are warm starts — no notice.)
                    if (string.Equals(status, "Started", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(status, "LiteStarted", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("[IndexBootstrap] emitting cold-start notice");
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "indexing",
                            data = "First-time indexing of this KB has started in the background. "
                                + "Search and analyze tools will return partial results while it runs; "
                                + "read, edit, build, and list tools are immediate and unaffected. "
                                + "Watch notifications/progress for live progress."
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log($"[IndexBootstrap] {ex.Message}");
                }
            });
        }

        private static void TriggerWorkerWarmupOnce()
        {
            if (Interlocked.CompareExchange(ref _workerWarmupStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_workerPool == null)
                    {
                        Log("[Warmup] WorkerPool not available, skipping warmup.");
                        return;
                    }

                    Log("[Warmup] Starting worker warmup sequence...");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup started.",
                        timestamp = DateTime.UtcNow
                    });

                    var listCommand = new JObject
                    {
                        ["module"] = "List",
                        ["action"] = "Objects",
                        ["target"] = string.Empty,
                        ["limit"] = 1,
                        ["offset"] = 0,
                        ["client"] = "mcp"
                    };

                    var listResponse = await SendWorkerCommandAsync(
                        listCommand,
                        30000,
                        "Warmup list timeout",
                        workerResponse => workerResponse,
                        (_, correlationId) => new JObject
                        {
                            ["error"] = new JObject
                            {
                                ["message"] = "Warmup list operation timed out.",
                                ["correlationId"] = correlationId
                            }
                        },
                        toolName: "gateway_warmup_list",
                        trackOperation: false);

                    var result = listResponse?["result"];
                    JArray? items = null;
                    if (result is JObject obj)
                    {
                        items = (obj["results"] ?? obj["objects"]) as JArray;
                    }
                    else if (result is JArray arr)
                    {
                        items = arr;
                    }

                    string? objectName = items?.FirstOrDefault()?["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(objectName))
                    {
                        var readCommand = new JObject
                        {
                            ["module"] = "Read",
                            ["action"] = "ExtractSource",
                            ["target"] = objectName,
                            ["part"] = "Source",
                            ["offset"] = 0,
                            ["limit"] = 1,
                            ["client"] = "mcp"
                        };

                        await SendWorkerCommandAsync(
                            readCommand,
                            30000,
                            "Warmup read timeout",
                            workerResponse => workerResponse,
                            (_, correlationId) => new JObject
                            {
                                ["error"] = new JObject
                                {
                                    ["message"] = "Warmup read operation timed out.",
                                    ["correlationId"] = correlationId
                                }
                            },
                            toolName: "gateway_warmup_read",
                            trackOperation: false);
                    }

                    Log("[Warmup] Worker warmup finished.");
                    BroadcastNotification("notifications/message", new
                    {
                        level = "info",
                        logger = "warmup",
                        data = "Worker warmup finished.",
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    Log("[Warmup] Worker warmup failed: " + ex.Message);
                    BroadcastNotification("notifications/message", new
                    {
                        level = "warning",
                        logger = "warmup",
                        data = "Worker warmup failed: " + ex.Message,
                        timestamp = DateTime.UtcNow
                    });
                }
            });
        }
    }
}
