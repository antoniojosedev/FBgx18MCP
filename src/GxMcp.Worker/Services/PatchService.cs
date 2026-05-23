using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Structure;

namespace GxMcp.Worker.Services
{
    public class PatchService
    {
        private sealed class SourceCacheEntry
        {
            public string Source { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly ConcurrentDictionary<string, SourceCacheEntry> _sourceCache =
            new ConcurrentDictionary<string, SourceCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SourceCacheTtl = TimeSpan.FromSeconds(20);

        private readonly ObjectService _objectService;
        private readonly WriteService _writeService;
        private readonly PatternAnalysisService _patternAnalysisService;

        public PatchService(ObjectService objectService, WriteService writeService, PatternAnalysisService patternAnalysisService = null)
        {
            _objectService = objectService;
            _writeService = writeService;
            _patternAnalysisService = patternAnalysisService;
        }

        /// <summary>
        /// v2.3.8 Task 3.3 — Pure-string {find, replace} patch shape that routes through
        /// <see cref="WriteService.TryMatch"/> so multi-line context with CRLF/LF mismatch
        /// or trailing-whitespace differences still resolves to a unique window. Returns a
        /// tuple of (ok, result). On failure result echoes the input source unchanged so
        /// callers can splice the value safely. Used directly by unit tests; the live
        /// editor path goes through ApplyPatch which also handles persistence.
        /// </summary>
        public static (bool ok, string result, string reason) ApplyFindReplace(string source, JObject patch)
        {
            if (source == null) return (false, string.Empty, "source is null");
            if (patch == null) return (false, source, "patch is null");
            string find = patch["find"]?.ToString();
            string replace = patch["replace"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(find)) return (false, source, "patch.find is required");

            if (WriteService.TryMatch(source, find, out int start, out int end) && end > start)
            {
                string spliced = source.Substring(0, start) + replace + source.Substring(end);
                return (true, spliced, null);
            }
            return (false, source, "NoMatch");
        }

        // Returns a warning if the agent is editing a visual part (WebForm/Layout) on
        // an object whose WorkWithPlus host has a PatternInstance — hand edits there
        // can be overwritten on next pattern apply/save.
        //
        // Resolution covers three target shapes:
        //   (1) The WWP host itself (`WorkWithPlus<X>`) — direct match.
        //   (2) The Transaction the host is built on — ResolveWWPInstance("WorkWithPlus"+name).
        //   (3) A generated WW WebPanel (`WW<X>`, `View<X>`, `Export*`, etc.) — these
        //       don't follow the `WorkWithPlus<X>` naming rule, so we resolve the host
        //       by checking GetParent() up to 3 levels, looking for a WWP-typed ancestor.
        private JArray BuildPatternShadowWarningsIfAny(string target, string partName, string typeFilter)
        {
            try
            {
                if (_patternAnalysisService == null || _objectService == null) return null;
                if (!GxMcp.Worker.Helpers.WebFormXmlHelper.IsVisualPart(partName)) return null;

                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null) return null;

                var resolved = _patternAnalysisService.ResolveWWPInstance(obj);

                // Fallback for generated WW family (WW<Trn>, View<Trn>, etc.): WWP host
                // for `WWX` is named `WorkWithPlusX`. We try a few common prefixes; the
                // ResolveWWPInstance also walks Children, so a hit here closes the gap
                // for the generated WebPanel case that motivated F6.
                if (resolved == null && !string.IsNullOrEmpty(obj.Name))
                {
                    string[] candidatePrefixes = { "WW", "View", "ViewWW", "Prompt" };
                    foreach (var pre in candidatePrefixes)
                    {
                        if (!obj.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                        string baseName = obj.Name.Substring(pre.Length);
                        if (string.IsNullOrEmpty(baseName)) continue;
                        var hostName = "WorkWithPlus" + baseName;
                        try
                        {
                            var host = _objectService.FindObject(hostName);
                            if (host != null && string.Equals(host.TypeDescriptor?.Name, "WorkWithPlus", StringComparison.OrdinalIgnoreCase))
                            {
                                resolved = host;
                                break;
                            }
                        }
                        catch { /* lookup best-effort */ }
                    }
                }

                if (resolved == null) return null;

                var part = _patternAnalysisService.FindPatternPart(resolved, "PatternInstance");
                if (part == null) return null;

                return new JArray
                {
                    new JObject
                    {
                        ["code"] = "EditingWebFormUnderPattern",
                        ["severity"] = "warning",
                        ["message"] =
                            "This object is covered by a WorkWithPlus PatternInstance ('" + resolved.Name +
                            "'). Hand edits to " + partName + " can be overwritten on the next pattern apply/save. " +
                            "Consider editing part=PatternInstance on '" + resolved.Name + "' instead. " +
                            "Toggle SDPlus_Editor_Apply_On_Save=False on " + resolved.Name +
                            " if you must keep a hard override on the visual part.",
                        ["patternInstance"] = resolved.Name
                    }
                };
            }
            catch (Exception ex)
            {
                Logger.Debug("[PatchService] PatternShadow warning probe skipped: " + ex.Message);
                return null;
            }
        }

        private static string AttachWarningsToJson(string json, JArray warnings)
        {
            if (warnings == null || warnings.Count == 0 || string.IsNullOrEmpty(json)) return json;
            try
            {
                var obj = JObject.Parse(json);
                obj["warnings"] = warnings;
                return obj.ToString();
            }
            catch
            {
                return json;
            }
        }

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1, string typeFilter = null, bool dryRun = false, bool verifyRollback = false, bool returnPostState = true, bool verbose = false, bool replaceAll = false)
        {
            // Friction 2026-05-22: capture entry timestamp so a NoMatch we see at
            // the end can be cross-checked against WriteService.WasTargetWrittenSince
            // — if the file changed while this patch was queued/running, the context
            // is stale (concurrent edit) rather than truly absent. Use strict UtcNow
            // (no backdating) so only writes that landed AFTER this patch entered
            // the read trigger the Stale verdict; a write that completed strictly
            // before patch entry is not concurrent with us.
            DateTime patchEnteredAtUtc = DateTime.UtcNow;
            try
            {
                // Probe pattern-shadow warning ONCE before doing any work. If the agent
                // is patching a WebForm/Layout on an object whose WorkWithPlus host has
                // a populated PatternInstance, attach a warning to the terminal response
                // so they know hand edits to the visual XML can be overwritten by the
                // next pattern apply/save. Mirrors the same probe in WriteService.WriteVisualPart.
                JArray patternShadowWarnings = BuildPatternShadowWarningsIfAny(target, partName, typeFilter);

                string cacheKey = BuildCacheKey(target, partName, typeFilter);
                bool sourceFromCache = false;
                long readMs = 0;
                // Stale-cache prevention: every patch starts from a fresh authoritative read.
                // Writes that bypass PatchService (or external IDE edits) can leave _sourceCache
                // holding pre-write text; trusting it produced silent NoChange / wrong-base patches.
                _sourceCache.TryRemove(cacheKey, out _);
                _objectService.MarkReadCacheDirty(_objectService.FindObject(target, typeFilter), partName);
                string originalSource = null;
                if (originalSource == null)
                {
                    var readStopwatch = Stopwatch.StartNew();
                    string currentResponse = ReadSourceFast(target, partName, typeFilter);
                    readStopwatch.Stop();
                    readMs = readStopwatch.ElapsedMilliseconds;
                    string readError = TryExtractError(currentResponse);
                    if (!string.IsNullOrWhiteSpace(readError))
                    {
                        return Models.McpResponse.Error("Patch read failed", target, partName, readError);
                    }

                    var json = JObject.Parse(currentResponse);
                    originalSource = json["source"]?.ToString();
                    if (originalSource == null)
                    {
                        return Models.McpResponse.Error("Patch read failed", target, partName, "Could not retrieve source for requested part.");
                    }
                    UpdateCachedSource(cacheKey, originalSource);
                }
                else
                {
                    sourceFromCache = true;
                }

                // Normalize line endings for internal processing
                string workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContext = context?.Replace("\r\n", "\n").Replace("\r", "\n");
                string workContent = (content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");

                var sourceLines = workSource.Split('\n');
                var contextLines = workContext?.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string normalizedOperation = NormalizeOperation(operation);

                // 2. Matching Logic
                string updatedSource = null;
                int matchCount = 0;
                string status = null;
                string details = null;

                if (expectedCount <= 0)
                {
                    return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "expectedCount must be >= 1.");
                }

                var patchStopwatch = Stopwatch.StartNew();
                switch (normalizedOperation)
                {
                    case "replace":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "'context' (old_string) is required for Replace.");

                        if (NormalizeSourceForComparison(workContext) == NormalizeSourceForComparison(workContent))
                        {
                            return BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, 1, "Patch content is identical to context. Write skipped.");
                        }

                        updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount, replaceAll);
                        break;

                    case "insert_after":
                        if (string.IsNullOrEmpty(workContext))
                            return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "'context' (anchor) is required for Insert_After.");
                        updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                        break;

                    case "append":
                        matchCount = 1;
                        if (string.IsNullOrWhiteSpace(workContent))
                        {
                            status = "NoChange";
                            details = "Append payload is empty. Write skipped.";
                            updatedSource = workSource;
                            break;
                        }
                        updatedSource = workSource.TrimEnd() + "\n" + workContent;
                        status = "Applied";
                        break;

                    default:
                        return BuildPatchResult("Error", partName, normalizedOperation, expectedCount, 0, "Unknown operation: " + operation);
                }
                patchStopwatch.Stop();
                long patchMs = patchStopwatch.ElapsedMilliseconds;

                // One guarded retry against stale cache: refresh source once and recompute.
                if (sourceFromCache &&
                    string.IsNullOrEmpty(updatedSource) &&
                    (string.Equals(status, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status, "Ambiguous", StringComparison.OrdinalIgnoreCase)))
                {
                    var refreshReadSw = Stopwatch.StartNew();
                    string refreshedResponse = ReadSourceFast(target, partName, typeFilter);
                    refreshReadSw.Stop();
                    readMs += refreshReadSw.ElapsedMilliseconds;
                    string refreshedError = TryExtractError(refreshedResponse);
                    if (string.IsNullOrWhiteSpace(refreshedError))
                    {
                        var refreshedJson = JObject.Parse(refreshedResponse);
                        string refreshedSource = refreshedJson["source"]?.ToString();
                        if (refreshedSource != null)
                        {
                            UpdateCachedSource(cacheKey, refreshedSource);
                            originalSource = refreshedSource;
                            workSource = originalSource.Replace("\r\n", "\n").Replace("\r", "\n");
                            sourceLines = workSource.Split('\n');
                            patchStopwatch.Restart();
                            if (normalizedOperation == "replace")
                            {
                                updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount, replaceAll);
                            }
                            else if (normalizedOperation == "insert_after")
                            {
                                updatedSource = TryInsertAfter(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
                            }
                            patchStopwatch.Stop();
                            patchMs += patchStopwatch.ElapsedMilliseconds;
                        }
                    }
                }

                if (string.IsNullOrEmpty(updatedSource))
                {
                    string dbg = string.Empty;
                    if (sourceLines.Length > 0 && contextLines?.Length > 0)
                    {
                        dbg = $" | Example: Source='{sourceLines[0].Trim()}' vs Context='{contextLines[0].Trim()}'";
                    }
                    Logger.Warn($"[PATCH] Match failed for {target}. Context: '{context}'{dbg}");

                    string failedStatus = string.IsNullOrWhiteSpace(status) ? "NoMatch" : status;
                    string failedDetails = string.IsNullOrWhiteSpace(details)
                        ? $"Context not found. Ensure the context matches a unique block in the source code.{dbg}"
                        : details;

                    // Friction 2026-05-22: distinguish "match truly absent" from
                    // "a sibling write to this same target landed before us".
                    // When the cross-check is positive, surface a distinct hint
                    // so the caller doesn't burn turns retrying with the same context.
                    bool concurrentWrite = WriteService.WasTargetWrittenSince(target, patchEnteredAtUtc);
                    if (concurrentWrite && string.Equals(failedStatus, "NoMatch", StringComparison.OrdinalIgnoreCase))
                    {
                        failedStatus = "Stale";
                        failedDetails = "File modified during patch (concurrent edit landed against the same target). Re-read the object source and retry with refreshed context. Original failure: " + failedDetails;
                    }

                    string failure = BuildPatchResult(failedStatus, partName, normalizedOperation, expectedCount, matchCount, failedDetails);

                    // FR#18 (friction-report 2026-05-14): attach near-match diagnostics so the
                    // agent can adjust context in one iteration instead of re-reading the whole
                    // file. Only runs on NoMatch (Ambiguous already gives the matched indices).
                    if (string.Equals(failedStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) &&
                        contextLines != null && contextLines.Length > 0 && contextLines.Length <= 50)
                    {
                        var near = FindNearMatches(sourceLines, contextLines, topN: 3);
                        if (near.Count > 0)
                        {
                            try
                            {
                                var failureJson = JObject.Parse(failure);
                                var arr = new JArray();
                                foreach (var nm in near)
                                {
                                    arr.Add(new JObject
                                    {
                                        ["line"] = nm.StartLine + 1, // 1-based for humans
                                        ["similarity"] = Math.Round(nm.Similarity, 2),
                                        ["snippet"] = nm.Snippet
                                    });
                                }
                                failureJson["nearMatches"] = arr;
                                failureJson["nearMatchHint"] = "Top similar windows in source. Adjust 'context' to match exact tabs/whitespace of one of these and retry.";

                                // v2.3.8 Task 3.2: byte-level divergence detail for the top window
                                // when similarity is high enough that the agent likely just has
                                // the wrong tabs/EOLs/one wrong char.
                                var top = near[0];
                                if (top.StartLine + contextLines.Length <= sourceLines.Length)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    for (int i = 0; i < contextLines.Length; i++)
                                    {
                                        if (i > 0) sb.Append('\n');
                                        sb.Append(sourceLines[top.StartLine + i]);
                                    }
                                    string bestWindow = sb.ToString();
                                    string ctxJoined = string.Join("\n", contextLines);

                                    if (top.Similarity >= 0.80)
                                    {
                                        failureJson["nearMatchHintDetail"] = GxMcp.Worker.Helpers.DiffBuilder.ByteLevelDivergence(bestWindow, ctxJoined);
                                    }

                                    // Item 4 (friction 2026-05-22): EOL mismatch short diff.
                                    // When context lines have different EOL from source, show the
                                    // first 3 lines the agent passed vs what the file actually has,
                                    // so the agent can see the divergence at a glance.
                                    int eolDiffLines = Math.Min(3, contextLines.Length);
                                    bool eolMismatchDetected = false;
                                    var eolDiffArr = new JArray();
                                    for (int i = 0; i < eolDiffLines; i++)
                                    {
                                        string agentLine = contextLines[i];
                                        string fileLine = sourceLines[top.StartLine + i];
                                        // Highlight if they differ only by EOL/trailing whitespace
                                        bool linesMatchNorm = string.Equals(agentLine.TrimEnd(), fileLine.TrimEnd(), StringComparison.Ordinal);
                                        bool linesMatchExact = string.Equals(agentLine, fileLine, StringComparison.Ordinal);
                                        if (linesMatchNorm && !linesMatchExact)
                                        {
                                            eolMismatchDetected = true;
                                        }
                                        eolDiffArr.Add(new JObject
                                        {
                                            ["lineNo"] = top.StartLine + i + 1,
                                            ["agent"] = ShowControlChars(agentLine),
                                            ["file"] = ShowControlChars(fileLine),
                                            ["match"] = linesMatchNorm ? "eol_only" : (linesMatchExact ? "exact" : "differs")
                                        });
                                    }
                                    if (eolMismatchDetected || top.Similarity >= 0.60)
                                    {
                                        failureJson["eolDiff"] = eolDiffArr;
                                        if (eolMismatchDetected)
                                        {
                                            failureJson["eolDiffHint"] = "Lines differ only in trailing whitespace or EOL. The find context is otherwise correct — the patch pipeline normalizes EOLs automatically, so this mismatch should not cause NoMatch. Check for invisible characters or tab/space mix beyond trailing whitespace.";
                                        }
                                    }

                                    // Item 17 (friction 2026-05-22): Levenshtein-based did_you_mean.
                                    // Compute edit distance between joined context and best window.
                                    // Threshold: distance <= ceil(0.20 * len(context)).
                                    // Guards: skip on huge contexts (O(n²) on STA thread would
                                    // stall all in-process operations) and on low-similarity
                                    // near-matches (the suggestion would point at unrelated code).
                                    int ctxLen = ctxJoined.Length;
                                    const int LevenshteinMaxLen = 2000;
                                    if (ctxLen <= LevenshteinMaxLen && top.Similarity >= 0.50)
                                    {
                                        int threshold = (int)Math.Ceiling(0.20 * ctxLen);
                                        int levenDist = LevenshteinDistance(ctxJoined, bestWindow, threshold + 1);
                                        if (levenDist <= threshold)
                                        {
                                            failureJson["did_you_mean"] = new JObject
                                            {
                                                ["startLine"] = top.StartLine + 1,
                                                ["endLine"] = top.StartLine + contextLines.Length,
                                                ["levenshteinDistance"] = levenDist,
                                                ["snippet"] = bestWindow.Length > 300 ? bestWindow.Substring(0, 297) + "..." : bestWindow
                                            };
                                        }
                                    }
                                }

                                failure = failureJson.ToString();
                            }
                            catch { /* keep original failure on serialization issue */ }
                        }
                    }

                    return AttachTimings(failure, readMs, patchMs, 0, sourceFromCache);
                }

                if (NormalizeForPartCompare(partName, workSource) == NormalizeForPartCompare(partName, updatedSource))
                {
                    // Friction 2026-05-22: distinguish the two NoChange cases.
                    // case-a: matched + content identical to context (caught earlier
                    //   inside switch).
                    // case-b: matched + replacement normalized back to original via
                    //   the part-normalizer (XML comment preservation, attribute
                    //   ordering, trailing whitespace). The match worked — the
                    //   serializer treated the change as semantically equivalent.
                    bool literalIdentical = string.Equals(workSource, updatedSource, StringComparison.Ordinal);
                    string detailMessage = literalIdentical
                        ? "Patch produced no effective changes. Write skipped."
                        : "Patch matched and applied, but the part-normalizer treated the replacement as equivalent to the original (often XML attribute ordering, comment preservation, or trailing whitespace). The on-disk file is unchanged. Inspect the serialized source diff and adjust semantics, not just text.";
                    string noChange = BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, matchCount, detailMessage);
                    try
                    {
                        var nj = JObject.Parse(noChange);
                        nj["noChangeReason"] = literalIdentical ? "literal_identical" : "serializer_normalized";
                        noChange = nj.ToString();
                    }
                    catch { }
                    return AttachTimings(noChange, readMs, patchMs, 0, sourceFromCache);
                }

                if (dryRun)
                {
                    string dryRunResult = BuildPatchResult("Applied", partName, normalizedOperation, expectedCount, matchCount, "Dry-run succeeded. Write skipped.");
                    dryRunResult = AttachWarningsToJson(dryRunResult, patternShadowWarnings);
                    return AttachTimings(dryRunResult, readMs, patchMs, 0, sourceFromCache);
                }

                // v2.6.6 FR#10 — invariant: never persist a payload that looks like
                // a NoMatch fall-through. Belt-and-braces against the v2.6.5 Events-part
                // friction where an empty/near-empty result reached the SDK save path
                // and the on-disk source was lost with sha256 = e3b0c44... (empty).
                bool anyOpApplied = string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase)
                                    || matchCount > 0;
                if (!WriteService.IsPatchWriteSafe(workSource, updatedSource, anyOpApplied, out string safetyReason))
                {
                    Logger.Error($"[PATCH] Refusing unsafe write for {target}/{partName}: reason={safetyReason} origLen={workSource?.Length ?? 0} newLen={updatedSource?.Length ?? 0}");
                    var safety = BuildPatchResult("NoMatch", partName, normalizedOperation, expectedCount, matchCount,
                        "Refused to persist suspected NoMatch payload (" + safetyReason + "). No write was attempted; on-disk source unchanged.");
                    try
                    {
                        var safetyJson = JObject.Parse(safety);
                        safetyJson["code"] = "patch_no_match";
                        safetyJson["safetyReason"] = safetyReason;
                        safety = safetyJson.ToString();
                    }
                    catch (Exception jsonEx)
                    {
                        Logger.Debug("[PATCH] safety envelope augmentation failed: " + jsonEx.Message);
                    }
                    return AttachTimings(safety, readMs, patchMs, 0, sourceFromCache);
                }

                // 3. Write Back (re-normalize to CRLF for GeneXus)
                string finalCode = updatedSource.Replace("\n", Environment.NewLine);
                var writeStopwatch = Stopwatch.StartNew();
                string writeResult = _writeService.WriteObject(target, partName, finalCode, typeFilter, autoValidate: false, preferFastSourceSave: true, autoInjectVariables: false);
                writeStopwatch.Stop();
                long writeMs = writeStopwatch.ElapsedMilliseconds;
                JObject writePayload = ParseWriteResult(writeResult);

                bool primaryWriteSuccess = string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                bool persistedMatches = false;
                // Pattern parts: trust WriteService's own XmlEquivalence verification (it runs
                // INSIDE WritePatternPart after the SDK save). PatchService's byte-level re-verify
                // here would compare against `finalCode` — the pre-reconciler input — and would
                // flag legitimate childrenOrderedList rewrites and SDK attribute reordering as
                // false negatives. The WriteService payload already says Success only when the
                // persisted pattern XML matches the saved content semantically.
                bool isPatternPart = Services.PatternAnalysisService.IsPatternPart(partName);
                if (primaryWriteSuccess && isPatternPart)
                {
                    persistedMatches = true;
                    writePayload["persistedVerified"] = true;
                    writePayload["persistedVerifyNote"] = "Pattern parts use WriteService's internal XmlEquivalence verification; byte-level re-verify was skipped because the auto-reconcile of childrenOrderedList legitimately reshapes the input.";
                }
                else if (primaryWriteSuccess)
                {
                    persistedMatches = VerifyPersistedSource(target, partName, typeFilter, finalCode, out string verifyError);
                    writePayload["persistedVerified"] = persistedMatches;
                    if (!string.IsNullOrWhiteSpace(verifyError))
                    {
                        writePayload["persistedVerifyError"] = verifyError;
                    }

                    if (!persistedMatches)
                    {
                        AttachPersistedSnippet(writePayload, target, partName, typeFilter, finalCode);
                        // Fast path can report success before the physical source part is fully persisted.
                        string fallbackWrite = _writeService.WriteObject(target, partName, finalCode, typeFilter, autoValidate: false, preferFastSourceSave: false, autoInjectVariables: false);
                        JObject fallbackPayload = ParseWriteResult(fallbackWrite);

                        bool fallbackSuccess = string.Equals(fallbackPayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                        writePayload["fallbackWriteStatus"] = fallbackPayload["status"]?.ToString() ?? "Error";
                        // Friction 2026-05-22 #8: classifier helper extracted so the envelope shape
                        // is unit-testable without standing up an SDK. See ClassifyFallbackFailure.
                        if (!fallbackSuccess)
                        {
                            // Friction 2026-05-22 #8: differentiate two distinct failure modes
                            // the agent previously couldn't tell apart from this single error.
                            //
                            // (a) write_not_persisted — neither write reached disk. Retry-safe
                            //     because the on-disk source is still the original. SDK
                            //     reported failure on the fallback AND a re-verify shows the
                            //     persisted bytes still match the original.
                            //
                            // (b) persisted_with_concurrent_change — the primary write DID land
                            //     (or a sibling write landed) and the persisted bytes diverge
                            //     from the original (and from finalCode). Hash drifted *post-
                            //     write*. Returning Error here forced the agent to retry,
                            //     which then either no-op'd or clobbered the sibling. Surface
                            //     as Success + postWriteHashDrift warning so the agent knows
                            //     to re-read instead of re-write.
                            string fallbackErrText = fallbackPayload["error"]?.ToString() ?? "Unknown fallback write error.";
                            try
                            {
                                bool matchesOriginal = VerifyPersistedSource(target, partName, typeFilter, originalSource, out _);
                                bool matchesFinal = VerifyPersistedSource(target, partName, typeFilter, finalCode, out _);
                                var classification = ClassifyFallbackFailure(matchesOriginal, matchesFinal, fallbackErrText);
                                writePayload["status"] = classification.Status;
                                writePayload["code"] = classification.Code;
                                if (classification.PatchLanded)
                                {
                                    writePayload["persistedVerified"] = true;
                                    writePayload["persistedVerifyError"] = null;
                                    persistedMatches = true;
                                    UpdateCachedSource(cacheKey, finalCode);
                                }
                                else if (string.Equals(classification.Status, "Success", StringComparison.Ordinal))
                                {
                                    // Concurrent write without our content — keep persistedVerified=false
                                    // and surface a re-read hint.
                                    writePayload["persistedVerified"] = false;
                                    writePayload["persistedVerifyError"] = "concurrent write detected; persisted bytes diverged from both original and patched content.";
                                }
                                if (string.Equals(classification.Status, "Success", StringComparison.Ordinal))
                                {
                                    var meta = writePayload["_meta"] as JObject ?? new JObject();
                                    var drift = new JObject
                                    {
                                        ["code"] = classification.Code,
                                        ["mode"] = classification.Mode,
                                        ["message"] = classification.Message,
                                        ["fallbackWriteError"] = fallbackErrText
                                    };
                                    if (classification.RequiresReread)
                                    {
                                        drift["suggestedAction"] = "re-read target then re-targeted patch (do not blindly retry the same patch).";
                                    }
                                    meta["postWriteHashDrift"] = drift;
                                    writePayload["_meta"] = meta;
                                }
                                else
                                {
                                    writePayload["error"] = classification.Message;
                                    writePayload["fallbackWriteError"] = fallbackErrText;
                                    writePayload["suggested_next_step"] = "Retry the same patch — on-disk source is the same as before the attempt.";
                                }
                            }
                            catch (Exception verifyEx)
                            {
                                // Verify itself failed — fall back to the legacy generic error so
                                // we don't lose the signal. Keep status=Error.
                                Logger.Debug("[PATCH] post-fallback verify failed: " + verifyEx.Message);
                                writePayload["status"] = "Error";
                                writePayload["error"] = "Patch write fallback failed after persistence mismatch.";
                                writePayload["fallbackWriteError"] = fallbackErrText;
                            }
                        }
                        else
                        {
                            persistedMatches = VerifyPersistedSource(target, partName, typeFilter, finalCode, out string fallbackVerifyError);
                            writePayload["persistedVerified"] = persistedMatches;
                            if (!string.IsNullOrWhiteSpace(fallbackVerifyError))
                            {
                                writePayload["persistedVerifyError"] = fallbackVerifyError;
                            }

                            if (!persistedMatches)
                            {
                                // v2.3.8 Task 4.6 (friction-report #13 / #6): before rolling back,
                                // classify the divergence. If every hunk between the source we asked
                                // the SDK to save (`finalCode`) and the actual persisted source lies
                                // OUTSIDE the lines we actually edited, this is an SDK
                                // side-effect normalization (e.g. `DATETIME(10,5)` → `DATETIME(8,5)`
                                // on an untouched line) and not a verification failure. Surface the
                                // normalizations under `_meta.sideEffectNormalizations` and keep
                                // status=Success. Only when an in-window hunk diverges do we treat
                                // it as a real divergence and roll back.
                                if (TryClassifyOutOfWindowOnly(target, partName, typeFilter, workSource, updatedSource, finalCode, out var sideEffects))
                                {
                                    writePayload["status"] = "Success";
                                    writePayload["persistedVerified"] = true;
                                    writePayload["persistedVerifyError"] = null;
                                    var meta = writePayload["_meta"] as JObject ?? new JObject();
                                    meta["sideEffectNormalizations"] = sideEffects;
                                    writePayload["_meta"] = meta;
                                    persistedMatches = true;
                                    UpdateCachedSource(cacheKey, finalCode);
                                }
                                else
                                {
                                writePayload["status"] = "Error";
                                writePayload["error"] = "Patch write verification mismatch after fallback write.";
                                AttachPersistedSnippet(writePayload, target, partName, typeFilter, finalCode);

                                // Restore original source: without this, a fallback write that reports
                                // success but fails verification leaves the matched context deleted and
                                // the replacement missing (data loss).
                                try
                                {
                                    string rollbackBody = originalSource.Replace("\n", Environment.NewLine);
                                    string rollbackResult = _writeService.WriteObject(target, partName, rollbackBody, typeFilter, autoValidate: false, preferFastSourceSave: false, autoInjectVariables: false);
                                    JObject rbPayload = ParseWriteResult(rollbackResult);
                                    bool rbSuccess = string.Equals(rbPayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                                    bool rbVerified = false;
                                    if (rbSuccess)
                                    {
                                        rbVerified = VerifyPersistedSource(target, partName, typeFilter, originalSource, out _);
                                    }
                                    writePayload["autoRollbackStatus"] = rbSuccess ? (rbVerified ? "Restored" : "WriteSucceededVerifyFailed") : "Failed";
                                    writePayload["error"] = rbVerified
                                        ? "Patch write verification mismatch after fallback write. Original source restored — re-read and retry."
                                        : "Patch write verification mismatch after fallback write. Auto-rollback could not be verified — re-read source to confirm state.";
                                    if (rbVerified)
                                    {
                                        UpdateCachedSource(cacheKey, originalSource);
                                    }
                                }
                                catch (Exception rbEx)
                                {
                                    writePayload["autoRollbackStatus"] = "Failed";
                                    writePayload["autoRollbackError"] = rbEx.Message;
                                }
                                }
                            }
                        }
                    }
                }

                if (string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase) && persistedMatches)
                {
                    UpdateCachedSource(cacheKey, finalCode);
                }

                if (verifyRollback && string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                {
                    string verifyReadResponse = ReadSourceFast(target, partName, typeFilter);
                    string verifyReadError = TryExtractError(verifyReadResponse);
                    if (!string.IsNullOrWhiteSpace(verifyReadError))
                    {
                        writePayload["status"] = "Error";
                        writePayload["error"] = "Apply verification read failed: " + verifyReadError;
                        writePayload["verifyRollback"] = true;
                    }
                    else
                    {
                        var verifyJson = JObject.Parse(verifyReadResponse);
                        string persistedSource = verifyJson["source"]?.ToString() ?? string.Empty;
                        bool applyVerified = NormalizeForPartCompare(partName, persistedSource) == NormalizeForPartCompare(partName, finalCode);
                        writePayload["applyVerified"] = applyVerified;
                        writePayload["verifyRollback"] = true;
                        if (!applyVerified)
                        {
                            writePayload["status"] = "Error";
                            writePayload["error"] = "Apply verification mismatch: persisted content differs from patched content.";
                        }
                    }

                    string rollbackWrite = _writeService.WriteObject(target, partName, originalSource, typeFilter, autoValidate: false, preferFastSourceSave: true, autoInjectVariables: false);
                    JObject rollbackPayload = ParseWriteResult(rollbackWrite);

                    bool rollbackSuccess = string.Equals(rollbackPayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                    writePayload["rollbackStatus"] = rollbackPayload["status"]?.ToString() ?? "Error";
                    if (!rollbackSuccess)
                    {
                        writePayload["status"] = "Error";
                        writePayload["rollbackError"] = rollbackPayload["error"]?.ToString() ?? "Rollback failed.";
                    }
                    else
                    {
                        string rollbackReadResponse = ReadSourceFast(target, partName, typeFilter);
                        string rollbackReadError = TryExtractError(rollbackReadResponse);
                        if (!string.IsNullOrWhiteSpace(rollbackReadError))
                        {
                            writePayload["status"] = "Error";
                            writePayload["rollbackError"] = "Rollback verification read failed: " + rollbackReadError;
                        }
                        else
                        {
                            var rollbackReadJson = JObject.Parse(rollbackReadResponse);
                            string rollbackSource = rollbackReadJson["source"]?.ToString() ?? string.Empty;
                            bool rollbackVerified = NormalizeForPartCompare(partName, rollbackSource) == NormalizeForPartCompare(partName, originalSource);
                            writePayload["rollbackVerified"] = rollbackVerified;
                            if (!rollbackVerified)
                            {
                                writePayload["status"] = "Error";
                                writePayload["rollbackError"] = "Rollback verification mismatch: current content differs from original content.";
                            }
                        }
                    }
                }

                bool finalSuccess = string.Equals(writePayload["status"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase);
                writePayload["patchStatus"] = finalSuccess ? "Applied" : "Failed";
                writePayload["operation"] = normalizedOperation;
                writePayload["expectedCount"] = expectedCount;
                writePayload["matchCount"] = matchCount;
                writePayload["timings"] = new JObject
                {
                    ["readMs"] = readMs,
                    ["patchMs"] = patchMs,
                    ["writeMs"] = writeMs,
                    ["usedSourceCache"] = sourceFromCache
                };
                if (returnPostState && finalSuccess && updatedSource != null)
                    writePayload["post_state"] = GxMcp.Worker.Services.JsonPatchService.BuildPostState(originalSource, updatedSource, verbose);
                if (patternShadowWarnings != null && patternShadowWarnings.Count > 0)
                    writePayload["warnings"] = patternShadowWarnings;
                return writePayload.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"[PATCH] Error applying patch: {ex.Message}");
                return BuildPatchResult("Error", partName, NormalizeOperation(operation), expectedCount, 0, ex.Message);
            }
        }

        private string TryReplace(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount, bool replaceAll = false)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);

            // 1. Exact match attempt
            int exactCount = CountOccurrences(source, context);
            matchCount = exactCount;
            // Item 9: replaceAll=true → treat expectedCount as "however many exist"
            int effectiveExpected = replaceAll && exactCount > 0 ? exactCount : expectedCount;
            if (exactCount == effectiveExpected && exactCount > 0)
            {
                Logger.Info("[PATCH] Exact match found.");
                return source.Replace(context, newContent);
            }
            if (exactCount > 0 && !replaceAll)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {exactCount} exact matches, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return string.Empty;
            }

            // 2. Fuzzy match attempt
            Logger.Info("[PATCH] Exact match failed or count mismatch (" + exactCount + " vs " + expectedCount + "). Attempting fuzzy match.");
            var indices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = indices.Count;
            int fuzzyEffective = replaceAll && indices.Count > 0 ? indices.Count : expectedCount;

            if (indices.Count == fuzzyEffective && indices.Count > 0)
            {
                var resultLines = new List<string>(sourceLines);
                var replacementLines = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                indices.Sort();
                indices.Reverse();
                foreach (int idx in indices)
                {
                    Logger.Info($"[PATCH] Fuzzy match found at line {idx}.");
                    string indentation = GetIndentation(sourceLines[idx]);
                    var indentedReplacement = ApplyIndentation(replacementLines, indentation);
                    resultLines.RemoveRange(idx, contextLines.Length);
                    resultLines.InsertRange(idx, indentedReplacement);
                }
                return string.Join("\n", resultLines);
            }

            if (indices.Count > 0 && !replaceAll)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {indices.Count} fuzzy matches, but expected {expectedCount}. Provide more context to uniquely identify the block, or pass replaceAll=true to apply to all occurrences.";
                return string.Empty;
            }

            // FR#17 (friction-report 2026-05-14): last-resort whitespace-normalized match.
            // Handles the tab-vs-space context case where the user's context is semantically
            // identical to source but used different indentation characters than the file.
            // We collapse runs of whitespace on both sides, find the unique block window,
            // then apply the replacement preserving source's original characters.
            string normalizedSource = NormalizeWhitespace(source);
            string normalizedContext = NormalizeWhitespace(context);
            if (!string.IsNullOrEmpty(normalizedContext))
            {
                int normalizedHits = CountOccurrences(normalizedSource, normalizedContext);
                // Item 9 follow-up: honor replaceAll on the whitespace-normalized fallback too,
                // so the flag isn't silently ignored when only this last-resort path finds matches.
                int normalizedExpected = replaceAll && normalizedHits > 0 ? normalizedHits : expectedCount;
                if (normalizedHits == normalizedExpected && normalizedHits > 0)
                {
                    // Walk source line-by-line accumulating windows until a window's collapsed
                    // form equals the normalized context, then splice in the replacement.
                    var rebuilt = TryWhitespaceNormalizedReplace(sourceLines, contextLines, newContent);
                    if (rebuilt != null)
                    {
                        Logger.Info("[PATCH] Whitespace-normalized match applied.");
                        matchCount = normalizedHits;
                        return rebuilt;
                    }
                }
                else if (normalizedHits > 0 && !replaceAll)
                {
                    status = "Ambiguous";
                    matchCount = normalizedHits;
                    details = $"Ambiguous patch (whitespace-normalized): {normalizedHits} matches, expected {expectedCount}. Pass replaceAll=true to apply to every match.";
                    return string.Empty;
                }
            }

            // v2.3.8 Task 3.1 (friction-report #4): final EOL-normalized fallback.
            // Both the exact match and the prior fuzzy/whitespace-normalized passes
            // already collapse CRLF→LF up-front (workContext is normalized at entry),
            // but they do NOT tolerate per-line trailing whitespace differences. The
            // helper below normalizes both axes (EOL + trailing whitespace) and maps
            // the normalized hit back to original-source indices so the splice
            // preserves the on-disk bytes outside the matched window.
            if (expectedCount == 1 && contextLines != null && contextLines.Length > 0)
            {
                if (WriteService.TryMatch(source, context, out int splStart, out int splEnd) && splEnd > splStart)
                {
                    Logger.Info("[PATCH] EOL/trailing-whitespace normalized match applied.");
                    matchCount = 1;
                    string replacement = (newContent ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
                    return source.Substring(0, splStart) + replacement + source.Substring(splEnd);
                }
            }

            status = "NoMatch";
            details = "Context block not found.";
            return string.Empty;
        }

        private static string TryWhitespaceNormalizedReplace(string[] sourceLines, string[] contextLines, string newContent)
        {
            // Slide a window of contextLines.Length over source; compare collapsed text.
            if (sourceLines == null || contextLines == null || contextLines.Length == 0) return null;
            if (sourceLines.Length < contextLines.Length) return null;

            string normalizedTarget = NormalizeWhitespace(string.Join("\n", contextLines));
            for (int i = 0; i <= sourceLines.Length - contextLines.Length; i++)
            {
                string window = string.Join("\n", sourceLines, i, contextLines.Length);
                if (NormalizeWhitespace(window) == normalizedTarget)
                {
                    var resultLines = new List<string>(sourceLines);
                    string indentation = GetIndentation(sourceLines[i]);
                    var replacementLines = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                    var indented = ApplyIndentation(replacementLines, indentation);
                    resultLines.RemoveRange(i, contextLines.Length);
                    resultLines.InsertRange(i, indented);
                    return string.Join("\n", resultLines);
                }
            }
            return null;
        }

        private string TryInsertAfter(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            var exactIndices = FindExactMatches(sourceLines, contextLines);
            matchCount = exactIndices.Count;
            if (exactIndices.Count == expectedCount && exactIndices.Count > 0)
            {
                return InsertAfterIndices(sourceLines, contextLines, newContent, exactIndices);
            }

            if (exactIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {exactIndices.Count} exact matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            var fuzzyIndices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = fuzzyIndices.Count;
            if (fuzzyIndices.Count == expectedCount && fuzzyIndices.Count > 0)
            {
                return InsertAfterIndices(sourceLines, contextLines, newContent, fuzzyIndices);
            }

            if (fuzzyIndices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous anchor: Found {fuzzyIndices.Count} fuzzy matches for the anchor, expected {expectedCount}.";
                return string.Empty;
            }

            status = "NoMatch";
            details = "Anchor block not found.";
            return string.Empty;
        }

        private List<int> FindFuzzyMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;

            string normalizedFirst = NormalizeWhitespace(targetLines[0]);
            string normalizedLast = NormalizeWhitespace(targetLines[targetLines.Length - 1]);

            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                if (!string.Equals(NormalizeWhitespace(sourceLines[i]), normalizedFirst, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int tailIndex = i + targetLines.Length - 1;
                if (!string.Equals(NormalizeWhitespace(sourceLines[tailIndex]), normalizedLast, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!LinesMatchFuzzy(sourceLines[i + j], targetLines[j]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matches.Add(i);
            }
            return matches;
        }

        private List<int> FindExactMatches(string[] sourceLines, string[] targetLines)
        {
            var matches = new List<int>();
            if (targetLines.Length == 0 || sourceLines.Length < targetLines.Length) return matches;

            for (int i = 0; i <= sourceLines.Length - targetLines.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < targetLines.Length; j++)
                {
                    if (!string.Equals(sourceLines[i + j], targetLines[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    matches.Add(i);
                }
            }

            return matches;
        }

        private string InsertAfterIndices(string[] sourceLines, string[] contextLines, string newContent, List<int> indices)
        {
            var resultLines = new List<string>(sourceLines);
            var insertLinesRaw = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            indices.Sort();
            indices.Reverse();
            foreach (int idx in indices)
            {
                string indentation = GetIndentation(sourceLines[idx]);
                var indentedInsert = ApplyIndentation(insertLinesRaw, indentation);
                resultLines.InsertRange(idx + contextLines.Length, indentedInsert);
            }

            return string.Join("\n", resultLines);
        }

        // FR#18 (friction-report 2026-05-14): produce a small list of "looks-similar" windows
        // when an exact/fuzzy match fails. Score = ratio of fuzzy-matching lines per window;
        // we keep the top-N. Only the first line is used as the snippet to keep responses small.
        private sealed class NearMatch
        {
            public int StartLine;
            public double Similarity;
            public string Snippet = string.Empty;
        }

        private List<NearMatch> FindNearMatches(string[] sourceLines, string[] contextLines, int topN)
        {
            var hits = new List<NearMatch>();
            if (sourceLines == null || contextLines == null) return hits;
            if (contextLines.Length == 0 || sourceLines.Length < contextLines.Length) return hits;

            // Pre-normalize both sides once; the inner comparison drops from a regex+trim per
            // call to a direct OrdinalIgnoreCase string equals.
            string[] normalizedSource = new string[sourceLines.Length];
            for (int i = 0; i < sourceLines.Length; i++) normalizedSource[i] = NormalizeWhitespace(sourceLines[i]);
            string[] normalizedContext = new string[contextLines.Length];
            for (int j = 0; j < contextLines.Length; j++) normalizedContext[j] = NormalizeWhitespace(contextLines[j]);

            int maxStart = sourceLines.Length - contextLines.Length;
            for (int i = 0; i <= maxStart; i++)
            {
                int matches = 0;
                for (int j = 0; j < contextLines.Length; j++)
                {
                    if (string.Equals(normalizedSource[i + j], normalizedContext[j], StringComparison.OrdinalIgnoreCase))
                        matches++;
                }
                double similarity = (double)matches / contextLines.Length;
                if (similarity < 0.4) continue; // ignore noise

                string snippet = sourceLines[i].Trim();
                if (snippet.Length > 120) snippet = snippet.Substring(0, 117) + "...";

                hits.Add(new NearMatch { StartLine = i, Similarity = similarity, Snippet = snippet });
            }

            hits.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            if (hits.Count > topN) hits = hits.GetRange(0, topN);
            return hits;
        }

        // Item 4 (friction 2026-05-22): render control characters visibly so the
        // agent can see CRLF vs LF differences in the eolDiff output.
        private static string ShowControlChars(string s)
        {
            if (s == null) return string.Empty;
            return s.Replace("\r\n", "↵\n").Replace("\r", "←").Replace("\t", "→");
        }

        // Item 17 (friction 2026-05-22): Levenshtein edit distance with early-exit
        // when the running minimum exceeds maxDist (avoids O(n²) on large mismatches).
        // maxDist = -1 means "no limit".
        internal static int LevenshteinDistance(string a, string b, int maxDist = -1)
        {
            if (a == null) a = string.Empty;
            if (b == null) b = string.Empty;
            int m = a.Length, n = b.Length;
            bool hasLimit = maxDist >= 0;
            if (hasLimit && Math.Abs(m - n) > maxDist) return maxDist + 1;
            if (m == 0) return n;
            if (n == 0) return m;

            // Use two rows to limit memory; strings > 4 KB are capped to avoid O(n²) pathology.
            const int MaxLen = 4096;
            if (m > MaxLen || n > MaxLen) return hasLimit ? maxDist + 1 : int.MaxValue;

            var prev = new int[n + 1];
            var curr = new int[n + 1];
            for (int j = 0; j <= n; j++) prev[j] = j;

            for (int i = 1; i <= m; i++)
            {
                curr[0] = i;
                int rowMin = curr[0];
                for (int j = 1; j <= n; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                    if (curr[j] < rowMin) rowMin = curr[j];
                }
                if (hasLimit && rowMin > maxDist) return maxDist + 1;
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[n];
        }

        private static bool LinesMatchFuzzy(string s1, string s2)
        {
            string n1 = NormalizeWhitespace(s1);
            string n2 = NormalizeWhitespace(s2);
            return string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase);
        }

        // Trim + collapse runs of whitespace to a single space.
        private static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return Regex.Replace(s.Trim(), @"\s+", " ");
        }

        private static string GetIndentation(string line)
        {
            var match = Regex.Match(line ?? string.Empty, @"^(\s*)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static List<string> ApplyIndentation(IEnumerable<string> contentLines, string indentation)
        {
            var lines = contentLines.ToList();
            if (string.IsNullOrEmpty(indentation)) return lines;
            return lines.Select(line => indentation + line).ToList();
        }

        private int CountOccurrences(string text, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return 0;
            int count = 0, i = 0;
            while ((i = text.IndexOf(pattern, i)) != -1) { i += pattern.Length; count++; }
            return count;
        }

        private static string NormalizeOperation(string operation)
        {
            string value = operation?.Trim().Replace("-", "_").ToLowerInvariant() ?? "replace";
            switch (value)
            {
                case "insertafter":
                case "insert_after":
                    return "insert_after";
                default:
                    return value;
            }
        }

        private static string NormalizeSourceForComparison(string text)
        {
            if (text == null) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        }

        // Friction-report #5 write-side: VariablesPart's underlying SDK collection inserts new
        // variables at the FRONT of the list, so a patch that produced `<original>...\n&NewVar`
        // round-trips through SetVariablesFromText / GetVariablesAsText as
        // `&NewVar\n<original>...`. Line-by-line equality verification then fails even though
        // the persisted state semantically matches. Use a set-based comparison for Variables
        // so patch verification reflects semantic equality, not the SDK's collection order.
        //
        // Friction-report 05-13 #4: the Variables DSL renderer drops `,0` when Decimals==0
        // (`NUMERIC(4,0)` → `NUMERIC(4)`), so a patch that wrote `&Counter : NUMERIC(4,0)`
        // round-trips as `&Counter : NUMERIC(4)`. Strings then differ even after set sort,
        // and verification falsely fails → auto-rollback wipes the new variable. Canonicalize
        // both sides by stripping trailing `,0)` so the comparison is semantic.
        internal static string NormalizeForPartCompare(string partName, string text)
        {
            string normalized = NormalizeSourceForComparison(text);
            if (!string.Equals(partName, "Variables", StringComparison.OrdinalIgnoreCase)) return normalized;
            var lines = normalized
                .Split('\n')
                .Select(l => CanonicalizeVariablesLine(l))
                .Where(l => l.Length > 0)
                .OrderBy(l => l, StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static readonly System.Text.RegularExpressions.Regex VariableTrailingZeroDecimals =
            new System.Text.RegularExpressions.Regex(
                @"\(\s*(\d+)\s*,\s*0\s*\)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string CanonicalizeVariablesLine(string line)
        {
            if (line == null) return string.Empty;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return string.Empty;
            // Collapse intra-line whitespace runs so "&X  :  NUMERIC ( 4 , 0 )" and
            // "&X : NUMERIC(4,0)" canonicalize identically.
            trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
            // Normalize "(N,0)" → "(N)" since the SDK renderer drops trailing zero decimals.
            trimmed = VariableTrailingZeroDecimals.Replace(trimmed, "($1)");
            return trimmed;
        }

        private static JObject ParseWriteResult(string writeResult)
        {
            try { return JObject.Parse(writeResult); }
            catch { return new JObject { ["status"] = "Error", ["error"] = writeResult }; }
        }

        private static string TryExtractError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return "Empty response.";

            try
            {
                var json = JObject.Parse(response);
                return json["error"]?.ToString();
            }
            catch
            {
                return "Non-JSON response.";
            }
        }

        private string ReadSourceFast(string target, string partName, string typeFilter)
        {
            try
            {
                var obj = _objectService.FindObject(target, typeFilter);
                if (obj == null)
                {
                    return Models.McpResponse.Error("Object not found", target, partName, "The requested object is not available in the active Knowledge Base.");
                }

                string resolvedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName;

                // Pattern parts (PatternInstance / PatternVirtual, e.g. WorkWithPlus) do not
                // implement ISource and PartAccessor.GetPart on the source object will miss them
                // (the part lives on the resolved WWP instance). Route through
                // PatternAnalysisService so patch-mode can read & rewrite pattern XML.
                if (_patternAnalysisService != null && PatternAnalysisService.IsPatternPart(resolvedPart))
                {
                    string patternXml = _patternAnalysisService.ReadPatternPartXml(obj, resolvedPart, out _, out var resolvedPatternPartName);
                    if (patternXml == null)
                    {
                        return Models.McpResponse.Error("Part not found", target, resolvedPart, "The object does not expose the requested pattern part.");
                    }
                    return new JObject
                    {
                        ["status"] = "Success",
                        ["part"] = string.IsNullOrWhiteSpace(resolvedPatternPartName) ? resolvedPart : resolvedPatternPartName,
                        ["source"] = patternXml
                    }.ToString();
                }

                KBObjectPart part = PartAccessor.GetPart(obj, resolvedPart);
                if (part == null)
                {
                    return Models.McpResponse.Error("Part not found", target, resolvedPart, "The object does not expose the requested part.");
                }

                string source = null;
                if (part is global::Artech.Genexus.Common.Parts.VariablesPart varPart)
                {
                    // Variables part is exposed via DSL serialization
                    source = GxMcp.Worker.Helpers.VariableInjector.GetVariablesAsText(varPart);
                }
                else if (resolvedPart.Equals("Structure", StringComparison.OrdinalIgnoreCase) &&
                         (obj is global::Artech.Genexus.Common.Objects.Transaction
                          || obj is global::Artech.Genexus.Common.Objects.Table
                          || obj.TypeDescriptor.Name.Equals("SDT", StringComparison.OrdinalIgnoreCase)))
                {
                    source = GxMcp.Worker.Helpers.StructureParser.SerializeToText(obj);
                }
                else if (GxMcp.Worker.Helpers.WebFormXmlHelper.IsVisualPart(resolvedPart))
                {
                    // WebForm/Layout: expose the GxMultiForm XML as patchable text.
                    source = GxMcp.Worker.Helpers.WebFormXmlHelper.ReadEditableXml(obj);
                }
                else if (part is ISource sourcePart)
                {
                    source = sourcePart.Source;
                }
                else
                {
                    var contentProp = part.GetType().GetProperty("Source") ?? part.GetType().GetProperty("Content");
                    if (contentProp != null && contentProp.CanRead && contentProp.PropertyType == typeof(string))
                    {
                        source = contentProp.GetValue(part)?.ToString();
                    }
                }

                if (source == null)
                {
                    return Models.McpResponse.Error("Part does not expose text source", target, resolvedPart, "Patch operations require a textual part.");
                }

                return new JObject
                {
                    ["status"] = "Success",
                    ["part"] = resolvedPart,
                    ["source"] = source
                }.ToString();
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Error("Patch read failed", target, partName, ex.Message);
            }
        }

        private static string BuildCacheKey(string target, string partName, string typeFilter)
        {
            string normalizedTarget = target?.Trim() ?? string.Empty;
            string normalizedPart = string.IsNullOrWhiteSpace(partName) ? "Source" : partName.Trim();
            string normalizedType = typeFilter?.Trim() ?? string.Empty;
            return normalizedType + "|" + normalizedTarget + "|" + normalizedPart;
        }

        private static string TryGetCachedSource(string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey)) return null;
            if (!_sourceCache.TryGetValue(cacheKey, out var entry) || entry == null) return null;
            if (DateTime.UtcNow - entry.UpdatedUtc > SourceCacheTtl)
            {
                _sourceCache.TryRemove(cacheKey, out _);
                return null;
            }

            return entry.Source;
        }

        private static void UpdateCachedSource(string cacheKey, string source)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || source == null) return;
            _sourceCache[cacheKey] = new SourceCacheEntry
            {
                Source = source,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Friction 2026-05-22 #8: classify a post-fallback "SDK reported failure" outcome.
        /// Inputs are the cheap-to-compute "what does disk look like now?" booleans plus
        /// the SDK error text. Output is the envelope shape the caller should apply to
        /// writePayload. Pure function — unit-tested without standing up the SDK.
        /// </summary>
        public sealed class FallbackFailureClassification
        {
            /// <summary>"Success" or "Error" — the value to put on writePayload.status.</summary>
            public string Status { get; set; }
            /// <summary>Stable code: "write_not_persisted" or "post_write_hash_drift".</summary>
            public string Code { get; set; }
            /// <summary>"write_not_persisted" or "persisted_with_concurrent_change".</summary>
            public string Mode { get; set; }
            /// <summary>Human-readable error/note text.</summary>
            public string Message { get; set; }
            /// <summary>True when the agent's safe move is to re-read before retrying.</summary>
            public bool RequiresReread { get; set; }
            /// <summary>True when the patched content actually landed despite the SDK signal.</summary>
            public bool PatchLanded { get; set; }
        }

        public static FallbackFailureClassification ClassifyFallbackFailure(
            bool persistedMatchesOriginal,
            bool persistedMatchesFinal,
            string fallbackError)
        {
            if (persistedMatchesFinal)
            {
                return new FallbackFailureClassification
                {
                    Status = "Success",
                    Code = "post_write_hash_drift",
                    Mode = "persisted_with_concurrent_change",
                    Message = "Primary write landed; SDK reported a fallback-write failure but the persisted bytes match the patched content. Treating as Success.",
                    RequiresReread = false,
                    PatchLanded = true
                };
            }
            if (!persistedMatchesOriginal)
            {
                return new FallbackFailureClassification
                {
                    Status = "Success",
                    Code = "post_write_hash_drift",
                    Mode = "persisted_with_concurrent_change",
                    Message = "A concurrent write modified this target between our write and our verify. The on-disk source no longer matches either our input or the original — re-read before any further patch.",
                    RequiresReread = true,
                    PatchLanded = false
                };
            }
            return new FallbackFailureClassification
            {
                Status = "Error",
                Code = "write_not_persisted",
                Mode = "write_not_persisted",
                Message = "Write did not reach disk (SDK fallback failed and the on-disk source is unchanged). Retry is safe.",
                RequiresReread = false,
                PatchLanded = false
            };
        }

        public static void InvalidateCachedSource(string target, string partName, string typeFilter)
        {
            try
            {
                string cacheKey = BuildCacheKey(target, partName, typeFilter);
                _sourceCache.TryRemove(cacheKey, out _);
            }
            catch { }
        }

        public static void InvalidateAllForTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            string normalizedTarget = target.Trim();
            foreach (var key in _sourceCache.Keys)
            {
                if (key.IndexOf("|" + normalizedTarget + "|", StringComparison.OrdinalIgnoreCase) >= 0)
                    _sourceCache.TryRemove(key, out _);
            }
        }

        private bool VerifyPersistedSource(string target, string partName, string typeFilter, string expectedSource, out string error)
        {
            error = null;
            try
            {
                // Friction-report #6: post-write verification must read from a forced cache miss.
                // Without this, the ObjectService _readCache or the patch-local _sourceCache can
                // hold pre-write content and falsely report persistedVerified=true even though the
                // next user-facing read shows stale source. Drop both caches before the verify read.
                //
                // FR#2 (friction-report 2026-05-14): the SDK sometimes flushes to disk slightly
                // after Save() returns. Verify once immediately; if it disagrees, retry after a
                // short pause before reporting Error. This collapses the false-negative
                // "persistedVerified=false but next genexus_read shows the write applied" case
                // that produced spurious fallback writes / auto-rollbacks.
                string verifyKey = BuildCacheKey(target, partName, typeFilter);
                string expectedNormalized = NormalizeForPartCompare(partName, expectedSource);
                int attempts = 2;
                for (int i = 0; i < attempts; i++)
                {
                    _sourceCache.TryRemove(verifyKey, out _);
                    try
                    {
                        var verifyObj = _objectService.FindObject(target, typeFilter);
                        if (verifyObj != null) _objectService.MarkReadCacheDirty(verifyObj, partName);
                    }
                    catch { }

                    string verifyReadResponse = ReadSourceFast(target, partName, typeFilter);
                    string verifyReadError = TryExtractError(verifyReadResponse);
                    if (!string.IsNullOrWhiteSpace(verifyReadError))
                    {
                        error = verifyReadError;
                        return false;
                    }

                    var verifyJson = JObject.Parse(verifyReadResponse);
                    string persistedSource = verifyJson["source"]?.ToString() ?? string.Empty;
                    if (NormalizeForPartCompare(partName, persistedSource) == expectedNormalized)
                    {
                        return true;
                    }

                    if (i < attempts - 1)
                    {
                        // SDK persistence can lag the Save() return; brief pause before retry.
                        System.Threading.Thread.Sleep(120);
                    }
                    else
                    {
                        // Surface a small diff sample so callers can decide instead of guessing.
                        error = BuildVerifyDiffHint(expectedSource, persistedSource);
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // When verify mismatches, surface a line-based slice of the actual on-disk content
        // so the agent can confirm the state without a follow-up genexus_read call.
        private void AttachPersistedSnippet(JObject writePayload, string target, string partName, string typeFilter, string expectedSource)
        {
            try
            {
                string readResp = ReadSourceFast(target, partName, typeFilter);
                if (!string.IsNullOrWhiteSpace(TryExtractError(readResp))) return;
                var json = JObject.Parse(readResp);
                string persisted = json["source"]?.ToString() ?? string.Empty;

                string[] persistedLines = persisted.Replace("\r\n", "\n").Split('\n');
                string[] expectedLines = (expectedSource ?? string.Empty).Replace("\r\n", "\n").Split('\n');

                int divergeLine = 0;
                int max = Math.Min(persistedLines.Length, expectedLines.Length);
                while (divergeLine < max && persistedLines[divergeLine] == expectedLines[divergeLine]) divergeLine++;

                const int contextLines = 10;
                int start = Math.Max(0, divergeLine - contextLines);
                int end = Math.Min(persistedLines.Length, divergeLine + contextLines + 1);
                int len = Math.Max(0, end - start);

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < len; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(persistedLines[start + i]);
                }

                writePayload["persistedSnippet"] = new JObject
                {
                    ["startLine"] = start + 1,
                    ["divergeLine"] = divergeLine + 1,
                    ["content"] = sb.ToString(),
                    ["totalLines"] = persistedLines.Length
                };
            }
            catch { /* keep payload valid even if snippet capture fails */ }
        }

        // v2.3.8 Task 4.6 — patch-window-only rollback verification.
        //
        // Inputs:
        //   workSource    = LF-normalized original on-disk source BEFORE the patch.
        //   updatedSource = LF-normalized source we computed from the patch (what we wanted).
        //   finalCode     = CRLF version that was actually handed to WriteService.WriteObject().
        // Output:
        //   sideEffects   = JArray of {line, before, after} describing every out-of-window
        //                   normalization the SDK applied on save. Only set when the method
        //                   returns true (i.e. all hunks are out-of-window).
        //
        // Strategy:
        //   1. Edit window = union of line ranges of HunkDiff(workSource, updatedSource).
        //   2. Read persisted source from disk (forced cache miss inside ReadSourceFast).
        //   3. divergenceHunks = HunkDiff(updatedSource, persistedSource).
        //   4. If any divergence hunk overlaps the edit window → return false (real divergence).
        //   5. Else → return true; serialize the out-of-window hunks.
        internal bool TryClassifyOutOfWindowOnly(
            string target,
            string partName,
            string typeFilter,
            string workSource,
            string updatedSource,
            string finalCode,
            out JArray sideEffects)
        {
            sideEffects = null;
            try
            {
                if (string.IsNullOrEmpty(updatedSource) || string.IsNullOrEmpty(workSource)) return false;

                // 1. Compute edit window (1-based inclusive line range).
                var editHunks = GxMcp.Worker.Helpers.XmlEquivalence.HunkDiff(workSource, updatedSource);
                if (editHunks.Count == 0) return false; // nothing changed — nothing to classify
                int windowStart = int.MaxValue, windowEnd = 0;
                foreach (var h in editHunks)
                {
                    int hStart = h.Line;
                    int hEnd = h.Line + Math.Max(0, h.BeforeLineCount - 1);
                    if (h.BeforeLineCount == 0) hEnd = h.Line;
                    if (hStart < windowStart) windowStart = hStart;
                    if (hEnd > windowEnd) windowEnd = hEnd;
                }
                if (windowEnd < windowStart) return false;

                // 2. Read persisted source.
                string readResp = ReadSourceFast(target, partName, typeFilter);
                if (!string.IsNullOrWhiteSpace(TryExtractError(readResp))) return false;
                var json = JObject.Parse(readResp);
                string persisted = json["source"]?.ToString();
                if (persisted == null) return false;

                string persistedLf = persisted.Replace("\r\n", "\n").Replace("\r", "\n");
                string requestedLf = (finalCode ?? updatedSource).Replace("\r\n", "\n").Replace("\r", "\n");

                // Fast equality check (raw, not part-normalized).
                if (string.Equals(requestedLf, persistedLf, StringComparison.Ordinal))
                {
                    sideEffects = new JArray();
                    return true;
                }

                // 3. Classify post-save divergence hunks.
                var divergeHunks = GxMcp.Worker.Helpers.XmlEquivalence.HunkDiff(requestedLf, persistedLf);
                if (divergeHunks.Count == 0) { sideEffects = new JArray(); return true; }

                var outOfWindow = new JArray();
                foreach (var h in divergeHunks)
                {
                    if (GxMcp.Worker.Helpers.XmlEquivalence.HunkOverlapsWindow(h, windowStart, windowEnd))
                    {
                        // A hunk INSIDE the edited window means what we asked for is not what
                        // landed on disk. That's a real verification failure → caller rolls back.
                        return false;
                    }
                    outOfWindow.Add(new JObject
                    {
                        ["line"] = h.Line,
                        ["before"] = h.Before,
                        ["after"] = h.After
                    });
                }

                sideEffects = outOfWindow;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug("[PATCH] window-classification skipped: " + ex.Message);
                return false;
            }
        }

        // FR#2: emit a compact diff hint so the caller sees WHY verify disagreed instead of
        // a bare false. We deliberately cap the dump to keep responses small.
        private static string BuildVerifyDiffHint(string expected, string actual)
        {
            try
            {
                string e = NormalizeSourceForComparison(expected ?? string.Empty);
                string a = NormalizeSourceForComparison(actual ?? string.Empty);
                if (e == a) return "Mismatch under part-specific normalization (likely Variables ordering / NUMERIC(N,0) folding).";
                int firstDiff = 0;
                int max = Math.Min(e.Length, a.Length);
                while (firstDiff < max && e[firstDiff] == a[firstDiff]) firstDiff++;
                int snippetStart = Math.Max(0, firstDiff - 40);
                int snippetLen = Math.Min(80, Math.Max(e.Length, a.Length) - snippetStart);
                string expectedSnippet = snippetStart < e.Length ? e.Substring(snippetStart, Math.Min(snippetLen, e.Length - snippetStart)) : "";
                string actualSnippet   = snippetStart < a.Length ? a.Substring(snippetStart, Math.Min(snippetLen, a.Length - snippetStart)) : "";
                return $"Verify diff at char {firstDiff}: expected='{expectedSnippet}' actual='{actualSnippet}'";
            }
            catch
            {
                return "Verification mismatch (diff unavailable).";
            }
        }

        private static string AttachTimings(string patchResultJson, long readMs, long patchMs, long writeMs, bool usedSourceCache)
        {
            try
            {
                var json = JObject.Parse(patchResultJson);
                json["timings"] = new JObject
                {
                    ["readMs"] = readMs,
                    ["patchMs"] = patchMs,
                    ["writeMs"] = writeMs,
                    ["usedSourceCache"] = usedSourceCache
                };
                return json.ToString();
            }
            catch
            {
                return patchResultJson;
            }
        }

        private static string BuildPatchResult(string patchStatus, string partName, string operation, int expectedCount, int matchCount, string details)
        {
            bool isError = string.Equals(patchStatus, "NoMatch", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(patchStatus, "Ambiguous", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(patchStatus, "Error", StringComparison.OrdinalIgnoreCase);
            var payload = new JObject
            {
                ["status"] = isError ? "Error" : "Success",
                ["patchStatus"] = patchStatus,
                ["part"] = string.IsNullOrWhiteSpace(partName) ? "Source" : partName,
                ["operation"] = operation,
                ["expectedCount"] = expectedCount,
                ["matchCount"] = matchCount
            };

            if (!string.IsNullOrWhiteSpace(details))
            {
                payload["details"] = details;
            }

            if (isError)
            {
                payload["error"] = details;
            }

            return payload.ToString();
        }
    }
}
