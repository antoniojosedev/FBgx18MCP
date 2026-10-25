using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #638: the variableReference of add_grid_variable is the variable's own GUID in the
    // owning object's Variables part, followed by its name. The earlier lookup asked the KB for
    // an object with that GUID, which a variable never is, so no reference a client could read
    // was ever accepted and the one it could not read (the host object's GUID) hit a name mismatch.
    public class WwpGridVariableReferenceTests
    {
        private const string OpLinGuid = "5a0f6a3e-12b4-4a1c-9d7e-0c1b2a3d4e5f";
        private const string OtherGuid = "9b7c1d2e-3f40-4a5b-8c6d-7e8f90a1b2c3";
        private const string HostGuid = "c1d2e3f4-0a1b-4c2d-8e3f-405162738495";

        private static IList<KeyValuePair<string, string>> Declared() => new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Selected", OtherGuid),
            new KeyValuePair<string, string>("OpLin", OpLinGuid)
        };

        [Theory]
        [InlineData("OpLin")]
        [InlineData("&OpLin")]
        [InlineData("oplin")]
        public void DeclaredVariable_WithItsOwnGuidAndName_IsAccepted(string requestedName)
        {
            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                requestedName, OpLinGuid + "-OpLin", Declared(), out string reference);

            Assert.Null(error);
            Assert.Equal(OpLinGuid + "-OpLin", reference);
        }

        [Fact]
        public void MissingReference_IsRefusedButNamesTheIdentityToUse()
        {
            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                "OpLin", null, Declared(), out string reference);

            Assert.Null(reference);
            Assert.Equal("GridVariableIdentityRequired", (string)error["code"]);
            Assert.Equal(OpLinGuid + "-OpLin", (string)error["expectedVariableReference"]);
        }

        [Fact]
        public void TheHostObjectGuid_IsRefusedAndTheVariableGuidIsReported()
        {
            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                "OpLin", HostGuid + "-OpLin", Declared(), out string reference);

            Assert.Null(reference);
            Assert.Equal("GridVariableIdentityMismatch", (string)error["code"]);
            Assert.Equal(OpLinGuid + "-OpLin", (string)error["expectedVariableReference"]);
            Assert.Contains("own GUID", (string)error["error"]);
        }

        [Fact]
        public void AnotherVariablesGuid_IsRefused()
        {
            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                "OpLin", OtherGuid + "-OpLin", Declared(), out _);

            Assert.Equal("GridVariableIdentityMismatch", (string)error["code"]);
        }

        [Fact]
        public void UndeclaredVariable_IsRefusedWithTheDeclaredCatalog()
        {
            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                "Ghost", OpLinGuid + "-Ghost", Declared(), out string reference);

            Assert.Null(reference);
            Assert.Equal("GridVariableIdentityNotFound", (string)error["code"]);
            Assert.Equal(new[] { OtherGuid + "-Selected", OpLinGuid + "-OpLin" },
                error["declaredVariables"].Select(v => (string)v["variableReference"]));
        }

        [Fact]
        public void DeclaredVariableWithoutAUsableGuid_IsNotAnIdentity()
        {
            var declared = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("OpLin", "not-a-guid") };

            JObject error = WwpActionService.ResolveDeclaredVariableReference(
                "OpLin", OpLinGuid + "-OpLin", declared, out string reference);

            Assert.Null(reference);
            Assert.Equal("GridVariableIdentityNotFound", (string)error["code"]);
        }
    }
}
