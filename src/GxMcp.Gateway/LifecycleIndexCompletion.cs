using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    internal static class LifecycleIndexCompletion
    {
        internal static bool IsActive(JObject payload)
        {
            string? state = payload["operationState"]?.ToString();
            return payload["isIndexing"]?.ToObject<bool?>() == true
                || (!string.IsNullOrEmpty(state)
                    && !string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase));
        }

        internal static async Task CompleteJobAsync(
            BackgroundJobRegistry registry,
            JobEntry job,
            JObject acknowledgement,
            Func<CancellationToken, Task<JObject?>> readStatus,
            TimeSpan hardCap)
        {
            CancellationTokenSource observer;
            lock (job.SyncRoot)
            {
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return;
                observer = CancellationTokenSource.CreateLinkedTokenSource(registry.RegisterCancellation(job.Id));
            }
            using (observer)
            {
                observer.CancelAfter(hardCap);
                try
                {
                    JObject result = await WaitAsync(acknowledgement, readStatus, observer.Token).ConfigureAwait(false);
                    bool success = Program.IsSuccessfulBackgroundToolCompletion(result);
                    registry.Complete(job.Id, success,
                        success ? "Index completed" : "Index failed; completion is unverified.", result);
                }
                catch (OperationCanceledException)
                {
                    registry.Complete(job.Id, false,
                        "Index observation cancelled or timed out; underlying operation completion is unverified. Reconcile before retrying.");
                }
                catch (Exception ex)
                {
                    registry.Complete(job.Id, false, $"Index completion is unverified: {ex.Message}");
                }
            }
        }

        internal static async Task<JObject> WaitAsync(
            JObject acknowledgement,
            Func<CancellationToken, Task<JObject?>> readStatus,
            CancellationToken cancellationToken)
        {
            if (acknowledgement["error"] != null
                || string.Equals(acknowledgement["status"]?.ToString(), "Error", StringComparison.OrdinalIgnoreCase)
                || (acknowledgement["result"] as JObject)?["error"] != null)
                return acknowledgement;

            string? operationId = (acknowledgement["result"] as JObject)?["operationId"]?.ToString()
                ?? acknowledgement["operationId"]?.ToString();
            if (string.IsNullOrWhiteSpace(operationId))
                throw new InvalidOperationException("Index acknowledgement lacks operation identity; completion is unverified. Reconcile before retrying.");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JObject? status = await readStatus(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (status == null || status["error"] != null)
                    throw new InvalidOperationException("Index status unavailable; index completion is unverified. Reconcile before retrying.");
                if (!string.Equals(operationId, status["operationId"]?.ToString(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Index operation identity changed; original completion is unverified. Reconcile before retrying.");

                string? state = status["operationState"]?.ToString();
                string? indexStatus = status["indexStatus"]?.ToString();
                if (string.Equals(state, "Stalled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "WorkerExited", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "Recovering", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(indexStatus, "Failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(indexStatus, "Error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Index operation did not complete successfully (operationState={state}, indexStatus={indexStatus}).");
                if (string.IsNullOrEmpty(state) || status["isIndexing"]?.Type != JTokenType.Boolean)
                    throw new InvalidOperationException("Index status lacks operation activity evidence; completion is unverified.");

                if (string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase)
                    && status["isIndexing"]?.ToObject<bool?>() == false)
                {
                    if (!string.Equals(indexStatus, "Ready", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(status["freshness"]?.ToString(), "current", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Index stopped without Ready/current evidence; completion is unverified.");
                    return new JObject { ["status"] = "ok", ["code"] = "IndexCompleted", ["result"] = status };
                }

                // Worker status uses an event-driven bounded wait. Also bound older
                // Workers that return Ready/current immediately while still Building.
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
