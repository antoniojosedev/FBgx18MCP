using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using System.Reflection;

namespace GxMcp.Worker.Parsers
{
    public class SdtDslParser : IDslParser
    {
        private static readonly Guid SDT_STRUCTURE_PART = Guid.Parse("8597371d-1941-4c12-9c17-48df9911e2f3");

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                KBObject sdt = obj;
                KBObjectPart structure = null;
                
                // Find structure part: match by descriptor name, class name, or GUID
                foreach (KBObjectPart part in sdt.Parts)
                {
                    try {
                        string descName = part.TypeDescriptor?.Name ?? "";
                        string className = part.GetType().Name;
                        if (descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            part.Type == SDT_STRUCTURE_PART)
                        { 
                            structure = part; 
                            break; 
                        }
                    } catch { }
                }
                
                // Fallback: duck typing - any part with Root
                if (structure == null)
                {
                    foreach (KBObjectPart part in sdt.Parts)
                    {
                        try {
                            dynamic dp = part;
                            if (dp.Root != null)
                            { structure = part; break; }
                        } catch { }
                    }
                }

                if (structure != null)
                {
                    dynamic ds = structure;
                    // DEEP DISCOVERY
                    try {
                        var partProps = new List<string>();
                        foreach (var p in structure.GetType().GetProperties()) partProps.Add(p.Name);
                        Logger.Debug($"[SDT PART DISCOVERY] Type: {structure.GetType().FullName} | Props: {string.Join(",", partProps)}");
                    } catch { }

                    // Try to find the root level (Root or StructureRoot)
                    dynamic root = null;
                    try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }

                    if (root != null)
                    {
                        // Try all known collections for SDT levels
                        try { foreach (dynamic child in root.Items) SerializeLevel(child, sb, 0); } 
                        catch {
                            try { foreach (dynamic child in root.Children) SerializeLevel(child, sb, 0); }
                            catch {
                                try { foreach (dynamic child in root.InternalItems) SerializeLevel(child, sb, 0); }
                                catch { }
                            }
                        }
                    }
                }
                else
                {
                    Logger.Error($"SdtDslParser: Could not find structure part for SDT {obj.Name}");
                }
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase))
            {
                KBObjectPart structure = null;
                foreach (KBObjectPart p in obj.Parts)
                {
                    try
                    {
                        string descName = p.TypeDescriptor?.Name ?? "";
                        string className = p.GetType().Name;
                        if (p.Type == SDT_STRUCTURE_PART ||
                            descName.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            descName.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                            className.IndexOf("SDTStructure", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            structure = p;
                            break;
                        }
                    }
                    catch { }
                }

                if (structure == null)
                {
                    foreach (KBObjectPart p in obj.Parts)
                    {
                        try
                        {
                            dynamic dp = p;
                            if (dp.Root != null) { structure = p; break; }
                        }
                        catch { }
                    }
                }

                if (structure == null)
                {
                    Logger.Error("[SDT PARSE] Structure part not found for " + obj.Name);
                    return;
                }

                // Diagnostic: log the first item's full property set so we can see what's missing
                try
                {
                    dynamic dsDbg = structure;
                    dynamic rootDbg = null;
                    try { rootDbg = dsDbg.Root; } catch { try { rootDbg = dsDbg.StructureRoot; } catch { } }
                    if (rootDbg != null)
                    {
                        foreach (dynamic item in rootDbg.Items)
                        {
                            try
                            {
                                var itemType = ((object)item).GetType();
                                var dump = new System.Text.StringBuilder();
                                foreach (var prop in itemType.GetProperties())
                                {
                                    try { object val = prop.GetValue(item); if (val != null) dump.Append(prop.Name + "=" + val + "; "); } catch { }
                                }
                                Logger.Info("[SDT ITEM DUMP] " + obj.Name + "/" + item.Name + ": " + dump.ToString());
                            }
                            catch { }
                            break;
                        }
                    }
                }
                catch { }

                Logger.Info("[SDT PARSE] Begin parse for " + obj.Name + " using part " + structure.GetType().Name);
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();
                var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                dynamic ds = structure;
                dynamic root = null;
                try { root = ds.Root; } catch { try { root = ds.StructureRoot; } catch { } }
                if (root == null) { Logger.Error("[SDT PARSE] Root not found for " + obj.Name); return; }
                SyncSDTNodes(root, parsedNodes);
            }
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            string collectionMarker = "";
            try { collectionMarker = level.IsCollection ? " Collection" : ""; } catch { }
            
            bool isLeaf = true;
            try { isLeaf = level.IsLeafItem; } catch { }
            
            if (!isLeaf)
            {
                sb.AppendLine($"{indentStr}{level.Name}{collectionMarker}");
                sb.AppendLine($"{indentStr}{{");
                try { foreach (dynamic child in level.Items) SerializeLevel(child, sb, indent + 1); } catch { }
                sb.AppendLine($"{indentStr}}}");
            }
            else
            {
                string typeStr = "Unknown";
                try { typeStr = level.Type != null ? level.Type.ToString() : "Unknown"; } catch { }
                sb.AppendLine($"{indentStr}{level.Name} : {typeStr}{collectionMarker}");
            }
        }

        private static object ResolveDbType(Type eDBTypeT, string typeStr)
        {
            if (eDBTypeT == null) return null;
            string name;
            if (string.IsNullOrEmpty(typeStr)) name = "VARCHAR";
            else if (typeStr.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase)) name = "NUMERIC";
            else if (typeStr.StartsWith("Char", StringComparison.OrdinalIgnoreCase)) name = "CHARACTER";
            else if (typeStr.StartsWith("Varchar", StringComparison.OrdinalIgnoreCase)) name = "VARCHAR";
            else if (typeStr.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) name = "DATE";
            else if (typeStr.StartsWith("Bool", StringComparison.OrdinalIgnoreCase)) name = "Boolean";
            else if (typeStr.StartsWith("LongVarchar", StringComparison.OrdinalIgnoreCase)) name = "LONGVARCHAR";
            else name = typeStr.ToUpperInvariant();
            try { return Enum.Parse(eDBTypeT, name, true); }
            catch { try { return Enum.Parse(eDBTypeT, "VARCHAR", true); } catch { return null; } }
        }

        private void SyncSDTNodes(dynamic node, List<DslParserUtils.ParsedNode> parsedNodes)
        {
            // REFLECTION DISCOVERY (Log once per session)
            try {
                var props = new List<string>();
                foreach (PropertyInfo p in node.GetType().GetProperties()) props.Add(p.Name);
                var methods = new List<string>();
                foreach (MethodInfo m in node.GetType().GetMethods()) methods.Add(m.Name);
                Logger.Debug($"[SDT DISCOVERY] Node: {node.GetType().FullName} | Props: {string.Join(",", props)} | Methods: {string.Join(",", methods)}");
            } catch { }

            // Try to find the collection of items (Items or Children or Levels or Elements)
            dynamic items = null;
            try { items = node.Items; } catch { try { items = node.Children; } catch { try { items = node.Levels; } catch { try { items = node.Elements; } catch { } } } }
            if (items == null) return;

            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic child in items) { existingItems[child.Name] = child; }

            var toRemove = new List<dynamic>();
            foreach (dynamic child in items) {
                if (!parsedNodes.Any(p => p.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase))) toRemove.Add(child);
            }
            foreach (dynamic dead in toRemove) { try { items.Remove(dead); } catch {} }

            foreach (var pNode in parsedNodes)
            {
                dynamic targetChild = null;
                if (existingItems.TryGetValue(pNode.Name, out var existing)) targetChild = existing;
                else
                {
                    // Discovery for SDTItem/SDTLevel type
                    Type sdtItemType = null;
                    string[] namespaces = { "Artech.Genexus.Common.Parts", "Artech.Genexus.Common.Objects", "Artech.Genexus.Common" };
                    foreach (var ns in namespaces) {
                        sdtItemType = node.GetType().Assembly.GetType($"{ns}.SDTItem") ?? 
                                      node.GetType().Assembly.GetType($"{ns}.SDTLevel");
                        if (sdtItemType != null) break;
                    }

                    bool added = false;
                    try
                    {
                        Type nodeType = ((object)node).GetType();
                        if (pNode.IsCompound)
                        {
                            MethodInfo addLevel = nodeType.GetMethod("AddLevel", new Type[] { typeof(string) })
                                                ?? nodeType.GetMethods().FirstOrDefault(m => m.Name == "AddLevel" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                            if (addLevel != null)
                            {
                                targetChild = addLevel.Invoke(node, new object[] { pNode.Name });
                                added = (targetChild != null);
                            }
                        }
                        else
                        {
                            Type eDBTypeT = nodeType.Assembly.GetType("Artech.Genexus.Common.eDBType");
                            object dbType = ResolveDbType(eDBTypeT, pNode.TypeStr);
                            MethodInfo addItem = nodeType.GetMethod("AddItem", new Type[] { typeof(string), eDBTypeT });
                            if (addItem != null && eDBTypeT != null && dbType != null)
                            {
                                targetChild = addItem.Invoke(node, new object[] { pNode.Name, dbType });
                                added = (targetChild != null);
                            }
                            else
                            {
                                var sigs = string.Join("; ", nodeType.GetMethods().Where(m => m.Name == "AddItem").Select(m => "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
                                Logger.Error("[SDT PARSE] AddItem(string, eDBType) not invokable. eDBType=" + (eDBTypeT?.FullName ?? "null") + " resolved=" + (dbType?.ToString() ?? "null") + " Sigs=[" + sigs + "]");
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Error("[SDT PARSE] AddItem/AddLevel('" + pNode.Name + "') failed: " + (ex.InnerException?.Message ?? ex.Message)); }

                    if (!added && sdtItemType != null)
                    {
                        object[][] ctorArgVariants = new object[][] {
                            new object[] { node },
                            new object[] { },
                            new object[] { items }
                        };
                        foreach (var args in ctorArgVariants)
                        {
                            try { targetChild = Activator.CreateInstance(sdtItemType, args); if (targetChild != null) { added = true; break; } }
                            catch { targetChild = null; }
                        }
                        if (added)
                        {
                            try { targetChild.Name = pNode.Name; items.Add(targetChild); }
                            catch (Exception ex) { Logger.Error("[SDT PARSE] Manual add fallback failed: " + ex.Message); added = false; }
                        }
                    }

                    if (!added)
                    {
                        Logger.Error("[SDT PARSE] Could not add item '" + pNode.Name + "'");
                    }
                }

                if (targetChild != null)
                {
                    try { targetChild.IsCollection = pNode.IsCollection; } catch { }
                    if (pNode.IsCompound) SyncSDTNodes(targetChild, pNode.Children);
                    else
                    {
                        try {
                            Type eDBType = targetChild.GetType().Assembly.GetType("Artech.Genexus.Common.eDBType");
                            if (pNode.TypeStr.StartsWith("Numeric", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "NUMERIC");
                            else if (pNode.TypeStr.StartsWith("Char", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                            else if (pNode.TypeStr.StartsWith("Date", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "DATE");
                            else if (pNode.TypeStr.StartsWith("Bool", StringComparison.OrdinalIgnoreCase)) targetChild.Type = Enum.Parse(eDBType, "Boolean");
                            else targetChild.Type = Enum.Parse(eDBType, "VARCHAR");
                        } catch { }
                    }
                }
            }
        }
    }
}
