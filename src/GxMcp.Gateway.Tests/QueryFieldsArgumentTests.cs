using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // `fields` is declared as an array of strings, but a client whose schema lacked it sent the
    // array as text and the comma split produced `["name"` / `"type"]` (every result projected
    // to an empty object, issue #637). Both readers of `fields` accept the text form.
    public class QueryFieldsArgumentTests
    {
        private static JObject Project(JToken fields)
        {
            var payload = new JObject
            {
                ["results"] = new JArray(new JObject
                {
                    ["name"] = "ProcSample",
                    ["type"] = "Procedure",
                    ["guid"] = "abc-123",
                    ["description"] = "Sample procedure"
                })
            };
            var args = new JObject { ["query"] = "type:Procedure", ["fields"] = fields };
            var envelope = Program.BuildToolTextResponse(new JValue("1"), payload, isError: false, toolName: "genexus_query", toolArgs: args);
            return JObject.Parse(envelope["result"]?["content"]?[0]?["text"]?.ToString()!);
        }

        [Theory]
        [InlineData("[\"name\",\"type\"]")]
        [InlineData("  [\"name\", \"type\"]  ")]
        [InlineData("name,type")]
        public void Query_FieldsGivenAsText_ProjectTheNamedFields(string fields)
        {
            var item = (JObject)((JArray)Project(new JValue(fields))["results"]!)[0];

            Assert.Equal("ProcSample", item["name"]?.ToString());
            Assert.Equal("Procedure", item["type"]?.ToString());
            Assert.Null(item["guid"]);
            Assert.Null(item["description"]);
        }

        [Fact]
        public void Query_FieldsGivenAsAnArray_ProjectTheSameFields()
        {
            var item = (JObject)((JArray)Project(new JArray("name", "type"))["results"]!)[0];

            Assert.Equal("ProcSample", item["name"]?.ToString());
            Assert.Equal("Procedure", item["type"]?.ToString());
            Assert.Null(item["guid"]);
        }

        [Fact]
        public void NormalizeFields_ReadsATextArrayLiteralAndLeavesEverythingElseAlone()
        {
            var parsed = Assert.IsType<JArray>(EngineFieldSelection.NormalizeFields(new JValue("[\"name\",\"type\"]")));
            Assert.Equal(new[] { "name", "type" }, parsed.ToObject<string[]>());

            Assert.Equal("name,type", EngineFieldSelection.NormalizeFields(new JValue("name,type"))!.ToString());
            Assert.Equal("[name", EngineFieldSelection.NormalizeFields(new JValue("[name"))!.ToString());
            Assert.Equal("[not json]", EngineFieldSelection.NormalizeFields(new JValue("[not json]"))!.ToString());
            Assert.Null(EngineFieldSelection.NormalizeFields(null));
            var array = new JArray("name");
            Assert.Same(array, EngineFieldSelection.NormalizeFields(array));
        }
    }
}
