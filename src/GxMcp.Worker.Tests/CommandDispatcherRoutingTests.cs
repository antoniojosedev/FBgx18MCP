using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    public sealed class CommandDispatcherRoutingTests
    {
        private static JObject observedArgs;

        [Theory]
        [InlineData("SetAttributeProperties", false)]
        [InlineData("SetLevelProperties", false)]
        [InlineData("SetDomainProperties", false)]
        [InlineData("UpdateGroupStructure", false)]
        [InlineData("SetAttributeProperties", true)]
        [InlineData("SetLevelProperties", true)]
        [InlineData("SetDomainProperties", true)]
        [InlineData("UpdateGroupStructure", true)]
        public void UnsupportedStructureDryRunRejectsBeforeAccessingWriteServices(string action, bool nested)
        {
            // No StructureService is installed: entering any writer would fail.
            var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
            var tableField = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
            var table = (IDictionary)Activator.CreateInstance(tableField.FieldType);
            var handlerType = tableField.FieldType.GetGenericArguments()[1];
            table.Add("structure", Delegate.CreateDelegate(handlerType, dispatcher,
                typeof(CommandDispatcher).GetMethod("Handle_Structure", BindingFlags.NonPublic | BindingFlags.Instance)));
            tableField.SetValue(dispatcher, table);
            var args = new JObject { ["dryRun"] = true };
            var request = new JObject
            {
                ["method"] = "structure", ["action"] = action, ["target"] = "TestAttribute",
                ["payload"] = "{\"formula\":\"Value * 2\"}",
                ["params"] = nested ? new JObject { ["params"] = args } : args
            };
            var response = JObject.Parse(dispatcher.Dispatch(request.ToString()));
            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("StructureDryRunUnsupported", response["error"]?["code"]?.ToString());
        }

        [Fact]
        public void Nested_tool_args_do_not_receive_worker_routing_module_action_or_target()
        {
            observedArgs = null;
            var dispatcher = (CommandDispatcher)FormatterServices.GetUninitializedObject(typeof(CommandDispatcher));
            var tableField = typeof(CommandDispatcher).GetField("_commandTable", BindingFlags.NonPublic | BindingFlags.Instance);
            var table = (IDictionary)Activator.CreateInstance(tableField.FieldType);
            var handlerType = tableField.FieldType.GetGenericArguments()[1];
            table.Add("object", Delegate.CreateDelegate(
                handlerType,
                typeof(CommandDispatcherRoutingTests).GetMethod(nameof(Observe), BindingFlags.NonPublic | BindingFlags.Static)));
            tableField.SetValue(dispatcher, table);

            var request = new JObject
            {
                ["method"] = "object",
                ["action"] = "ExportTextBatch",
                ["target"] = "Procedure:Customer",
                ["params"] = new JObject
                {
                    ["module"] = "Object",
                    ["action"] = "ExportTextBatch",
                    ["target"] = "Procedure:Customer",
                    ["params"] = new JObject { ["outputPath"] = "C:\\temp\\text" }
                }
            };

            JObject response = JObject.Parse(dispatcher.Dispatch(request.ToString()));

            Assert.Equal("RoutingProbe", response["code"]?.ToString());
            Assert.NotNull(observedArgs);
            Assert.Null(observedArgs["module"]);
            Assert.Null(observedArgs["action"]);
            Assert.Null(observedArgs["target"]);
            Assert.Equal("C:\\temp\\text", observedArgs["outputPath"]?.ToString());
        }

        private static string Observe(JObject request, string method, string action, string target, string payload, JObject args)
        {
            observedArgs = args;
            return "{\"status\":\"ok\",\"code\":\"RoutingProbe\"}";
        }
    }
}
