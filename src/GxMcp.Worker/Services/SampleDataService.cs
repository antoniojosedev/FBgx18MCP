using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Artech.Genexus.Common.Objects;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Wave-3 item 42: emit INSERT statements with fake-but-typed data for a
    // Transaction. Pure SQL — no execution, no DB connection touched. Useful
    // when scripting initial fixtures or seeding a dev environment by hand.
    public class SampleDataService
    {
        private readonly ObjectService _objectService;

        private static readonly string[] LoremWords = {
            "lorem", "ipsum", "dolor", "sit", "amet", "consectetur",
            "adipiscing", "elit", "sed", "do", "eiusmod", "tempor"
        };

        public SampleDataService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        public string Generate(string trnName, int rows)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(trnName))
                    return Error("Transaction name required");
                if (rows <= 0) rows = 5;
                if (rows > 1000) rows = 1000;

                var obj = _objectService?.FindObject(trnName);
                if (obj == null) return Error("Transaction not found: " + trnName);
                if (!(obj is Transaction trn)) return Error("Object is not a Transaction: " + trnName);

                var attrs = CollectAttributes(trn);
                if (attrs.Count == 0) return Error("Transaction has no attributes: " + trnName);

                string tableName = trnName;
                try { tableName = (trn.Structure?.Root?.AssociatedTable?.Name) ?? trnName; } catch { }

                var inserts = new JArray();
                var rng = new Random(unchecked(trnName.GetHashCode() ^ rows));
                var columnList = string.Join(", ", attrs.Select(a => a.Name));
                for (int i = 0; i < rows; i++)
                {
                    var values = string.Join(", ", attrs.Select(a => FakeValueSql(a, i, rng)));
                    var sql = "INSERT INTO " + tableName + " (" + columnList + ") VALUES (" + values + ");";
                    inserts.Add(sql);
                }

                var result = new JObject();
                result["insertStatements"] = inserts;
                var summary = new JObject();
                summary["transaction"] = trnName;
                summary["table"] = tableName;
                summary["rows"] = rows;
                summary["attributeCount"] = attrs.Count;
                summary["note"] = "SQL only; statements are NOT executed by the worker.";
                result["summary"] = summary;
                return result.ToString();
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        private static List<AttrInfo> CollectAttributes(Transaction trn)
        {
            var list = new List<AttrInfo>();
            try
            {
                dynamic root = trn.Structure?.Root;
                if (root == null) return list;
                foreach (dynamic ta in root.Attributes)
                {
                    string name = null;
                    string typeName = null;
                    int length = 0;
                    int decimals = 0;
                    try { name = ta.Attribute != null ? (string)ta.Attribute.Name : (string)ta.Name; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    try { typeName = (ta.Attribute?.Type?.ToString()) ?? ""; } catch { typeName = ""; }
                    try { length = (int)ta.Attribute.Length; } catch { }
                    try { decimals = (int)ta.Attribute.Decimals; } catch { }
                    list.Add(new AttrInfo { Name = name, Type = typeName ?? "", Length = length, Decimals = decimals });
                }
            }
            catch { }
            return list;
        }

        internal static string FakeValueSql(AttrInfo a, int rowIndex, Random rng)
        {
            string t = (a.Type ?? "").ToUpperInvariant();
            if (t.Contains("NUMERIC") || t.Contains("INT"))
            {
                if (a.Decimals > 0)
                {
                    int wholeDigits = Math.Max(1, a.Length - a.Decimals);
                    long whole = (long)(rng.NextDouble() * Math.Pow(10, Math.Min(wholeDigits, 6)));
                    long frac = (long)(rng.NextDouble() * Math.Pow(10, Math.Min(a.Decimals, 4)));
                    return (whole + (frac / Math.Pow(10, Math.Min(a.Decimals, 4))))
                        .ToString("F" + Math.Min(a.Decimals, 4), CultureInfo.InvariantCulture);
                }
                int digits = a.Length > 0 ? Math.Min(a.Length, 6) : 4;
                long max = (long)Math.Pow(10, digits);
                return ((rowIndex + 1) % max).ToString(CultureInfo.InvariantCulture);
            }
            if (t.Contains("CHARACTER") || t.Contains("VARCHAR") || t.Contains("LONGVARCHAR"))
            {
                int target = a.Length > 0 ? Math.Min(a.Length, 40) : 16;
                var sb = new StringBuilder();
                while (sb.Length < target)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(LoremWords[rng.Next(LoremWords.Length)]);
                }
                string s = sb.ToString();
                if (s.Length > target) s = s.Substring(0, target);
                return "'" + s.Replace("'", "''") + "'";
            }
            if (t.Contains("DATETIME"))
            {
                var dt = DateTime.UtcNow.AddDays(-rng.Next(0, 365)).AddHours(-rng.Next(0, 24));
                return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'";
            }
            if (t.Contains("DATE"))
            {
                var dt = DateTime.UtcNow.Date.AddDays(-rng.Next(0, 365));
                return "'" + dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
            }
            if (t.Contains("BOOLEAN") || t.Contains("BIT"))
            {
                return (rng.Next(2) == 0) ? "'f'" : "'t'";
            }
            // Fallback — emit a quoted lorem token to avoid blowing up unfamiliar
            // domain types like Image / Blob (a real seed would tailor these).
            return "'" + LoremWords[rng.Next(LoremWords.Length)] + "'";
        }

        private static string Error(string msg)
            => "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(msg ?? "") + "\"}";

        internal class AttrInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Length { get; set; }
            public int Decimals { get; set; }
        }
    }
}
