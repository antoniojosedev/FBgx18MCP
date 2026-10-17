using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    [Collection("StderrCapture")]
    public class ReadDiagnosticObservabilityTests : IDisposable
    {
        private static void ResetFaultInjector()
        {
            ReadDiagnosticFaultInjector.InjectedStage = null;
            ReadDiagnosticFaultInjector.InjectedException = null;
            ReadDiagnosticFaultInjector.PersistenceFailure = false;
        }

        public ReadDiagnosticObservabilityTests()
        {
            ResetFaultInjector();
            ReadDiagnosticContext.CurrentCorrelationId = null;
        }

        public void Dispose()
        {
            ResetFaultInjector();
            ReadDiagnosticContext.CurrentCorrelationId = null;
        }

        [Fact]
        public void Sanitizer_RedactsSensitiveDataAndVariableAssignments()
        {
            var rawEx = new InvalidOperationException(
                "Failed connecting with Server=10.0.0.1;User Id=admin;Password=supersecretpass;Token=abc123token; and assigning &UserPassword = \"topsecret\"");

            var sanitized = ReadDiagnosticSanitizer.SanitizeException(rawEx);

            Assert.NotNull(sanitized);
            string message = sanitized["message"]?.ToString();
            Assert.NotNull(message);
            Assert.DoesNotContain("supersecretpass", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abc123token", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("topsecret", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<redacted>", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StateSnapshot_CapsReadFailedObjectsAndKeepsFailedCount()
        {
            int total = SourceStoreBackfillState.MaxReportedReadFailedObjects + 10;
            var state = new SourceStoreBackfillState
            {
                State = "completeWithFailures",
                ReadFailedObjects = Enumerable.Range(1, total).Select(i => Guid.NewGuid().ToString("D")).ToList()
            };

            var snapshot = ReadDiagnosticHelper.BuildSourceStoreSnapshot(state);

            Assert.Equal("completeWithFailures", snapshot["state"]?.ToString());
            Assert.Equal(total, snapshot["failedCount"]?.ToObject<int>());
            Assert.Equal(
                SourceStoreBackfillState.MaxReportedReadFailedObjects,
                ((JArray)snapshot["readFailedObjects"]).Count);
        }

        [Fact]
        public void Sanitizer_EnforcesMessageAndStackTraceAndDepthBounds()
        {
            string longMessage = new string('A', 800);
            Exception current = new InvalidOperationException(longMessage);
            for (int i = 0; i < 8; i++)
            {
                current = new ApplicationException($"Nested level {i}", current);
            }

            var sanitized = ReadDiagnosticSanitizer.SanitizeException(current);

            Assert.NotNull(sanitized);
            Assert.True(sanitized["truncated"]?.Value<bool>());
            var reasons = sanitized["truncationReasons"] as JArray;
            Assert.NotNull(reasons);
            Assert.Contains(reasons, r => r.ToString() == "max_depth_exceeded");

            // Verify inner exception depth is capped
            int depth = 0;
            JObject curToken = sanitized;
            while (curToken?["innerException"] is JObject inner)
            {
                depth++;
                curToken = inner;
            }
            Assert.True(depth <= ReadDiagnosticSanitizer.MaxExceptionDepth);
        }

        [Fact]
        public void Sanitizer_TruncatesStackTraceWhenExceedingLimits()
        {
            string fakeTrace = string.Join(Environment.NewLine, Enumerable.Range(1, 30).Select(i => $"   at Foo.Bar.Step{i}() in C:\\repo\\Foo.cs:line {i}"));
            string sanitizedTrace = ReadDiagnosticSanitizer.SanitizeStackTrace(fakeTrace, out bool truncated);

            Assert.True(truncated);
            Assert.Contains("[TRUNCATED: max stack frames exceeded]", sanitizedTrace);
            var frameLines = sanitizedTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(frameLines.Length <= ReadDiagnosticSanitizer.MaxStackTraceFrames + 1);
        }

        [Fact]
        public void Helper_BuildsEnvelopeWithRequiredFieldsAndRecordsDiagnostics()
        {
            var context = new ReadDiagnosticContext
            {
                CorrelationId = "test-read-cid-479",
                Target = "InvoiceProcedure",
                PartName = "Source",
                Refresh = false,
                CurrentStage = ReadDiagnosticStages.ResolveObject,
                RequestedSelector = new JObject
                {
                    ["target"] = "InvoiceProcedure",
                    ["part"] = "Source"
                }
            };

            var ex = new InvalidOperationException("Failed to locate object metadata in KB index.");
            string envelopeJson = ReadDiagnosticHelper.CreateFailureEnvelope(context, ex, null);

            var envelope = JObject.Parse(envelopeJson);
            Assert.Equal("error", envelope["status"]?.ToString());
            Assert.Equal("InvoiceProcedure", envelope["target"]?.ToString());

            var error = envelope["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("ReadFailure", error["code"]?.ToString());
            Assert.Equal("test-read-cid-479", error["correlationId"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.ResolveObject, error["failedStage"]?.ToString());
            Assert.NotNull(error["timestampUtc"]?.ToString());
            Assert.True(error["durationMs"]?.Value<long>() >= 0);

            var diag = error["diagnostics"] as JObject;
            Assert.NotNull(diag);
            Assert.Equal(ReadDiagnosticStages.ResolveObject, diag["failedStage"]?.ToString());
            Assert.Equal("unknown", diag["context"]?["resolvedIdentity"]?.ToString());
            Assert.False(diag["persisted"]?.Value<bool>());
            Assert.True(diag["enqueued"]?.Value<bool>());

            // Check ring buffer storage
            Assert.True(ReadDiagnosticStore.TryGet("test-read-cid-479", out var stored));
            Assert.NotNull(stored);
            Assert.Equal(ReadDiagnosticStages.ResolveObject, stored["failedStage"]?.ToString());
        }

        [Fact]
        public void Helper_DegradesCleanlyOnPersistenceFailure()
        {
            var context = new ReadDiagnosticContext
            {
                CorrelationId = "test-persist-fail-479",
                Target = "CustomerTxn",
                PartName = "Rules",
                CurrentStage = ReadDiagnosticStages.AccessPart
            };

            ReadDiagnosticFaultInjector.PersistenceFailure = true;
            try
            {
                var ex = new ArgumentException("Part descriptor missing.");
                string envelopeJson = ReadDiagnosticHelper.CreateFailureEnvelope(context, ex, null);

                var envelope = JObject.Parse(envelopeJson);
                var error = envelope["error"] as JObject;
                Assert.NotNull(error);
                Assert.Equal("ReadFailure", error["code"]?.ToString());

                var diag = error["diagnostics"] as JObject;
                Assert.NotNull(diag);
                Assert.False(diag["persisted"]?.Value<bool>());
                Assert.False(diag["enqueued"]?.Value<bool>());
                Assert.NotNull(diag["persistenceError"]?.ToString());
            }
            finally
            {
                ReadDiagnosticFaultInjector.PersistenceFailure = false;
            }
        }

        [Fact]
        public void Helper_EnvelopeNeverLeaksSourceMarkerFromOuterOrInnerException()
        {
            // Issue #549: the synthetic source marker must be absent from every envelope field,
            // including the inner exception chain and the ring-buffer record derived from it.
            var context = new ReadDiagnosticContext
            {
                CorrelationId = "cid-source-leak-549",
                Target = "Synthetic549",
                PartName = "Events",
                CurrentStage = ReadDiagnosticStages.ExtractContent
            };
            var inner = new ApplicationException("Sub 'Validate'\nmsg('INNER_SOURCE_MARKER_549')\nEndSub");
            var outer = new InvalidOperationException("Event Start\nmsg('OUTER_SOURCE_MARKER_549')\nEndEvent", inner);

            string envelopeJson = ReadDiagnosticHelper.CreateFailureEnvelope(context, outer, null);

            Assert.DoesNotContain("OUTER_SOURCE_MARKER_549", envelopeJson);
            Assert.DoesNotContain("INNER_SOURCE_MARKER_549", envelopeJson);

            var envelope = JObject.Parse(envelopeJson);
            var error = envelope["error"] as JObject;
            Assert.NotNull(error);
            Assert.Contains("Failed to read object 'Synthetic549'", error["message"]?.ToString());
            Assert.Equal("cid-source-leak-549", error["correlationId"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.ExtractContent, error["failedStage"]?.ToString());

            var exception = error["diagnostics"]?["exception"] as JObject;
            Assert.NotNull(exception);
            Assert.Equal("System.InvalidOperationException", exception["type"]?.ToString());
            Assert.Contains("[source code omitted]", exception["message"]?.ToString());
            Assert.Equal("System.ApplicationException", exception["innerException"]?["type"]?.ToString());
            Assert.Contains("[source code omitted]", exception["innerException"]?["message"]?.ToString());

            Assert.True(ReadDiagnosticStore.TryGet("cid-source-leak-549", out var stored));
            Assert.DoesNotContain("OUTER_SOURCE_MARKER_549", stored.ToString());
            Assert.DoesNotContain("INNER_SOURCE_MARKER_549", stored.ToString());
        }

        [Fact]
        public void Helper_StderrSummaryOmitsSourceMarker()
        {
            // Logger mirrors every line to Console.Error synchronously before enqueueing the
            // same string for the file writer, so the stderr capture covers both channels.
            var context = new ReadDiagnosticContext
            {
                CorrelationId = "cid-stderr-549",
                Target = "Synthetic549",
                PartName = "Events",
                CurrentStage = ReadDiagnosticStages.ExtractContent
            };
            var ex = new InvalidOperationException("Event Start\nmsg('STDERR_SOURCE_MARKER_549')\nEndEvent");

            var originalErr = Console.Error;
            var capture = new StringWriter();
            Console.SetError(capture);
            try
            {
                ReadDiagnosticHelper.CreateFailureEnvelope(context, ex, null);
            }
            finally
            {
                Console.SetError(originalErr);
            }

            string stderr = capture.ToString();
            Assert.Contains("[READ-DIAGNOSTIC] [cid:cid-stderr-549]", stderr);
            Assert.DoesNotContain("STDERR_SOURCE_MARKER_549", stderr);
        }

        [Fact]
        public void ObjectService_CapturesResolveObjectStageFailure()
        {
            ReadDiagnosticFaultInjector.InjectedStage = ReadDiagnosticStages.ResolveObject;
            ReadDiagnosticFaultInjector.InjectedException = new InvalidOperationException("Simulated index corruption during object lookup.");

            var service = new ObjectService(null, null);
            string response = service.ReadObject("Customer", correlationId: "cid-resolve-test");

            var json = JObject.Parse(response);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("ReadFailure", json["error"]?["code"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.ResolveObject, json["error"]?["failedStage"]?.ToString());
            Assert.Equal("cid-resolve-test", json["error"]?["correlationId"]?.ToString());
            Assert.Equal("unknown", json["error"]?["diagnostics"]?["context"]?["resolvedIdentity"]?.ToString());
        }

        [Fact]
        public void ObjectService_CapturesRefreshObjectStageFailure()
        {
            ReadDiagnosticFaultInjector.InjectedStage = ReadDiagnosticStages.RefreshObject;
            ReadDiagnosticFaultInjector.InjectedException = new TimeoutException("Simulated fresh sync lock timeout.");

            var service = new ObjectService(null, null);
            string response = service.ReadObjectSource("Customer", "Source", refresh: true, correlationId: "cid-refresh-test");

            var json = JObject.Parse(response);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("ReadFailure", json["error"]?["code"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.RefreshObject, json["error"]?["failedStage"]?.ToString());
            Assert.Equal("cid-refresh-test", json["error"]?["correlationId"]?.ToString());
        }

        [Fact]
        public void FailureEnvelope_PreservesAccessPartStage()
        {
            // A null KbService cannot resolve an object and never reaches AccessPart.
            // Verify envelope metadata here; the SDK-backed read path needs a live KB.
            var context = new ReadDiagnosticContext
            {
                CurrentStage = ReadDiagnosticStages.AccessPart,
                Target = "Customer",
                CorrelationId = "cid-access-test"
            };
            string response = ReadDiagnosticHelper.CreateFailureEnvelope(
                context, new InvalidOperationException("Part accessor crashed."), null);

            var json = JObject.Parse(response);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("ReadFailure", json["error"]?["code"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.AccessPart, json["error"]?["failedStage"]?.ToString());
            Assert.Equal("cid-access-test", json["error"]?["correlationId"]?.ToString());
        }

        [Fact]
        public void McpResponseNormalizer_PreservesCorrelationIdAndFailedStage()
        {
            var rawError = new JObject
            {
                ["status"] = "Error",
                ["code"] = "ReadFailure",
                ["message"] = "Failed to read part Source",
                ["correlationId"] = "cid-normalizer-test",
                ["failedStage"] = ReadDiagnosticStages.ExtractContent,
                ["errorExtra"] = new JObject
                {
                    ["correlationId"] = "cid-normalizer-test",
                    ["failedStage"] = ReadDiagnosticStages.ExtractContent
                }
            };

            string normalizedJson = McpResponseNormalizer.Normalize(rawError.ToString());
            var normalized = JObject.Parse(normalizedJson);

            Assert.Equal("error", normalized["status"]?.ToString());
            var error = normalized["error"] as JObject;
            Assert.NotNull(error);
            Assert.Equal("ReadFailure", error["code"]?.ToString());
            Assert.Equal("cid-normalizer-test", error["correlationId"]?.ToString());
            Assert.Equal(ReadDiagnosticStages.ExtractContent, error["failedStage"]?.ToString());
        }

        [Fact]
        public void AllReadDiagnosticStages_AreDistinctAndNonEmpty()
        {
            var stages = new[]
            {
                ReadDiagnosticStages.ResolveObject,
                ReadDiagnosticStages.RefreshObject,
                ReadDiagnosticStages.AccessPart,
                ReadDiagnosticStages.QueryVariables,
                ReadDiagnosticStages.ExtractContent
            };

            Assert.Equal(5, stages.Distinct(StringComparer.Ordinal).Count());
            Assert.All(stages, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        }
    }
}
