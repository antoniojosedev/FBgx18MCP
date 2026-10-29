using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // object_atomic reads folder, module and parentPath. Two of them naming different places used to be
    // resolved by silent precedence; now the call is refused before anything is created.
    public class PlacementRequestTests
    {
        [Fact]
        public void FromCreateArgs_NoPlacement_IsNull()
        {
            var request = PlacementRequest.FromCreateArgs(JObject.Parse("{\"name\":\"P\",\"folder\":\"  \"}"), out string conflict);

            Assert.Null(request);
            Assert.Null(conflict);
        }

        [Fact]
        public void FromCreateArgs_ParentPath_KeepsThePathAndNoKind()
        {
            var request = PlacementRequest.FromCreateArgs(
                JObject.Parse("{\"parentPath\":\" Root Module/Stock/Returns \"}"), out string conflict);

            Assert.Null(conflict);
            Assert.Equal("Root Module/Stock/Returns", request.Raw);
            Assert.Null(request.Kind);
            Assert.Equal("parentPath", request.Source);
        }

        [Theory]
        [InlineData("folder", "Folder")]
        [InlineData("module", "Module")]
        public void FromCreateArgs_FolderAndModule_CarryTheirKind(string argument, string kind)
        {
            var args = new JObject { [argument] = "Alvo" };
            var request = PlacementRequest.FromCreateArgs(args, out string conflict);

            Assert.Null(conflict);
            Assert.Equal(kind, request.Kind);
            Assert.Equal(argument, request.Source);
        }

        [Fact]
        public void FromCreateArgs_TwoDifferentPlaces_IsAConflict()
        {
            var request = PlacementRequest.FromCreateArgs(
                JObject.Parse("{\"folder\":\"A\",\"parentPath\":\"Root Module/B\"}"), out string conflict);

            Assert.Null(request);
            Assert.Contains("folder='A'", conflict);
            Assert.Contains("parentPath='Root Module/B'", conflict);
        }

        [Fact]
        public void FromCreateArgs_TheSamePlaceSpelledTwice_IsNotAConflict()
        {
            var request = PlacementRequest.FromCreateArgs(
                JObject.Parse("{\"folder\":\"Stock/Alvo\",\"parentPath\":\"Root Module\\\\STOCK\\\\alvo\"}"), out string conflict);

            Assert.Null(conflict);
            Assert.Equal("Folder", request.Kind);
        }

        [Fact]
        public void FromCreateArgs_FolderAndModuleNamingOnePlace_IsAConflictOfKinds()
        {
            var request = PlacementRequest.FromCreateArgs(
                JObject.Parse("{\"folder\":\"Alvo\",\"module\":\"Alvo\"}"), out string conflict);

            Assert.Null(request);
            Assert.Contains("different kinds", conflict);
        }
    }
}
