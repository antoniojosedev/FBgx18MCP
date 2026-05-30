using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class DatabaseInfoService
    {
        private readonly KbService _kbService;

        public DatabaseInfoService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetInfo()
        {
            var env = new JObject();
            try
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null)
                {
                    return McpResponse.Err(
                        code: "KbNotOpen",
                        message: "No KB is currently open.",
                        hint: "Open a KB first with genexus_kb action=open.",
                        nextSteps: new Newtonsoft.Json.Linq.JArray(McpResponse.NextStep(
                            "genexus_kb",
                            new JObject { ["action"] = "open", ["path"] = "<kb path>" },
                            "Open the target KB before calling db_info.")));
                }

                dynamic environment = kb.DesignModel.Environment;
                string environmentName = TryGet(() => (string)environment.Name) ?? "";
                env["environment"] = environmentName;

                var stores = new JArray();
                JObject defaultStore = null;

                foreach (dynamic ds in EnumerateDataStores(kb))
                {
                    if (ds == null) continue;
                    var entry = BuildEntry(ds);
                    stores.Add(entry);
                    if (defaultStore == null && entry["isDefault"]?.Value<bool>() == true)
                    {
                        defaultStore = (JObject)entry;
                    }
                }

                if (defaultStore == null && stores.Count > 0)
                {
                    defaultStore = (JObject)stores[0];
                    defaultStore["isDefault"] = true;
                }

                env["datastores"] = stores;
                if (defaultStore != null)
                {
                    env["default"] = new JObject
                    {
                        ["name"] = defaultStore["name"],
                        ["type"] = defaultStore["type"],
                        ["dialect"] = defaultStore["dialect"],
                        ["provider"] = defaultStore["provider"],
                        ["serverName"] = defaultStore["serverName"],
                        ["schema"] = defaultStore["schema"]
                    };
                    env["dialect"] = defaultStore["dialect"];
                }
                return McpResponse.Ok(code: "DatabaseInfoCollected", result: env);
            }
            catch (Exception ex)
            {
                return McpResponse.Err(
                    code: "DatabaseInfoFailed",
                    message: ex.Message,
                    hint: "Check that the KB environment exposes DataStores via the SDK.");
            }
        }

        private static IEnumerable<dynamic> EnumerateDataStores(dynamic kb)
        {
            var collected = new List<dynamic>();

            // v2.8.2 — primary path: the DataStoresPart KBModelPart. The legacy paths below
            // miss it on many KBs because KBModelPartCollection is keyed by Guid (so
            // Parts.Get("DataStores") with a string finds nothing) and Environment.DataStores /
            // TargetModel.DataStore come back null. DataStoresPart.DataStores is the real,
            // documented accessor for the GxDataStore collection.
            try
            {
                var viaPart = EnumerateViaDataStoresPart(kb);
                if (viaPart.Count > 0) return viaPart;
            }
            catch { }

            try
            {
                dynamic part = TryGet(() => kb.DesignModel.Parts.Get("DataStores"));
                if (part != null)
                {
                    foreach (dynamic ds in (System.Collections.IEnumerable)part)
                    {
                        collected.Add(ds);
                    }
                    if (collected.Count > 0) return collected;
                }
            }
            catch { }

            try
            {
                dynamic envDs = TryGet(() => kb.DesignModel.Environment.DataStores);
                if (envDs != null)
                {
                    foreach (dynamic ds in (System.Collections.IEnumerable)envDs)
                    {
                        collected.Add(ds);
                    }
                    if (collected.Count > 0) return collected;
                }
            }
            catch { }

            try
            {
                dynamic targetDs = TryGet(() => kb.DesignModel.Environment.TargetModel.DataStore);
                if (targetDs != null) collected.Add(targetDs);
            }
            catch { }

            return collected;
        }

        // v2.8.2 — enumerate GxDataStores via the DataStoresPart model part. The part lives in
        // model.Parts (a Guid-keyed KBModelPartCollection), so we iterate and match by type name
        // rather than guessing the part's Guid. Tries the design model first, then the
        // environment's target model. Shared with KbService's [KB-OPEN-DATASTORE] diagnostic.
        internal static List<dynamic> EnumerateViaDataStoresPart(dynamic kb)
        {
            var found = new List<dynamic>();
            if (kb == null) return found;

            var models = new List<dynamic>();
            try { var m = kb.DesignModel; if (m != null) models.Add(m); } catch { }
            try { var tm = kb.DesignModel.Environment.TargetModel; if (tm != null) models.Add(tm); } catch { }
            // The DataStoresPart often lives on an environment model that is neither the design
            // model nor the target model. KBEnvironment.Models exposes them all — try each.
            try
            {
                var all = kb.DesignModel.Environment.Models;
                if (all is System.Collections.IEnumerable me)
                    foreach (var m in me) { if (m != null) models.Add(m); }
            }
            catch { }

            foreach (var model in models)
            {
                dynamic parts = null;
                try { parts = model.Parts; } catch { }
                if (!(parts is System.Collections.IEnumerable seq)) continue;

                foreach (var item in seq)
                {
                    if (item == null) continue;
                    // KBModelPartCollection is IDictionary<Guid, KBModelPart>; the default
                    // enumerator may yield KeyValuePair<Guid, KBModelPart>. Unwrap to the part.
                    object part = item;
                    try
                    {
                        var it = item.GetType();
                        if (it.Name.StartsWith("KeyValuePair", StringComparison.Ordinal))
                            part = it.GetProperty("Value")?.GetValue(item);
                    }
                    catch { }
                    if (part == null) continue;

                    string typeName = null;
                    try { typeName = part.GetType().Name; } catch { }
                    if (!string.Equals(typeName, "DataStoresPart", StringComparison.Ordinal)) continue;

                    dynamic dss = null;
                    try { dss = ((dynamic)part).DataStores; } catch { }
                    if (dss is System.Collections.IEnumerable dsSeq)
                    {
                        foreach (var ds in dsSeq) if (ds != null) found.Add(ds);
                    }
                    if (found.Count > 0) return found;
                }
            }
            return found;
        }

        private static JObject BuildEntry(dynamic ds)
        {
            // GxDataStore has no direct Name property — fall back to its Category name / Type.
            string name = TryGet(() => (string)ds.Name)
                          ?? TryGet(() => (string)ds.Category.Name)
                          ?? TryGet(() => (string)ds.Type)
                          ?? "";
            int dbmsInt = TryGetInt(() => (int)ds.Dbms);
            string family = ExecutionPlanFetcher.ResolveDbmsFamily(dbmsInt);
            string typeLabel = DbmsTypeLabel(dbmsInt);
            bool isDefault = false;
            try { isDefault = (bool)ds.IsDefault; }
            catch
            {
                try { isDefault = string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase); }
                catch { }
            }

            string provider = TryProperty(ds, "AdoNetProvider")
                              ?? TryProperty(ds, "Provider")
                              ?? "";
            string serverName = TryProperty(ds, "ServerName") ?? "";
            string schema = TryProperty(ds, "DatabaseSchema")
                            ?? TryProperty(ds, "Schema")
                            ?? "";
            string accessTech = TryProperty(ds, "AccessTechnology") ?? "";
            string userId = TryProperty(ds, "UserId")
                            ?? TryProperty(ds, "User")
                            ?? "";

            var entry = new JObject
            {
                ["name"] = name,
                ["type"] = typeLabel,
                ["dialect"] = family,
                ["dbmsCode"] = dbmsInt,
                ["isDefault"] = isDefault,
                ["provider"] = provider,
                ["serverName"] = serverName,
                ["schema"] = schema,
                ["accessTechnology"] = accessTech,
                ["userId"] = userId
            };
            return entry;
        }

        private static string TryProperty(dynamic ds, string propertyName)
        {
            try
            {
                var raw = ds.Properties.GetPropertyValue(propertyName);
                if (raw == null) return null;
                string s = raw.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        private static T TryGet<T>(Func<T> f)
        {
            try { return f(); } catch { return default(T); }
        }

        private static int TryGetInt(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        public static string DbmsTypeLabel(int dbmsType)
        {
            switch (dbmsType)
            {
                case 1: return "SqlServer";
                case 2: return "Db2";
                case 3: return "Informix";
                case 4: return "Oracle";
                case 5: return "MySQL";
                case 6: return "PostgreSQL";
                case 7: return "Oracle";
                case 8: return "Db2/AS400";
                case 9: return "Db2Universal";
                case 10: return "SAPHana";
                case 11: return "DynamoDB";
                default: return "Unknown";
            }
        }
    }
}
