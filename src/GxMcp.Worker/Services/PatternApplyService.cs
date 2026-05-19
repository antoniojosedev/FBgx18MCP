using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Applies (and re-applies) GeneXus patterns to KBObjects. Equivalent to the
    /// IDE's "Right-click → Apply Pattern" entry point.
    ///
    /// SDK surface lives in Artech.Packages.Patterns.dll inside the GeneXus
    /// install's Packages\ folder, which is NOT statically referenced by the
    /// worker. We load it on demand by reflection so the worker can build/run
    /// on machines without the pattern engine installed; in that case we return
    /// a graceful "pattern_unavailable" status instead of throwing.
    ///
    /// Tests inject a fake <see cref="IPatternEngineAdapter"/> so the unit
    /// suite does not depend on the live SDK / license / open KB.
    /// </summary>
    public class PatternApplyService
    {
        // Well-known pattern GUIDs. WorkWithPlus is the only one in scope for W2;
        // additional pattern keys can be registered here as we expose more tools.
        public static readonly Guid WorkWithPlusPatternId = new Guid("07135890-56fc-489b-b408-063722fa9f7d");

        private static readonly Dictionary<string, Guid> KnownPatterns = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            { "WorkWithPlus", WorkWithPlusPatternId },
            { "WWP", WorkWithPlusPatternId },
        };

        private readonly ObjectService _objectService;
        private readonly IPatternEngineAdapter _engine;
        // Test seam: when set, used instead of _objectService.FindObject to resolve
        // the parent KBObject. The live path always uses _objectService.
        private readonly Func<string, KBObject> _findObjectOverride;

        public PatternApplyService(ObjectService objectService)
            : this(objectService, new ReflectionPatternEngineAdapter(), null)
        {
        }

        public PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine)
            : this(objectService, engine, null)
        {
        }

        // Test ctor: lets unit tests bypass the live SDK FindObject lookup.
        internal PatternApplyService(ObjectService objectService, IPatternEngineAdapter engine, Func<string, KBObject> findObjectOverride)
        {
            _objectService = objectService;
            _engine = engine;
            _findObjectOverride = findObjectOverride;
        }

        private KBObject ResolveObject(string objectName)
        {
            if (_findObjectOverride != null) return _findObjectOverride(objectName);
            return _objectService != null ? _objectService.FindObject(objectName) : null;
        }

        /// <summary>
        /// First-time apply of a pattern to a parent object.
        /// </summary>
        public string ApplyPattern(string objectName, string patternKey, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Error("Object name is required.", objectName);
                if (string.IsNullOrWhiteSpace(patternKey))
                    return McpResponse.Error("Pattern key is required.", objectName);

                if (!TryResolvePatternId(patternKey, out Guid patternId))
                    return PatternUnavailable(patternKey, "Unknown pattern key. Pass 'WorkWithPlus' or a known GUID.");

                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    // Reuse existing not-found shape (best-effort: tests may inject a null _objectService)
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetIndex());
                    return McpResponse.Error("Object not found", objectName);
                }

                return ApplyPatternToObject(obj, patternId, patternKey, settings, reapply: false);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ApplyPattern failed: " + ex);
                return McpResponse.Error(ex.Message, objectName);
            }
        }

        /// <summary>
        /// Re-apply (regenerate) an existing pattern instance on the object.
        /// </summary>
        public string ReapplyPattern(string objectName, JObject settings = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectName))
                    return McpResponse.Error("Object name is required.", objectName);

                KBObject obj = ResolveObject(objectName);
                if (obj == null)
                {
                    if (_objectService != null)
                        return HealingService.FormatNotFoundError(objectName, _objectService.GetIndex());
                    return McpResponse.Error("Object not found", objectName);
                }

                // For reapply we default to WorkWithPlus until other patterns are wired.
                return ApplyPatternToObject(obj, WorkWithPlusPatternId, "WorkWithPlus", settings, reapply: true);
            }
            catch (Exception ex)
            {
                Logger.Error("PatternApplyService.ReapplyPattern failed: " + ex);
                return McpResponse.Error(ex.Message, objectName);
            }
        }

        internal string ApplyPatternToObject(KBObject obj, Guid patternId, string patternKey, JObject settings, bool reapply, string objectNameForResponse = null)
        {
            // Probe the engine; if license/package is missing we degrade gracefully.
            object patternDefinition = _engine.GetPatternDefinition(patternId);
            if (patternDefinition == null)
            {
                return PatternUnavailable(patternKey, "WorkWithPlus pattern not loaded — check license / package install");
            }

            // Detect existing instance to decide between first-apply and re-apply.
            object existingInstance = _engine.GetPatternInstance(obj, patternId);
            bool wasFirstApply = existingInstance == null;

            PatternApplyResult result;
            try
            {
                if (reapply)
                {
                    // Re-apply: if there is no existing instance we fall back to first-time apply
                    // so callers don't have to special-case freshly created objects.
                    if (existingInstance != null)
                    {
                        result = _engine.ReapplyPattern(existingInstance, settings);
                        wasFirstApply = false;
                    }
                    else
                    {
                        result = _engine.ApplyPattern(obj, patternDefinition, settings);
                        wasFirstApply = true;
                    }
                }
                else
                {
                    if (existingInstance != null)
                    {
                        // Already applied — repeat call should re-apply rather than fail.
                        result = _engine.ReapplyPattern(existingInstance, settings);
                        wasFirstApply = false;
                    }
                    else
                    {
                        result = _engine.ApplyPattern(obj, patternDefinition, settings);
                        wasFirstApply = true;
                    }
                }
            }
            catch (Exception ex)
            {
                string errName = objectNameForResponse ?? obj?.Name ?? "";
                Logger.Error("PatternEngine apply failed for '" + errName + "': " + ex);
                var err = new JObject
                {
                    ["status"] = "Error",
                    ["target"] = errName,
                    ["patternKey"] = patternKey,
                    ["error"] = ex.Message
                };
                return err.ToString(Newtonsoft.Json.Formatting.None);
            }

            string targetName = objectNameForResponse ?? obj?.Name ?? "";
            var response = new JObject
            {
                ["status"] = "Success",
                ["target"] = targetName,
                ["patternKey"] = patternKey,
                ["patternId"] = patternId.ToString(),
                ["wasFirstApply"] = wasFirstApply,
                ["generatedObjects"] = new JArray(result?.GeneratedObjects ?? Enumerable.Empty<string>()),
                ["errors"] = new JArray(result?.Errors ?? Enumerable.Empty<string>())
            };
            return response.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool TryResolvePatternId(string patternKey, out Guid id)
        {
            if (KnownPatterns.TryGetValue(patternKey.Trim(), out id)) return true;
            return Guid.TryParse(patternKey.Trim(), out id);
        }

        private static string PatternUnavailable(string patternKey, string message)
        {
            var j = new JObject
            {
                ["status"] = "pattern_unavailable",
                ["patternKey"] = patternKey,
                ["message"] = message
            };
            return j.ToString(Newtonsoft.Json.Formatting.None);
        }
    }

    /// <summary>
    /// Result of an apply/re-apply call — kept minimal because the SDK does not
    /// surface a structured return for first-time apply (void) and only a bool
    /// for re-apply. Both are normalized into this shape.
    /// </summary>
    public class PatternApplyResult
    {
        public IList<string> GeneratedObjects { get; set; } = new List<string>();
        public IList<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Engine boundary so tests can mock the live SDK without a KB or license.
    /// </summary>
    public interface IPatternEngineAdapter
    {
        /// <summary>Returns a PatternDefinition (as object) or null when unavailable.</summary>
        object GetPatternDefinition(Guid patternId);

        /// <summary>Returns the existing PatternInstance for (object, patternId), or null.</summary>
        object GetPatternInstance(KBObject parent, Guid patternId);

        /// <summary>First-time apply. SDK signature is void; throws are propagated.</summary>
        PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings);

        /// <summary>Re-apply against an existing PatternInstance.</summary>
        PatternApplyResult ReapplyPattern(object patternInstance, JObject settings);
    }

    /// <summary>
    /// Live implementation. Loads Artech.Packages.Patterns.dll by reflection so
    /// the worker compiles without a hard reference. All calls return null /
    /// throw a controlled NotAvailable status when the assembly is missing,
    /// which the service surfaces as "pattern_unavailable".
    /// </summary>
    internal class ReflectionPatternEngineAdapter : IPatternEngineAdapter
    {
        private const string PatternsAssemblyName = "Artech.Packages.Patterns";
        private const string PatternEngineTypeName = "Artech.Packages.Patterns.PatternEngine";
        private const string PatternInstanceTypeName = "Artech.Packages.Patterns.Objects.PatternInstance";

        private readonly object _lock = new object();
        private bool _probed;
        private Assembly _patternsAssembly;
        private Type _patternEngineType;
        private Type _patternInstanceType;
        private MethodInfo _getPatternDefinition;
        private MethodInfo _applyPatternFirst;     // ApplyPattern(KBObject, PatternDefinition)
        private MethodInfo _applyPatternReapply;   // ApplyPattern(PatternInstance, ApplySettings)
        private MethodInfo _patternInstanceGet;    // PatternInstance.Get(KBObject, Guid)

        private bool EnsureProbed()
        {
            if (_probed) return _patternEngineType != null;
            lock (_lock)
            {
                if (_probed) return _patternEngineType != null;
                try
                {
                    _patternsAssembly = LoadPatternsAssembly();
                    if (_patternsAssembly != null)
                    {
                        _patternEngineType = _patternsAssembly.GetType(PatternEngineTypeName, false);
                        _patternInstanceType = _patternsAssembly.GetType(PatternInstanceTypeName, false);
                    }

                    if (_patternEngineType != null)
                    {
                        _getPatternDefinition = _patternEngineType.GetMethod(
                            "GetPatternDefinition",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(Guid) }, null);

                        // Disambiguate by parameter count; concrete parameter types are
                        // resolved at call time via assembly-loaded Type references.
                        foreach (var m in _patternEngineType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "ApplyPattern") continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 2) continue;
                            if (typeof(KBObject).IsAssignableFrom(ps[0].ParameterType))
                                _applyPatternFirst = m;
                            else
                                _applyPatternReapply = m;
                        }
                    }

                    if (_patternInstanceType != null)
                    {
                        _patternInstanceGet = _patternInstanceType.GetMethod(
                            "Get",
                            BindingFlags.Public | BindingFlags.Static,
                            null, new[] { typeof(KBObject), typeof(Guid) }, null);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("PatternEngine probe failed: " + ex.Message);
                }
                _probed = true;
                return _patternEngineType != null;
            }
        }

        private static Assembly LoadPatternsAssembly()
        {
            // 1. Already loaded?
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, PatternsAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            // 2. Try the standard install path under Packages\.
            string gxPath = Environment.GetEnvironmentVariable("GX_PATH");
            if (string.IsNullOrEmpty(gxPath))
                gxPath = @"C:\Program Files (x86)\GeneXus\GeneXus18";

            string candidate = Path.Combine(gxPath, "Packages", PatternsAssemblyName + ".dll");
            if (File.Exists(candidate))
            {
                try { return Assembly.LoadFrom(candidate); }
                catch (Exception ex) { Logger.Warn("LoadFrom " + candidate + " failed: " + ex.Message); }
            }

            // 3. Last-resort: by name (will hit fusion's probing paths).
            try { return Assembly.Load(PatternsAssemblyName); }
            catch { return null; }
        }

        public object GetPatternDefinition(Guid patternId)
        {
            if (!EnsureProbed() || _getPatternDefinition == null) return null;
            try { return _getPatternDefinition.Invoke(null, new object[] { patternId }); }
            catch (TargetInvocationException tie)
            {
                Logger.Warn("GetPatternDefinition threw: " + (tie.InnerException?.Message ?? tie.Message));
                return null;
            }
        }

        public object GetPatternInstance(KBObject parent, Guid patternId)
        {
            if (!EnsureProbed() || _patternInstanceGet == null) return null;
            try { return _patternInstanceGet.Invoke(null, new object[] { parent, patternId }); }
            catch (TargetInvocationException) { return null; }
        }

        public PatternApplyResult ApplyPattern(KBObject parent, object patternDefinition, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternFirst == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(KBObject, PatternDefinition) not found");

            try
            {
                _applyPatternFirst.Invoke(null, new[] { parent, patternDefinition });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            // The SDK doesn't surface generated-object names from the void overload.
            // Best-effort: scan the new PatternInstance for owned objects after apply.
            return new PatternApplyResult();
        }

        public PatternApplyResult ReapplyPattern(object patternInstance, JObject settings)
        {
            if (!EnsureProbed() || _applyPatternReapply == null)
                throw new InvalidOperationException("PatternEngine.ApplyPattern(PatternInstance, ApplySettings) not found");

            // ApplySettings is a pattern-internal type; pass null to use defaults.
            // Full settings projection is future work (would require building an
            // ApplySettings instance + populating its tree from the JObject).
            object applySettings = null;

            try
            {
                _applyPatternReapply.Invoke(null, new[] { patternInstance, applySettings });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }

            return new PatternApplyResult();
        }
    }
}
