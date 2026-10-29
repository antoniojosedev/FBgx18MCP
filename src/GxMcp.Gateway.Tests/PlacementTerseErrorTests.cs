using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The terse error projection keeps the first message line and an allowlist of fields. A refused
    // destination, a failed move and a failed atomic placement report their cause in fields, so each must
    // survive it: without them the caller sees only "MoveFailed" and cannot tell which step refused.
    public class PlacementTerseErrorTests
    {
        private static JObject Trim(string json) => McpRouter.TrimErrorEnvelope(JObject.Parse(json), verbose: false);

        [Fact]
        public void TrimErrorEnvelope_KeepsWhereADestinationPathStopped()
        {
            var input = @"{""code"":""DestinationNotFound"",""message"":""'X' is not a Folder or Module under 'Root Module/A'"",
                ""reasonCode"":""DestinationNotFound"",""phase"":""destination-resolution"",
                ""placement"":{""requested"":""Root Module/A/X"",""failedSegment"":""X"",""resolvedPrefix"":""Root Module/A"",""availableChildren"":[""B""]},
                ""persisted"":false,""implicitOperations"":[]}";

            var trimmed = Trim(input);

            foreach (var property in JObject.Parse(input).Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsTheCandidatesOfAnAmbiguousDestination()
        {
            var trimmed = Trim(@"{""code"":""DestinationAmbiguous"",""message"":""'X' matches 2 Folders/Modules"",
                ""placement"":{""candidates"":[{""guid"":""g1"",""path"":""Root Module/A/X""},{""guid"":""g2"",""path"":""Root Module/B/X""}]}}");

            Assert.Equal(2, ((JArray)trimmed["placement"]!["candidates"]!).Count);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsWhyAMoveFailedAndThatTheRollbackWasVerified()
        {
            var input = @"{""code"":""MoveFailed"",""message"":""Could not safely persist the move into 'Root Module/A/B': the SDK refused"",
                ""reasonCode"":""SdkPersistRefused"",""phase"":""transaction"",""strategy"":""EntityManager.UpdateParent"",
                ""expectedParent"":{""guid"":""g1"",""path"":""Root Module/A/B""},""actualParent"":{""guid"":null,""path"":""Root Module""},
                ""saved"":false,""persisted"":false,""verified"":false,
                ""changedParts"":[""Rules""],""propertyDifferences"":[{""path"":""Properties/Property[2]/Value[1]""}],
                ""rollback"":{""attempted"":true,""verified"":true,""freshRead"":true},""implicitOperations"":[]}";

            var trimmed = Trim(input);

            foreach (var property in JObject.Parse(input).Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsEveryBlockerOfARefusedMove()
        {
            var trimmed = Trim(@"{""code"":""BaseVersionRequired"",""message"":""A move needs baseVersion"",
                ""reasonCode"":""BaseVersionRequired"",""phase"":""preflight"",""currentVersion"":""639:abc"",
                ""blockers"":[{""code"":""BaseVersionRequired""},{""code"":""DestinationNameConflict""}],""persisted"":false}");

            Assert.Equal(2, ((JArray)trimmed["blockers"]!).Count);
            Assert.Equal("639:abc", (string)trimmed["currentVersion"]!);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsTheCompensationOfAFailedAtomicPlacement()
        {
            var input = @"{""code"":""AtomicCreatePlacementFailed"",""message"":""The SDK left the new object in Root Module"",
                ""reasonCode"":""PlacementSdkRefused"",""phase"":""placement"",
                ""placement"":{""requested"":""Root Module/A/B"",""attempt"":{""afterCreate"":{""readable"":true}}},
                ""compensation"":{""action"":""delete_created_object"",""rolledBack"":true,""absentVerified"":true},
                ""rolledBack"":true,""rollbackVerified"":true,""persisted"":false,""implicitOperations"":[]}";

            var trimmed = Trim(input);

            foreach (var property in JObject.Parse(input).Properties())
                Assert.True(JToken.DeepEquals(property.Value, trimmed[property.Name]), property.Name);
        }

        [Fact]
        public void TrimErrorEnvelope_FindsTheFieldsInsideTheCanonicalErrorObjectToo()
        {
            // The Worker puts diagnostics under error{} (errorExtra); the projection must read them there.
            var trimmed = Trim(@"{""status"":""error"",""target"":""P"",""error"":{""code"":""DestinationReadOnly"",""message"":""inside an imported module"",
                ""reasonCode"":""DestinationReadOnly"",""phase"":""preflight"",""placement"":{""requested"":""Root Module/GeneXus/Common""},
                ""persisted"":false,""implicitOperations"":[]}}");

            Assert.Equal("DestinationReadOnly", (string)trimmed["code"]!);
            Assert.Equal("preflight", (string)trimmed["phase"]!);
            Assert.Equal("Root Module/GeneXus/Common", (string)trimmed["placement"]!["requested"]!);
            Assert.Empty((JArray)trimmed["implicitOperations"]!);
        }
    }
}
