using System;
using System.Collections.Generic;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // The failure branches of the form-layout verification and native writes: a malformed
    // projection must be reported as not confirmed, and an SDK command that rejects a typed
    // value must fall back to the text form instead of failing the whole operation.
    public class WwpFormLayoutFailureTests
    {
        [Fact]
        public void VerifyAddedTableProjection_ReportsMalformedWebFormXmlAsNotConfirmed()
        {
            JObject result = WwpActionService.VerifyAddedTableProjection("<form><table", new JObject { ["tableName"] = "tableOp" });

            Assert.False((bool)result["confirmed"]);
            Assert.Equal("The projected WebForm is not valid XML.", (string)result["message"]);
        }

        [Fact]
        public void VerifyMovedControlProjection_ReportsMalformedWebFormXmlAsNotConfirmed()
        {
            JObject result = WwpActionService.VerifyMovedControlProjection("<form><textblock", new JObject());

            Assert.False((bool)result["confirmed"]);
            Assert.Equal("The projected WebForm is not valid XML.", (string)result["message"]);
        }

        [Fact]
        public void SetNativeNumberPair_FallsBackToText_WhenTheSdkCommandRejectsTheTypedValue()
        {
            var element = new TextOnlyElement();

            typeof(WwpActionService)
                .GetMethod("SetNativeNumberPair", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { element, "numberOfColumns", 3 });

            Assert.Equal("3", element.Attributes.GetPropertyValueString("numberOfColumns"));
            Assert.Equal("3", element.Attributes.GetPropertyValueString("defaultNumberOfColumns"));
            Assert.Equal(2, element.Attributes.RejectedTypedWrites);
        }

        private sealed class TextOnlyElement
        {
            public string Type => "table";
            public TextOnlyAttributes Attributes { get; } = new TextOnlyAttributes();
        }

        private sealed class TextOnlyAttributes
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public int RejectedTypedWrites { get; private set; }

            public object GetPropertyValue(string name) => GetPropertyValueString(name);

            public string GetPropertyValueString(string name) => _values.TryGetValue(name, out string value) ? value : null;

            public void SetPropertyValue(string name, object value)
            {
                if (!(value is string text))
                {
                    RejectedTypedWrites++;
                    throw new InvalidOperationException("The converter accepts text only.");
                }

                _values[name] = text;
            }
        }
    }
}
