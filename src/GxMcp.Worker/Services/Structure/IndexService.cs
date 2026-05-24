using System;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
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

        public string GetVisualIndexes(string targetName)
        {
            try {
                var obj = _objectService.FindObject(targetName);
                if (obj == null) return Models.McpResponse.Error("Object not found", targetName, "Indexes", "The requested object is not available in the active Knowledge Base.");
                
                Table tbl = null;
                if (obj is Table t) tbl = t;
                else if (obj is Transaction trn) tbl = trn.Structure.Root.AssociatedTable;
                
                if (tbl == null) return Models.McpResponse.Error("Associated table not found", targetName, "Indexes", "The requested object does not expose a physical table structure for index inspection.", obj.Name, obj.TypeDescriptor?.Name);

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
                return result.ToString();
            } catch (Exception ex) {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }
    }
}
