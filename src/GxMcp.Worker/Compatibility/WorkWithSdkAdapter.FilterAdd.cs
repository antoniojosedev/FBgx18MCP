using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal static partial class WorkWithSdkAdapter
    {
        private static readonly Regex FilterAttributeNamePattern = new Regex(@"^&?[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        /// Deterministic request validation: identifier shape of the filter attribute name
        /// (an optional leading `&` marks a variable), no empty condition, and no condition
        /// repeated within the request or against the conditions already on the filter.
        internal static bool ValidateFilterRequest(
            string attributeName,
            IList<string> conditions,
            JArray existingConditions,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(attributeName) || !FilterAttributeNamePattern.IsMatch(attributeName))
            {
                errorCode = "InvalidFilterAttributeName";
                errorMessage = $"Filter attribute name '{attributeName}' is not a valid identifier (letters, digits and underscore, optionally prefixed with '&').";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (existingConditions != null)
            {
                foreach (var existing in existingConditions)
                    seen.Add(existing.ToString());
            }

            if (conditions == null) return true;
            for (int i = 0; i < conditions.Count; i++)
            {
                string condition = conditions[i];
                if (string.IsNullOrWhiteSpace(condition))
                {
                    errorCode = "FilterConditionEmpty";
                    errorMessage = $"conditions[{i}] is empty; every filter condition must be a non-empty expression.";
                    return false;
                }

                if (!seen.Add(condition))
                {
                    errorCode = "FilterConditionDuplicate";
                    errorMessage = $"conditions[{i}] '{condition}' repeats another condition in the request or one already present on the filter.";
                    return false;
                }
            }

            return true;
        }

        /// Filter authoring writes only through the native WorkWith SDK. Authoring the
        /// filter through raw PatternInstance XML is exactly what the structural preflight
        /// refuses, so there is deliberately no XML write fallback here (issue #559).
        /// `defaultValue` is the schema's `default` field: stored as given, never compiled
        /// (issue #588).
        public static bool AddFilterAttribute(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            string levelId,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            FilterObjectLookup lookup,
            bool dryRun,
            out JObject result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = null;
            errorMessage = null;

            if (instanceObj == null)
            {
                errorCode = "NullInstance";
                errorMessage = "WorkWith PatternInstance object is null.";
                return false;
            }

            if (!ListFilters(instanceObj, patterns, levelId, out var existingFilters, out var preCheckSource, out errorCode, out errorMessage))
                return false;

            var existingAttrs = existingFilters["attributes"] as JArray ?? new JArray();
            var existingConditions = existingFilters["conditions"] as JArray;
            string resolvedLevel = existingFilters["level"]?.ToString();

            if (!ValidateFilterRequest(attributeName, conditions, existingConditions, out errorCode, out errorMessage))
                return false;

            if (existingAttrs.Any(a => string.Equals(a["name"]?.ToString(), attributeName, StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "FilterAttributeAlreadyExists";
                errorMessage = $"Filter attribute '{attributeName}' already exists in WorkWith instance '{instanceObj.Name}'.";
                return false;
            }

            if (!TryResolveFilterDomain(domain, lookup, out var domainResolution, out errorCode, out errorMessage))
                return false;

            JObject domainAdvisory = null;
            if (domainResolution != null)
                domainAdvisory = BuildFilterDomainAdvisory(attributeName, ResolveFilterAttributeTarget(attributeName, lookup), domainResolution);

            var plan = BuildFilterPlan(instanceObj.Name, resolvedLevel, attributeName, description, domain, defaultValue, conditions, domainResolution, domainAdvisory);

            if (GetWorkWithAssembly() == null)
            {
                result = plan;
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: the Artech.Patterns.WorkWith assembly could not be resolved. Filter writes have no XML fallback.";
                return false;
            }

            if (dryRun)
            {
                result = plan;
                return true;
            }

            if (!TryAddFilterAttributeNative(instanceObj, resolvedLevel, attributeName, description, domainResolution, defaultValue, conditions, out string domainAppliedThrough, out errorCode, out errorMessage))
            {
                result = plan;
                return false;
            }

            if (!ListFilters(instanceObj, patterns, resolvedLevel, out var verifyFilters, out var observedSource, out _, out _))
            {
                errorCode = "VerificationReadFailed";
                errorMessage = "Filter attribute was saved, but verification re-read failed.";
                return false;
            }

            var verifiedAttrs = verifyFilters["attributes"] as JArray ?? new JArray();
            var verifiedConditions = verifyFilters["conditions"] as JArray ?? new JArray();
            int expectedAttributeCount = existingAttrs.Count + 1;
            if (verifiedAttrs.Count < expectedAttributeCount)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Filter attributes were truncated after write: expected at least {expectedAttributeCount}, found {verifiedAttrs.Count}.";
                return false;
            }

            var verifiedAttr = verifiedAttrs.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), attributeName, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (verifiedAttr == null)
            {
                errorCode = "VerificationFailed";
                errorMessage = $"Filter attribute '{attributeName}' was not found in the verified post-write state.";
                return false;
            }

            // The re-read reports the plain Domain name (DomainName), so a request made
            // by GUID or `{guid}-Name` is verified against the resolved name.
            if (!VerifyFilterAttributeMatches(verifiedAttr, verifiedConditions, attributeName, description, domainResolution?.Name, defaultValue, conditions, out string mismatchDetail))
            {
                errorCode = "VerificationMismatch";
                errorMessage = $"Filter attribute '{attributeName}' was persisted, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            result = BuildFilterReceipt(instanceObj.Name, resolvedLevel, verifiedAttr, verifiedConditions, attributeName, description, domain, defaultValue, conditions, preCheckSource, observedSource, domainResolution, domainAdvisory, domainAppliedThrough);
            return true;
        }

        internal static JObject BuildFilterPlan(
            string instanceName,
            string level,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            FilterDomainResolution domainResolution = null,
            JObject domainAdvisory = null)
        {
            var filterAttribute = new JObject { ["name"] = attributeName };
            if (!string.IsNullOrWhiteSpace(description)) filterAttribute["description"] = description;
            string plannedDomain = domainResolution?.Name ?? domain;
            if (!string.IsNullOrWhiteSpace(plannedDomain)) filterAttribute["domain"] = plannedDomain;
            if (!string.IsNullOrWhiteSpace(defaultValue)) filterAttribute["default"] = defaultValue;

            var validations = new JObject
            {
                ["targetObjectResolved"] = true,
                ["workWithInstanceResolved"] = true,
                ["levelResolved"] = true,
                ["filterPresent"] = true,
                ["attributeNameFormatValid"] = true,
                ["attributeNameUnique"] = true,
                ["conditionsNonEmptyAndUnique"] = true
            };
            if (domainResolution != null)
            {
                validations["domainResolved"] = true;
                validations["domainAppliesToVariable"] = domainAdvisory == null || domainAdvisory["appliesToVariable"]?.ToObject<bool>() != false;
            }

            var plan = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = filterAttribute,
                ["conditions"] = new JArray(conditions ?? Array.Empty<string>()),
                ["route"] = "native-sdk",
                ["validations"] = validations,
                ["validationLimits"] = new JObject
                {
                    ["attributeName"] = "The filter attribute name is stored as given; it is looked up as a KB Attribute only to classify the Domain advisory, not validated.",
                    ["domain"] = "The Domain is resolved against KB Domains by name or GUID and applied as a typed reference; its fitness for the filter variable's type is not evaluated and the pattern's own resolver is not run.",
                    ["default"] = "The default value is stored as given; it is not compiled or validated against the type of the attribute or variable.",
                    ["conditionExpressions"] = "Filter condition expressions are not compiled or evaluated."
                },
                ["defaultNormalization"] = NativeDefaultNormalization
            };

            if (domainResolution != null) plan["domainResolution"] = DescribeDomainResolution(domainResolution, appliedThrough: null);
            if (domainAdvisory != null) plan["domainAdvisory"] = domainAdvisory;
            return plan;
        }

        internal sealed class NativeFilterSurface
        {
            public MethodInfo Load;
            public PropertyInfo Levels;
            public MethodInfo SaveTo;
            public MethodInfo AddFilterAttribute;
            public MethodInfo AddCondition;
            public PropertyInfo Description;
            /// Typed Domain reference setter (the supported write path on GeneXus 18).
            public PropertyInfo Domain;
            /// String setter fallback; GeneXus 18 exposes DomainName read-only, so this is null there.
            public PropertyInfo DomainName;
            public PropertyInfo Default;
        }

        /// GeneXus 18 exposes `Domain` as a writable Domain reference and `DomainName` as
        /// a read-only projection of it (`Domain?.Name`), so the typed reference is the
        /// supported write path; a string setter is kept only as the fallback for SDK
        /// builds that expose one.
        internal static void SelectDomainSetters(Type attributeType, out PropertyInfo typedDomain, out PropertyInfo stringDomain)
        {
            typedDomain = null;
            stringDomain = null;
            if (attributeType == null) return;

            var domain = attributeType.GetProperty("Domain");
            if (domain != null && domain.CanWrite && domain.PropertyType != typeof(string)) typedDomain = domain;
            stringDomain = new[] { attributeType.GetProperty("DomainName"), domain }
                .FirstOrDefault(p => p != null && p.CanWrite && p.PropertyType == typeof(string));
        }

        /// Applies the resolved Domain to a new filter attribute element: the typed
        /// reference when the SDK accepts the resolved object, the string setter otherwise.
        internal static bool TryApplyFilterDomain(
            PropertyInfo typedDomain,
            PropertyInfo stringDomain,
            object attributeElement,
            FilterDomainResolution domain,
            out string appliedThrough,
            out string failure)
        {
            appliedThrough = null;
            failure = null;
            if (domain == null) return true;

            if (typedDomain != null && domain.Instance != null && typedDomain.PropertyType.IsInstanceOfType(domain.Instance))
            {
                typedDomain.SetValue(attributeElement, domain.Instance, null);
                appliedThrough = "Domain";
                return true;
            }

            if (stringDomain != null)
            {
                stringDomain.SetValue(attributeElement, domain.Name, null);
                appliedThrough = "DomainName";
                return true;
            }

            failure = typedDomain != null
                ? $"the resolved Domain '{domain.Name}' ({domain.Instance?.GetType().FullName ?? "null"}) is not accepted by FilterAttributeElement.Domain ({typedDomain.PropertyType.FullName}) and no DomainName string setter exists"
                : "FilterAttributeElement exposes neither a Domain reference setter nor a DomainName string setter";
            return false;
        }

        internal static bool TryResolveNativeFilterSurface(
            Type instanceType,
            bool needsDescription,
            bool needsDomain,
            bool needsDefault,
            bool needsConditions,
            out NativeFilterSurface surface,
            out string missingMember)
        {
            surface = null;
            missingMember = null;
            var asm = GetWorkWithAssembly();
            if (asm == null)
            {
                missingMember = "the Artech.Patterns.WorkWith assembly could not be resolved";
                return false;
            }

            try
            {
                var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                var filterType = asm.GetType("Artech.Patterns.WorkWith.FilterElement");
                var resolved = new NativeFilterSurface
                {
                    Load = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static),
                    Levels = wwiType?.GetProperty("Levels"),
                    SaveTo = (instanceType != null ? wwiType?.GetMethod("SaveTo", new[] { instanceType }) : null)
                        ?? wwiType?.GetMethod("SaveTo"),
                    AddFilterAttribute = filterType?.GetMethod("AddFilterAttribute", new[] { typeof(string) }),
                    AddCondition = filterType?.GetMethod("AddCondition", new[] { typeof(string) })
                };
                var attributeType = resolved.AddFilterAttribute?.ReturnType;
                resolved.Description = attributeType?.GetProperty("Description");
                SelectDomainSetters(attributeType, out resolved.Domain, out resolved.DomainName);
                var defaultProperty = attributeType?.GetProperty("Default");
                resolved.Default = defaultProperty != null && defaultProperty.CanWrite && defaultProperty.PropertyType == typeof(string)
                    ? defaultProperty
                    : null;

                var missing = new List<string>();
                if (wwiType == null) missing.Add("WorkWithInstance");
                if (resolved.Load == null) missing.Add("WorkWithInstance.Load");
                if (resolved.Levels == null) missing.Add("WorkWithInstance.Levels");
                if (resolved.SaveTo == null) missing.Add("WorkWithInstance.SaveTo");
                if (filterType == null) missing.Add("FilterElement");
                if (resolved.AddFilterAttribute == null) missing.Add("FilterElement.AddFilterAttribute(string)");
                if (needsConditions && resolved.AddCondition == null) missing.Add("FilterElement.AddCondition(string)");
                if (needsDescription && (resolved.Description == null || !resolved.Description.CanWrite)) missing.Add("FilterAttributeElement.Description setter");
                if (needsDomain && resolved.Domain == null && resolved.DomainName == null) missing.Add("FilterAttributeElement.Domain reference setter (or DomainName string setter)");
                if (needsDefault && resolved.Default == null) missing.Add("FilterAttributeElement.Default string setter");
                if (missing.Count > 0)
                {
                    missingMember = "unresolved member(s): " + string.Join(", ", missing);
                    return false;
                }

                surface = resolved;
                return true;
            }
            catch (Exception ex)
            {
                missingMember = "surface reflection failed: " + ex.Message;
                return false;
            }
        }

        /// Native write: every SDK member is resolved before the typed model is touched,
        /// and nothing is saved unless every requested piece was applied.
        internal static bool TryAddFilterAttributeNative(
            KBObject instanceObj,
            string levelId,
            string attributeName,
            string description,
            FilterDomainResolution domain,
            string defaultValue,
            IList<string> conditions,
            out string domainAppliedThrough,
            out string errorCode,
            out string errorMessage)
        {
            domainAppliedThrough = null;
            errorCode = null;
            errorMessage = null;
            bool hasDescription = !string.IsNullOrWhiteSpace(description);
            bool hasDomain = domain != null;
            bool hasDefault = !string.IsNullOrWhiteSpace(defaultValue);
            bool hasConditions = conditions != null && conditions.Count > 0;

            if (!TryResolveNativeFilterSurface(instanceObj?.GetType(), hasDescription, hasDomain, hasDefault, hasConditions, out var surface, out string missingMember))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: " + missingMember + ". Filter writes have no XML fallback.";
                return false;
            }

            bool writtenToPart = false;
            try
            {
                object wwi = surface.Load.Invoke(null, new object[] { instanceObj });
                if (wwi == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "WorkWithInstance.Load returned null for the PatternInstance.";
                    return false;
                }

                if (!TryAuthorFilterAttributeOnTypedModel(surface, wwi, levelId, attributeName, description, domain, defaultValue, conditions, out _, out domainAppliedThrough, out errorCode, out errorMessage))
                    return false;

                surface.SaveTo.Invoke(wwi, new object[] { instanceObj });
                writtenToPart = true;
                instanceObj.Save();
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "Native WorkWith filter authoring failed: " + cause.Message
                    + (writtenToPart
                        ? " The typed model was written to the in-memory PatternInstance part but Save failed; reopen the object before retrying."
                        : " Nothing was saved.");
                return false;
            }
        }

        /// Mutates the loaded typed model only: resolves the Level, adds the filter
        /// attribute, applies the supplied fields and conditions. The caller owns
        /// `SaveTo` and the KBObject save, so this step can be exercised without a KB.
        /// SDK exceptions propagate to the caller's handler.
        internal static bool TryAuthorFilterAttributeOnTypedModel(
            NativeFilterSurface surface,
            object workWith,
            string levelId,
            string attributeName,
            string description,
            FilterDomainResolution domain,
            string defaultValue,
            IList<string> conditions,
            out object newAttribute,
            out string domainAppliedThrough,
            out string errorCode,
            out string errorMessage)
        {
            newAttribute = null;
            domainAppliedThrough = null;
            errorCode = null;
            errorMessage = null;

            var levels = surface.Levels.GetValue(workWith, null) as IEnumerable;
            var candidates = levels != null ? CollectNativeFilterLevels(levels) : new List<FilterLevelCandidate>();
            if (!TryResolveFilterLevel(candidates, levelId, out var resolved, out errorCode, out errorMessage))
                return false;

            object created = surface.AddFilterAttribute.Invoke(resolved.Filter, new object[] { attributeName });
            if (created == null)
            {
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "FilterElement.AddFilterAttribute returned null; nothing was saved.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(description)) surface.Description.SetValue(created, description, null);
            if (domain != null && !TryApplyFilterDomain(surface.Domain, surface.DomainName, created, domain, out domainAppliedThrough, out string domainFailure))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter authoring is unavailable: " + domainFailure + ". Nothing was saved.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(defaultValue)) surface.Default.SetValue(created, defaultValue, null);
            if (conditions != null)
            {
                foreach (var condition in conditions)
                    surface.AddCondition.Invoke(resolved.Filter, new object[] { condition });
            }

            newAttribute = created;
            return true;
        }

        /// Only the fields the request supplied are compared; `default` joins the list
        /// exactly like description and domain, so an omitted request value is never
        /// compared against the reader's "" normalization.
        internal static string[] ComparedFilterFields(string description, string domain, string defaultValue)
        {
            var fields = new List<string> { "name" };
            if (!string.IsNullOrWhiteSpace(description)) fields.Add("description");
            if (!string.IsNullOrWhiteSpace(domain)) fields.Add("domain");
            if (!string.IsNullOrWhiteSpace(defaultValue)) fields.Add("default");
            fields.Add("conditions");
            return fields.ToArray();
        }

        internal static bool VerifyFilterAttributeMatches(
            JObject observedAttribute,
            JArray observedConditions,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (observedAttribute == null)
            {
                mismatchDetail = "Filter attribute not found in verified state.";
                return false;
            }

            string observedName = observedAttribute["name"]?.ToString() ?? "";
            if (!string.Equals(observedName, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                mismatchDetail = $"Filter attribute name mismatch: expected '{attributeName}', observed '{observedName}'.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                string observedDescription = observedAttribute["description"]?.ToString() ?? "";
                if (!string.Equals(observedDescription, description, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Description mismatch: expected '{description}', observed '{observedDescription}'.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                string observedDomain = observedAttribute["domain"]?.ToString() ?? "";
                if (!string.Equals(observedDomain, domain, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Domain mismatch: expected '{domain}', observed '{observedDomain}'.";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                string observedDefault = observedAttribute["default"]?.ToString() ?? "";
                if (!string.Equals(observedDefault, defaultValue, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Default mismatch: expected '{defaultValue}', observed '{observedDefault}'.";
                    return false;
                }
            }

            var observed = observedConditions?.Select(c => c.ToString()).ToList() ?? new List<string>();
            foreach (var condition in conditions ?? Array.Empty<string>())
            {
                if (!observed.Contains(condition, StringComparer.Ordinal))
                {
                    mismatchDetail = $"Condition '{condition}' was not found in the verified filter conditions [{string.Join(", ", observed)}].";
                    return false;
                }
            }

            return true;
        }

        internal static JObject BuildFilterReceipt(
            string instanceName,
            string level,
            JObject observedAttribute,
            JArray observedConditions,
            string attributeName,
            string description,
            string domain,
            string defaultValue,
            IList<string> conditions,
            WorkWithReadSource preCheckSource,
            WorkWithReadSource observedSource,
            FilterDomainResolution domainResolution = null,
            JObject domainAdvisory = null,
            string domainAppliedThrough = null)
        {
            var verification = new JObject
            {
                ["scope"] = "filter_attribute_reread",
                ["fieldsCompared"] = new JArray(ComparedFilterFields(description, domain, defaultValue)),
                ["allFieldsMatched"] = true
            };
            DescribeReadSource(verification, "observedSource", observedSource);
            DescribeReadSource(verification, "preCheckSource", preCheckSource);

            var receipt = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = observedAttribute.DeepClone(),
                ["conditions"] = observedConditions.DeepClone(),
                ["requested"] = new JObject
                {
                    ["name"] = attributeName,
                    ["description"] = description ?? "",
                    ["domain"] = domain ?? "",
                    ["default"] = defaultValue ?? "",
                    ["conditions"] = new JArray(conditions ?? Array.Empty<string>())
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = true,
                ["verification"] = verification,
                ["route"] = "native-sdk",
                ["verificationScope"] = "filter attribute definition, the Level's filter conditions, filter attribute count integrity, and " + DescribeRereadScope(observedSource),
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed",
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };

            if (domainResolution != null) receipt["domainResolution"] = DescribeDomainResolution(domainResolution, domainAppliedThrough ?? "Domain");
            if (domainAdvisory != null) receipt["domainAdvisory"] = domainAdvisory;
            return receipt;
        }
    }
}
