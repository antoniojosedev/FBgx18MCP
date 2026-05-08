using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using Artech.Architecture.Common.Services;
using GxMcp.Worker.Services;
using Artech.Genexus.Common.Objects;
using Artech.Common.Collections;
using Artech.Genexus.Common;

namespace GxMcp.Worker.Helpers
{
    public static class VariableInjector
    {
        private static readonly HashSet<string> StandardVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Pgmname", "Pgmdesc", "Today", "Time", "Mode", "Message", "EventName", "CtlName"
        };

        public static void InjectVariables(KBObject obj, string code, Models.SearchIndex index = null)
        {
            var variablesPart = obj.Parts.Get<VariablesPart>();
            if (variablesPart == null) return;

            var matches = System.Text.RegularExpressions.Regex.Matches(code, @"&(\w+)");
            var varNames = matches.Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            // Detect &var.Field usage — these vars are likely SDTs/BCs, not scalars
            var sdtCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"&(\w+)\."))
            {
                sdtCandidates.Add(m.Groups[1].Value);
            }

            foreach (var varName in varNames)
            {
                if (!variablesPart.Variables.Any(v => v.Name.Equals(varName, StringComparison.OrdinalIgnoreCase)))
                {
                    global::Artech.Genexus.Common.Variable v = CreateVariable(variablesPart, varName, index, sdtCandidates.Contains(varName));
                    if (v != null)
                    {
                        variablesPart.Variables.Add(v);
                        Logger.Info($"Injected variable: {varName} into {obj.Name}");
                    }
                }
            }
        }

        internal static global::Artech.Genexus.Common.Variable CreateVariable(VariablesPart part, string name, Models.SearchIndex index = null, bool sdtMemberAccessHint = false)
        {
            global::Artech.Genexus.Common.Variable v = new global::Artech.Genexus.Common.Variable(part);
            v.Name = name;

            // 0. SDT/BC heuristic: name starts with Sdt/SDT or used as &var.Field — try resolving as SDT first
            bool sdtNamePrefix = name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SDT", StringComparison.Ordinal);
            if (sdtMemberAccessHint || sdtNamePrefix)
            {
                // Try direct match by the variable name
                var tryNames = new List<string> { name };
                // Strip Sdt/SDT prefix and try suffix-only too
                if (name.StartsWith("Sdt", StringComparison.OrdinalIgnoreCase) && name.Length > 3) tryNames.Add(name.Substring(3));

                foreach (var candidateName in tryNames)
                {
                    var sdtObj = ResolveTypeObject(part.Model, candidateName);
                    if (sdtObj != null && sdtObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                    {
                        v.Type = global::Artech.Genexus.Common.eDBType.GX_SDT;
                        v.SetPropertyValue("DataType", sdtObj.Key);
                        try { v.SetPropertyValue("DataTypeString", sdtObj.Name); } catch { }
                        Logger.Info($"Injected variable {name} bound to SDT {sdtObj.Name} (heuristic: prefix={sdtNamePrefix}, memberAccess={sdtMemberAccessHint})");
                        return v;
                    }
                    if (sdtObj is Transaction bc && bc.IsBusinessComponent)
                    {
                        v.Type = global::Artech.Genexus.Common.eDBType.GX_BUSCOMP;
                        v.SetPropertyValue("DataType", sdtObj.Key);
                        try { v.SetPropertyValue("DataTypeString", sdtObj.Name); } catch { }
                        Logger.Info($"Injected variable {name} bound to BC {sdtObj.Name}");
                        return v;
                    }
                }
                // If member access hint but no matching SDT found, skip injection — better to leave undeclared so the user notices
                if (sdtMemberAccessHint && !sdtNamePrefix)
                {
                    Logger.Warn($"Variable {name} used with .Field access but no matching SDT/BC found. Skipping auto-inject.");
                    return null;
                }
            }

            // 1. Inherit from Attribute (FAST INDEX LOOKUP)
            if (index != null)
            {
                string key = "Attribute:" + name;
                if (index.Objects.TryGetValue(key, out var entry))
                {
                    if (TryParseDbType(entry.DataType, out var itype))
                    {
                        v.Type = itype;
                        v.Length = entry.Length;
                        v.Decimals = entry.Decimals;
                        Logger.Info($"Injected variable {name} inheriting from INDEXED attribute {name}");
                        return v;
                    }
                }
            }

            // Fallback (SDK lookup - only if not in index or index not provided)
            var attribute = FindAttribute(part.Model, name);
            if (attribute != null)
            {
                v.Type = attribute.Type;
                v.Length = attribute.Length;
                v.Decimals = attribute.Decimals;
                v.Signed = attribute.Signed;
                Logger.Info($"Injected variable {name} inheriting from SDK attribute {attribute.Name}");
                return v;
            }

            // 2. Naming Heuristics
            string lowerName = name.ToLower();

            // Boolean
            if (lowerName.StartsWith("is") || lowerName.StartsWith("has") || lowerName.StartsWith("flg") || 
                lowerName.Contains("ativo") || lowerName.Contains("pode") || lowerName.EndsWith("ok"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.Boolean;
                Logger.Info($"Injected Boolean variable: {name}");
                return v;
            }

            // Date / DateTime
            if (lowerName.EndsWith("data") || lowerName.EndsWith("dt") || lowerName.Contains("emissao") || lowerName.Contains("vencimento"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATE;
                Logger.Info($"Injected Date variable: {name}");
                return v;
            }
            if (lowerName.Contains("hora") || lowerName.Contains("timestamp") || lowerName.Contains("moment"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.DATETIME;
                Logger.Info($"Injected DateTime variable: {name}");
                return v;
            }

            // Numeric
            if (lowerName.EndsWith("id") || lowerName.EndsWith("seq") || lowerName.EndsWith("qtd") || 
                lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total"))
            {
                v.Type = global::Artech.Genexus.Common.eDBType.NUMERIC;
                v.Length = 10;
                v.Decimals = lowerName.Contains("valor") || lowerName.Contains("preco") || lowerName.Contains("total") ? 2 : 0;
                Logger.Info($"Injected Numeric variable: {name} ({v.Length},{v.Decimals})");
                return v;
            }

            // 3. Fallback: VarChar(100)
            v.Type = global::Artech.Genexus.Common.eDBType.VARCHAR;
            v.Length = 100;
            Logger.Info($"Injected Default VarChar variable: {name}");

            return v;
        }

        public static string GetVariablesAsText(KBObject obj)
        {
            var varPart = obj.Parts.Get<VariablesPart>();
            if (varPart == null) return string.Empty;
            return GetVariablesAsText(varPart);
        }

        public static string GetVariablesAsText(VariablesPart varPart)
        {
            var sb = new System.Text.StringBuilder();
            foreach (global::Artech.Genexus.Common.Variable v in varPart.Variables)
            {
                string typeRepr = ResolveTypeRepresentation(varPart.Model, v);
                string collectionSuffix = v.IsCollection ? " Collection" : "";
                sb.AppendLine(string.Format("&{0} : {1}{2}", v.Name, typeRepr, collectionSuffix));
            }
            return sb.ToString();
        }

        private static bool _loggedVarProps = false;

        private static string TryResolveBoundObjectName(global::Artech.Genexus.Common.Variable v, KBModel model)
        {
            if (model == null) return null;

            // One-time diagnostic dump to discover the actual property name carrying SDT/BC binding
            if (!_loggedVarProps && v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT)
            {
                try
                {
                    var dump = new System.Text.StringBuilder();
                    var propsProp = v.GetType().GetProperty("Properties");
                    if (propsProp != null)
                    {
                        var coll = propsProp.GetValue(v) as System.Collections.IEnumerable;
                        if (coll != null)
                        {
                            foreach (object p in coll)
                            {
                                try
                                {
                                    string name = (string)p.GetType().GetProperty("Name")?.GetValue(p);
                                    object val = p.GetType().GetProperty("Value")?.GetValue(p);
                                    if (val != null) dump.Append(name + "=" + val.GetType().Name + "[" + val.ToString() + "]; ");
                                }
                                catch { }
                            }
                        }
                    }
                    Logger.Info("[VAR INNER PROPS] " + v.Name + ": " + dump.ToString());
                    _loggedVarProps = true;
                }
                catch { _loggedVarProps = true; }
            }

            // Fast path: GX18 stores the SDT/BC name directly in DataTypeString
            try
            {
                object dts = v.GetPropertyValue("DataTypeString");
                if (dts is string dtsStr && !string.IsNullOrEmpty(dtsStr)) return dtsStr;
            }
            catch { }

            // Fallback: try known key-bearing properties
            string[] candidateProps = { "DataType", "DataTypeKey", "ItemType", "BasedOn", "BasedOnKey", "TypeKey", "ObjectKey", "DataItemTypeName" };
            foreach (var prop in candidateProps)
            {
                object value = null;
                try { value = v.GetPropertyValue(prop); } catch { continue; }
                if (value == null) continue;

                KBObject obj = TryGetObjectFromKey(model, value);
                if (obj != null) return obj.Name;
            }

            // Fallback: enumerate all properties of the variable and look for any value
            // that resolves to an object in the model
            try
            {
                foreach (var prop in v.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    object value;
                    try { value = prop.GetValue(v); } catch { continue; }
                    if (value == null) continue;
                    KBObject obj = TryGetObjectFromKey(model, value);
                    if (obj != null && (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)
                                      || (obj is Transaction trn && trn.IsBusinessComponent)))
                        return obj.Name;
                }
            }
            catch { }

            return null;
        }

        private static KBObject TryGetObjectFromKey(KBModel model, object key)
        {
            try
            {
                if (key is global::Artech.Udm.Framework.EntityKey ek) return model.Objects.Get(ek);
                if (key is Guid g) return model.Objects.Get(g);
                if (key is string s && Guid.TryParse(s, out var gp)) return model.Objects.Get(gp);
            }
            catch { }
            return null;
        }

        private static string ResolveTypeRepresentation(KBModel model, global::Artech.Genexus.Common.Variable v)
        {
            // Domain binding wins over raw type
            try
            {
                if (v.DomainBasedOn != null) return v.DomainBasedOn.Name;
            }
            catch { }

            // SDT or BC binding via DataType property (stored as object Key)
            if (v.Type == global::Artech.Genexus.Common.eDBType.GX_SDT || v.Type == global::Artech.Genexus.Common.eDBType.GX_BUSCOMP)
            {
                string boundName = TryResolveBoundObjectName(v, model);
                if (!string.IsNullOrEmpty(boundName)) return boundName;
                // Fallback: emit raw enum + length so user sees something is up
                return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
            }

            return string.Format("{0}({1}{2})", v.Type, v.Length, v.Decimals > 0 ? "," + v.Decimals : "");
        }

        public static void SetVariablesFromText(VariablesPart part, string text)
        {
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var seenVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines)
            {
                // Format: &Name : Type(Length,Decimals) [Collection]
                var match = System.Text.RegularExpressions.Regex.Match(line, @"&?(\w+)\s*:\s*([\w\.\-]+)(?:\s*\(\s*(\d+)(?:\s*,\s*(\d+))?\s*\))?(?:\s+(Collection))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string name = match.Groups[1].Value;
                    string typeStr = match.Groups[2].Value;
                    int length = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                    int decimals = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
                    bool isCollection = match.Groups[5].Success;

                    seenVars.Add(name);

                    var v = part.Variables.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (v == null)
                    {
                        v = new global::Artech.Genexus.Common.Variable(part);
                        v.Name = name;
                        part.Variables.Add(v);
                    }

                    v.IsCollection = isCollection;

                    // 1. Map string type to eDBType (including Aliases)
                    if (TryParseDbType(typeStr, out var dbType))
                    {
                        v.Type = dbType;
                        v.Length = length;
                        v.DomainBasedOn = null; 
                        v.SetPropertyValue("DataType", null); // Reset user type if it was set
                    }
                    else
                    {
                        // 2. Resolve as Domain, SDT, or BC
                        var targetObj = ResolveTypeObject(part.Model, typeStr);
                        if (targetObj != null)
                        {
                            if (targetObj is global::Artech.Genexus.Common.Objects.Domain dom)
                                v.DomainBasedOn = dom;
                            else if (targetObj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
                            {
                                v.Type = global::Artech.Genexus.Common.eDBType.GX_SDT;
                                v.SetPropertyValue("DataType", targetObj.Key);
                                try { v.SetPropertyValue("DataTypeString", targetObj.Name); } catch { }
                            }
                            else if (targetObj is global::Artech.Genexus.Common.Objects.Transaction trn && trn.IsBusinessComponent)
                            {
                                v.Type = global::Artech.Genexus.Common.eDBType.GX_BUSCOMP;
                                v.SetPropertyValue("DataType", targetObj.Key);
                                try { v.SetPropertyValue("DataTypeString", targetObj.Name); } catch { }
                            }
                            Logger.Info($"Resolved variable {name} type to {targetObj.TypeDescriptor.Name}: {targetObj.Name}");
                        }
                    }
                }
            }

            // Remove variables not in the text, except standard variables
            var toRemove = part.Variables
                .Where(v => !seenVars.Contains(v.Name) && !StandardVariables.Contains(v.Name))
                .ToList();

            foreach (var v in toRemove)
            {
                part.Variables.Remove(v);
                Logger.Info($"Removed variable {v.Name} (no longer in text)");
            }
        }

        public static bool TryParseDbType(string typeStr, out global::Artech.Genexus.Common.eDBType type)
        {
            // Type Aliases mapping
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Character", "VARCHAR" },
                { "VarChar", "VARCHAR" },
                { "Numeric", "NUMERIC" },
                { "Boolean", "Boolean" },
                { "Date", "DATE" },
                { "DateTime", "DATETIME" },
                { "Blob", "BLOB" },
                { "Image", "IMAGE" },
                { "Audio", "AUDIO" },
                { "Video", "VIDEO" },
                { "GUID", "GUID" },
                { "Geography", "GEOGRAPHY" }
            };

            if (aliases.TryGetValue(typeStr, out var mappedType))
            {
                typeStr = mappedType;
            }

            return Enum.TryParse<global::Artech.Genexus.Common.eDBType>(typeStr, true, out type);
        }

        public static KBObject ResolveTypeObject(KBModel model, string typeName)
        {
            try
            {
                foreach (var obj in model.Objects.GetByName(null, null, typeName))
                {
                    // Check for Domain
                    if (obj is global::Artech.Genexus.Common.Objects.Domain) return obj;
                    
                    // Check for SDT
                    if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)) return obj;

                    // Check for Transaction (as BC)
                    if (obj is Transaction trn && trn.IsBusinessComponent) return obj;
                }
            }
            catch { /* Ignore model errors */ }
            return null;
        }

        private static global::Artech.Genexus.Common.Objects.Attribute FindAttribute(global::Artech.Architecture.Common.Objects.KBModel model, string name)
        {
            try
            {
                foreach (var result in model.Objects.GetByName(null, null, name))
                {
                    if (result is global::Artech.Genexus.Common.Objects.Attribute attr) return attr;
                }
            }
            catch { /* Object not found or model access error */ }
            return null;
        }
    }
}
