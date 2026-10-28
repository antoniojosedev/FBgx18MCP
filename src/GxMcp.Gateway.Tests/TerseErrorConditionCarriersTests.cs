using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Issue #711: the terse projection must keep the per-control values of a refused
    // WebForm write, or the caller sees "would diverge" with nothing to correct.
    public class TerseErrorConditionCarriersTests
    {
        [Fact]
        public void KeepsDivergencesAndTheCanonicalCarrier()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'ControlConditionCarriersDiverge',message:'m',hint:'h'},
                part:'WebForm',persisted:false,savePathExercised:false,dryRun:true,
                canonicalCarrier:'PATTERN_ELEMENT_CUSTOM_PROPERTIES',
                divergences:[{control:'&Sel (var:5)',property:'ControlWhere',PATTERN_ELEMENT_CUSTOM_PROPERTIES:'A = 1;',WebUserControlProperties:'A = 2;'}],
                internalOnly:'dropped'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            var divergence = Assert.IsType<JObject>(Assert.Single((JArray)output["divergences"]!));
            Assert.Equal("ControlWhere", (string?)divergence["property"]);
            Assert.Equal("A = 2;", (string?)divergence["WebUserControlProperties"]);
            Assert.Equal("PATTERN_ELEMENT_CUSTOM_PROPERTIES", (string?)output["canonicalCarrier"]);
            Assert.False((bool)output["savePathExercised"]!);
            Assert.True((bool)output["dryRun"]!);
            Assert.Null(output["internalOnly"]);
        }

        [Fact]
        public void AnotherCodeDoesNotGainTheseFields()
        {
            var input = JObject.Parse(@"{status:'error',error:{code:'WriteFailed',message:'m'},divergences:[{}],canonicalCarrier:'x'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Null(output["divergences"]);
            Assert.Null(output["canonicalCarrier"]);
        }
    }
}
