using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// The parameter list of a Dynamic Combo Box travels as <c>expressions</c> through
    /// <c>genexus_properties</c>. Dropping it here would turn a typed, SDK-validated write into the
    /// generic scalar one, so the router must carry it to the Worker verbatim and only on a set.
    /// </summary>
    public sealed class PropertiesRouterControlParametersTests
    {
        [Fact]
        public void Set_ForwardsTheOrderedExpressionsWithTheControlAndTheConcurrencyToken()
        {
            var router = new GxMcp.Gateway.Routers.PropertiesRouter();

            var write = JObject.FromObject(router.ConvertToolCall("genexus_properties", new JObject
            {
                ["action"] = "set", ["name"] = "SampleTransaction", ["type"] = "Transaction",
                ["control"] = "att:123", ["propertyName"] = "DataProviderParameters",
                ["expressions"] = new JArray("AttrA", "&Var", "'literal'"),
                ["expectedVersion"] = "v7", ["dryRun"] = true
            })!);

            Assert.Equal("Property", write["module"]?.ToString());
            Assert.Equal("Set", write["action"]?.ToString());
            Assert.Equal("att:123", write["control"]?.ToString());
            Assert.Equal("DataProviderParameters", write["propertyName"]?.ToString());
            Assert.Equal(new[] { "AttrA", "&Var", "'literal'" },
                ((JArray)write["expressions"]!).ToObject<string[]>());
            Assert.Equal("v7", write["expectedVersion"]?.ToString());
            Assert.True(write["dryRun"]?.Value<bool>());
            Assert.True(write["rollbackOnFailure"]?.Value<bool>());
        }

        [Fact]
        public void Set_WithoutExpressions_DoesNotInventAnEmptyList()
        {
            var router = new GxMcp.Gateway.Routers.PropertiesRouter();

            var write = JObject.FromObject(router.ConvertToolCall("genexus_properties", new JObject
            {
                ["action"] = "set", ["name"] = "SamplePanel", ["propertyName"] = "Description", ["value"] = "x"
            })!);

            Assert.True(write["expressions"] == null || write["expressions"]!.Type == JTokenType.Null);
        }

        [Fact]
        public void Set_AnEmptyList_IsForwardedAsAnEmptyList_ItClearsTheParameters()
        {
            var router = new GxMcp.Gateway.Routers.PropertiesRouter();

            var write = JObject.FromObject(router.ConvertToolCall("genexus_properties", new JObject
            {
                ["action"] = "set", ["name"] = "SampleTransaction", ["control"] = "att:1",
                ["propertyName"] = "DataProviderParameters", ["expressions"] = new JArray(),
                ["expectedVersion"] = "v1"
            })!);

            Assert.Equal(JTokenType.Array, write["expressions"]!.Type);
            Assert.Empty((JArray)write["expressions"]!);
        }
    }
}
