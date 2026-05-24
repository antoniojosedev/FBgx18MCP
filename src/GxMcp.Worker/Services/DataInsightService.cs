using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class DataInsightService
    {
        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly NavigationService _navigationService;
        private readonly PatternAnalysisService _patternAnalysisService;

        public DataInsightService(KbService kbService, ObjectService objectService, NavigationService navigationService, PatternAnalysisService patternAnalysisService)
        {
            _kbService = kbService;
            _objectService = objectService;
            _navigationService = navigationService;
            _patternAnalysisService = patternAnalysisService;
        }

        public string GetTableDDL(string target, bool includeSubordinated = false)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Error("Object not found", target, null, "The requested object is not available in the active Knowledge Base.");

                Table tbl = null;
                Transaction trnObj = null;
                if (obj is Transaction trn)
                {
                    trnObj = trn;
                    tbl = _objectService.FindObject(trn.Name) as Table;
                }
                else if (obj is Table)
                {
                    tbl = obj as Table;
                }

                if (tbl == null) return Models.McpResponse.Error("Associated table not found", target, null, "The target object does not resolve to a Transaction or Table with physical structure.");

                dynamic kb = _kbService.GetKB();
                var model = kb.DesignModel.Environment.TargetModel;
                int dbmsType = 7; // Force Oracle by default for this environment
                try {
                    dynamic ds = ((dynamic)model).DataStore;
                    if (ds != null && ds.Dbms != 0) dbmsType = ds.Dbms;
                } catch {}

                var result = new JObject();
                result["tableName"] = tbl.Name;
                result["description"] = tbl.Description;
                
                try {
                    result["dbms"] = dbmsType.ToString(); // Simplified for resilience
                } catch {}

                // 1. Try Native SQL from Reorganization folder
                string nativeSql = TryGetNativeSql(tbl);
                if (!string.IsNullOrEmpty(nativeSql))
                {
                    result["ddl"] = nativeSql;
                    result["source"] = "Native (reorg.sql)";
                }
                else
                {
                    // 2. Fallback: Generate SQL manually from structure
                    result["ddl"] = GenerateHeuristicSql(tbl, dbmsType);
                    result["source"] = "Heuristic (SDK Structure)";
                }

                // Subordinated Levels enumeration (for Transactions only)
                if (trnObj != null)
                {
                    var subTables = ResolveSubordinatedTables(trnObj);
                    var subNames = new JArray();
                    foreach (var s in subTables) subNames.Add(s.Name);
                    result["subordinatedTables"] = subNames;

                    if (includeSubordinated && subTables.Count > 0)
                    {
                        var ddlMap = new JObject();
                        foreach (var s in subTables)
                        {
                            string subDdl;
                            string subNativeSql = TryGetNativeSql(s);
                            if (!string.IsNullOrEmpty(subNativeSql)) subDdl = subNativeSql;
                            else subDdl = GenerateHeuristicSql(s, dbmsType);
                            ddlMap[s.Name] = subDdl;
                        }
                        result["subordinatedDDL"] = ddlMap;
                    }
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private List<Table> ResolveSubordinatedTables(Transaction trn)
        {
            var result = new List<Table>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trn.Name };

            try
            {
                var stack = new Stack<dynamic>();
                try
                {
                    dynamic root = trn.Structure.Root;
                    if (root?.Levels != null)
                        foreach (dynamic child in root.Levels) stack.Push(child);
                }
                catch { return result; }

                while (stack.Count > 0)
                {
                    dynamic level = stack.Pop();

                    // Resolve the physical table for this level. Try common SDK property names.
                    string tableName = null;
                    try { tableName = (string)level.AssociatedTableName; } catch { }
                    if (string.IsNullOrEmpty(tableName))
                        try { tableName = (string)level.AssociatedTable?.Name; } catch { }
                    if (string.IsNullOrEmpty(tableName))
                        try { tableName = (string)level.Name; } catch { }

                    if (!string.IsNullOrEmpty(tableName) && seen.Add(tableName))
                    {
                        if (_objectService.FindObject(tableName) is Table t)
                            result.Add(t);
                    }

                    try
                    {
                        if (level.Levels != null)
                            foreach (dynamic child in level.Levels) stack.Push(child);
                    }
                    catch { }
                }
            }
            catch { }

            return result;
        }

        private string TryGetNativeSql(Table tbl)
        {
            return null; // For now, heuristic is more flexible.
        }

        private string GenerateHeuristicSql(Table tbl, int dbmsType)
        {
            bool isOracle = dbmsType == 7; // DbmsType.Oracle
            string quoteStart = isOracle ? "" : "[";
            string quoteEnd = isOracle ? "" : "]";

            string dataTablespace = "";
            string indexTablespace = "";

            if (isOracle)
            {
                try {
                    dynamic kb = _kbService.GetKB();
                    dynamic ds = ((dynamic)kb.DesignModel.Environment.TargetModel).DataStore;
                    dataTablespace = ds.Properties.GetPropertyValue("DefaultTablesStorageArea") ?? "";
                    indexTablespace = ds.Properties.GetPropertyValue("DefaultIndicesStorageArea") ?? "";
                    
                    if (string.IsNullOrEmpty(dataTablespace)) dataTablespace = "TBS_DAD_ACADEMICO_GNX";
                    if (string.IsNullOrEmpty(indexTablespace)) indexTablespace = "TBS_IDX_ACADEMICO_GNX";
                } catch {
                    dataTablespace = "TBS_DAD_ACADEMICO_GNX";
                    indexTablespace = "TBS_IDX_ACADEMICO_GNX";
                }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"CREATE TABLE {quoteStart}{tbl.Name}{quoteEnd} (");
            
            var cols = new List<string>();
            foreach (var attr in tbl.TableStructure.Attributes)
            {
                string typeStr = MapGxTypeToSql(attr.Attribute, dbmsType);
                bool isNullable = attr.IsNullable == TableAttribute.IsNullableValue.True;
                string nullStr = isNullable ? "" : " NOT NULL";
                
                cols.Add($"  {quoteStart}{attr.Name.PadRight(24)}{quoteEnd} {typeStr}{nullStr}");
            }

            // Primary Key
            var pkAttrs = tbl.TableStructure.Attributes.Where(a => a.IsKey).Select(a => $"{quoteStart}{a.Name}{quoteEnd}");
            if (pkAttrs.Any())
            {
                string pkPart = $"  PRIMARY KEY ({string.Join(", ", pkAttrs)})";
                if (isOracle && !string.IsNullOrEmpty(indexTablespace))
                {
                    pkPart += Environment.NewLine + "             USING INDEX" + Environment.NewLine + "             TABLESPACE " + indexTablespace;
                }
                cols.Add(pkPart);
            }

            sb.AppendLine(string.Join("," + Environment.NewLine, cols));
            
            if (isOracle && !string.IsNullOrEmpty(dataTablespace))
            {
                sb.AppendLine(")");
                sb.Append("  TABLESPACE " + dataTablespace);
            }
            else
            {
                sb.Append(")");
            }
            
            return sb.ToString();
        }

        private string MapGxTypeToSql(Artech.Genexus.Common.Objects.Attribute attr, int dbmsType)
        {
            bool isOracle = dbmsType == 7;
            string typeName = attr.Type.ToString();

            if (typeName.Contains("NUMERIC")) {
                if (isOracle) return $"NUMERIC({attr.Length}{ (attr.Decimals > 0 ? "," + attr.Decimals : "") })";
                if (attr.Decimals > 0) return $"DECIMAL({attr.Length}, {attr.Decimals})";
                if (attr.Length > 9) return "BIGINT";
                return "INT";
            }
            if (typeName.Contains("CHARACTER") || typeName.Contains("VARCHAR")) {
                return isOracle ? $"VARCHAR2({attr.Length})" : $"NVARCHAR({attr.Length})";
            }
            if (typeName.Contains("DATE")) return "DATE";
            
            return isOracle ? "VARCHAR2(4000)" : "NVARCHAR(MAX)";
        }

        public string GetDataContext(string target)
        {
            try
            {
                var obj = _objectService.FindObject(target);
                if (obj == null) return Models.McpResponse.Error("Object not found", target, null, "The requested object is not available in the active Knowledge Base.");

                var result = new JObject();
                result["objectName"] = obj.Name;
                result["objectType"] = obj.TypeDescriptor.Name;

                var tableSchemas = new JObject();
                var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                // Get Tables used via Navigation Report
                string navJson = _navigationService.GetNavigation(target);
                if (!navJson.Contains("\"error\""))
                {
                    var nav = JObject.Parse(navJson);
                    var levels = nav["levels"] as JArray;
                    if (levels != null)
                    {
                        foreach (var lvl in levels)
                        {
                            string tblName = lvl["baseTable"]?.ToString();
                            if (!string.IsNullOrEmpty(tblName)) tableNames.Add(tblName);
                        }
                    }
                }

                // Fallback: Check Direct References
                if (tableNames.Count == 0)
                {
                    foreach (var reference in obj.GetReferences())
                    {
                        try {
                            dynamic kb = _kbService.GetKB();
                            var refObj = kb.DesignModel.Objects.Get(reference.To);
                            if (refObj is Table tblRef) tableNames.Add(tblRef.Name);
                        } catch {}
                    }
                }

                foreach (var tblName in tableNames)
                {
                    var tbl = _objectService.FindObject(tblName) as Table;
                    if (tbl != null) tableSchemas[tblName] = GetTableStructure(tbl);
                }
                result["dataSchema"] = tableSchemas;
                result["variables"] = GetVariables(obj);

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        private JObject GetTableStructure(Table tbl)
        {
            var res = new JObject();
            res["description"] = tbl.Description;
            
            var columns = new JArray();
            foreach (var attr in tbl.TableStructure.Attributes)
            {
                var col = new JObject();
                col["name"] = attr.Name;
                col["isKey"] = attr.IsKey;
                col["type"] = attr.Attribute.Type.ToString();
                col["length"] = attr.Attribute.Length;
                col["decimals"] = attr.Attribute.Decimals;
                col["isNullable"] = attr.IsNullable.ToString();
                columns.Add(col);
            }
            res["columns"] = columns;
            return res;
        }

        private JArray GetVariables(KBObject obj)
        {
            var vars = new JArray();
            var part = obj.Parts.Get<VariablesPart>();
            if (part != null)
            {
                foreach (var v in part.Variables)
                {
                    var varObj = new JObject();
                    varObj["name"] = v.Name;
                    varObj["type"] = v.Type.ToString();
                    varObj["length"] = v.Length;
                    varObj["decimals"] = v.Decimals;
                    varObj["isCollection"] = v.IsCollection;
                    vars.Add(varObj);
                }
            }
            return vars;
        }
    }
}
