using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave 3 — IDE "Save As..." parity. Clones a KBObject's parts under a new
    /// name (same type, same module/folder), optionally also cloning a linked
    /// WorkWithPlus pattern instance.
    ///
    /// All SDK access is hidden behind <see cref="IObjectCloner"/> so the
    /// service can be unit-tested without a live KB. The production cloner
    /// (<see cref="SdkObjectCloner"/>) wires through the existing
    /// ObjectService / WriteService / PatternApplyService code paths so we
    /// preserve every IDE-compatibility fix already living there.
    /// </summary>
    public class SaveAsService
    {
        /// <summary>
        /// Seam used by tests. Production code uses <see cref="SdkObjectCloner"/>.
        /// All methods return a JSON string (Success/Error envelope) to mirror
        /// the rest of the worker service surface.
        /// </summary>
        public interface IObjectCloner
        {
            /// <summary>Resolve a source object by name (+ optional type). Returns null when not found.</summary>
            SourceDescriptor FindSource(string name, string typeFilter);

            /// <summary>Returns true when an object with that name already exists in the KB.</summary>
            bool TargetExists(string newName);

            /// <summary>Create a new empty object of the given type and host name. Returns Success/Error envelope JSON.</summary>
            string CreateObject(string type, string newName);

            /// <summary>Clone a single part's content from source → target. Returns Success/Error envelope JSON.</summary>
            string ClonePart(string sourceName, string newName, string partName, string typeFilter);

            /// <summary>Return the WorkWithPlus pattern instance bound to the source, or null when none.</summary>
            PatternInstanceDescriptor FindWwpInstance(string sourceName);

            /// <summary>Apply WorkWithPlus pattern to the new host. Returns Success/Error envelope JSON.</summary>
            string ApplyWwpPattern(string newName, PatternInstanceDescriptor sourceInstance);
        }

        public sealed class SourceDescriptor
        {
            public string Name;
            public string Type;
            public IList<string> Parts;
        }

        public sealed class PatternInstanceDescriptor
        {
            public string PatternKey;   // e.g. "WorkWithPlus"
            public string HostName;     // source host name
        }

        private readonly IObjectCloner _cloner;

        public SaveAsService(IObjectCloner cloner)
        {
            if (cloner == null) throw new ArgumentNullException("cloner");
            _cloner = cloner;
        }

        public string SaveAs(JObject args)
        {
            string sourceName = args?["name"]?.ToString();
            string newName = args?["newName"]?.ToString();
            string typeFilter = args?["type"]?.ToString();
            bool includePattern = args?["includePatternInstance"]?.ToObject<bool?>() ?? false;
            bool overwrite = args?["overwrite"]?.ToObject<bool?>() ?? false;
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() ?? false;

            if (string.IsNullOrWhiteSpace(sourceName))
                return Err("usage_error", "'name' is required (source object).", null);
            if (string.IsNullOrWhiteSpace(newName))
                return Err("usage_error", "'newName' is required (target object).", null);
            if (string.Equals(sourceName, newName, StringComparison.OrdinalIgnoreCase))
                return Err("usage_error", "newName must differ from source name.", null);

            var src = _cloner.FindSource(sourceName, typeFilter);
            if (src == null)
            {
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "NotFound",
                    ["error"] = "Source object not found: " + sourceName,
                    ["sourceName"] = sourceName
                }.ToString();
            }

            if (_cloner.TargetExists(newName))
            {
                // v1: refuse even with overwrite=true — deleting an existing
                // object is destructive enough that we want the agent to call
                // genexus_delete_object explicitly. overwrite=true currently
                // just surfaces a clearer hint.
                return new JObject
                {
                    ["status"] = "Error",
                    ["code"] = "TargetExists",
                    ["error"] = "An object named '" + newName + "' already exists.",
                    ["hint"] = overwrite
                        ? "overwrite=true is reserved for a future revision. For now, delete the existing object first via genexus_delete_object name=" + newName + " confirm=true, then re-run genexus_save_as."
                        : "Pick a different newName, or delete the existing object via genexus_delete_object name=" + newName + " confirm=true.",
                    ["sourceName"] = sourceName,
                    ["newName"] = newName
                }.ToString();
            }

            // ---- dryRun: build the plan, never touch the SDK. ----
            if (dryRun)
            {
                var plan = new JObject
                {
                    ["status"] = "DryRun",
                    ["sourceName"] = sourceName,
                    ["plan"] = new JObject
                    {
                        ["createType"] = src.Type,
                        ["newName"] = newName,
                        ["partsToClone"] = new JArray(src.Parts ?? new List<string>()),
                        ["includePatternInstance"] = includePattern
                    }
                };
                return plan.ToString();
            }

            // ---- Live path. Track progress so a partial failure surfaces a
            //      clear "where it stopped" + undo hint envelope. ----
            var completedSteps = new JArray();
            var partsCloned = new JArray();

            // Step 1: create empty target object of the same type.
            string createResult = _cloner.CreateObject(src.Type, newName);
            if (!IsSuccess(createResult))
            {
                return Partial(sourceName, newName, completedSteps, "create:" + newName, createResult);
            }
            completedSteps.Add("create:" + newName);

            // Step 2: clone each part.
            foreach (var part in (src.Parts ?? new List<string>()))
            {
                string r = _cloner.ClonePart(sourceName, newName, part, typeFilter);
                if (!IsSuccess(r))
                {
                    return Partial(sourceName, newName, completedSteps, "clonePart:" + part, r);
                }
                completedSteps.Add("clonePart:" + part);
                partsCloned.Add(part);
            }

            // Step 3: optionally clone the WWP pattern instance.
            JObject patternBlock = null;
            if (includePattern)
            {
                var inst = _cloner.FindWwpInstance(sourceName);
                if (inst != null)
                {
                    string applyResult = _cloner.ApplyWwpPattern(newName, inst);
                    bool ok = IsSuccess(applyResult);
                    patternBlock = new JObject
                    {
                        ["name"] = newName,
                        ["pattern"] = inst.PatternKey,
                        ["status"] = ok ? "Success" : "Failed",
                        ["detail"] = SafeParseOrString(applyResult)
                    };
                    if (ok) completedSteps.Add("applyPattern:" + inst.PatternKey);
                }
                // No WWP instance on source → silently omit the block (per spec).
            }

            var resp = new JObject
            {
                ["status"] = "Success",
                ["sourceName"] = sourceName,
                ["created"] = new JObject
                {
                    ["name"] = newName,
                    ["type"] = src.Type,
                    ["partsCloned"] = partsCloned
                }
            };
            if (patternBlock != null) resp["patternInstance"] = patternBlock;
            return resp.ToString();
        }

        private static string Partial(string sourceName, string newName, JArray completedSteps, string failedStep, string innerResult)
        {
            return new JObject
            {
                ["status"] = "PartialFailure",
                ["sourceName"] = sourceName,
                ["newName"] = newName,
                ["completedSteps"] = completedSteps,
                ["failedStep"] = failedStep,
                ["detail"] = SafeParseOrString(innerResult),
                ["hint"] = "Some parts were already cloned. Use genexus_undo to revert, or genexus_delete_object name=" + newName + " confirm=true to remove the half-cloned target."
            }.ToString();
        }

        private static string Err(string code, string message, string sourceName)
        {
            var j = new JObject
            {
                ["status"] = "Error",
                ["code"] = code,
                ["error"] = message
            };
            if (sourceName != null) j["sourceName"] = sourceName;
            return j.ToString();
        }

        private static bool IsSuccess(string envelope)
        {
            if (string.IsNullOrEmpty(envelope)) return false;
            try
            {
                var j = JObject.Parse(envelope);
                string status = j["status"]?.ToString();
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)) return true;
                // Some services return no status on success and only set "error" on failure.
                return j["error"] == null && j["code"] == null;
            }
            catch { return false; }
        }

        private static JToken SafeParseOrString(string s)
        {
            if (s == null) return JValue.CreateNull();
            try { return JToken.Parse(s); } catch { return new JValue(s); }
        }
    }

    /// <summary>
    /// Production cloner — wires <see cref="SaveAsService.IObjectCloner"/> to
    /// the existing worker services so every IDE-compat fix already living in
    /// ObjectService/WriteService applies on the cloned target too.
    ///
    /// Note: clone-via-source-text is the safe, type-agnostic strategy that
    /// works for every object type the SDK exposes a textual Source for
    /// (Transaction, Procedure, WebPanel, SDPanel, SDT, DataProvider, Domain,
    /// Dashboard, Theme, MasterPage). Binary-only parts (assets, theme
    /// images) are not cloned — same limitation as genexus_export_unified.
    /// </summary>
    public sealed class SdkObjectCloner : SaveAsService.IObjectCloner
    {
        private readonly ObjectService _objects;
        private readonly WriteService _writes;
        private readonly PatternApplyService _patterns;

        public SdkObjectCloner(ObjectService objects, WriteService writes, PatternApplyService patterns)
        {
            _objects = objects;
            _writes = writes;
            _patterns = patterns;
        }

        public SaveAsService.SourceDescriptor FindSource(string name, string typeFilter)
        {
            var obj = _objects.FindObject(name, typeFilter);
            if (obj == null) return null;
            string[] parts;
            try { parts = GxMcp.Worker.Structure.PartAccessor.GetAvailableParts(obj); }
            catch { parts = new[] { "Source" }; }
            return new SaveAsService.SourceDescriptor
            {
                Name = obj.Name,
                Type = obj.TypeDescriptor?.Name ?? typeFilter ?? "Unknown",
                Parts = parts
            };
        }

        public bool TargetExists(string newName)
        {
            try { return _objects.FindObject(newName, null) != null; }
            catch { return false; }
        }

        public string CreateObject(string type, string newName)
        {
            return _objects.CreateObject(type, newName);
        }

        public string ClonePart(string sourceName, string newName, string partName, string typeFilter)
        {
            // Read source-part as text, write to new object via the same
            // WriteService pipeline a normal genexus_edit goes through.
            string readJson = _objects.ReadObjectSource(sourceName, partName, null, null, "mcp", false, typeFilter);
            JObject readObj;
            try { readObj = JObject.Parse(readJson); }
            catch { return readJson; }

            var srcToken = readObj["source"];
            if (srcToken == null)
            {
                // Nothing to clone for this part (binary / unsupported) — that's not an error.
                return new JObject { ["status"] = "Success", ["skipped"] = true, ["part"] = partName }.ToString();
            }

            string code = srcToken.ToString();
            return _writes.WriteObject(newName, partName, code);
        }

        public SaveAsService.PatternInstanceDescriptor FindWwpInstance(string sourceName)
        {
            // The SDK exposes pattern-instance metadata through the object's
            // PatternInstance property, but the discovery path differs per
            // KB. Conservative approach: rely on PatternApplyService's
            // existing detection, which already powers genexus_apply_pattern
            // reapply / diagnose. If the source has a WWP instance, the
            // service knows; otherwise we return null and SaveAs skips the
            // pattern block.
            try
            {
                var obj = _objects.FindObject(sourceName, null);
                if (obj == null) return null;
                // PatternApplyService.HasPatternInstance is not part of the
                // public surface in every build — guard reflectively so we
                // don't break compilation if it gets renamed. The fallback
                // (no instance detected) is the safe behaviour.
                var mi = _patterns?.GetType().GetMethod("HasWorkWithPlusInstance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (mi != null)
                {
                    bool has = (bool)mi.Invoke(_patterns, new object[] { obj });
                    if (!has) return null;
                }
                else
                {
                    // No detection helper available — caller will see no patternInstance block.
                    return null;
                }
                return new SaveAsService.PatternInstanceDescriptor
                {
                    PatternKey = "WorkWithPlus",
                    HostName = sourceName
                };
            }
            catch { return null; }
        }

        public string ApplyWwpPattern(string newName, SaveAsService.PatternInstanceDescriptor sourceInstance)
        {
            // Delegate to the existing pattern-apply pipeline. settings=null
            // means "use defaults", which is the IDE's Save-As behaviour too.
            try
            {
                var argsObj = new JObject
                {
                    ["name"] = newName,
                    ["pattern"] = sourceInstance?.PatternKey ?? "WorkWithPlus"
                };
                var mi = _patterns?.GetType().GetMethod("Apply",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (mi == null)
                {
                    return new JObject
                    {
                        ["status"] = "Error",
                        ["error"] = "PatternApplyService.Apply not available on this worker build."
                    }.ToString();
                }
                var parms = mi.GetParameters();
                object result;
                if (parms.Length == 1 && parms[0].ParameterType == typeof(JObject))
                    result = mi.Invoke(_patterns, new object[] { argsObj });
                else if (parms.Length == 1)
                    result = mi.Invoke(_patterns, new object[] { argsObj.ToString() });
                else
                    return new JObject { ["status"] = "Error", ["error"] = "Unrecognised PatternApplyService.Apply signature." }.ToString();
                return result?.ToString() ?? "{\"status\":\"Success\"}";
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "Error", ["error"] = ex.Message }.ToString();
            }
        }
    }
}
