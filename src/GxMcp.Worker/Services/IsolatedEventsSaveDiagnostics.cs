using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// The stages of the isolated Events save, as reported in <c>savePhase</c>.
    /// The names are a contract with callers: they say where a failed save stopped.
    /// </summary>
    internal static class IsolatedEventsSavePhase
    {
        /// <summary>Pin, fingerprint and broker checks, and the write gate, before any SDK read for write.</summary>
        internal const string Preflight = "preflight";
        /// <summary>First fresh read of the object, outside any transaction.</summary>
        internal const string FreshRead = "fresh-read";
        /// <summary><c>baseVersion</c> against the version just read, before and inside the transaction.</summary>
        internal const string VersionConflictCheck = "version-conflict-check";
        /// <summary>Canonical snapshot, whole-KB inventory and transaction start.</summary>
        internal const string BaselineCapture = "baseline-capture";
        /// <summary>Fresh read inside the transaction.</summary>
        internal const string TransactionFreshRead = "transaction-fresh-read";
        /// <summary>Isolation pins re-checked against the in-transaction instance.</summary>
        internal const string TargetRecheck = "target-recheck";
        /// <summary>The requested Source set on the in-memory Events part.</summary>
        internal const string SourceAssignment = "source-assignment";
        /// <summary>Pattern save isolation set-up and <c>eventsPart.Save()</c>.</summary>
        internal const string PartSave = "part-save";
        /// <summary>The complete <c>KBObject.Save</c> and the isolation scope teardown.</summary>
        internal const string ObjectSave = "object-save";
        /// <summary>Snapshot, placement and whole-KB inventory compared before the commit.</summary>
        internal const string PreCommitVerification = "pre-commit-verification";
        /// <summary>The transaction commit.</summary>
        internal const string Commit = "commit";
        /// <summary>Fresh read after the transaction closed.</summary>
        internal const string PostCommitReread = "post-commit-reread";
        /// <summary>Persistence, metadata, sibling-part and whole-KB checks after the commit.</summary>
        internal const string PostCommitVerification = "post-commit-verification";
        /// <summary>The save and its verification finished successfully.</summary>
        internal const string Completed = "completed";

        /// <summary>Every phase, in the order a successful save passes through them.</summary>
        internal static readonly string[] All =
        {
            Preflight, FreshRead, VersionConflictCheck, BaselineCapture, TransactionFreshRead, TargetRecheck,
            SourceAssignment, PartSave, ObjectSave, PreCommitVerification, Commit, PostCommitReread,
            PostCommitVerification, Completed
        };
    }

    /// <summary>
    /// What a failed isolated save knows about its own failure: the stage, the
    /// exception, the SDK's own messages and a message a caller can act on. Every
    /// string is sanitized: no local path, credential, token or connection string.
    /// </summary>
    internal sealed class IsolatedEventsFailure
    {
        internal string Phase { get; set; }
        internal string ExceptionType { get; set; }
        internal List<string> InnerMessages { get; } = new List<string>();
        internal List<string> InnerExceptionTypes { get; } = new List<string>();
        internal JArray SdkMessages { get; set; } = new JArray();
        internal JArray SourceValidationErrors { get; set; } = new JArray();
        internal bool SdkMessagesTruncated { get; set; }
        internal string SdkMessagesError { get; set; }

        /// <summary>The cause, readable in one line; completes a sentence that names the phase.</summary>
        internal string Detail { get; set; }

        /// <summary>The exception's own message, kept when <see cref="Detail"/> replaced it.</summary>
        internal string OriginalError { get; set; }

        internal void WriteTo(JObject target)
        {
            target["savePhase"] = Phase;
            target["exceptionType"] = ExceptionType;
            target["innerMessages"] = new JArray(InnerMessages);
            if (InnerExceptionTypes.Count > 0) target["innerExceptionTypes"] = new JArray(InnerExceptionTypes);
            target["sdkMessages"] = SdkMessages;
            target["sourceValidationErrors"] = SourceValidationErrors;
            if (SdkMessagesTruncated) target["sdkMessagesTruncated"] = true;
            if (!string.IsNullOrEmpty(SdkMessagesError)) target["sdkMessagesError"] = SdkMessagesError;
            if (!string.IsNullOrEmpty(OriginalError)) target["originalError"] = OriginalError;
        }
    }

    /// <summary>
    /// Tracks the stage of an isolated save in its receipt. <c>savePhase</c> is
    /// updated before each stage, so after a failure it names the stage that
    /// failed and is then frozen: the verification that follows a rollback is
    /// reported under <c>verificationPhase</c> and cannot hide where the save broke.
    /// </summary>
    internal sealed class IsolatedEventsSaveTrace
    {
        internal IsolatedEventsSaveTrace(JObject receipt)
        {
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal JObject Receipt { get; }
        internal string Phase { get; private set; } = IsolatedEventsSavePhase.Preflight;

        /// <summary>The phase of the first recorded failure; null while nothing failed.</summary>
        internal string FailedPhase { get; private set; }

        internal bool Failed => FailedPhase != null;

        internal void Enter(string phase)
        {
            Phase = phase;
            Receipt[Failed ? "verificationPhase" : "savePhase"] = phase;
        }

        /// <summary>
        /// Records a failure at the current phase. The first one fills the receipt's
        /// diagnostic fields; a later one, raised while verifying after the rollback,
        /// goes to <c>verificationFailure</c> so it cannot overwrite the cause of the
        /// failed save. Never throws: it runs on the failure path.
        /// </summary>
        internal IsolatedEventsFailure RecordFailure(Exception ex, Func<IEnumerable<KeyValuePair<string, object>>> sdkSources)
        {
            IsolatedEventsFailure failure = IsolatedEventsSaveDiagnostics.Describe(ex, Phase, sdkSources);
            try
            {
                if (!Failed)
                {
                    failure.WriteTo(Receipt);
                    FailedPhase = Phase;
                }
                else
                {
                    var nested = new JObject();
                    failure.WriteTo(nested);
                    Receipt["verificationFailure"] = nested;
                }
            }
            catch (Exception writeEx)
            {
                Logger.Debug("[ISOLATED-EVENTS] could not attach failure diagnostics: " + writeEx.Message);
            }
            return failure;
        }
    }

    internal static class IsolatedEventsSaveDiagnostics
    {
        internal const int MaxInnerMessages = 5;
        internal const int MaxSdkMessages = 25;

        // A safety net, not a tuning knob: a diagnostic on the failure path must never hang.
        // A pattern that times out makes Sanitize withhold the text instead.
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

        // Directory segments may hold spaces ("C:\Users\Jane Doe\..."), so they are
        // consumed segment by segment; the last token ends at the next space.
        private static readonly Regex WindowsPath = new Regex(
            @"(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\[^\s\\/]+[\\/])(?:[^\\/\r\n""'<>|*?:,;]{0,64}[\\/])*[^\s""'<>|*?,;]*",
            RegexOptions.Compiled, PatternTimeout);

        private static readonly Regex ConnectionPart = new Regex(
            @"(?i)\b(?<key>server|data\s+source|initial\s+catalog|database|user\s*id|uid|host|provider|dsn)\s*=\s*[^;\r\n]+",
            RegexOptions.Compiled, PatternTimeout);

        private static readonly Regex OpaqueToken = new Regex(
            @"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}"
            + @"|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]*)?"
            + @"|\b(?:ghp|gho|ghs|ghu|github_pat|xox[abpr]|sk)[-_][A-Za-z0-9_-]{16,}",
            RegexOptions.Compiled, PatternTimeout);

        private static readonly Regex SdkCode = new Regex(
            @"^\s*(?<code>[A-Za-z]{2,5}\d{3,5})\s*:", RegexOptions.Compiled, PatternTimeout);

        private static readonly Regex TextLine = new Regex(
            @"(?i)\bline\s*:?\s*(?<line>\d+)", RegexOptions.Compiled, PatternTimeout);

        /// <summary>
        /// Redacts what must not leave the Worker in a diagnostic: credentials and
        /// tokens, connection-string parts, local paths and raw GeneXus source, then
        /// bounds the length. Always single-line.
        /// </summary>
        internal static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            try
            {
                string value = WindowsPath.Replace(text, "<path>");
                value = ConnectionPart.Replace(value, m => m.Groups["key"].Value + "=<redacted>");
                value = OpaqueToken.Replace(value, "<redacted>");
                value = ReadDiagnosticSanitizer.SanitizeText(value, out _);
                return Regex.Replace(value, @"\s*[\r\n]+\s*", " ").Trim();
            }
            catch
            {
                return "[diagnostic text withheld: sanitization failed]";
            }
        }

        /// <summary>
        /// Builds the failure description. <paramref name="sdkSources"/> yields the
        /// SDK objects whose <c>Messages</c> may explain the failure (label, instance);
        /// it is evaluated here, guarded, so a broken SDK object cannot mask the failure.
        /// </summary>
        internal static IsolatedEventsFailure Describe(
            Exception ex, string phase, Func<IEnumerable<KeyValuePair<string, object>>> sdkSources)
        {
            var failure = new IsolatedEventsFailure
            {
                Phase = phase,
                ExceptionType = ex?.GetType().FullName ?? "unknown"
            };
            var rawInner = new List<string>();
            try
            {
                foreach (Exception inner in EnumerateInner(ex))
                {
                    rawInner.Add(inner.Message ?? string.Empty);
                    failure.InnerMessages.Add(Sanitize(inner.Message));
                    failure.InnerExceptionTypes.Add(inner.GetType().FullName);
                }
            }
            catch (Exception chainEx)
            {
                Logger.Debug("[ISOLATED-EVENTS] inner exception walk failed: " + chainEx.Message);
            }

            string sdkErrorText = string.Empty;
            try
            {
                if (sdkSources != null)
                    sdkErrorText = CollectSdkMessages(sdkSources(), failure);
            }
            catch (Exception sdkEx)
            {
                failure.SdkMessagesError = Sanitize(sdkEx.Message);
            }

            string rawMessage = ex?.Message ?? string.Empty;
            // A wrapper's own message ("One or more errors occurred.") says nothing
            // about the cause, so it competes like a bare "Erro" does.
            string wrapperAware = ex is AggregateException || ex is System.Reflection.TargetInvocationException
                ? string.Empty
                : rawMessage;
            string detail = WritePolicy.PreferDetailedMessage(wrapperAware, sdkErrorText, null);
            if (WritePolicy.IsBareGenericError(detail))
            {
                // An informative inner exception beats a bare "Erro" on the outer one.
                string informativeInner = rawInner.FirstOrDefault(m => !WritePolicy.IsBareGenericError(m));
                if (informativeInner != null) detail = informativeInner;
            }

            bool generic = WritePolicy.IsBareGenericError(detail);
            failure.Detail = generic
                ? "the SDK raised a generic error ('" + failure.ExceptionType + "') and exposed no detailed message; see exceptionType, innerMessages and sdkMessages"
                : Sanitize(detail);
            string original = rawMessage.Trim();
            if (original.Length > 0 && (generic || !string.Equals(detail.Trim(), original, StringComparison.Ordinal)))
                failure.OriginalError = Sanitize(original);
            return failure;
        }

        private static IEnumerable<Exception> EnumerateInner(Exception ex)
        {
            int emitted = 0;
            var pending = new Queue<Exception>();
            Enqueue(pending, ex);
            while (pending.Count > 0 && emitted < MaxInnerMessages)
            {
                Exception current = pending.Dequeue();
                emitted++;
                yield return current;
                Enqueue(pending, current);
            }
        }

        private static void Enqueue(Queue<Exception> pending, Exception parent)
        {
            if (parent is AggregateException aggregate)
            {
                foreach (Exception child in aggregate.InnerExceptions) pending.Enqueue(child);
            }
            else if (parent?.InnerException != null)
            {
                pending.Enqueue(parent.InnerException);
            }
        }

        /// <summary>
        /// Reads the SDK's own messages from <paramref name="sources"/>, fills the
        /// structured lists on <paramref name="failure"/> and returns the raw text of
        /// the error-severity ones, the input <see cref="WritePolicy.PreferDetailedMessage"/> needs.
        /// </summary>
        private static string CollectSdkMessages(IEnumerable<KeyValuePair<string, object>> sources, IsolatedEventsFailure failure)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var errorTexts = new List<string>();
            var messages = new JArray();
            var sourceErrors = new JArray();
            foreach (var source in sources ?? Enumerable.Empty<KeyValuePair<string, object>>())
            {
                if (source.Value == null) continue;
                foreach (object message in ReadMessages(source.Value))
                {
                    var entry = DescribeMessage(message, source.Key, out string rawText);
                    string identity = source.Key + "|" + entry["code"] + "|" + entry["line"] + "|" + entry["message"];
                    if (!seen.Add(identity)) continue;
                    if (messages.Count >= MaxSdkMessages)
                    {
                        failure.SdkMessagesTruncated = true;
                        continue;
                    }
                    messages.Add(entry);
                    bool isError = string.Equals(entry["severity"]?.ToString(), "Error", StringComparison.Ordinal);
                    if (isError) errorTexts.Add(rawText);
                    if (isError && string.Equals(source.Key, "Events", StringComparison.OrdinalIgnoreCase))
                    {
                        var sourceError = new JObject();
                        foreach (string key in new[] { "code", "line", "column", "message" })
                            if (entry[key] != null) sourceError[key] = entry[key];
                        sourceErrors.Add(sourceError);
                    }
                }
            }
            failure.SdkMessages = messages;
            failure.SourceValidationErrors = sourceErrors;
            return string.Join(" | ", errorTexts);
        }

        private static List<object> ReadMessages(object owner)
        {
            var messages = new List<object>();
            try
            {
                var property = owner.GetType().GetProperty("Messages",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (property?.GetValue(owner) is IEnumerable list)
                    foreach (object message in list)
                        if (message != null) messages.Add(message);
            }
            catch
            {
                // A part whose Messages cannot be read contributes nothing; the others still do.
            }
            return messages;
        }

        /// <summary>One SDK message as <c>{code?, part, line?, column?, severity, message}</c>.</summary>
        private static JObject DescribeMessage(object message, string part, out string rawText)
        {
            Type type = message.GetType();
            rawText = SdkDiagnosticsHelper.ReadStringMember(message, type, "Text")
                ?? SdkDiagnosticsHelper.ReadStringMember(message, type, "Description")
                ?? SdkDiagnosticsHelper.ReadStringMember(message, type, "Message")
                ?? message.ToString();
            string code = SdkDiagnosticsHelper.ReadStringMember(message, type, "ErrorCode")
                ?? SdkDiagnosticsHelper.ReadStringMember(message, type, "Id")
                ?? SdkDiagnosticsHelper.ReadStringMember(message, type, "Code");
            if (string.IsNullOrEmpty(code))
            {
                Match match = SdkCode.Match(rawText);
                if (match.Success) code = match.Groups["code"].Value;
            }

            int? line = null;
            int? column = null;
            object position = SdkDiagnosticsHelper.ReadMember(message, type, "Position");
            if (position != null && position.GetType().Name.Contains("TextPosition"))
            {
                Type positionType = position.GetType();
                line = ToInt(SdkDiagnosticsHelper.ReadMember(position, positionType, "Line"));
                column = ToInt(SdkDiagnosticsHelper.ReadMember(position, positionType, "Char")
                    ?? SdkDiagnosticsHelper.ReadMember(position, positionType, "Column"));
            }
            if (line == null)
            {
                Match match = TextLine.Match(rawText);
                if (match.Success && int.TryParse(match.Groups["line"].Value, out int parsed)) line = parsed;
            }

            string level = (SdkDiagnosticsHelper.ReadMember(message, type, "Level")?.ToString())
                ?? (SdkDiagnosticsHelper.ReadMember(message, type, "Type")?.ToString()) ?? string.Empty;
            string severity = "Error";
            string descriptor = level + " " + type.Name;
            if (descriptor.IndexOf("Warn", StringComparison.OrdinalIgnoreCase) >= 0) severity = "Warning";
            else if (descriptor.IndexOf("Info", StringComparison.OrdinalIgnoreCase) >= 0) severity = "Information";

            var entry = new JObject();
            if (!string.IsNullOrEmpty(code)) entry["code"] = code;
            entry["part"] = part;
            if (line.HasValue) entry["line"] = line.Value;
            if (column.HasValue) entry["column"] = column.Value;
            entry["severity"] = severity;
            entry["message"] = Sanitize(rawText);
            return entry;
        }

        private static int? ToInt(object value)
        {
            if (!(value is IConvertible)) return null;
            try { return Convert.ToInt32(value); }
            catch { return null; }
        }
    }
}
