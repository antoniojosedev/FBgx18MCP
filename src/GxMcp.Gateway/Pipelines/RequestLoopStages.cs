using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway.Pipelines
{
    /// <summary>
    /// The ordered, gateway-owned stages surrounding the legacy dispatch core.
    /// Stages deliberately do not manufacture MCP responses: the core remains
    /// the single source of truth for envelopes while this pipeline makes the
    /// request lifecycle and its ordering explicit.
    /// </summary>
    public static class RequestLoopStages
    {
        public static readonly string[] Names =
        {
            "protocol", "kb-resolution", "args-validation", "idempotency",
            "semantic-cache", "worker-dispatch", "response-shaping"
        };

        public static McpMiddlewarePipeline Create()
        {
            return new McpMiddlewarePipeline()
                .Use(new ProtocolHandshakeMiddleware())
                .Use(new KbResolutionMiddleware())
                .Use(new ArgsValidationMiddleware())
                .Use(new IdempotencyStageMiddleware())
                .Use(new SemanticCacheMiddleware())
                .Use(new WorkerDispatchMiddleware())
                .Use(new ResponseShapingMiddleware());
        }
    }

    public abstract class RequestLoopStageMiddleware : IMcpMiddleware
    {
        protected abstract string StageName { get; }

        public Task<JObject?> InvokeAsync(McpPipelineContext context, McpPipelineNextDelegate next)
        {
            context.Properties["requestLoop.stage." + StageName] = true;
            return next();
        }
    }

    public sealed class ProtocolHandshakeMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "protocol";
    }

    public sealed class KbResolutionMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "kb-resolution";
    }

    public sealed class ArgsValidationMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "args-validation";
    }

    public sealed class IdempotencyStageMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "idempotency";
    }

    public sealed class SemanticCacheMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "semantic-cache";
    }

    public sealed class WorkerDispatchMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "worker-dispatch";
    }

    public sealed class ResponseShapingMiddleware : RequestLoopStageMiddleware
    {
        protected override string StageName => "response-shaping";
    }
}
