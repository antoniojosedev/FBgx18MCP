using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Item 40 — genexus_ocr_screenshot. Heavyweight OCR (Tesseract.NET) is not
    /// wired by default. Returns an explicit Unwired envelope so the agent gets a
    /// machine-readable code + hint rather than a vague "not implemented" string.
    /// Set GXMCP_OCR_ENGINE=tesseract once a Tesseract.NET dependency is added.
    /// </summary>
    public class OcrScreenshotService
    {
        public string Run(string path)
        {
            string engine = Environment.GetEnvironmentVariable("GXMCP_OCR_ENGINE");

            // Even when unwired, surface a basic path-validation diagnostic so the
            // agent can distinguish "wrong path" from "no engine".
            bool pathExists = false;
            try { pathExists = !string.IsNullOrWhiteSpace(path) && File.Exists(path); } catch { }

            var resp = new JObject
            {
                ["status"] = "Unwired",
                ["code"] = "OcrEngineUnwired",
                ["hint"] = "install Tesseract.NET and set GXMCP_OCR_ENGINE=tesseract",
                ["engine"] = engine ?? "(unset)",
                ["path"] = path ?? "",
                ["pathExists"] = pathExists
            };
            return resp.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
