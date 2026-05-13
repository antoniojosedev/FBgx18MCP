using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Surgical diagnostics around obj.Save(prefs) for the WebForm write fix.
    /// Logs byte-level state of SerializeData() and a probe element value at each
    /// save lifecycle checkpoint so we can localize where the mutation is lost.
    /// </summary>
    internal static class WebFormSaveDiagnostics
    {
        private const string ProbeId = "TextBlockSaldoHoras";

        public static void DumpState(object webFormPart, object kbObject, string tag)
        {
            try
            {
                // 1. m_Document field via reflection
                var docField = webFormPart.GetType().GetField("m_Document",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var doc = docField?.GetValue(webFormPart) as XmlDocument;

                // 2. Document property
                var docProp = webFormPart.GetType().GetProperty("Document",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var docPropVal = docProp?.GetValue(webFormPart, null) as XmlDocument;

                string fieldXml = doc?.OuterXml ?? "(null)";
                string propXml = docPropVal?.OuterXml ?? "(null)";
                bool sameRef = ReferenceEquals(doc, docPropVal);

                string fieldProbe = ExtractProbeAttr(doc);
                string propProbe = ExtractProbeAttr(docPropVal);

                Logger.Info($"[Diag/{tag}] m_Document len={fieldXml.Length} hash={Sha1(fieldXml)} probe={fieldProbe}");
                Logger.Info($"[Diag/{tag}] Document(prop) len={propXml.Length} hash={Sha1(propXml)} probe={propProbe} sameRef={sameRef}");

                // 3. SerializeData() bytes (what the SDK actually persists)
                try
                {
                    var serializeMi = FindNonPublicMethod(webFormPart.GetType(), "SerializeData", Type.EmptyTypes);
                    if (serializeMi != null)
                    {
                        var bytes = serializeMi.Invoke(webFormPart, null) as byte[];
                        if (bytes != null)
                        {
                            string asXml = SafeBytesToXmlString(bytes);
                            string bytesProbe = ExtractProbeAttrFromXml(asXml);
                            Logger.Info($"[Diag/{tag}] SerializeData() bytes={bytes.Length} hash={Sha1Bytes(bytes)} probe={bytesProbe}");
                        }
                        else
                        {
                            Logger.Info($"[Diag/{tag}] SerializeData() returned null");
                        }
                    }
                    else
                    {
                        Logger.Info($"[Diag/{tag}] SerializeData method not found");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    Logger.Info($"[Diag/{tag}] SerializeData() threw: {inner.GetType().Name}: {inner.Message}");
                }

                // 4. Mode / Modifications enum values on part + kbObject
                LogModeAndModifications(webFormPart, $"[Diag/{tag}] part");
                LogModeAndModifications(kbObject, $"[Diag/{tag}] obj");

                // 5. StructurePart presence (gates the OnBeforeSaveEntity clobber)
                try
                {
                    var partsProp = kbObject.GetType().GetProperty("Parts");
                    var parts = partsProp?.GetValue(kbObject, null);
                    if (parts != null)
                    {
                        var structurePartType = FindType("Artech.Genexus.Common.Parts.StructurePart");
                        if (structurePartType != null)
                        {
                            var getGeneric = parts.GetType().GetMethods().FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                            if (getGeneric != null)
                            {
                                var structPart = getGeneric.MakeGenericMethod(structurePartType).Invoke(parts, null);
                                Logger.Info($"[Diag/{tag}] StructurePart present={structPart != null}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Info($"[Diag/{tag}] StructurePart check threw: {ex.Message}");
                }

                // 6. Dirty flag + flags
                try
                {
                    var dirtyProp = webFormPart.GetType().GetProperty("Dirty",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dirtyProp != null && dirtyProp.CanRead)
                    {
                        Logger.Info($"[Diag/{tag}] part.Dirty={dirtyProp.GetValue(webFormPart, null)}");
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Info($"[Diag/{tag}] DumpState top-level threw: {ex.Message}");
            }
        }

        private static void LogModeAndModifications(object o, string prefix)
        {
            if (o == null) return;
            try
            {
                foreach (var name in new[] { "Mode", "PartsMode", "Modifications" })
                {
                    var p = o.GetType().GetProperty(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanRead)
                    {
                        try { Logger.Info($"{prefix}.{name}={p.GetValue(o, null)}"); }
                        catch (Exception ex) { Logger.Info($"{prefix}.{name} threw: {ex.Message}"); }
                    }
                }
            }
            catch { }
        }

        private static string ExtractProbeAttr(XmlDocument doc)
        {
            if (doc?.DocumentElement == null) return "(no doc)";
            try
            {
                var xp = $"//*[@id='{ProbeId}']";
                var node = doc.SelectSingleNode(xp) as XmlElement;
                if (node == null) return "(no probe)";
                var capExpr = node.Attributes["CaptionExpression"]?.Value;
                return Truncate(capExpr ?? "(null)", 120);
            }
            catch (Exception ex)
            {
                return "(xpath threw: " + ex.Message + ")";
            }
        }

        private static string ExtractProbeAttrFromXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return "(empty)";
            try
            {
                var d = new XmlDocument();
                d.LoadXml(xml);
                return ExtractProbeAttr(d);
            }
            catch (Exception ex)
            {
                return "(parse threw: " + Truncate(ex.Message, 60) + ")";
            }
        }

        private static string SafeBytesToXmlString(byte[] bytes)
        {
            // WebFormPart serializes m_Document via Artech.Common.Helpers.Convert.ToByteArray.
            // Typically UTF-8 XML; sometimes a binary header. Try UTF-8 first, fall back to scanning for '<'.
            try
            {
                var s = Encoding.UTF8.GetString(bytes);
                int idx = s.IndexOf('<');
                return idx >= 0 ? s.Substring(idx) : s;
            }
            catch { return string.Empty; }
        }

        private static string Sha1(string s)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? string.Empty));
                return BitConverter.ToString(b, 0, 6).Replace("-", "");
            }
        }

        private static string Sha1Bytes(byte[] b)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(b ?? Array.Empty<byte>());
                return BitConverter.ToString(h, 0, 6).Replace("-", "");
            }
        }

        private static MethodInfo FindNonPublicMethod(Type t, string name, Type[] paramTypes)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var m = cur.GetMethod(name, flags, null, paramTypes, null);
                if (m != null) return m;
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? s : (s.Length > n ? s.Substring(0, n) + "…" : s);

        /// <summary>
        /// Bypass path: directly call Entity.SaveModelEntityOutput(outputTypeId, version, ts, bytes)
        /// on the WebFormPart entity. This is the lowest-level persistence primitive on Entity —
        /// SaveWithParent presumably routes through it after computing the bytes via SerializeData.
        /// If we call it ourselves with the fresh bytes, we sidestep whatever gate in SaveWithParent
        /// is dropping our write.
        /// </summary>
        public static void TryDirectSaveModelEntityOutput(object webFormPart, object kbObject)
        {
            try
            {
                // Read TypeId / TypeVersionId from the entity (inherited Entity properties).
                int? typeId = TryReadInt(webFormPart, "TypeId");
                int? versionId = TryReadInt(webFormPart, "TypeVersionId");
                Logger.Info($"[DirectSave] part.TypeId={typeId} part.TypeVersionId={versionId}");
                if (typeId == null || versionId == null)
                {
                    Logger.Info("[DirectSave] TypeId/TypeVersionId unavailable — abort");
                    return;
                }

                // Fresh bytes from the part (with our mutation).
                var serializeMi = FindNonPublicMethod(webFormPart.GetType(), "SerializeData", Type.EmptyTypes);
                if (serializeMi == null) { Logger.Info("[DirectSave] SerializeData not found"); return; }
                var bytes = serializeMi.Invoke(webFormPart, null) as byte[];
                if (bytes == null || bytes.Length == 0) { Logger.Info("[DirectSave] SerializeData returned empty"); return; }
                Logger.Info($"[DirectSave] SerializeData bytes={bytes.Length} hash={Sha1Bytes(bytes)}");

                // Find SaveModelEntityOutput(int, int, DateTime, byte[]) on the part (inherited from Entity).
                var saveMi = FindMethod(webFormPart.GetType(), "SaveModelEntityOutput",
                    new[] { typeof(int), typeof(int), typeof(DateTime), typeof(byte[]) });
                if (saveMi == null)
                {
                    // Try on kbObject as fallback.
                    saveMi = FindMethod(kbObject.GetType(), "SaveModelEntityOutput",
                        new[] { typeof(int), typeof(int), typeof(DateTime), typeof(byte[]) });
                    if (saveMi == null)
                    {
                        Logger.Info("[DirectSave] SaveModelEntityOutput method not found on part or kbObject");
                        return;
                    }
                    Logger.Info("[DirectSave] using kbObject.SaveModelEntityOutput");
                    saveMi.Invoke(kbObject, new object[] { typeId.Value, versionId.Value, DateTime.UtcNow, bytes });
                }
                else
                {
                    Logger.Info("[DirectSave] using part.SaveModelEntityOutput");
                    saveMi.Invoke(webFormPart, new object[] { typeId.Value, versionId.Value, DateTime.UtcNow, bytes });
                }
                Logger.Info($"[DirectSave] SaveModelEntityOutput(typeId={typeId}, version={versionId}, bytes={bytes.Length}) completed.");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Logger.Info($"[DirectSave] threw: {inner.GetType().Name}: {inner.Message}");
                if (inner.StackTrace != null)
                    Logger.Info("[DirectSave]   at " + inner.StackTrace.Split('\n')[0].Trim());
            }
        }

        private static int? TryReadInt(object o, string propName)
        {
            try
            {
                for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (p != null && p.CanRead && p.PropertyType == typeof(int))
                        return (int)p.GetValue(o, null);
                }
            }
            catch { }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, Type[] paramTypes)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var m = cur.GetMethod(name, flags, null, paramTypes, null);
                if (m != null) return m;
            }
            return null;
        }
    }
}
