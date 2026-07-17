using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Parsers
{
    // issue #36.1 — raised when the SDK could not remove one or more attributes that a
    // Structure write intended to drop (mode:full replacement, or a remove_attribute op).
    // The interceptor turns this into a StructureAttributeNotRemoved error so the caller
    // never sees a false success for an additive-only no-op.
    public class StructureRemovalException : Exception
    {
        public IReadOnlyList<string> Failures { get; }
        public StructureRemovalException(IReadOnlyList<string> failures)
            : base("Could not remove attribute(s): " + string.Join("; ", failures))
        {
            Failures = failures;
        }
    }

    public class TransactionDslParser : IDslParser
    {
        // Accumulates removal failures across all (recursive) SyncTransactionNodes calls;
        // checked once after the top-level sync in Parse(). A fresh parser is created per
        // ParseFromText call (StructureParser.GetParser), so this is never shared.
        private readonly List<string> _removalFailures = new List<string>();

        public void Serialize(KBObject obj, StringBuilder sb)
        {
            if (obj is Transaction trn)
            {
                SerializeLevel(trn.Structure.Root, sb, 0);
            }
        }

        public void Parse(KBObject obj, string text)
        {
            if (obj is Transaction trn)
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToList();
                var parsedNodes = DslParserUtils.ParseLinesIntoNodes(lines);
                
                var targetNodes = parsedNodes;
                if (parsedNodes.Count == 1 && parsedNodes[0].IsCompound)
                {
                    targetNodes = parsedNodes[0].Children;
                }

                SyncTransactionNodes(trn.Structure.Root, targetNodes, obj.Model);

                if (_removalFailures.Count > 0)
                    throw new StructureRemovalException(_removalFailures.AsReadOnly());
            }
        }

        private void SerializeLevel(dynamic level, StringBuilder sb, int indent)
        {
            string indentStr = new string(' ', indent * 4);
            if (indent > 0)
            {
                sb.AppendLine($"{indentStr}{level.Name}");
                sb.AppendLine($"{indentStr}{{");
            }

            if (level.Attributes != null)
            {
                foreach (dynamic attr in level.Attributes)
                {
                    string keyMarker = attr.IsKey ? "*" : "";
                    string typeStr = "Unknown";
                    try {
                        if (attr.Type != null) typeStr = attr.Type.ToString();
                    } catch {
                        try { if (attr.Attribute != null && attr.Attribute.Type != null) typeStr = attr.Attribute.Type.ToString(); } catch { }
                    }

                    try {
                        if (attr.Attribute != null) {
                            int len = attr.Attribute.Length;
                            int dec = attr.Attribute.Decimals;
                            if (len > 0) {
                                if (dec > 0) typeStr += $"({len},{dec})";
                                else typeStr += $"({len})";
                            }
                        }
                    } catch { }

                    string desc = "";
                    string formula = "";
                    bool isNullable = false;
                    try {
                        if (attr.Attribute != null) {
                            desc = attr.Attribute.Description?.ToString() ?? "";
                            formula = attr.Attribute.Formula?.ToString() ?? "";
                            dynamic pNullable = attr.Attribute.Properties.Get("Nullable");
                            if (pNullable != null) {
                                string nVal = pNullable.ToString();
                                isNullable = nVal.Equals("Yes", StringComparison.OrdinalIgnoreCase) || nVal.Equals("Nullable", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    } catch { }

                    var lineElements = new List<string>();
                    lineElements.Add(string.Format("{0}{1} : {2}", attr.Name, keyMarker, typeStr));
                    if (!string.IsNullOrEmpty(desc) && !desc.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)) lineElements.Add(string.Format("\"{0}\"", desc));
                    if (!string.IsNullOrEmpty(formula)) lineElements.Add(string.Format("[Formula: {0}]", formula));
                    if (isNullable) lineElements.Add("[Nullable]");

                    string extraInfo = lineElements.Count > 1 ? " // " + string.Join(", ", lineElements.Skip(1)) : "";
                    sb.AppendLine(string.Format("{0}{1}{2}{3}", indentStr, indent > 0 ? "    " : "", lineElements[0], extraInfo));
                }
            }

            if (level.Levels != null)
            {
                foreach (dynamic subLevel in level.Levels) SerializeLevel(subLevel, sb, indent + 1);
            }

            if (indent > 0) sb.AppendLine($"{indentStr}}}");
        }

        private void SyncTransactionNodes(dynamic sdkLevel, List<DslParserUtils.ParsedNode> parsedNodes, KBModel model)
        {
            var existingItems = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            if (sdkLevel.Attributes != null) { foreach (dynamic attr in sdkLevel.Attributes) existingItems[attr.Name] = attr; }

            foreach (var pNode in parsedNodes)
            {
                if (pNode.IsCompound)
                {
                    dynamic targetSubLevel = null;
                    if (sdkLevel.Levels != null) {
                        foreach (dynamic subLvl in sdkLevel.Levels) { if (subLvl.Name.Equals(pNode.Name, StringComparison.OrdinalIgnoreCase)) { targetSubLevel = subLvl; break; } }
                    }

                    if (targetSubLevel == null)
                    {
                        // Use the (TransactionLevel parent) ctor + typed AddLevel(level) so the SDK
                        // wires the new level into the structure with bookkeeping EnsureSave honors.
                        // The previous Activator+Levels.Add path silently dropped new sub-levels —
                        // same shape as the BC attribute regression fixed in 8c8f433.
                        try {
                            targetSubLevel = new TransactionLevel(sdkLevel);
                            targetSubLevel.Name = pNode.Name;
                            sdkLevel.AddLevel(targetSubLevel);
                        } catch (Exception lvlEx) {
                            Logger.Error("[TransactionDslParser] Failed to add sub-level '" + pNode.Name + "': " + (lvlEx.InnerException?.Message ?? lvlEx.Message));
                            targetSubLevel = null;
                        }
                    }
                    if (targetSubLevel != null) SyncTransactionNodes(targetSubLevel, pNode.Children, model);
                }
                else
                {
                    if (existingItems.TryGetValue(pNode.Name, out var existing))
                    {
                        existing.IsKey = pNode.IsKey;
                        // Update existing attribute's type if DSL specifies one.
                        ApplyTypeFromDsl(existing, pNode.TypeStr, model);
                    }
                    else
                    {
                        try {
                            // Resolve (or create) the global Attribute first — the SDK's typed
                            // sdkLevel.AddAttribute(globalAttr) is the only path that links the new
                            // TransactionAttribute into the structure with the bookkeeping that
                            // EnsureSave honors. The previous Activator+Attributes.Add path silently
                            // dropped new items because the SDK's TransactionAttribute proxy needs
                            // to be created by the level itself.
                            var globalAttr = Artech.Genexus.Common.Objects.Attribute.Get(model, pNode.Name);
                            bool createdGlobal = false;
                            if (globalAttr == null)
                            {
                                try
                                {
                                    var attrGuid = KBObjectDescriptor.Get<Artech.Genexus.Common.Objects.Attribute>().Id;
                                    var newAttr = KBObject.Create(model, attrGuid);
                                    newAttr.Name = pNode.Name;
                                    globalAttr = newAttr as Artech.Genexus.Common.Objects.Attribute;
                                    if (globalAttr != null)
                                    {
                                        ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                                        newAttr.Save();
                                        createdGlobal = true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warn("[TransactionDslParser] global Attribute create failed for '" + pNode.Name + "': " + ex.Message);
                                    globalAttr = null;
                                }
                            }

                            if (globalAttr == null)
                            {
                                Logger.Error("[TransactionDslParser] cannot add attribute '" + pNode.Name + "' — no global Attribute available; skipping.");
                                continue;
                            }

                            dynamic trnAttr = sdkLevel.AddAttribute(globalAttr);
                            try { trnAttr.IsKey = pNode.IsKey; } catch { }

                            // If the global already existed, still apply the DSL type so changes
                            // like TokenUser : Numeric(4) → TokenUser : UserLogin propagate.
                            if (!createdGlobal && !string.IsNullOrEmpty(pNode.TypeStr))
                            {
                                ApplyTypeFromDsl(globalAttr, pNode.TypeStr, model);
                            }
                        } catch (Exception addEx) {
                            Logger.Error("[TransactionDslParser] Failed to add attribute '" + pNode.Name + "': " + (addEx.InnerException?.Message ?? addEx.Message));
                        }
                    }
                }
            }

            // issue #36.1 — removals run AFTER adds/updates so a key REPLACEMENT succeeds:
            // the new key already exists when the old one is dropped (the SDK refuses to
            // leave a transaction momentarily keyless). Failures are no longer swallowed —
            // a removal the SDK rejects (key still in use, referenced by an FK/relation/index)
            // is recorded and surfaced by Parse() as a hard error, instead of a silent merge
            // that leaves the attribute in place and reports success ("additive-only" bug).
            if (sdkLevel.Attributes != null)
            {
                var toRemove = new List<dynamic>();
                foreach (dynamic attr in sdkLevel.Attributes)
                {
                    if (!parsedNodes.Any(p => !p.IsCompound && p.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase)))
                        toRemove.Add(attr);
                }
                foreach (dynamic dead in toRemove)
                {
                    string deadName = dead.Name;
                    try { sdkLevel.Attributes.Remove(dead); }
                    catch (Exception remEx)
                    {
                        _removalFailures.Add(deadName + " (" + (remEx.InnerException?.Message ?? remEx.Message) + ")");
                        continue;
                    }
                    // Remove() can no-op WITHOUT throwing when the SDK refuses — verify it is gone.
                    bool stillPresent = false;
                    foreach (dynamic a in sdkLevel.Attributes)
                    {
                        if (((string)a.Name).Equals(deadName, StringComparison.OrdinalIgnoreCase)) { stillPresent = true; break; }
                    }
                    if (stillPresent)
                        _removalFailures.Add(deadName + " (SDK kept the attribute — it is likely a key in use, or referenced by a relation/index)");
                }
            }
        }

        private static void ApplyTypeFromDsl(dynamic trnAttrOrAttribute, string typeStr, KBModel model)
        {
            if (trnAttrOrAttribute == null || string.IsNullOrWhiteSpace(typeStr)) return;
            var spec = GxMcp.Worker.Helpers.AttributeTypeApplier.Parse(typeStr);
            if (!spec.Recognized) return;

            // Resolve to the underlying global Attribute. For TransactionAttribute the property is .Attribute;
            // for a raw Artech.Genexus.Common.Objects.Attribute it is itself.
            object globalAttr = trnAttrOrAttribute;
            try
            {
                // Walk the hierarchy to avoid AmbiguousMatchException when the SDK shadows
                // an inherited `Attribute` property on the derived TransactionAttribute.
                System.Reflection.PropertyInfo attrProp = null;
                Type tt = trnAttrOrAttribute.GetType();
                try { attrProp = tt.GetProperty("Attribute"); }
                catch (System.Reflection.AmbiguousMatchException)
                {
                    for (Type cur = tt; cur != null && attrProp == null; cur = cur.BaseType)
                    {
                        attrProp = cur.GetProperty("Attribute",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                    }
                }
                if (attrProp != null)
                {
                    var maybeAttr = attrProp.GetValue(trnAttrOrAttribute, null);
                    if (maybeAttr != null) globalAttr = maybeAttr;
                }
            }
            catch { }

            if (spec.CanonicalType == "DomainReference" && !string.IsNullOrEmpty(spec.DomainName))
            {
                try
                {
                    Artech.Genexus.Common.Objects.Domain domain = null;
                    foreach (var obj in model.Objects.GetByName(null, null, spec.DomainName))
                    {
                        if (obj is Artech.Genexus.Common.Objects.Domain d) { domain = d; break; }
                    }
                    if (domain != null)
                    {
                        GxMcp.Worker.Helpers.AttributeTypeApplier.ApplyDomain(globalAttr, domain);
                    }
                }
                catch { }
                return;
            }

            GxMcp.Worker.Helpers.AttributeTypeApplier.ApplyPrimitive(globalAttr, spec.CanonicalType, spec.Length, spec.Decimals);
        }
    }
}
