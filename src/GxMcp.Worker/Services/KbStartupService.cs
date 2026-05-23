using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Wave-3 item — IDE "Set As Startup Object" / get-startup parity.
    ///
    /// Wraps the SDK's startup-object resolution. <see cref="GetStartup"/>
    /// returns the active Environment's <c>StartupObject</c> and the
    /// fall-back <c>DefaultObject</c>, mirroring what
    /// <see cref="KbService.GetLauncherObjectName"/> already uses on F5.
    /// <see cref="SetStartup"/> verifies the object exists and then writes
    /// the active Environment's <c>StartupObject</c> via the SDK's
    /// <c>SetPropertyValue</c> (probed by reflection across SDK shapes).
    ///
    /// All SDK access is funneled through <see cref="IEnvPropertyStore"/>
    /// so unit tests can run against an in-memory implementation without
    /// touching the GeneXus SDK at all.
    /// </summary>
    public class KbStartupService
    {
        public interface IEnvPropertyStore
        {
            string Get(string propertyName);
            // Returns true on success; false when the SDK rejects the write
            // (unknown property, type mismatch, env handle missing, etc.).
            bool Set(string propertyName, string value);
        }

        private readonly KbService _kbService;
        private readonly ObjectService _objectService;
        private readonly IEnvPropertyStore _store;

        public KbStartupService(KbService kbService, ObjectService objectService)
            : this(kbService, objectService, new SdkEnvPropertyStore(kbService))
        {
        }

        public KbStartupService(KbService kbService, ObjectService objectService, IEnvPropertyStore store)
        {
            _kbService = kbService;
            _objectService = objectService;
            _store = store;
        }

        public string GetStartup()
        {
            try
            {
                string startup = _store.Get("StartupObject");
                string fallback = _store.Get("DefaultObject");
                string effective = _kbService?.GetLauncherObjectName();
                return new JObject
                {
                    ["startupObject"] = startup ?? string.Empty,
                    ["defaultObject"] = fallback ?? string.Empty,
                    ["effective"] = effective ?? string.Empty,
                    ["hint"] = string.IsNullOrEmpty(startup)
                        ? "StartupObject not set; effective launcher resolves through DefaultObject / first Main-tagged object."
                        : null
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message, ["code"] = "GetStartupFailed" }
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        public string SetStartup(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return new JObject { ["error"] = "Missing 'name'." }.ToString(Newtonsoft.Json.Formatting.None);

            try
            {
                var obj = _objectService?.FindObject(objectName, null);
                if (obj == null)
                {
                    return new JObject
                    {
                        ["error"] = $"Object '{objectName}' not found in KB.",
                        ["code"] = "NotFound",
                        ["name"] = objectName
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                string previous = null;
                try { previous = _store.Get("StartupObject"); } catch { }

                bool ok = _store.Set("StartupObject", obj.Name);
                if (!ok)
                {
                    return new JObject
                    {
                        ["error"] = "SDK refused to write StartupObject.",
                        ["code"] = "SetFailed",
                        ["name"] = obj.Name,
                        ["hint"] = "Verify the active Environment is loaded and that the SDK shape exposes StartupObject as a writable env property."
                    }.ToString(Newtonsoft.Json.Formatting.None);
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["startupObject"] = obj.Name,
                    ["previousStartupObject"] = previous ?? string.Empty
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message, ["code"] = "SetStartupFailed" }
                    .ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        // SDK-backed store. Probes the same Environment shapes that
        // KbService.GetActiveEnvironment + GetLauncherObjectName already
        // know about; writes through SetPropertyValue/SetPropertyValueString
        // by reflection so we work across SDK major versions.
        internal sealed class SdkEnvPropertyStore : IEnvPropertyStore
        {
            private readonly KbService _kbService;
            public SdkEnvPropertyStore(KbService kbService) { _kbService = kbService; }

            public string Get(string propertyName)
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null) return null;

                if (string.Equals(propertyName, "StartupObject", StringComparison.OrdinalIgnoreCase))
                {
                    try { var v = kb.DefaultStartupObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }
                if (string.Equals(propertyName, "DefaultObject", StringComparison.OrdinalIgnoreCase))
                {
                    try { var v = kb.UserInterface?.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                    try { var v = kb.MainObject?.Name; if (!string.IsNullOrEmpty((string)v)) return (string)v; } catch { }
                }

                object envContainer = ResolveEnvContainer(kb);
                if (envContainer == null) return null;
                try
                {
                    object v = TryInvokeGetPropertyValue(envContainer, propertyName);
                    return v?.ToString();
                }
                catch { return null; }
            }

            public bool Set(string propertyName, string value)
            {
                dynamic kb = _kbService?.GetKB();
                if (kb == null) return false;

                object envContainer = ResolveEnvContainer(kb);
                if (envContainer == null) return false;

                if (TryInvokeSetPropertyValueString(envContainer, propertyName, value)) return true;
                if (TryInvokeSetPropertyValue(envContainer, propertyName, value)) return true;
                return false;
            }

            private static object ResolveEnvContainer(dynamic kb)
            {
                try { object v = kb.Environment; if (v != null) return v; } catch { }
                try { object v = kb.UserInterface?.ActiveEnvironment; if (v != null) return v; } catch { }
                try { object v = kb.DesignModel?.Environment; if (v != null) return v; } catch { }
                try { object v = kb.ActiveModel; if (v != null) return v; } catch { }
                return null;
            }

            private static object TryInvokeGetPropertyValue(object target, string propName)
            {
                if (target == null) return null;
                var t = target.GetType();
                var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetPropertyValue"
                                      && m.GetParameters().Length == 1
                                      && m.GetParameters()[0].ParameterType == typeof(string));
                if (mi == null) return null;
                return mi.Invoke(target, new object[] { propName });
            }

            private static bool TryInvokeSetPropertyValueString(object target, string propName, string value)
            {
                if (target == null) return false;
                try
                {
                    var t = target.GetType();
                    var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SetPropertyValueString"
                                          && m.GetParameters().Length == 2
                                          && m.GetParameters()[0].ParameterType == typeof(string)
                                          && m.GetParameters()[1].ParameterType == typeof(string));
                    if (mi == null) return false;
                    mi.Invoke(target, new object[] { propName, value });
                    return true;
                }
                catch (Exception ex) { Logger.Debug("[KbStartup] SetPropertyValueString failed: " + ex.Message); return false; }
            }

            private static bool TryInvokeSetPropertyValue(object target, string propName, object value)
            {
                if (target == null) return false;
                try
                {
                    var t = target.GetType();
                    var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SetPropertyValue"
                                          && m.GetParameters().Length == 2
                                          && m.GetParameters()[0].ParameterType == typeof(string));
                    if (mi == null) return false;
                    mi.Invoke(target, new object[] { propName, value });
                    return true;
                }
                catch (Exception ex) { Logger.Debug("[KbStartup] SetPropertyValue failed: " + ex.Message); return false; }
            }
        }
    }
}
