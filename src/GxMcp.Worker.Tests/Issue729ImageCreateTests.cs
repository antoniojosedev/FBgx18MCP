using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Issue #729: genexus_create type=Image must be built from an image (imagePath or
    /// imageUrl) and refuse a sourceless request before Save(), dryRun included, instead of
    /// passing the preview and failing the real Save with "validation failed".
    /// </summary>
    public class Issue729ImageCreateTests
    {
        private static ImageCreatePlan Plan(JObject options, bool exists = true)
            => ImageCreatePlan.FromOptions(options, _ => exists);

        [Theory]
        [InlineData("Image", true)]
        [InlineData("image", true)]
        [InlineData("Procedure", false)]
        [InlineData(null, false)]
        public void IsImageType_MatchesOnlyImage(string type, bool expected)
        {
            Assert.Equal(expected, ImageCreatePlan.IsImageType(type));
        }

        [Fact]
        public void NoSource_IsRefusedWithSourceRequired()
        {
            var plan = Plan(new JObject { ["type"] = "Image", ["name"] = "ImgTesoura" });

            Assert.Equal(ImageCreatePlan.SourceRequiredCode, plan.ErrorCode);
            Assert.Contains("imagePath", plan.Error);
            Assert.Null(plan.FilePath);
            Assert.Null(plan.Url);
        }

        [Fact]
        public void NullSourceFields_AreTreatedAsAbsent()
        {
            // The Gateway forwards absent arguments as JSON nulls.
            var plan = Plan(new JObject { ["imagePath"] = JValue.CreateNull(), ["imageUrl"] = JValue.CreateNull() });

            Assert.Equal(ImageCreatePlan.SourceRequiredCode, plan.ErrorCode);
        }

        [Fact]
        public void AbsoluteExistingPath_IsAccepted()
        {
            var plan = Plan(new JObject { ["imagePath"] = @"C:\images\tesoura.png" });

            Assert.Null(plan.Error);
            Assert.Equal(@"C:\images\tesoura.png", plan.FilePath);
            Assert.Equal("file", plan.ToJson()["source"]?.ToString());
        }

        [Fact]
        public void MissingFile_IsRefusedWithSourceInvalid()
        {
            var plan = Plan(new JObject { ["imagePath"] = @"C:\images\missing.png" }, exists: false);

            Assert.Equal(ImageCreatePlan.SourceInvalidCode, plan.ErrorCode);
            Assert.Contains("does not exist", plan.Error);
        }

        [Fact]
        public void RelativePath_IsRefused()
        {
            var plan = Plan(new JObject { ["imagePath"] = @"images\tesoura.png" });

            Assert.Equal(ImageCreatePlan.SourceInvalidCode, plan.ErrorCode);
            Assert.Contains("absolute", plan.Error);
        }

        [Fact]
        public void BothSources_AreRefused()
        {
            var plan = Plan(new JObject { ["imagePath"] = @"C:\a.png", ["imageUrl"] = "https://example.com/a.png" });

            Assert.Equal(ImageCreatePlan.SourceInvalidCode, plan.ErrorCode);
        }

        [Theory]
        [InlineData("https://example.com/logo.png", true)]
        [InlineData("http://example.com/logo.png", true)]
        [InlineData("ftp://example.com/logo.png", false)]
        [InlineData("logo.png", false)]
        public void ImageUrl_RequiresAbsoluteHttpUrl(string url, bool accepted)
        {
            var plan = Plan(new JObject { ["imageUrl"] = url });

            if (accepted)
            {
                Assert.Null(plan.Error);
                Assert.Equal(url, plan.Url);
                Assert.Equal("external", plan.ToJson()["source"]?.ToString());
            }
            else
            {
                Assert.Equal(ImageCreatePlan.SourceInvalidCode, plan.ErrorCode);
            }
        }
    }
}
