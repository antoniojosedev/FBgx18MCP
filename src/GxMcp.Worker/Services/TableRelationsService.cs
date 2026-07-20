using System;
using System.Collections;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Udm.Framework;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using GenexusServices = Artech.Genexus.Common.Services;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_analyze mode=table_relations (P2 #7). Read-only.
    ///
    /// Given a Transaction, reports its associated table, the other transactions mapped to
    /// that table, and the SDK's redundant / possibly-redundant attribute detection —
    /// over <c>ITablesService</c>. The table itself comes from
    /// <c>transaction.Structure.Root.AssociatedTable</c> (same access SampleDataService uses).
    ///
    /// <c>ITablesService</c> isn't in the headless worker's service registry, so we construct
    /// the public concrete <c>TablesService</c> directly (see
    /// <see cref="SdkServiceLocator.ConstructOrResolve{T}"/> and
    /// reference_headless_service_registration_wall).
    /// </summary>
    public class TableRelationsService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public TableRelationsService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            string name = args?["name"]?.ToString() ?? args?["target"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return McpResponse.Err("BadArgs", "table_relations requires a Transaction name.", "Pass name=<Transaction>.");

            KBModel model;
            try { model = (_kb?.GetKB() as KnowledgeBase)?.DesignModel; }
            catch { model = null; }
            if (model == null)
                return McpResponse.Err("NoKbOpen", "No open KB / design model available.", "Open a KB first (genexus_kb action=open).");

            Transaction trn;
            try { trn = _objects?.FindObject(name, "Transaction") as Transaction; }
            catch { trn = null; }
            if (trn == null)
                return McpResponse.Err("ObjectNotFound", "Transaction '" + name + "' not found.", "Check the name (genexus_query type:Transaction).", target: name);

            Table table = null;
            try { table = trn.Structure?.Root?.AssociatedTable; } catch { }
            if (table == null)
                return McpResponse.Err("NoAssociatedTable", "Transaction '" + name + "' has no associated table (not yet normalized / built).", "Build the KB first (genexus_lifecycle action=build).", target: name);

            var svc = SdkServiceLocator.ConstructOrResolve<GenexusServices.ITablesService>(
                () => new Artech.Packages.Genexus.BL.Services.TablesService());
            if (svc == null)
                return McpResponse.Err("TablesServiceUnavailable", "Could not construct the SDK's TablesService.", "Restart the worker (genexus_worker_reload mode=hard) and retry.");

            try
            {
                var associatedTrns = new JArray();
                try { foreach (var t in svc.GetAssociatedTransactions(table)) associatedTrns.Add(SafeName(t)); } catch { }

                return McpResponse.Ok(
                    code: "TableRelationsRetrieved",
                    result: new JObject
                    {
                        ["transaction"] = name,
                        ["associatedTable"] = SafeStr(() => table.Name),
                        ["associatedTransactions"] = associatedTrns,
                        ["redundantAttributes"] = KeysToNames(model, () => svc.GetRedundantAttributes(table)),
                        ["possibleRedundantAttributes"] = KeysToNames(model, () => svc.GetPossibleRedundantAttributes(table)),
                        ["source"] = "sdk:ITablesService"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("TableRelationsFailed", ex.Message, "Check the worker log for the full stack trace.");
            }
        }

        private static JArray KeysToNames(KBModel model, Func<IEnumerable> f)
        {
            var arr = new JArray();
            try
            {
                foreach (var k in f())
                {
                    if (k == null) continue;
                    string label = null;
                    try { var o = model.Objects.Get((EntityKey)k); label = o?.Name; } catch { }
                    arr.Add(label ?? k.ToString());
                }
            }
            catch { }
            return arr;
        }

        private static JToken SafeName(object o)
        {
            try { return (o as KBObject)?.Name ?? o?.ToString(); } catch { return null; }
        }

        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }
    }
}
