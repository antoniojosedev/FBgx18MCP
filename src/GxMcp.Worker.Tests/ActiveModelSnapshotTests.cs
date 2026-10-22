using System.Reflection;
using System.Runtime.Serialization;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // gx-engine reads storage directly and silently served the trunk Design model on a KB whose SDK
    // session had a branch active. The Worker now reports the active version and its Design model.
    public sealed class ActiveModelSnapshotTests
    {
        private static CommandDispatcher Dispatcher()
            => (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));

        private static string HandleKbVersion(CommandDispatcher dispatcher, string action, JObject args)
        {
            MethodInfo handler = typeof(CommandDispatcher).GetMethod(
                "Handle_KbVersion", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)handler.Invoke(dispatcher, new object[] { new JObject(), "kbversion", action, null, null, args });
        }

        [Fact]
        public void AnUnknownSnapshotIsAnErrorNotADefault()
        {
            JObject response = JObject.Parse(ActiveModelSnapshot.Describe(null));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal(ActiveModelSnapshot.UnknownCode, response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void AKnownSnapshotReportsTheVersionAndItsDesignModel()
        {
            var snapshot = new ActiveModelSnapshot(@"C:\KBs\KBTesteClone", 2, "Branch", false, false, 4);

            JObject response = JObject.Parse(ActiveModelSnapshot.Describe(snapshot));

            Assert.Equal("ok", response["status"]?.ToString());
            Assert.Equal(ActiveModelSnapshot.KnownCode, response["code"]?.ToString());
            JObject result = (JObject)response["result"];
            Assert.Equal(@"C:\KBs\KBTesteClone", result["kbPath"]?.ToString());
            Assert.Equal(JTokenType.Integer, result["designModelId"]?.Type);
            Assert.Equal(4, result["designModelId"]?.ToObject<int>());
            Assert.Equal(2, result["kbVersionId"]?.ToObject<int>());
            Assert.Equal("Branch", result["kbVersionName"]?.ToString());
            Assert.False(result["isTrunk"]?.ToObject<bool>());
        }

        [Fact]
        public void TheDispatcherAnswersFromTheSnapshotWithoutTheSdk()
        {
            // The uninitialized dispatcher has no KbVersionService: reaching it would throw.
            var dispatcher = Dispatcher();
            try
            {
                ActiveModelSnapshot.Publish(new ActiveModelSnapshot(@"C:\KBs\K", 0, "K", true, false, 1));
                JObject known = JObject.Parse(HandleKbVersion(dispatcher, "ActiveModel", new JObject()));
                Assert.Equal(1, known["result"]?["designModelId"]?.ToObject<int>());

                ActiveModelSnapshot.Clear();
                JObject unknown = JObject.Parse(HandleKbVersion(dispatcher, "activemodel", new JObject()));
                Assert.Equal(ActiveModelSnapshot.UnknownCode, unknown["error"]?["code"]?.ToString());
            }
            finally
            {
                ActiveModelSnapshot.Clear();
            }
        }

        [Fact]
        public void ASuccessfulRecaptureKeepsTheOldSnapshotVisibleUntilTheNewOneReplacesIt()
        {
            var old = new ActiveModelSnapshot(@"C:\KBs\K", 0, "Trunk", true, false, 1);
            var replacement = new ActiveModelSnapshot(@"C:\KBs\K", 2, "Branch", false, false, 4);
            ActiveModelSnapshot seenWhileObserving = null;
            try
            {
                ActiveModelSnapshot.Publish(old);

                ActiveModelSnapshot.Refresh(() =>
                {
                    seenWhileObserving = ActiveModelSnapshot.Current;
                    return replacement;
                });

                Assert.Same(old, seenWhileObserving);
                Assert.Same(replacement, ActiveModelSnapshot.Current);
            }
            finally
            {
                ActiveModelSnapshot.Clear();
            }
        }

        [Fact]
        public void AFailedOrEmptyRecaptureClearsTheOldSnapshot()
        {
            var old = new ActiveModelSnapshot(@"C:\KBs\K", 0, "Trunk", true, false, 1);
            try
            {
                ActiveModelSnapshot.Publish(old);
                ActiveModelSnapshot.Refresh(() => null);
                Assert.Null(ActiveModelSnapshot.Current);

                ActiveModelSnapshot.Publish(old);
                ActiveModelSnapshot.Refresh(() => throw new System.InvalidOperationException("sdk failed"));
                Assert.Null(ActiveModelSnapshot.Current);

                ActiveModelSnapshot.Publish(old);
                ActiveModelSnapshot.Capture(null);
                Assert.Null(ActiveModelSnapshot.Current);
            }
            finally
            {
                ActiveModelSnapshot.Clear();
            }
        }

        [Fact]
        public void OnlyTheSnapshotReadLeavesTheSdkThread()
        {
            var dispatcher = Dispatcher();

            Assert.True(dispatcher.IsThreadSafe(@"{""method"":""kbversion"",""action"":""ActiveModel""}"));
            Assert.False(dispatcher.IsThreadSafe(@"{""method"":""kbversion"",""action"":""Run""}"));
            Assert.True(WriteDestinationGuard.IsObservation("kbversion", "ActiveModel", new JObject()));
            Assert.False(WriteDestinationGuard.IsObservation("kbversion", "Run", new JObject { ["action"] = "set_active" }));
        }
    }
}
