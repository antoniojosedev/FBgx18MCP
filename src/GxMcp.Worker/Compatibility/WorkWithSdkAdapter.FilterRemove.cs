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
        // ── remove_filter_attribute (issue #586) ─────────────────────────────────

        /// Reads the filter of one Level as `list_filters` projects it. The remove
        /// orchestration takes this as a delegate so the native collection write can
        /// be exercised against a fake typed model without a Knowledge Base.
        internal delegate bool FilterReader(string levelId, out JObject filters, out string error, out string errorMessage);

        private static readonly Regex FilterAttributeIdentifierPattern = new Regex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

        internal const string RemoveFilterNoXmlFallback = "Filter writes have no XML fallback.";

        private static readonly string[] RemoveFilterReferencesChecked =
        {
            "conditions of the resolved Level's filter (lexical &Variable reference analysis)",
            "default of every neighbor filter attribute on the same filter"
        };

        private static readonly string[] RemoveFilterReferencesNotVerified =
        {
            "WebForm, Events and Rules of the derived objects (Selection WebPanel, <Trn>General)",
            "other Levels of this instance and other pattern instances",
            "Knowledge Base objects outside the WorkWith instance"
        };

        /// Filter removal writes only through the native WorkWith SDK, exactly like
        /// AddFilterAttribute: the typed FilterElement exposes no removal member, so the
        /// write operates the native Attributes/Conditions collections (BaseCollection
        /// Remove) and refuses with WorkWithSdkUnavailable when that surface is absent.
        /// There is deliberately no XML write fallback (issue #586).
        public static bool RemoveFilterAttribute(
            KBObject instanceObj,
            PatternAnalysisService patterns,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
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

            // The post-save reread goes through ListFilters, which may fall back to the XML
            // projection; the receipt reports the reader that actually answered (issue #589).
            var observedSource = WorkWithReadSource.NativeInstance;
            FilterReader reader = (string level, out JObject filters, out string error, out string message) =>
                ListFilters(instanceObj, patterns, level, out filters, out observedSource, out error, out message);

            bool succeeded = RemoveFilterAttributeCore(
                instanceObj,
                instanceObj.Name,
                GetWorkWithAssembly(),
                reader,
                () => instanceObj.Save(),
                levelId,
                attributeName,
                removeConditions,
                dryRun,
                out result,
                out errorCode,
                out errorMessage);

            if (!dryRun && result?["verification"] is JObject verification && verification["observedSource"]?.ToString() != "none")
            {
                verification.Remove("observedSourceReason");
                DescribeReadSource(verification, "observedSource", observedSource);
                result["verificationScope"] = result["verificationScope"]?.ToString().Replace("post-write in-memory instance re-read", DescribeRereadScope(observedSource));
            }

            return succeeded;
        }

        internal static bool RemoveFilterAttributeCore(
            KBObject instanceObj,
            string instanceName,
            Assembly workWithAssembly,
            FilterReader readFilters,
            Action persist,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
            bool dryRun,
            out JObject result,
            out string errorCode,
            out string errorMessage)
        {
            result = null;
            errorCode = null;
            errorMessage = null;

            if (!readFilters(levelId, out var existingFilters, out errorCode, out errorMessage))
                return false;

            string resolvedLevel = existingFilters?["level"]?.ToString();
            if (!PlanFilterAttributeRemoval(instanceName, existingFilters, attributeName, removeConditions, out var plan, out errorCode, out errorMessage))
            {
                result = plan;
                return false;
            }

            if (workWithAssembly == null)
            {
                result = plan;
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter removal is unavailable: the Artech.Patterns.WorkWith assembly could not be resolved. " + RemoveFilterNoXmlFallback;
                return false;
            }

            if (dryRun)
            {
                result = plan;
                return true;
            }

            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            var conditionsToRemove = removeConditions ?? Array.Empty<string>();
            if (!TryRemoveFilterAttributeNative(instanceObj, workWithAssembly, persist, resolvedLevel, normalizedName, conditionsToRemove, out var removal, out errorCode, out errorMessage))
                return false;

            if (!readFilters(resolvedLevel, out var observedFilters, out var readError, out var readMessage))
            {
                // The write happened; the receipt must not claim a verified removal
                // without a reread, and it must not invent a new state either.
                result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, null, attributeName, normalizedName, conditionsToRemove,
                    verified: false, allFieldsMatched: null,
                    unverifiedReason: "verification re-read failed after save: " + (readError ?? "unknown") + (string.IsNullOrWhiteSpace(readMessage) ? "" : " - " + readMessage));
                return true;
            }

            var expectedAttributes = plan["preservedAttributes"] as JArray ?? new JArray();
            var expectedConditions = (plan["preservedConditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();
            if (!VerifyFilterAttributeRemoval(expectedAttributes, expectedConditions, normalizedName, conditionsToRemove, observedFilters, out string mismatchDetail))
            {
                result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, observedFilters, attributeName, normalizedName, conditionsToRemove,
                    verified: false, allFieldsMatched: false, unverifiedReason: mismatchDetail);
                errorCode = "VerificationMismatch";
                errorMessage = $"Filter attribute '{normalizedName}' was saved, but post-save verification failed: {mismatchDetail}";
                return false;
            }

            result = BuildRemoveFilterReceipt(instanceName, resolvedLevel, removal, observedFilters, attributeName, normalizedName, conditionsToRemove,
                verified: true, allFieldsMatched: true, unverifiedReason: null);
            return true;
        }

        private static string ConditionVerdictLabel(FilterConditionReferenceMatcher.ReferenceVerdict verdict)
        {
            switch (verdict)
            {
                case FilterConditionReferenceMatcher.ReferenceVerdict.Referenced: return "referenced";
                case FilterConditionReferenceMatcher.ReferenceVerdict.Unanalyzable: return "unanalyzable";
                default: return "not_referenced";
            }
        }

        /// Pure removal planner over the `list_filters` projection: validates the request,
        /// locates the target, classifies every condition with the reference matcher and
        /// refuses when an unlisted condition (or a neighbor's default) still references the
        /// variable. The plan it returns is the dryRun payload and the verification baseline.
        internal static bool PlanFilterAttributeRemoval(
            string instanceName,
            JObject existingFilters,
            string attributeName,
            IList<string> removeConditions,
            out JObject plan,
            out string errorCode,
            out string errorMessage)
        {
            plan = null;
            errorCode = null;
            errorMessage = null;

            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            if (!FilterAttributeIdentifierPattern.IsMatch(normalizedName))
            {
                errorCode = "InvalidFilterAttributeName";
                errorMessage = $"Filter attribute name '{attributeName}' is not a valid identifier (letters, digits and underscore, optionally prefixed with '&').";
                return false;
            }

            var requested = removeConditions ?? Array.Empty<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < requested.Count; i++)
            {
                string condition = requested[i];
                if (string.IsNullOrWhiteSpace(condition))
                {
                    errorCode = "FilterConditionEmpty";
                    errorMessage = $"removeConditions[{i}] is empty; every condition to remove must be its exact text.";
                    return false;
                }

                if (!seen.Add(condition))
                {
                    errorCode = "FilterConditionDuplicate";
                    errorMessage = $"removeConditions[{i}] '{condition}' repeats another entry of removeConditions; list each condition once (every exact occurrence on the filter is removed).";
                    return false;
                }
            }

            string level = existingFilters?["level"]?.ToString();
            var existingAttrs = existingFilters?["attributes"] as JArray ?? new JArray();
            var existingConditions = (existingFilters?["conditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();

            var target = existingAttrs.FirstOrDefault(a => string.Equals(a["name"]?.ToString(), normalizedName, StringComparison.OrdinalIgnoreCase)) as JObject;
            if (target == null)
            {
                string available = existingAttrs.Count > 0 ? string.Join(", ", existingAttrs.Select(a => a["name"]?.ToString())) : "(none)";
                errorCode = "FilterAttributeNotFound";
                errorMessage = $"Filter attribute '{normalizedName}' was not found on the filter of Level '{level}' in WorkWith instance '{instanceName}'. Available filter attributes: {available}.";
                return false;
            }

            var removeEntries = new JArray();
            for (int i = 0; i < requested.Count; i++)
            {
                int occurrences = existingConditions.Count(c => string.Equals(c, requested[i], StringComparison.Ordinal));
                if (occurrences == 0)
                {
                    errorCode = "FilterConditionNotFound";
                    errorMessage = $"removeConditions[{i}] '{requested[i]}' is not present on the filter of Level '{level}' (exact, case-sensitive text). Present conditions: [{string.Join(", ", existingConditions)}].";
                    return false;
                }

                var analysis = FilterConditionReferenceMatcher.Analyze(requested[i], normalizedName);
                removeEntries.Add(new JObject
                {
                    ["value"] = requested[i],
                    ["occurrences"] = occurrences,
                    ["referencesAttribute"] = analysis.BlocksRemoval
                });
            }

            var referencing = new JArray();
            var incidental = new JArray();
            var analyzedTexts = new HashSet<string>(StringComparer.Ordinal);
            var unlisted = new List<string>();
            foreach (var condition in existingConditions)
            {
                if (!analyzedTexts.Add(condition)) continue;
                var analysis = FilterConditionReferenceMatcher.Analyze(condition, normalizedName);
                if (analysis.BlocksRemoval)
                {
                    bool listed = seen.Contains(condition);
                    referencing.Add(new JObject
                    {
                        ["value"] = condition,
                        ["verdict"] = ConditionVerdictLabel(analysis.Verdict),
                        ["reason"] = analysis.Reason ?? "",
                        ["listedForRemoval"] = listed
                    });
                    if (!listed) unlisted.Add(condition);
                }
                else if (analysis.IncidentalMentions.Count > 0)
                {
                    incidental.Add(new JObject
                    {
                        ["value"] = condition,
                        ["mentions"] = new JArray(analysis.IncidentalMentions)
                    });
                }
            }

            var neighborDefaultReferences = new JArray();
            var preservedAttributes = new JArray();
            foreach (var attr in existingAttrs.OfType<JObject>())
            {
                if (ReferenceEquals(attr, target)) continue;
                preservedAttributes.Add(attr.DeepClone());
                string neighborDefault = attr["default"]?.ToString();
                if (string.IsNullOrWhiteSpace(neighborDefault)) continue;
                var analysis = FilterConditionReferenceMatcher.Analyze(neighborDefault, normalizedName);
                if (analysis.BlocksRemoval)
                {
                    neighborDefaultReferences.Add(new JObject
                    {
                        ["attribute"] = attr["name"]?.ToString() ?? "",
                        ["default"] = neighborDefault,
                        ["verdict"] = ConditionVerdictLabel(analysis.Verdict),
                        ["reason"] = analysis.Reason ?? ""
                    });
                }
            }

            var preservedConditions = new JArray(existingConditions.Where(c => !seen.Contains(c)));

            plan = new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["filterAttribute"] = target.DeepClone(),
                ["removeConditions"] = removeEntries,
                ["referencingConditions"] = referencing,
                ["incidentalMentions"] = incidental,
                ["neighborDefaultReferences"] = neighborDefaultReferences,
                ["preservedAttributes"] = preservedAttributes,
                ["preservedConditions"] = preservedConditions,
                ["route"] = "native-sdk",
                ["validations"] = new JObject
                {
                    ["targetObjectResolved"] = true,
                    ["workWithInstanceResolved"] = true,
                    ["levelResolved"] = true,
                    ["filterPresent"] = true,
                    ["attributeNameFormatValid"] = true,
                    ["attributeFound"] = true,
                    ["removeConditionsNonEmptyAndUnique"] = true,
                    ["removeConditionsPresentOnFilter"] = true,
                    ["noUnlistedConditionReferencesAttribute"] = unlisted.Count == 0,
                    ["noNeighborDefaultReferencesAttribute"] = neighborDefaultReferences.Count == 0
                },
                ["validationLimits"] = new JObject
                {
                    ["conditionReferences"] = "References are found lexically: a whole &Variable token outside string literals and comments. Bare attribute names, member accesses and literal/comment text are reported as incidental mentions, not references. A condition that cannot be analyzed is treated as a reference (fail-closed).",
                    ["referencesOutsideConditions"] = "Only the listed scopes are checked; WebForm, Events and Rules of the derived objects are not read and may still reference the variable.",
                    ["conditionExpressions"] = "Filter condition expressions are not compiled or evaluated."
                },
                ["references"] = new JObject
                {
                    ["checked"] = new JArray(RemoveFilterReferencesChecked),
                    ["notVerified"] = new JArray(RemoveFilterReferencesNotVerified)
                },
                ["policy"] = new JObject
                {
                    ["duplicateRemoveConditions"] = "refused with FilterConditionDuplicate",
                    ["repeatedOccurrencesOnFilter"] = "every exact (Ordinal) occurrence of a listed condition is removed",
                    ["unlistedConditions"] = "never removed; an unlisted condition that references the variable refuses the removal",
                    ["filterNode"] = "the filter node and its attributes/conditions containers are never removed"
                },
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed"
            };

            if (unlisted.Count > 0)
            {
                errorCode = "FilterAttributeReferencedByConditions";
                errorMessage = $"Filter attribute '{normalizedName}' (&{normalizedName}) is still referenced by {unlisted.Count} condition(s) not listed in removeConditions: [{string.Join(" | ", unlisted)}]. List them in removeConditions to remove them together with the attribute, or keep the attribute.";
                return false;
            }

            if (neighborDefaultReferences.Count > 0)
            {
                errorCode = "FilterAttributeReferencedByAttributeDefault";
                errorMessage = $"Filter attribute '{normalizedName}' (&{normalizedName}) is referenced by the default of neighbor filter attribute(s): {string.Join(", ", neighborDefaultReferences.Select(n => n["attribute"]?.ToString()))}. Change those defaults in the GeneXus IDE before removing the attribute.";
                return false;
            }

            return true;
        }

        private sealed class NativeFilterRemovalSurface
        {
            public MethodInfo Load;
            public PropertyInfo Levels;
            public MethodInfo SaveTo;
            public PropertyInfo Attributes;
            public PropertyInfo Conditions;
        }

        private static bool TryResolveNativeFilterRemovalSurface(Assembly asm, Type instanceType, out NativeFilterRemovalSurface surface, out string missingMember)
        {
            surface = null;
            missingMember = null;
            if (asm == null)
            {
                missingMember = "the Artech.Patterns.WorkWith assembly could not be resolved";
                return false;
            }

            try
            {
                var wwiType = asm.GetType("Artech.Patterns.WorkWith.WorkWithInstance");
                var filterType = asm.GetType("Artech.Patterns.WorkWith.FilterElement");
                var resolved = new NativeFilterRemovalSurface
                {
                    Load = wwiType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static),
                    Levels = wwiType?.GetProperty("Levels"),
                    SaveTo = (instanceType != null ? wwiType?.GetMethod("SaveTo", new[] { instanceType }) : null)
                        ?? wwiType?.GetMethod("SaveTo"),
                    Attributes = filterType?.GetProperty("Attributes"),
                    Conditions = filterType?.GetProperty("Conditions")
                };

                var missing = new List<string>();
                if (wwiType == null) missing.Add("WorkWithInstance");
                if (resolved.Load == null) missing.Add("WorkWithInstance.Load");
                if (resolved.Levels == null) missing.Add("WorkWithInstance.Levels");
                if (resolved.SaveTo == null) missing.Add("WorkWithInstance.SaveTo");
                if (filterType == null) missing.Add("FilterElement");
                if (resolved.Attributes == null) missing.Add("FilterElement.Attributes");
                if (resolved.Conditions == null) missing.Add("FilterElement.Conditions");
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

        private static string ReadElementString(object element, string propertyName)
        {
            return element?.GetType().GetProperty(propertyName)?.GetValue(element, null)?.ToString();
        }

        /// The native collections (`BaseCollection<T>`) expose `Remove(T)`; a fake or a
        /// future SDK may expose it through a different declared parameter type, so the
        /// lookup falls back to any single-parameter `Remove` assignable from the element.
        private static MethodInfo ResolveCollectionRemove(object collection, object element)
        {
            if (collection == null || element == null) return null;
            var collectionType = collection.GetType();
            var elementType = element.GetType();
            var exact = collectionType.GetMethod("Remove", new[] { elementType });
            if (exact != null) return exact;
            return collectionType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Remove"
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType.IsAssignableFrom(elementType));
        }

        private static bool InvokeCollectionRemove(MethodInfo remove, object collection, object element)
        {
            object outcome = remove.Invoke(collection, new[] { element });
            return !(outcome is bool removed) || removed;
        }

        /// Native removal: every SDK member and every element to remove is resolved before
        /// the typed model is touched; the listed conditions (all exact occurrences) go
        /// first, then the attribute, then SaveTo and the instance Save. The filter node
        /// and its containers are never removed.
        internal static bool TryRemoveFilterAttributeNative(
            KBObject instanceObj,
            Assembly asm,
            Action persist,
            string levelId,
            string attributeName,
            IList<string> removeConditions,
            out JObject removal,
            out string errorCode,
            out string errorMessage)
        {
            removal = null;
            errorCode = null;
            errorMessage = null;
            string normalizedName = FilterConditionReferenceMatcher.NormalizeAttributeName(attributeName);
            var listed = new HashSet<string>(removeConditions ?? Array.Empty<string>(), StringComparer.Ordinal);

            if (!TryResolveNativeFilterRemovalSurface(asm, instanceObj?.GetType(), out var surface, out string missingMember))
            {
                errorCode = "WorkWithSdkUnavailable";
                errorMessage = "Native WorkWith filter removal is unavailable: " + missingMember + ". " + RemoveFilterNoXmlFallback;
                return false;
            }

            bool mutated = false;
            bool writtenToPart = false;
            try
            {
                object wwi = surface.Load.Invoke(null, new object[] { instanceObj });
                if (wwi == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "WorkWithInstance.Load returned null for the PatternInstance. Nothing was saved.";
                    return false;
                }

                var levels = surface.Levels.GetValue(wwi, null) as IEnumerable;
                var candidates = levels != null ? CollectNativeFilterLevels(levels) : new List<FilterLevelCandidate>();
                if (!TryResolveFilterLevel(candidates, levelId, out var resolved, out errorCode, out errorMessage))
                    return false;

                var attributes = surface.Attributes.GetValue(resolved.Filter, null) as IEnumerable;
                var conditions = surface.Conditions.GetValue(resolved.Filter, null) as IEnumerable;
                if (attributes == null || conditions == null)
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = "FilterElement.Attributes or FilterElement.Conditions returned null. Nothing was saved.";
                    return false;
                }

                object targetElement = null;
                foreach (var fa in attributes)
                {
                    if (string.Equals(ReadElementString(fa, "Name"), normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetElement = fa;
                        break;
                    }
                }

                if (targetElement == null)
                {
                    errorCode = "FilterAttributeNotFound";
                    errorMessage = $"Filter attribute '{normalizedName}' is no longer present on the native filter of Level '{resolved.Id}'. Nothing was saved.";
                    return false;
                }

                var conditionElements = new List<KeyValuePair<string, object>>();
                var unlistedReferences = new List<string>();
                foreach (var ce in conditions)
                {
                    string value = ReadElementString(ce, "Value");
                    if (value == null) continue;
                    if (listed.Contains(value))
                    {
                        conditionElements.Add(new KeyValuePair<string, object>(value, ce));
                        continue;
                    }

                    if (FilterConditionReferenceMatcher.Analyze(value, normalizedName).BlocksRemoval)
                        unlistedReferences.Add(value);
                }

                var missingListed = listed.Where(l => conditionElements.All(ce => !string.Equals(ce.Key, l, StringComparison.Ordinal))).ToList();
                if (missingListed.Count > 0)
                {
                    errorCode = "FilterConditionNotFound";
                    errorMessage = $"Listed condition(s) are no longer present on the native filter of Level '{resolved.Id}': [{string.Join(" | ", missingListed)}]. The filter changed since it was read; nothing was saved.";
                    return false;
                }

                if (unlistedReferences.Count > 0)
                {
                    errorCode = "FilterAttributeReferencedByConditions";
                    errorMessage = $"Filter attribute '{normalizedName}' is referenced by condition(s) not listed in removeConditions on the native filter: [{string.Join(" | ", unlistedReferences)}]. Nothing was saved.";
                    return false;
                }

                var removeAttribute = ResolveCollectionRemove(attributes, targetElement);
                if (removeAttribute == null)
                {
                    errorCode = "WorkWithSdkUnavailable";
                    errorMessage = "Native WorkWith filter removal is unavailable: unresolved member(s): FilterAttributesElement.Remove(FilterAttributeElement). " + RemoveFilterNoXmlFallback;
                    return false;
                }

                MethodInfo removeCondition = null;
                if (conditionElements.Count > 0)
                {
                    removeCondition = ResolveCollectionRemove(conditions, conditionElements[0].Value);
                    if (removeCondition == null)
                    {
                        errorCode = "WorkWithSdkUnavailable";
                        errorMessage = "Native WorkWith filter removal is unavailable: unresolved member(s): ConditionsElement.Remove(ConditionElement). " + RemoveFilterNoXmlFallback;
                        return false;
                    }
                }

                var removedAttribute = new JObject
                {
                    ["name"] = ReadElementString(targetElement, "Name") ?? normalizedName,
                    ["description"] = ReadElementString(targetElement, "Description") ?? normalizedName,
                    ["domain"] = ReadElementString(targetElement, "DomainName") ?? ReadElementString(targetElement, "Domain") ?? "",
                    ["variableName"] = ReadElementString(targetElement, "VariableName") ?? ("&" + normalizedName)
                };

                var removedConditions = new JArray();
                foreach (var group in conditionElements.GroupBy(ce => ce.Key, StringComparer.Ordinal))
                {
                    int removedCount = 0;
                    foreach (var pair in group)
                    {
                        mutated = true;
                        if (!InvokeCollectionRemove(removeCondition, conditions, pair.Value))
                        {
                            errorCode = "WorkWithFilterAuthoringFailed";
                            errorMessage = $"The native Conditions collection refused to remove '{pair.Key}'. The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved.";
                            return false;
                        }
                        removedCount++;
                    }
                    removedConditions.Add(new JObject { ["value"] = group.Key, ["occurrencesRemoved"] = removedCount });
                }

                mutated = true;
                if (!InvokeCollectionRemove(removeAttribute, attributes, targetElement))
                {
                    errorCode = "WorkWithFilterAuthoringFailed";
                    errorMessage = $"The native Attributes collection refused to remove '{normalizedName}'. The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved.";
                    return false;
                }

                surface.SaveTo.Invoke(wwi, new object[] { instanceObj });
                writtenToPart = true;
                persist?.Invoke();

                removal = new JObject
                {
                    ["removedFilterAttribute"] = removedAttribute,
                    ["removedConditions"] = removedConditions
                };
                return true;
            }
            catch (Exception ex)
            {
                var cause = ex is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : ex;
                errorCode = "WorkWithFilterAuthoringFailed";
                errorMessage = "Native WorkWith filter removal failed: " + cause.Message
                    + (writtenToPart
                        ? " The typed model was written to the in-memory PatternInstance part but Save failed; reopen the object before retrying."
                        : mutated
                            ? " The typed in-memory model may be partially modified; reopen the object before retrying. Nothing was saved."
                            : " Nothing was saved.");
                return false;
            }
        }

        /// Field-by-field verification of the reread filter: the removed attribute and
        /// every listed condition must be absent, every neighbor attribute must keep its
        /// name, description and domain in order, and every preserved condition text must
        /// come back in order. Counts alone are never enough.
        internal static bool VerifyFilterAttributeRemoval(
            JArray expectedAttributes,
            IList<string> expectedConditions,
            string removedAttributeName,
            IList<string> removedConditions,
            JObject observedFilters,
            out string mismatchDetail)
        {
            mismatchDetail = null;
            if (observedFilters == null)
            {
                mismatchDetail = "No filter state was observed after save.";
                return false;
            }

            string removedName = FilterConditionReferenceMatcher.NormalizeAttributeName(removedAttributeName);
            var observedAttrs = (observedFilters["attributes"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var observedConditions = (observedFilters["conditions"] as JArray)?.Select(c => c.ToString()).ToList() ?? new List<string>();

            if (observedAttrs.Any(a => string.Equals(a["name"]?.ToString(), removedName, StringComparison.OrdinalIgnoreCase)))
            {
                mismatchDetail = $"Filter attribute '{removedName}' is still present after save.";
                return false;
            }

            foreach (var removed in removedConditions ?? Array.Empty<string>())
            {
                if (observedConditions.Contains(removed, StringComparer.Ordinal))
                {
                    mismatchDetail = $"Condition '{removed}' is still present after save.";
                    return false;
                }
            }

            var expected = expectedAttributes?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (observedAttrs.Count != expected.Count)
            {
                mismatchDetail = $"Neighbor filter attribute count mismatch: expected {expected.Count} [{string.Join(", ", expected.Select(a => a["name"]?.ToString()))}], observed {observedAttrs.Count} [{string.Join(", ", observedAttrs.Select(a => a["name"]?.ToString()))}].";
                return false;
            }

            for (int i = 0; i < expected.Count; i++)
            {
                string expectedName = expected[i]["name"]?.ToString() ?? "";
                string observedName = observedAttrs[i]["name"]?.ToString() ?? "";
                if (!string.Equals(expectedName, observedName, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Neighbor filter attribute at position {i}: expected '{expectedName}', observed '{observedName}'.";
                    return false;
                }

                string expectedDescription = expected[i]["description"]?.ToString() ?? "";
                string observedDescription = observedAttrs[i]["description"]?.ToString() ?? "";
                if (!string.Equals(expectedDescription, observedDescription, StringComparison.Ordinal))
                {
                    mismatchDetail = $"Neighbor filter attribute '{expectedName}' description mismatch: expected '{expectedDescription}', observed '{observedDescription}'.";
                    return false;
                }

                string expectedDomain = expected[i]["domain"]?.ToString() ?? "";
                string observedDomain = observedAttrs[i]["domain"]?.ToString() ?? "";
                if (!string.Equals(expectedDomain, observedDomain, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchDetail = $"Neighbor filter attribute '{expectedName}' domain mismatch: expected '{expectedDomain}', observed '{observedDomain}'.";
                    return false;
                }
            }

            var expectedConditionList = expectedConditions ?? Array.Empty<string>();
            if (!observedConditions.SequenceEqual(expectedConditionList, StringComparer.Ordinal))
            {
                mismatchDetail = $"Preserved conditions mismatch: expected [{string.Join(" | ", expectedConditionList)}], observed [{string.Join(" | ", observedConditions)}].";
                return false;
            }

            return true;
        }

        /// The receipt reuses the `saved`/`persisted`/`verified` trio of the other
        /// WorkWith receipts: a failed reread or a field mismatch is `verified: false` with
        /// its reason, never a new state and never a verified removal without a reread.
        internal static JObject BuildRemoveFilterReceipt(
            string instanceName,
            string level,
            JObject removal,
            JObject observedFilters,
            string requestedAttribute,
            string normalizedAttribute,
            IList<string> removeConditions,
            bool verified,
            bool? allFieldsMatched,
            string unverifiedReason)
        {
            var verification = new JObject
            {
                ["scope"] = "filter_reread",
                ["fieldsCompared"] = new JArray("attributes[].name", "attributes[].description", "attributes[].domain", "conditions[]"),
                ["allFieldsMatched"] = allFieldsMatched.HasValue ? new JValue(allFieldsMatched.Value) : JValue.CreateNull(),
                ["observedSource"] = observedFilters != null ? "in_memory_instance_reread" : "none",
                ["referencesChecked"] = new JArray(RemoveFilterReferencesChecked),
                ["referencesNotVerified"] = new JArray(RemoveFilterReferencesNotVerified)
            };
            if (!verified) verification["reason"] = unverifiedReason ?? "verification did not run";

            return new JObject
            {
                ["instance"] = instanceName,
                ["level"] = level,
                ["removedFilterAttribute"] = removal?["removedFilterAttribute"]?.DeepClone(),
                ["removedConditions"] = removal?["removedConditions"]?.DeepClone() ?? new JArray(),
                ["remainingAttributes"] = observedFilters?["attributes"]?.DeepClone(),
                ["remainingConditions"] = observedFilters?["conditions"]?.DeepClone(),
                ["requested"] = new JObject
                {
                    ["attribute"] = requestedAttribute ?? "",
                    ["normalizedAttribute"] = normalizedAttribute ?? "",
                    ["removeConditions"] = new JArray(removeConditions ?? Array.Empty<string>())
                },
                ["saved"] = true,
                ["persisted"] = true,
                ["verified"] = verified,
                ["verification"] = verification,
                ["route"] = "native-sdk",
                ["verificationScope"] = "removed filter attribute absence, listed condition absence, every neighbor filter attribute (name, description, domain) in order, every preserved condition text in order, and post-write in-memory instance re-read",
                ["defaultNormalization"] = NativeDefaultNormalization,
                ["derivedObjectRegeneration"] = "not_guaranteed",
                ["persistenceEvidence"] = BuildPersistenceEvidence()
            };
        }
    }
}
