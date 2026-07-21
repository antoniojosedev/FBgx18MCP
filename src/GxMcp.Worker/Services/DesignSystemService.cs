using System;
using System.Collections;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Helpers;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;
using DSObject = Artech.Genexus.Common.Objects.DesignSystem;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// genexus_layout action=design_system — read a Design System Object's (DSO) tokens,
    /// theme classes, images and referenced DSOs, over <c>DesignSystemHelper</c> (P3, the one
    /// helper worth wiring). Read-only. Complements action=list_controls (control catalog):
    /// this is the token/class/image catalog an agent needs to author styled WWP/DSO layouts.
    ///
    /// <c>DesignSystemHelper</c> is instance-based (`new DesignSystemHelper(dso)`) — no service
    /// registry involved. The DSO comes from resolving a <c>DesignSystem</c> KB object by name
    /// (or the first one in the KB when no name is given).
    /// </summary>
    public class DesignSystemService
    {
        private readonly KbService _kb;
        private readonly ObjectService _objects;

        public DesignSystemService(KbService kb, ObjectService objects)
        {
            _kb = kb;
            _objects = objects;
        }

        public string Run(JObject args)
        {
            if (!KbModelGuard.TryGetDesignModel(_kb, out var model, out var kbErr))
                return kbErr;

            string name = args?["name"]?.ToString();
            DSObject dso = null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                try { dso = _objects?.FindObject(name, "DesignSystem") as DSObject; } catch { }
                if (dso == null)
                    return McpResponse.Err("ObjectNotFound", "Design System Object '" + name + "' not found.", "Check the name (genexus_query type:DesignSystem), or omit name to use the first DSO.", target: name);
            }
            else
            {
                // Fast path: the search index already buckets objects by type. Resolve the
                // first DesignSystem via TypeIndex["DesignSystem"] instead of a full-KB
                // COM scan (mirrors SearchService/ListService type-bucket lookups).
                try
                {
                    var index = _objects?.GetLoadedIndexOrNull();
                    if (index?.TypeIndex != null && index.Objects != null
                        && index.TypeIndex.TryGetValue("DesignSystem", out var dsKeys))
                    {
                        string firstKey = null;
                        lock (dsKeys) { foreach (var k in dsKeys) { firstKey = k; break; } }
                        if (firstKey != null && index.Objects.TryGetValue(firstKey, out var entry)
                            && !string.IsNullOrEmpty(entry?.Name))
                        {
                            dso = _objects.FindObject(entry.Name, "DesignSystem") as DSObject;
                            if (dso != null) name = entry.Name;
                        }
                    }
                }
                catch { /* fall through to the full scan below */ }

                // Fallback: cold/absent index → the original full-KB scan.
                if (dso == null)
                {
                    try
                    {
                        foreach (KBObject o in model.Objects.GetAll())
                        {
                            if (string.Equals(o?.TypeDescriptor?.Name, "DesignSystem", StringComparison.OrdinalIgnoreCase))
                            { dso = o as DSObject; if (dso != null) { name = o.Name; break; } }
                        }
                    }
                    catch { }
                }

                if (dso == null)
                    return McpResponse.Err("NoDesignSystem", "This KB has no Design System Object.", "DSOs are created in the GeneXus IDE; nothing to read.");
            }

            try
            {
                var helper = new DesignSystemHelper(dso);

                var tokens = new JObject();
                try
                {
                    var byGroup = helper.GetTokensNames();
                    if (byGroup != null)
                        foreach (var kv in byGroup)
                            tokens[kv.Key] = ToArray(kv.Value);
                }
                catch { }

                return McpResponse.Ok(
                    code: "DesignSystemRetrieved",
                    result: new JObject
                    {
                        ["designSystem"] = name,
                        ["tokenGroups"] = tokens,
                        ["classes"] = ToArray(SafeList(() => helper.GetClassesNames())),
                        ["images"] = ToArray(SafeList(() => helper.GetAllImagesNames())),
                        ["referencedDSOs"] = ToArray(SafeList(() => helper.GetAllDSOsNames())),
                        ["source"] = "sdk:DesignSystemHelper"
                    });
            }
            catch (Exception ex)
            {
                return McpResponse.Err("DesignSystemReadFailed", ex.Message, "Check the worker log for the full stack trace.");
            }
        }

        private static IEnumerable SafeList(Func<IEnumerable> f) { try { return f(); } catch { return null; } }

        private static JArray ToArray(IEnumerable items)
        {
            var arr = new JArray();
            if (items != null) foreach (var i in items) if (i != null) arr.Add(i.ToString());
            return arr;
        }
    }
}
