using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Issue #712: genexus_properties action=move accepted a path destination such as
    // "Root Module/Operacao/GrupoDestino" (FindObject resolves it to the Folder), the SDK
    // re-parented the object, and then the post-save check compared the object's
    // immediate parent NAME ("GrupoDestino") with the raw destination PATH. The strings
    // never match, so every move into a Folder addressed by path was rolled back as
    // "The SDK did not persist the requested parent". Parent verification now uses the
    // resolved container's identity.
    public class ObjectMoverParentVerificationTests
    {
        private const string DestinationGuid = "a4239946-dc92-44ee-ba53-f791e5e0b23c";
        private const string OtherGuid = "675d3266-d4b8-4d37-8918-4dd8700d0847";

        [Theory]
        // Path destination: the parent is the resolved container (the #712 case).
        [InlineData(DestinationGuid, "GrupoDestino", "Root Module/Operacao/GrupoDestino", true)]
        // Bare names keep matching, by identity or by the legacy name comparison.
        [InlineData(DestinationGuid, "GrupoDestino", "GrupoDestino", true)]
        [InlineData(null, "Root Module", "root module", true)]
        // The object stayed where it was: neither identity nor name matches.
        [InlineData(OtherGuid, "Root Module", "Root Module/Operacao/GrupoDestino", false)]
        [InlineData(null, null, "GrupoDestino", false)]
        public void IsRequestedParent_MatchesResolvedContainer(
            string actualParentGuid, string actualParentName, string destination, bool expected)
        {
            Guid? parent = actualParentGuid == null ? (Guid?)null : new Guid(actualParentGuid);
            Assert.Equal(expected, ObjectMover.IsRequestedParent(
                parent, actualParentName, new Guid(DestinationGuid), destination));
        }
    }
}
