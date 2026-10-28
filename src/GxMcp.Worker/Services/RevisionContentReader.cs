using System;
using System.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>One part of a revision rendered as text, or the reason it could not be.</summary>
    internal sealed class RenderedRevisionPart
    {
        public string Content { get; private set; }
        public string Representation { get; private set; }
        public string ErrorCode { get; private set; }
        public string Reason { get; private set; }
        public bool Succeeded => ErrorCode == null;

        public static RenderedRevisionPart Ok(string content, string representation)
            => new RenderedRevisionPart { Content = content ?? string.Empty, Representation = representation };

        public static RenderedRevisionPart Fail(string errorCode, string reason)
            => new RenderedRevisionPart { ErrorCode = errorCode, Reason = reason };
    }

    /// <summary>
    /// Issue #710 — reads native SDK revisions (<c>KBObject.GetVersions()</c>) of an object
    /// and renders one part of a revision as text that can be read or line-diffed.
    /// </summary>
    internal sealed class RevisionContentReader
    {
        private readonly SDTService _sdtService;

        internal RevisionContentReader(SDTService sdtService)
        {
            _sdtService = sdtService;
        }

        /// <summary>
        /// A non-current revision returned by <c>KBObject.GetVersions()</c> belongs to a
        /// different <c>KBModel</c> (its own model GUID). Enumerating its <c>Parts</c>
        /// constructs a <c>VariablesPart</c>, whose standard variables call
        /// <c>ExternalObjectGenericTypesExtendProperties.UpdateDefinitionsVariable</c>. When
        /// no variable of the current design model has been initialised yet in the Worker
        /// process, that SDK call throws a <c>NullReferenceException</c> (surfaced as a
        /// <c>TargetInvocationException</c>), so every part of the revision was reported as
        /// <c>VersionPartUnavailable</c>. Loading the current object's parts first
        /// initialises those shared definitions against the design model, after which the
        /// revision's parts load. The prime always runs before the revision lookup.
        /// </summary>
        internal static T LoadAfterPrimingCurrentParts<T>(Action primeCurrentParts, Func<T> loadRevision)
        {
            if (primeCurrentParts == null) throw new ArgumentNullException(nameof(primeCurrentParts));
            if (loadRevision == null) throw new ArgumentNullException(nameof(loadRevision));
            primeCurrentParts();
            return loadRevision();
        }

        internal static KBObject FindRevision(KBObject current, int versionId)
        {
            return LoadAfterPrimingCurrentParts(
                () => PrimeCurrentParts(current),
                () => current.GetVersions().Cast<KBObject>().FirstOrDefault(v => v.VersionId == versionId));
        }

        private static void PrimeCurrentParts(KBObject current)
        {
            try
            {
                foreach (KBObjectPart _ in current.Parts) { }
            }
            catch (Exception ex)
            {
                Logger.Warn("[History] Loading the current parts before a revision read failed: " + InnermostMessage(ex));
            }
        }

        internal static JObject RevisionInfo(KBObject revision)
        {
            var info = new JObject { ["versionId"] = revision.VersionId };
            try { info["isCurrentVersion"] = revision.IsCurrentVersion; } catch { }
            try { info["date"] = revision.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
            try { info["user"] = revision.UserName; } catch { }
            try { info["comment"] = revision.Comment; } catch { }
            return info;
        }

        internal static string InnermostMessage(Exception ex)
        {
            while (ex?.InnerException != null) ex = ex.InnerException;
            return ex?.Message ?? string.Empty;
        }

        /// <summary>
        /// Renders one part of a revision (or of the current object). Both sides of
        /// <c>history_diff</c> go through this method, so a comparison never mixes two
        /// representations of the same part. Textual parts return their source; Variables
        /// return the same declaration text as <c>genexus_read</c>; visual parts return the
        /// editable form XML; any other stored part returns the SDK's serialized part XML.
        /// Pattern projection parts that the SDK does not store are refused rather than
        /// reported as empty.
        /// </summary>
        internal RenderedRevisionPart Render(KBObject source, string partName)
        {
            string label = DescribeRevision(source);
            if (string.Equals(partName, "SDTStructure", StringComparison.OrdinalIgnoreCase))
                return RenderSdtStructure(source, label);

            try
            {
                if (WebFormXmlHelper.IsVisualPart(partName))
                {
                    string visualXml = WebFormXmlHelper.ReadEditableXml(source);
                    if (!string.IsNullOrEmpty(visualXml))
                        return RenderedRevisionPart.Ok(visualXml, RevisionDiffEnvelope.VisualXml);
                }

                KBObjectPart part;
                try
                {
                    part = GxMcp.Worker.Structure.PartAccessor.GetPart(source, partName);
                }
                catch (Exception ex)
                {
                    return RenderedRevisionPart.Fail("VersionPartUnavailable",
                        label + " could not resolve part '" + partName + "': " + InnermostMessage(ex));
                }

                if (part == null)
                    return RenderedRevisionPart.Fail("VersionPartUnavailable",
                        label + " does not contain part '" + partName + "'.");

                return RenderPart(part, partName, label);
            }
            catch (Exception ex)
            {
                return RenderedRevisionPart.Fail("VersionSourceFailed",
                    label + " part '" + partName + "' could not be rendered: " + InnermostMessage(ex));
            }
        }

        private RenderedRevisionPart RenderSdtStructure(KBObject source, string label)
        {
            try
            {
                return RenderedRevisionPart.Ok(
                    _sdtService.ReadStructure(source, false).ToString(Newtonsoft.Json.Formatting.Indented),
                    "sdt-structure-json");
            }
            catch (Exception ex)
            {
                return RenderedRevisionPart.Fail("VersionPartUnavailable",
                    label + " could not read part 'SDTStructure': " + InnermostMessage(ex));
            }
        }

        private static RenderedRevisionPart RenderPart(KBObjectPart part, string partName, string label)
        {
            if (part is ISource sourcePart)
                return RenderedRevisionPart.Ok(sourcePart.Source, "source");

            if (part is global::Artech.Genexus.Common.Parts.VariablesPart variablesPart)
                return RenderedRevisionPart.Ok(VariableInjector.GetVariablesAsText(variablesPart), "variables");

            string xml = part.SerializeToXml() ?? string.Empty;
            if (GxMcp.Worker.Structure.PartAccessor.IsWorkWithDevicesProjectionPart(part) && IsEmptyPropertiesXml(xml))
                return RenderedRevisionPart.Fail("VersionPartNotExtractable",
                    label + " part '" + partName + "' is a pattern projection part; the SDK does not store its content, "
                    + "so no historical representation exists to read or compare.");

            return RenderedRevisionPart.Ok(xml, RevisionDiffEnvelope.PartXml);
        }

        private static string DescribeRevision(KBObject source)
        {
            try
            {
                return source.IsCurrentVersion
                    ? "The current version (" + source.VersionId + ")"
                    : "Version " + source.VersionId;
            }
            catch
            {
                return "The revision";
            }
        }

        internal static bool IsEmptyPropertiesXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return true;
            string t = xml.Trim();
            return t.Equals("<Properties />", StringComparison.OrdinalIgnoreCase)
                || t.Equals("<Properties/>", StringComparison.OrdinalIgnoreCase)
                || t.Equals("<Properties></Properties>", StringComparison.OrdinalIgnoreCase);
        }
    }
}
