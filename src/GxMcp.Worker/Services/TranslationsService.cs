using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Wave-3 item 92: import translations from a CSV file and update
    // CaptionExpression per row.
    //
    // STATUS: parser + plumbing landed; the SDK write path (per-language caption
    // expressions on Transaction / WebPanel / Menu items) is not yet wired —
    // GeneXus stores translations across several places (Theme Translation, Web
    // Panel form captions, Menu items, Transaction descriptions) and a faithful
    // implementation needs to route each row through the right SDK call to avoid
    // silently writing to the wrong property. We surface a structured
    // ItemDeferred response so callers can still drive the CSV ingestion plumbing
    // (skipped/errors lists are real) and detect when the write is wired.
    public class TranslationsService
    {
        private readonly WriteService _writeService;

        public TranslationsService(WriteService writeService)
        {
            _writeService = writeService;
        }

        public string Import(string inputPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath))
                    return Error("inputPath required");
                if (!File.Exists(inputPath))
                    return Error("inputPath does not exist: " + inputPath);

                var rows = ParseCsv(inputPath, out var parseErrors);

                var skipped = new JArray();
                var errors = new JArray();
                foreach (var pe in parseErrors) errors.Add(pe);

                int updated = 0;
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.ObjectName) ||
                        string.IsNullOrWhiteSpace(row.Attribute) ||
                        string.IsNullOrWhiteSpace(row.Language))
                    {
                        skipped.Add(new JObject
                        {
                            ["row"] = row.LineNumber,
                            ["reason"] = "missing-required-field",
                        });
                        continue;
                    }
                    // SDK write path not wired yet — record as skipped so the
                    // caller can see exactly which rows would have been written.
                    skipped.Add(new JObject
                    {
                        ["row"] = row.LineNumber,
                        ["objectName"] = row.ObjectName,
                        ["attribute"] = row.Attribute,
                        ["language"] = row.Language,
                        ["reason"] = "write-path-deferred",
                    });
                }

                var result = new JObject();
                result["status"] = "Unwired";
                result["code"] = "ItemDeferred";
                result["hint"] = "CSV parsed; SDK CaptionExpression write path is not yet wired. " +
                                  "Each row is reported under 'skipped' with reason 'write-path-deferred'. " +
                                  "See TranslationsService.cs for the per-object-type routing TODO.";
                result["inputPath"] = inputPath;
                result["updated"] = updated;
                result["rowsParsed"] = rows.Count;
                result["skipped"] = skipped;
                result["errors"] = errors;
                return result.ToString();
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        internal static List<TranslationRow> ParseCsv(string path, out List<string> parseErrors)
        {
            parseErrors = new List<string>();
            var rows = new List<TranslationRow>();
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { parseErrors.Add("read-failed: " + ex.Message); return rows; }
            if (lines.Length == 0) { parseErrors.Add("empty-file"); return rows; }

            int start = 0;
            // Optional header row: skip if first line literally matches the expected schema.
            if (lines[0].Trim().Equals("objectName,attribute,language,value", StringComparison.OrdinalIgnoreCase))
                start = 1;

            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = SplitCsvLine(line);
                if (fields.Count < 4)
                {
                    parseErrors.Add("row " + (i + 1) + ": expected 4 fields, got " + fields.Count);
                    continue;
                }
                rows.Add(new TranslationRow
                {
                    LineNumber = i + 1,
                    ObjectName = fields[0].Trim(),
                    Attribute = fields[1].Trim(),
                    Language = fields[2].Trim(),
                    Value = fields[3],
                });
            }
            return rows;
        }

        internal static List<string> SplitCsvLine(string line)
        {
            // Minimal CSV split: respects double-quoted fields with escaped "" inside.
            var result = new List<string>();
            if (line == null) return result;
            var cur = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(c);
                }
                else
                {
                    if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
                    else if (c == '"' && cur.Length == 0) inQuotes = true;
                    else cur.Append(c);
                }
            }
            result.Add(cur.ToString());
            return result;
        }

        private static string Error(string msg)
            => "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(msg ?? "") + "\"}";

        internal class TranslationRow
        {
            public int LineNumber { get; set; }
            public string ObjectName { get; set; }
            public string Attribute { get; set; }
            public string Language { get; set; }
            public string Value { get; set; }
        }
    }
}
