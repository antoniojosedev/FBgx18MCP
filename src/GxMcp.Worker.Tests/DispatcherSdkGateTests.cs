using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class DispatcherSdkGateTests
    {
        private bool gateHeld;

        private CommandDispatcher CreateDispatcher(string method)
        {
            var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
            var field = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
            var table = (IDictionary)Activator.CreateInstance(field.FieldType);
            table.Add(method, Delegate.CreateDelegate(field.FieldType.GetGenericArguments()[1], this,
                GetType().GetMethod(nameof(Observe), BindingFlags.NonPublic | BindingFlags.Instance)));
            field.SetValue(dispatcher, table);
            return dispatcher;
        }

        private string Observe(JObject request, string method, string action, string target, string payload, JObject args)
        {
            gateHeld = SdkGate.IsHeldByCurrentThread;
            return "{\"status\":\"ok\",\"code\":\"GateProbe\"}";
        }

        [Theory]
        [InlineData("read", "ExtractSource")]
        [InlineData("read", "ExtractParts")]
        [InlineData("read", "ExtractFullObject")]
        public void SdkDispatchHoldsTheSameGateAsBackgroundPartLoads(string method, string action)
        {
            var dispatcher = CreateDispatcher(method.ToLowerInvariant());
            var response = JObject.Parse(dispatcher.Dispatch(new JObject
            {
                ["method"] = method, ["action"] = action, ["target"] = "Scratch"
            }.ToString()));
            Assert.Equal("GateProbe", response["code"]?.ToString());
            Assert.True(gateHeld, "Foreground SDK dispatch must serialize with background part initialization.");
            Assert.False(SdkGate.IsHeldByCurrentThread);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SourceWriterWaitsForBackgroundSdkGateAndReleasesItOnFailure(bool facade)
        {
            var writer = (WriteService)FormatterServices.GetUninitializedObject(typeof(WriteService));
            using var started = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                started.Set();
                try
                {
                    // No ObjectService is installed: the writer will fail after admission.
                    if (facade) writer.WriteObject("Scratch", new JObject { ["content"] = "// test" });
                    else writer.WriteObject("Scratch", "Source", "// test");
                }
                catch (NullReferenceException) { /* Deliberately uninitialized writer. */ }
                finally { finished.Set(); }
            }) { IsBackground = true };
            bool ranInsideBackgroundScope;
            using (SdkGate.Enter())
            {
                thread.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                ranInsideBackgroundScope = finished.Wait(TimeSpan.FromMilliseconds(200));
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.False(ranInsideBackgroundScope, "Writer overlapped the background SDK scope.");
            // A failed write must not leave the process-wide gate owned by its thread.
            using var released = SdkGate.TryEnter(1000);
            Assert.NotNull(released);
        }

        [Theory]
        [InlineData("object", "ReadLogs")]
        [InlineData("build", "Status")]
        [InlineData("preview", "Render")]
        [InlineData("write", "Source")]
        public void NonSourceDispatchDoesNotWaitBehindTheSdkGate(string method, string action)
        {
            var dispatcher = CreateDispatcher(method);
            string response = null;
            var thread = new Thread(() => response = dispatcher.Dispatch(new JObject
            {
                ["method"] = method, ["action"] = action
            }.ToString())) { IsBackground = true };
            bool completed;
            using (SdkGate.Enter())
            {
                thread.Start();
                completed = thread.Join(TimeSpan.FromSeconds(5));
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Diagnostic thread did not finish after gate release.");
            Assert.True(completed, "Non-source orchestration/diagnostics must not wait behind the source gate.");
            Assert.Contains("GateProbe", response);
            Assert.False(gateHeld);
        }
    }
}
