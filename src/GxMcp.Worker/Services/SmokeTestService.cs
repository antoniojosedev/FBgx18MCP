using System;
using System.Net;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 smoke test: open target in headless browser, assert HTTP 200 on the rendered URL,
    /// assert no <c>&lt;scriptError&gt;</c> in body, assert browser console.exceptions are empty.
    /// Reuses <see cref="BrowserCaptureService"/> for the browser-side checks. HTTP fetch is via
    /// <see cref="IHttpProbe"/> so unit tests can stub the network without touching localhost.
    /// </summary>
    public class SmokeTestService
    {
        public interface IHttpProbe
        {
            ProbeResult Fetch(string url);
        }

        public class ProbeResult
        {
            public int StatusCode;
            public string Body = string.Empty;
            public string Error;
        }

        public class DefaultHttpProbe : IHttpProbe
        {
            public ProbeResult Fetch(string url)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 15000;
                    req.AllowAutoRedirect = true;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var rs = resp.GetResponseStream())
                    using (var sr = new System.IO.StreamReader(rs))
                    {
                        return new ProbeResult { StatusCode = (int)resp.StatusCode, Body = sr.ReadToEnd() };
                    }
                }
                catch (WebException wex) when (wex.Response is HttpWebResponse hr)
                {
                    string body;
                    using (var rs = hr.GetResponseStream())
                    using (var sr = new System.IO.StreamReader(rs))
                        body = sr.ReadToEnd();
                    return new ProbeResult { StatusCode = (int)hr.StatusCode, Body = body, Error = wex.Message };
                }
                catch (Exception ex)
                {
                    return new ProbeResult { StatusCode = 0, Error = ex.Message };
                }
            }
        }

        private readonly BrowserCaptureService _capture;
        private readonly IHttpProbe _http;
        private readonly Func<string, string> _urlBuilder;

        public SmokeTestService(BrowserCaptureService capture, IHttpProbe http = null, Func<string, string> urlBuilder = null)
        {
            _capture = capture;
            _http = http ?? new DefaultHttpProbe();
            _urlBuilder = urlBuilder ?? (t => "http://localhost/" + t + ".aspx");
        }

        public JObject Run(string target)
        {
            var result = new JObject
            {
                ["target"] = target,
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("o")
            };
            var steps = new JArray();

            if (string.IsNullOrWhiteSpace(target))
            {
                result["ok"] = false;
                result["code"] = "invalid_request";
                result["steps"] = steps;
                result["failedAt"] = "validate";
                return result;
            }

            // Step 1: browser open via capture service (driver-available gate)
            var captureResult = _capture.Capture(target, new JArray("exceptions"));
            bool captureSkipped = (captureResult["skipped"]?.ToObject<bool?>() ?? false);
            if (captureSkipped)
            {
                steps.Add(Step("driver", false, "BrowserDriverUnavailable"));
                result["ok"] = false;
                result["skipped"] = true;
                result["code"] = "BrowserDriverUnavailable";
                result["hint"] = captureResult["hint"]?.ToString();
                result["steps"] = steps;
                result["failedAt"] = "driver";
                return result;
            }
            steps.Add(Step("driver", true, captureResult["driverUsed"]?.ToString() ?? ""));

            // Step 2: HTTP 200
            string url = _urlBuilder(target);
            var probe = _http.Fetch(url);
            bool http200 = probe.StatusCode >= 200 && probe.StatusCode < 300;
            steps.Add(Step("http_200", http200, "status=" + probe.StatusCode + (probe.Error != null ? "; " + probe.Error : "")));
            if (!http200)
            {
                result["ok"] = false;
                result["failedAt"] = "http_200";
                result["steps"] = steps;
                return result;
            }

            // Step 3: no scriptError
            bool noScriptError = probe.Body == null || probe.Body.IndexOf("<scriptError", StringComparison.OrdinalIgnoreCase) < 0;
            steps.Add(Step("no_script_error", noScriptError, noScriptError ? "" : "body contains <scriptError>"));
            if (!noScriptError)
            {
                result["ok"] = false;
                result["failedAt"] = "no_script_error";
                result["steps"] = steps;
                return result;
            }

            // Step 4: console.exceptions empty
            var exArr = captureResult["exceptions"] as JArray;
            bool noExceptions = exArr == null || exArr.Count == 0;
            steps.Add(Step("no_exceptions", noExceptions, noExceptions ? "" : (exArr?.Count + " exception(s)")));
            if (!noExceptions)
            {
                result["ok"] = false;
                result["failedAt"] = "no_exceptions";
                result["steps"] = steps;
                return result;
            }

            result["ok"] = true;
            result["steps"] = steps;
            return result;
        }

        private static JObject Step(string name, bool ok, string message) =>
            new JObject { ["name"] = name, ["ok"] = ok, ["message"] = message ?? string.Empty };
    }
}
