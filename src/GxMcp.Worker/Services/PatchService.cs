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

        public PatchService(ObjectService objectService, WriteService writeService)
        {
            _objectService = objectService;
            _writeService = writeService;
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

        public string ApplyPatch(string target, string partName, string operation, string content, string context = null, int expectedCount = 1, string typeFilter = null, bool dryRun = false, bool verifyRollback = false, bool returnPostState = true, bool verbose = false)
        {
            try
            {
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

                        updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
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
                                updatedSource = TryReplace(sourceLines, contextLines ?? new string[0], workContent, expectedCount, out status, out details, out matchCount);
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
                                if (top.Similarity >= 0.80 && top.StartLine + contextLines.Length <= sourceLines.Length)
                                {
                                    var sb = new System.Text.StringBuilder();
                                    for (int i = 0; i < contextLines.Length; i++)
                                    {
                                        if (i > 0) sb.Append('\n');
                                        sb.Append(sourceLines[top.StartLine + i]);
                                    }
                                    string bestWindow = sb.ToString();
                                    string ctxJoined = string.Join("\n", contextLines);
                                    failureJson["nearMatchHintDetail"] = GxMcp.Worker.Helpers.DiffBuilder.ByteLevelDivergence(bestWindow, ctxJoined);
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
                    string noChange = BuildPatchResult("NoChange", partName, normalizedOperation, expectedCount, matchCount, "Patch produced no effective changes. Write skipped.");
                    return AttachTimings(noChange, readMs, patchMs, 0, sourceFromCache);
                }

                if (dryRun)
                {
                    string dryRunResult = BuildPatchResult("Applied", partName, normalizedOperation, expectedCount, matchCount, "Dry-run succeeded. Write skipped.");
                    return AttachTimings(dryRunResult, readMs, patchMs, 0, sourceFromCache);
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
                if (primaryWriteSuccess)
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
                        if (!fallbackSuccess)
                        {
                            writePayload["status"] = "Error";
                            writePayload["error"] = "Patch write fallback failed after persistence mismatch.";
                            writePayload["fallbackWriteError"] = fallbackPayload["error"]?.ToString() ?? "Unknown fallback write error.";
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
                return writePayload.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error($"[PATCH] Error applying patch: {ex.Message}");
                return BuildPatchResult("Error", partName, NormalizeOperation(operation), expectedCount, 0, ex.Message);
            }
        }

        private string TryReplace(string[] sourceLines, string[] contextLines, string newContent, int expectedCount, out string status, out string details, out int matchCount)
        {
            status = "Applied";
            details = string.Empty;
            matchCount = 0;

            string source = string.Join("\n", sourceLines);
            string context = string.Join("\n", contextLines);
            
            // 1. Exact match attempt
            int exactCount = CountOccurrences(source, context);
            matchCount = exactCount;
            if (exactCount == expectedCount)
            {
                Logger.Info("[PATCH] Exact match found.");
                return source.Replace(context, newContent);
            }
            if (exactCount > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {exactCount} exact matches, but expected {expectedCount}. Please provide more context to uniquely identify the block.";
                return string.Empty;
            }

            // 2. Fuzzy match attempt
            Logger.Info("[PATCH] Exact match failed or count mismatch (" + exactCount + " vs " + expectedCount + "). Attempting fuzzy match.");
            var indices = FindFuzzyMatches(sourceLines, contextLines);
            matchCount = indices.Count;
            
            if (indices.Count == expectedCount && indices.Count > 0)
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

            if (indices.Count > 0)
            {
                status = "Ambiguous";
                details = $"Ambiguous patch: Found {indices.Count} fuzzy matches, but expected {expectedCount}. Please provide more context to uniquely identify the block.";
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
                if (normalizedHits == expectedCount && expectedCount > 0)
                {
                    // Walk source line-by-line accumulating windows until a window's collapsed
                    // form equals the normalized context, then splice in the replacement.
                    var rebuilt = TryWhitespaceNormalizedReplace(sourceLines, contextLines, newContent);
                    if (rebuilt != null)
                    {
                        Logger.Info("[PATCH] Whitespace-normalized match applied.");
                        matchCount = expectedCount;
                        return rebuilt;
                    }
                }
                else if (normalizedHits > 0)
                {
                    status = "Ambiguous";
                    matchCount = normalizedHits;
                    details = $"Ambiguous patch (whitespace-normalized): {normalizedHits} matches, expected {expectedCount}.";
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
