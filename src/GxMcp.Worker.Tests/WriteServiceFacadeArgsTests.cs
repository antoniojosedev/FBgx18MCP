using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteServiceFacadeArgsTests
    {
        [Fact]
        public void NormalizeFacadeArgs_FullMode_UsesRealPartAndContent()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "Source",
                ["content"] = "parm(out:&Ok);",
                ["dryRun"] = true
            });

            Assert.Null(normalized.Mode);
            Assert.Equal("Source", normalized.PartName);
            Assert.Equal("parm(out:&Ok);", normalized.Content);
            Assert.True(normalized.DryRun);
        }

        [Fact]
        public void NormalizeFacadeArgs_PatchMode_UnwrapsFindReplaceShape()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["mode"] = "patch",
                ["part"] = "Source",
                ["content"] = new JObject
                {
                    ["find"] = "old line",
                    ["replace"] = "new line"
                },
                ["expectedCount"] = 2,
                ["replaceAll"] = true
            });

            Assert.Equal("patch", normalized.Mode);
            Assert.Equal("Source", normalized.PartName);
            Assert.Equal("Replace", normalized.Operation);
            Assert.Equal("old line", normalized.Context);
            Assert.Equal("new line", normalized.Content);
            Assert.Equal(2, normalized.ExpectedCount);
            Assert.True(normalized.ReplaceAll);
        }

        [Theory]
        [InlineData(null, true)]            // default → strict verify on
        [InlineData("strict", true)]        // explicit strict → verify on
        [InlineData("best-effort", false)]  // best-effort → skip post-write verify
        public void NormalizeFacadeArgs_Validate_DrivesStrictVerify(string validate, bool expectStrict)
        {
            var args = new JObject { ["part"] = "WebForm", ["content"] = "<GxMultiForm/>" };
            if (validate != null) args["validate"] = validate;

            var normalized = WriteService.NormalizeFacadeArgs(args);

            // strictVerify is derived at the WriteObject facade as
            // !Validate.Equals("best-effort"); mirror that derivation here so the
            // mapping stays locked even though the flag itself is a local.
            bool strictVerify = !string.Equals(normalized.Validate, "best-effort",
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(expectStrict, strictVerify);
        }

        [Fact]
        public void NormalizeFacadeArgs_ValidateOnly_ForcesDryRun()
        {
            var normalized = WriteService.NormalizeFacadeArgs(new JObject
            {
                ["part"] = "WebForm",
                ["content"] = "<GxMultiForm/>",
                ["validate"] = "only"
            });

            Assert.True(normalized.DryRun);
            Assert.Equal("only", normalized.Validate);
        }
    }
}
