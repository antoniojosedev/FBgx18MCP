using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The image source <c>genexus_create type=Image</c> builds a new Image object from
    /// (issue #729). The SDK refuses to save an Image without an image item, so a create
    /// needs either a local file (<c>imagePath</c>, embedded through
    /// <c>Image.CreateFromFile</c>) or an external URL (<c>imageUrl</c>, through
    /// <c>Image.CreateExternal</c>). Resolved from the request alone so the dry-run
    /// preview and the write path refuse the same requests before anything is saved.
    /// </summary>
    public sealed class ImageCreatePlan
    {
        public const string SourceRequiredCode = "ImageSourceRequired";
        public const string SourceInvalidCode = "ImageSourceInvalid";

        public string FilePath { get; private set; }
        public string Url { get; private set; }
        public string ErrorCode { get; private set; }
        public string Error { get; private set; }

        public static bool IsImageType(string type)
            => string.Equals(type, "Image", StringComparison.OrdinalIgnoreCase);

        public static ImageCreatePlan FromOptions(JObject options, Func<string, bool> fileExists = null)
        {
            fileExists = fileExists ?? File.Exists;
            string path = options?["imagePath"]?.ToString();
            string url = options?["imageUrl"]?.ToString();
            bool hasPath = !string.IsNullOrWhiteSpace(path);
            bool hasUrl = !string.IsNullOrWhiteSpace(url);

            if (!hasPath && !hasUrl)
                return Fail(SourceRequiredCode, "An Image object needs an image: pass imagePath (absolute path to a local image file, or a .zip of density variants) or imageUrl (external image URL).");
            if (hasPath && hasUrl)
                return Fail(SourceInvalidCode, "Pass either imagePath or imageUrl, not both.");

            if (hasPath)
            {
                path = path.Trim();
                if (!Path.IsPathRooted(path))
                    return Fail(SourceInvalidCode, "imagePath must be an absolute path on the Worker machine: '" + path + "'.");
                if (!fileExists(path))
                    return Fail(SourceInvalidCode, "imagePath does not exist: '" + path + "'.");
                return new ImageCreatePlan { FilePath = path };
            }

            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                return Fail(SourceInvalidCode, "imageUrl must be an absolute http(s) URL: '" + url + "'.");
            return new ImageCreatePlan { Url = url };
        }

        public JObject ToJson()
        {
            var json = new JObject();
            if (FilePath != null) { json["source"] = "file"; json["imagePath"] = FilePath; }
            if (Url != null) { json["source"] = "external"; json["imageUrl"] = Url; }
            return json;
        }

        private static ImageCreatePlan Fail(string code, string message)
            => new ImageCreatePlan { ErrorCode = code, Error = message };
    }
}
