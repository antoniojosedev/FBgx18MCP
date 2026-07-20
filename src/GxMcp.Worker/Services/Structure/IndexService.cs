using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    public class IndexService
    {
        private readonly ObjectService _objectService;

        public IndexService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // issue #39: author a user-defined index on a Transaction's associated table. This is the
        // GeneXus-parity way to enforce attribute uniqueness (there is no `Unique(...)` rule).
        // payload = { attributes: ["Attr1", ...], unique?: true, name?: "IX...", order?: "Ascending" }
        public string CreateIndex(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;
                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Index creation requires a Transaction (or Table) with a physical table.",
                    hint: "Only Transactions/Tables have indexes. For SDTs or code objects there is no table to index.",
                    target: targetName);

                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidIndexPayload",
                    message: "payload is required.",
                    hint: "Pass { \"attributes\": [\"AttrName\"], \"unique\": true }.",
                    target: targetName);

                var json = JObject.Parse(payload);
                var attrArr = json["attributes"] as JArray;
                if (attrArr == null || attrArr.Count == 0) return Models.McpResponse.Err(
                    code: "InvalidIndexPayload",
                    message: "payload.attributes must be a non-empty array of attribute names.",
                    hint: "Example: { \"attributes\": [\"CountryName\"], \"unique\": true }.",
                    target: targetName);

                bool unique = json["unique"]?.ToObject<bool?>() ?? true;
                string requestedName = json["name"]?.ToString();
                IndexOrder order = string.Equals(json["order"]?.ToString(), "Descending", StringComparison.OrdinalIgnoreCase)
                    ? IndexOrder.Descending : IndexOrder.Ascending;

                var model = tbl.Model;

                // Resolve the attributes up front so a typo fails cleanly before any mutation.
                var attributes = new List<Artech.Genexus.Common.Objects.Attribute>();
                foreach (var a in attrArr)
                {
                    string an = a?.ToString();
                    if (string.IsNullOrWhiteSpace(an)) continue;
                    var att = Artech.Genexus.Common.Objects.Attribute.Get(model, an);
                    if (att == null) return Models.McpResponse.Err(
                        code: "AttributeNotFound",
                        message: $"Attribute '{an}' does not exist in the KB.",
                        hint: "Index members must be existing attributes. Check spelling / that the attribute is defined.",
                        target: targetName);
                    attributes.Add(att);
                }

                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        var index = Index.Create(model);
                        index.IndexType = unique ? IndexType.Unique : IndexType.Duplicate;
                        index.Source = IndexSource.User;

                        foreach (var att in attributes)
                        {
                            var member = new IndexMember(index.IndexStructure) { Attribute = att, Order = order };
                            index.IndexStructure.Members.Add(member);
                        }

                        // Index.Table is read-only; the table association is established by adding the
                        // index to the table's TableIndexesPart. Do this before naming/save so
                        // CreateIndexName and validation see the owning table.
                        dynamic tableIndexes = ((dynamic)tbl).TableIndexes;
                        tableIndexes.AddIndex(index);

                        if (!string.IsNullOrWhiteSpace(requestedName)) index.Name = requestedName;
                        else index.CreateIndexName();

                        index.EnsureSave();
                        tbl.EnsureSave();

                        sdkTrans.Commit();

                        return Models.McpResponse.Ok(
                            target: targetName,
                            code: "IndexCreated",
                            result: new JObject
                            {
                                ["indexName"] = index.Name,
                                ["table"] = tbl.Name,
                                ["indexType"] = index.IndexType.ToString(),
                                ["source"] = index.Source.ToString(),
                                ["attributes"] = new JArray(attributes.Select(a => (JToken)a.Name)),
                                ["note"] = "Run genexus_lifecycle action=reorg to apply the unique constraint to the physical database."
                            });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "IndexCreateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace. Verify the attributes belong to the transaction's table.",
                            target: targetName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "IndexCreateFailed",
                    message: ex.Message,
                    hint: "Ensure the target exists and payload is valid JSON { attributes:[...], unique:true }.",
                    target: targetName);
            }
        }

        // issue #39: drop a user-defined index (pairs with create_index). A GeneXus index is a
        // KBObject, so removal is Index.Delete(). payload = { indexName: "IX..." }.
        public string DropIndex(string targetName, string payload)
        {
            try
            {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return HealingService.FormatNotFoundError(targetName, _objectService.GetKbService().GetIndexCache().GetIndex());

                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;
                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Index drop requires a Transaction (or Table) with a physical table.",
                    target: targetName);

                string indexName = null;
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    try { indexName = JObject.Parse(payload)["indexName"]?.ToString(); } catch { }
                }
                if (string.IsNullOrWhiteSpace(indexName)) return Models.McpResponse.Err(
                    code: "InvalidIndexPayload",
                    message: "payload.indexName is required.",
                    hint: "Pass { \"indexName\": \"IX...\" }. Read genexus_structure action=get_indexes to see names (only source=User indexes can be dropped).",
                    target: targetName);

                // Locate the TableIndex by name and confirm it is user-defined before deleting.
                dynamic tableIndexes = ((dynamic)tbl).TableIndexes;
                Index target = null;
                bool isUser = false;
                if (tableIndexes != null && tableIndexes.Indexes != null)
                {
                    foreach (dynamic ti in tableIndexes.Indexes)
                    {
                        dynamic idx = ti.Index;
                        if (idx == null) continue;
                        if (string.Equals((string)idx.Name, indexName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = idx as Index;
                            try { isUser = idx.Source != null && idx.Source.ToString().Contains("User"); } catch { }
                            break;
                        }
                    }
                }

                if (target == null) return Models.McpResponse.Err(
                    code: "IndexNotFound",
                    message: $"Index '{indexName}' not found on table '{tbl.Name}'.",
                    hint: "Use genexus_structure action=get_indexes to list index names.",
                    target: targetName);

                if (!isUser) return Models.McpResponse.Err(
                    code: "IndexNotUserDefined",
                    message: $"Index '{indexName}' is SDK-generated (Source=Automatic) and cannot be dropped.",
                    hint: "Only user-defined indexes (source=User, e.g. from create_index) can be dropped. Automatic indexes are managed by GeneXus from the data model.",
                    target: targetName);

                // A GeneXus index is a self-contained KBObject; KBObject.Delete() persists on its
                // own (same pattern as genexus_delete_object) — do NOT wrap it in a table save,
                // which would re-persist the table with the index still attached and resurrect it.
                try
                {
                    target.Delete();
                    return Models.McpResponse.Ok(
                        target: targetName,
                        code: "IndexDropped",
                        result: new JObject
                        {
                            ["indexName"] = indexName,
                            ["table"] = tbl.Name,
                            ["note"] = "Run genexus_lifecycle action=reorg to drop the constraint from the physical database."
                        });
                }
                catch (Exception ex)
                {
                    return Models.McpResponse.Err(
                        code: "IndexDropFailed",
                        message: ex.Message,
                        hint: "Check the worker log for the SDK stack trace.",
                        target: targetName,
                        extra: new JObject { ["stackTrace"] = ex.StackTrace });
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "IndexDropFailed",
                    message: ex.Message,
                    hint: "Ensure the target exists and payload is { indexName: \"...\" }.",
                    target: targetName);
            }
        }

        public string GetVisualIndexes(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return Models.McpResponse.Err(
                    code: "ObjectNotFound",
                    message: "Object not found.",
                    hint: "The requested object is not available in the active Knowledge Base.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_search",
                        args: new JObject { ["query"] = targetName },
                        why: "Search for objects matching the name to find the correct identifier.")),
                    target: targetName);

                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;

                if (tbl == null) return Models.McpResponse.Err(
                    code: "AssociatedTableNotFound",
                    message: "Associated table not found.",
                    hint: "The requested object does not expose a physical table structure for index inspection.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = targetName },
                        why: "Inspect the object to confirm whether it has an associated table.")),
                    target: targetName,
                    extra: new JObject { ["objectName"] = obj.Name, ["objectType"] = obj.TypeDescriptor?.Name });

                var result = new JObject();
                result["name"] = tbl.Name;
                var indexes = new JArray();
                dynamic dIndexesPart = ((dynamic)tbl).TableIndexes;
                if (dIndexesPart != null && dIndexesPart.Indexes != null) {
                    foreach (dynamic idxObj in dIndexesPart.Indexes) {
                        dynamic idx = idxObj.Index; if (idx == null) continue;
                        var indexItem = new JObject();
                        indexItem["name"] = idx.Name;

                        string typeStr = idx.IndexType != null ? idx.IndexType.ToString() : "";
                        bool isPrimary = typeStr.Contains("Primary");
                        indexItem["isPrimary"] = isPrimary;
                        indexItem["isUnique"] = typeStr.Contains("Unique") || isPrimary;
                        // issue #39: expose Source so callers can tell user-defined indexes
                        // (droppable via drop_index) apart from SDK-generated ones.
                        try { indexItem["source"] = idx.Source != null ? idx.Source.ToString() : ""; }
                        catch { indexItem["source"] = ""; }

                        var attrs = new JArray();
                        if (idx.IndexStructure != null && idx.IndexStructure.Members != null) {
                            foreach (dynamic m in idx.IndexStructure.Members) {
                                var attrObj = new JObject();
                                attrObj["name"] = m.Attribute != null ? m.Attribute.Name : m.Name;
                                try {
                                    attrObj["isAscending"] = m.Order.ToString().Contains("Ascending");
                                } catch {
                                    attrObj["isAscending"] = true;
                                }
                                attrs.Add(attrObj);
                            }
                        }
                        indexItem["attributes"] = attrs;
                        indexes.Add(indexItem);
                    }
                }
                result["indexes"] = indexes;
                return Models.McpResponse.Ok(target: targetName, code: "IndexesRead", result: result);
            } catch (Exception ex) {
                return Models.McpResponse.Err(
                    code: "IndexesReadFailed",
                    message: ex.Message,
                    hint: "Inspect the worker log; the table index metadata may not be accessible for this object.",
                    nextSteps: new JArray(Models.McpResponse.NextStep(
                        tool: "genexus_inspect",
                        args: new JObject { ["name"] = targetName },
                        why: "Inspect the object to confirm its structure is accessible before retrying.")),
                    target: targetName);
            }
        }
    }
}
