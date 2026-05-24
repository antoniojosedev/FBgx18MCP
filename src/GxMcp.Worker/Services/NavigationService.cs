using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using System.Xml;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class NavigationService
    {
        private readonly KbService _kbService;

        public NavigationService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetNavigation(string targetName)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Logger.Info($"GetNavigation START: {targetName}");

                string nvgPath = FindNavigationFile(targetName);
                if (nvgPath == null) return "{\"status\":\"Error\",\"error\": \"Navigation report not found for '" + targetName + "'. Make sure the object is specified.\"}";

                Logger.Info($"GetNavigation file resolved for {targetName}: {nvgPath}");

                var xmlSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CloseInput = true
                };

                XDocument doc = LoadNavigationDocument(nvgPath, xmlSettings, targetName);

                var result = new JObject();
                result["name"] = targetName;
                
                var levels = new JArray();
                foreach (var level in doc.Descendants("Level"))
                {
                    var levelObj = new JObject();
                    levelObj["number"] = (int?)level.Element("LevelNumber");
                    levelObj["type"] = level.Element("LevelType")?.Value;
                    levelObj["line"] = (int?)level.Element("LevelBeginRow");
                    
                    var baseTable = level.Element("BaseTable")?.Element("Table");
                    if (baseTable != null)
                    {
                        levelObj["baseTable"] = baseTable.Element("TableName")?.Value;
                        levelObj["baseTableDescription"] = baseTable.Element("Description")?.Value;
                    }

                    levelObj["index"] = level.Element("IndexName")?.Value;
                    
                    var order = new JArray();
                    var orderEl = level.Element("Order");
                    if (orderEl != null)
                    {
                        foreach (var att in orderEl.Elements("Attribute"))
                            order.Add(att.Element("AttriName")?.Value);
                    }
                    levelObj["order"] = order;

                    var optWhere = level.Element("OptimizedWhere");
                    bool hasOptimization = optWhere != null && optWhere.Elements().Any();
                    levelObj["isOptimized"] = hasOptimization;

                    var filters = new JArray();
                    if (optWhere != null)
                    {
                        foreach (var f in optWhere.Elements())
                        {
                            var fObj = new JObject();
                            fObj["element"] = f.Name.LocalName;
                            // OptimizedWhere children typically wrap an attribute name + comparison + variable/value
                            // We surface the raw inner text plus element name; downstream consumers can parse.
                            fObj["expression"] = f.Value?.Trim();
                            // Try to surface common sub-elements explicitly when present
                            var attrEl = f.Element("Attribute");
                            if (attrEl != null) fObj["attribute"] = attrEl.Value?.Trim();
                            var opEl = f.Element("Operator");
                            if (opEl != null) fObj["op"] = opEl.Value?.Trim();
                            var valEl = f.Element("Value");
                            if (valEl != null) fObj["value"] = valEl.Value?.Trim();
                            filters.Add(fObj);
                        }
                    }
                    levelObj["filters"] = filters;

                    levels.Add(levelObj);
                }

                result["levels"] = levels;

                var warnings = new JArray();
                foreach (var w in doc.Descendants("Warning"))
                    warnings.Add(w.Element("Message")?.Value);
                result["warnings"] = warnings;

                // Empty-levels envelope: without a status hint the caller can't tell
                // "no For Each / data-bound code in this object" from "the analysis
                // failed silently". Set status accordingly.
                if (levels.Count == 0)
                {
                    result["status"] = "NoNavigationBlocks";
                    result["hint"] = "Object has no For Each / data-bound navigation blocks. Use mode=summary or mode=data_context for variable/call analysis.";
                }
                else
                {
                    result["status"] = "OK";
                }

                Logger.Info($"GetNavigation SUCCESS: {targetName} in {sw.ElapsedMilliseconds}ms levels={levels.Count}");
                return result.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"GetNavigation ERROR for {targetName}: {ex.Message}");
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private string FindNavigationFile(string targetName)
        {
            var kb = _kbService.GetKB();
            if (kb == null) return null;

            string kbPath = kb.Location;
            if (File.Exists(kbPath))
            {
                kbPath = Path.GetDirectoryName(kbPath);
            }

            if (string.IsNullOrWhiteSpace(kbPath) || !Directory.Exists(kbPath))
            {
                return null;
            }

            var specFolders = Directory.EnumerateDirectories(kbPath, "GXSPC*", SearchOption.TopDirectoryOnly)
                                       .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (var specFolder in specFolders)
            {
                foreach (var genFolder in Directory.EnumerateDirectories(specFolder, "GEN*", SearchOption.TopDirectoryOnly))
                {
                    string genPath = Path.Combine(genFolder, "NVG");
                    if (!Directory.Exists(genPath))
                    {
                        continue;
                    }

                    string fullPath = Path.Combine(genPath, targetName + ".xml");
                    if (File.Exists(fullPath)) return fullPath;
                }
            }

            return null;
        }

        private static XDocument LoadNavigationDocument(string nvgPath, XmlReaderSettings xmlSettings, string targetName)
        {
            try
            {
                using (var stream = new FileStream(nvgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = XmlReader.Create(stream, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
            catch (XmlException ex) when (LooksLikeLegacySingleByteEncoding(ex))
            {
                Logger.Info($"GetNavigation fallback decoding for {targetName}: retrying as Windows-1252.");
                string xmlText = Encoding.GetEncoding(1252).GetString(File.ReadAllBytes(nvgPath));
                using (var stringReader = new StringReader(xmlText))
                using (var reader = XmlReader.Create(stringReader, xmlSettings))
                {
                    return XDocument.Load(reader, LoadOptions.None);
                }
            }
        }

        private static bool LooksLikeLegacySingleByteEncoding(XmlException ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
            {
                return false;
            }

            string message = ex.Message;
            return message.IndexOf("codifica", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("encoding", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
