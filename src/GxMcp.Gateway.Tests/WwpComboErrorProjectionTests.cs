using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The terse error projection drops everything it does not know. The combo-property refusals and
    // failures carry the receipt the caller corrects the next call with, so it has to survive.
    public class WwpComboErrorProjectionTests
    {
        [Fact]
        public void UnknownComboProperty_KeepsTheKeysTheSdkWouldDiscardAndTheNearestSupportedName()
        {
            var input = JObject.Parse(@"{status:'error',target:'Instance',code:'UnknownComboProperty',
                error:'The WorkWithPlus SDK does not know controlLoadDataDynamically.',
                unknown:[{property:'controlLoadDataDynamically',didYouMean:'LoadDataDynamically',sdkName:'controlLoadDynamically'}],
                supported:[{property:'Parameters',sdkName:'controlInputDPParameters'}],debugPath:'private path'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("UnknownComboProperty", (string?)output["code"]);
            Assert.Equal("controlLoadDynamically", (string?)output["unknown"]?[0]?["sdkName"]);
            Assert.Equal("controlInputDPParameters", (string?)output["supported"]?[0]?["sdkName"]);
            Assert.DoesNotContain("private", output.ToString());
        }

        [Fact]
        public void ComboNotApplicable_KeepsWhatTheControlIsConfiguredAs()
        {
            var input = JObject.Parse(@"{status:'error',code:'ComboPropertyNotApplicable',error:'needs a DVelop Combo',
                property:'LoadDataDynamically',sdkName:'controlLoadDynamically',requires:'controlInfoDef=Custom',
                current:{controlType:'Edit',controlInputDS:'Attributes'},visibleIf:'self::*[@x]'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("Edit", (string?)output["current"]?["controlType"]);
            Assert.Equal("controlInfoDef=Custom", (string?)output["requires"]);
            Assert.Equal("controlLoadDynamically", (string?)output["sdkName"]);
        }

        [Fact]
        public void ComboNameNotFound_KeepsTheValidCandidates()
        {
            var input = JObject.Parse(@"{status:'error',code:'ComboConditionNameNotFound',error:'not on the form',
                unresolved:['Missing'],candidates:['Company','Client']}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("Missing", (string?)output["unresolved"]?[0]);
            Assert.Equal(2, ((JArray)output["candidates"]!).Count);
        }

        [Fact]
        public void AFailedComboWrite_KeepsTheRollbackEvidenceAndWhetherAnythingStayedPersisted()
        {
            var input = JObject.Parse(@"{status:'error',code:'WwpComboNotPersisted',error:'re-read did not hold the value',
                persisted:false,partialPersistence:false,patternReReadConfirmed:false,webFormProjectionConfirmed:false,
                rollbackPerformed:true,rollbackVerified:true,versionTokenBefore:'t0',lifecycleExecuted:false,
                rollback:{attempted:true,patternSha256Expected:'a',patternSha256Restored:'a',verifiedByFreshReread:true,rollbackVerified:true},
                snapshot:{patternInstance:{sha256:'a'}},detail:{confirmed:false},properties:{Parameters:{before:'A',after:'A, B'}}}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.True((bool?)output["rollbackVerified"]);
            Assert.True((bool?)output["rollbackPerformed"]);
            Assert.False((bool?)output["lifecycleExecuted"]);
            Assert.False((bool?)output["patternReReadConfirmed"]);
            Assert.True((bool?)output["rollback"]?["verifiedByFreshReread"]);
            Assert.Equal("a", (string?)output["rollback"]?["patternSha256Restored"]);
            Assert.False((bool?)output["partialPersistence"]);
            Assert.Equal("t0", (string?)output["versionTokenBefore"]);
            Assert.False((bool?)output["detail"]?["confirmed"]);
            Assert.Equal("A, B", (string?)output["properties"]?["Parameters"]?["after"]);
            Assert.Equal("a", (string?)output["snapshot"]?["patternInstance"]?["sha256"]);
        }

        [Fact]
        public void TheComboReceiptIsNotAddedToUnrelatedErrors()
        {
            var input = JObject.Parse(@"{status:'error',code:'SomethingElse',error:'nope',
                properties:{a:1},unknown:[1],supported:[2],current:{x:1},snapshot:{y:2},detail:'z'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            foreach (string key in new[] { "properties", "unknown", "supported", "current", "snapshot", "detail" })
                Assert.Null(output[key]);
        }

        [Fact]
        public void ARefusedVersionToken_ShowsTheTokenThatIsCurrent()
        {
            var input = JObject.Parse(@"{status:'error',code:'StaleObject',error:'changed after the read',
                expectedVersion:'old',currentVersion:'new'}");

            var output = McpRouter.TrimErrorEnvelope(input, verbose: false);

            Assert.Equal("old", (string?)output["expectedVersion"]);
            Assert.Equal("new", (string?)output["currentVersion"]);
        }
    }
}
