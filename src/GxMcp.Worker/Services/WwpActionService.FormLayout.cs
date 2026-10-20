using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // Issue #595: a loose <table> (optionally a panel) created in a WorkWithPlus form, and an
    // existing leaf control moved into a table. Native Pattern SDK element commands first -
    // the path add_tab uses - with the XML projection for the preview and for the post-save
    // verification; exact snapshots, re-read, parent projection and rollback as the other
    // typed WorkWithPlus writes. A container's childrenOrderedList is maintained entry by
    // entry (PatternChildOrderEdit) and refused when it cannot be, never rebuilt by guess.
    public sealed partial class WwpActionService
    {
        private const string AddTableOperation = "add_table";
        private const string MoveControlOperation = "move_control";
        private static readonly string[] MovableControlKinds = { "textBlock", "variable", "attribute" };
        private static readonly Regex LayoutIdentifierPattern = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static bool IsFormLayoutOperation(string operation) =>
            operation == AddTableOperation || operation == MoveControlOperation;

        private sealed class FormLayoutOperation
        {
            internal string Name, DryRunCode, UpdatedCode, FailedCode, NotPersistedCode, LogTag, SdkOperation, StaleMessage, SnapshotMessage;
            internal Func<XDocument, JObject, JObject> Plan;
            internal Func<object, JObject, JObject, JObject> ApplyNative;
            internal Func<XDocument, XDocument, JObject, JObject> VerifyPattern;
            internal Func<string, JObject, JObject> VerifyWebForm;
        }

        private static readonly FormLayoutOperation AddTableDescriptor = new FormLayoutOperation
        {
            Name = AddTableOperation, DryRunCode = "WwpTableDryRun", UpdatedCode = "WwpTableAdded",
            FailedCode = "WwpTableFailed", NotPersistedCode = "WwpTableNotPersisted", LogTag = "ADDTABLE",
            SdkOperation = "PatternInstance.RootElement + CreateChildElement(table) + InsertElementCommand/AddElementCommand",
            StaleMessage = "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no table was added.",
            SnapshotMessage = "Exact PatternInstance and parent WebForm snapshots were not available; no table was added.",
            Plan = PlanAddTableXml, ApplyNative = ApplyNativeAddTable,
            VerifyPattern = VerifyAddedTable, VerifyWebForm = VerifyAddedTableProjection
        };

        private static readonly FormLayoutOperation MoveControlDescriptor = new FormLayoutOperation
        {
            Name = MoveControlOperation, DryRunCode = "WwpControlMoveDryRun", UpdatedCode = "WwpControlMoved",
            FailedCode = "WwpControlMoveFailed", NotPersistedCode = "WwpControlMoveNotPersisted", LogTag = "MOVECONTROL",
            SdkOperation = "PatternInstance.RootElement + RemoveElementCommand + InsertElementCommand/AddElementCommand",
            StaleMessage = "The WorkWithPlus PatternInstance changed after the caller's read/dry-run; no control was moved.",
            SnapshotMessage = "Exact PatternInstance and parent WebForm snapshots were not available; no control was moved.",
            Plan = PlanMoveControlXml, ApplyNative = ApplyNativeMoveControl,
            VerifyPattern = VerifyMovedControl, VerifyWebForm = VerifyMovedControlProjection
        };

        private string RunFormLayoutOperation(string target, KBObject requestedObject, KBObject instance,
            KBObjectPart instancePart, string xml, string operation, JObject args)
        {
            args = args ?? new JObject();
            FormLayoutOperation op = operation == AddTableOperation ? AddTableDescriptor : MoveControlDescriptor;
            bool dryRun = args["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args["rollbackOnFailure"]?.ToObject<bool?>() ?? true;

            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject preview = op.Plan(previewDocument, args);
            if (preview["error"] != null)
                return McpResponse.Err(code: preview["code"]?.ToString() ?? "WwpLayoutInvalid",
                    message: preview["error"].ToString(), target: target, extra: preview);

            string versionToken = WriteService.ComputeContentVersionToken(instance, xml);
            if (dryRun)
                return McpResponse.Ok(target: target, code: op.DryRunCode,
                    result: BuildFormLayoutReceipt(instance.Name, op, preview, versionToken, persisted: false, rollbackOnFailure));

            lock (WriteService.AcquirePerTargetLock(target))
            {
                KBObject lockedTarget = _objects.FindObject(target) ?? requestedObject;
                string currentXml = _patterns.ReadPatternPartXml(lockedTarget, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                    out KBObject currentInstance, out _);
                _patterns.BuildPatternPartEnvelope(lockedTarget, "PatternInstance", currentXml, PatternRegistry.WorkWithPlusPatternId,
                    out _, out KBObjectPart currentPart);
                if (currentInstance == null || currentPart == null || string.IsNullOrWhiteSpace(currentXml))
                    return BuildWwpInstanceNotResolvable(target);

                string expectedVersion = args["baseVersion"]?.ToString()
                    ?? args["expectedVersion"]?.ToString()
                    ?? args["versionToken"]?.ToString();
                string currentVersion = WriteService.ComputeContentVersionToken(currentInstance, currentXml);
                if (!string.IsNullOrWhiteSpace(expectedVersion)
                    && !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
                    return BuildWwpStaleObject(target, expectedVersion, currentVersion, op.StaleMessage);

                // Recomputed under the per-object lock so a preview is never applied to a
                // PatternInstance that moved in the meantime.
                XDocument lockedAfter = XDocument.Parse(currentXml, LoadOptions.PreserveWhitespace);
                JObject plan = op.Plan(lockedAfter, args);
                if (plan["error"] != null)
                    return McpResponse.Err(code: plan["code"]?.ToString() ?? "WwpLayoutInvalid",
                        message: plan["error"].ToString(), target: target, extra: plan);

                KBObject parent = WwpProjectionHelper.ResolveHostParent(currentInstance, _objects, currentXml);
                string parentWebFormBefore = ReadPart(parent, "WebForm");
                byte[] nativeBytes = ReadPartBytes(currentPart);
                SnapshotBundle snapshots = CaptureSnapshots(currentInstance, currentXml, parent, parentWebFormBefore);
                string applyOnSaveBefore = ReadObjectProperty(currentInstance, "SDPlus_Editor_Apply_On_Save");
                if (WwpSnapshotsIncomplete(nativeBytes, parent, parentWebFormBefore, snapshots))
                    return McpResponse.Err(code: "WwpSnapshotRequired", message: op.SnapshotMessage,
                        target: target, extra: new JObject
                        {
                            ["snapshot"] = snapshots.ToJson(),
                            ["nativeBytesAvailable"] = nativeBytes != null,
                            ["parentResolved"] = parent != null,
                            ["parentWebFormAvailable"] = parentWebFormBefore != null,
                            ["persisted"] = false
                        });

                bool persistenceStarted = false;
                try
                {
                    JObject nativeMutation = op.ApplyNative(currentPart, args, plan);
                    if (nativeMutation["error"] != null)
                        throw new WwpTabException(nativeMutation["code"]?.ToString() ?? "WwpNativeMutationRejected",
                            nativeMutation["error"].ToString());

                    persistenceStarted = true;
                    SaveNativePattern(currentInstance, currentPart);
                    bool applyOnSaveReenabled = WwpApplyOnSaveHelper.TryEnable(currentInstance);
                    string persistedXml = _patterns.ReadPatternPartXml(currentInstance, "PatternInstance", PatternRegistry.WorkWithPlusPatternId,
                        out KBObject persistedInstance, out _);
                    if (string.IsNullOrWhiteSpace(persistedXml))
                        throw new WwpTabException(op.NotPersistedCode, "The SDK save completed, but the PatternInstance could not be re-read.");

                    XDocument persistedDocument = XDocument.Parse(persistedXml, LoadOptions.PreserveWhitespace);
                    JObject verification = op.VerifyPattern(lockedAfter, persistedDocument, plan);
                    if (verification["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException(op.NotPersistedCode,
                            verification["message"]?.ToString()
                                ?? "The SDK save completed, but the requested layout state did not survive the PatternInstance re-read.");

                    string applyOnSaveAfter = ReadObjectProperty(persistedInstance ?? currentInstance, "SDPlus_Editor_Apply_On_Save");
                    if (IsFalse(applyOnSaveAfter))
                        throw new WwpTabException("WwpApplyOnSaveDisabled",
                            "SDPlus_Editor_Apply_On_Save became False after the native layout save.");

                    bool projected = WwpProjectionHelper.TryProjectHostOntoParent(parent, persistedInstance ?? currentInstance);
                    if (!projected)
                        throw new WwpTabException("WwpProjectionFailed",
                            "The PatternInstance persisted, but the WorkWithPlus SDK did not project the parent WebForm.");
                    JObject projection = op.VerifyWebForm(ReadPart(parent, "WebForm"), plan);
                    if (projection["confirmed"]?.ToObject<bool?>() != true)
                        throw new WwpTabException("WwpProjectionNotConfirmed",
                            projection["message"]?.ToString() ?? "The projected WebForm did not contain the requested layout state.");

                    WriteService.NotePerTargetWrite(target);
                    JObject receipt = BuildFormLayoutReceipt(persistedInstance?.Name ?? currentInstance.Name, op, plan,
                        WriteService.ComputeContentVersionToken(persistedInstance ?? currentInstance, persistedXml),
                        persisted: true, rollbackOnFailure);
                    receipt["parent"] = parent.Name;
                    receipt["patternVerification"] = verification;
                    receipt["projection"] = projection;
                    receipt["applyOnSaveBefore"] = applyOnSaveBefore;
                    receipt["applyOnSaveAfter"] = applyOnSaveAfter;
                    receipt["applyOnSaveReenabled"] = applyOnSaveReenabled;
                    receipt["snapshot"] = snapshots.ToJson();
                    receipt["rollbackPerformed"] = false;
                    receipt["partialPersistenceDetected"] = false;
                    return McpResponse.Ok(target: target, code: op.UpdatedCode, result: receipt);
                }
                catch (Exception ex)
                {
                    WwpTabException typed = ex as WwpTabException;
                    JObject rollback = rollbackOnFailure
                        ? RestoreSnapshots(currentInstance, currentPart, nativeBytes, currentXml, parent, parentWebFormBefore, applyOnSaveBefore)
                        : new JObject { ["performed"] = false, ["skipped"] = true, ["exact"] = false };
                    ExceptionRoot.Log("[WWP-" + op.LogTag + "] failed", ex);
                    return McpResponse.Err(code: typed?.Code ?? op.FailedCode, message: ExceptionRoot.Message(ex),
                        target: target, extra: new JObject
                        {
                            ["operation"] = op.Name,
                            ["exceptionType"] = ExceptionRoot.Unwrap(ex)?.GetType().Name,
                            ["failureTrace"] = ExceptionRoot.FailureTrace(ex),
                            ["persisted"] = false,
                            ["saved"] = false,
                            ["partialPersistenceDetected"] = persistenceStarted,
                            ["patternReReadConfirmed"] = false,
                            ["webFormProjectionConfirmed"] = false,
                            ["rollback"] = rollback,
                            ["rollbackPerformed"] = rollbackOnFailure,
                            ["stateRestoredExactly"] = rollback["exact"]?.DeepClone() ?? false,
                            ["snapshot"] = snapshots.ToJson(),
                            ["rollbackOnFailure"] = rollbackOnFailure,
                            ["lifecycleExecuted"] = false
                        });
                }
            }
        }

        internal static JObject BuildFormLayoutReceipt(string instanceName, string operation, JObject plan, string versionToken, bool persisted)
            => BuildFormLayoutReceipt(instanceName, operation == AddTableOperation ? AddTableDescriptor : MoveControlDescriptor,
                plan, versionToken, persisted, true);

        private static JObject BuildFormLayoutReceipt(string instanceName, FormLayoutOperation op, JObject plan,
            string versionToken, bool persisted, bool rollbackOnFailure)
        {
            var receipt = new JObject { ["instance"] = instanceName, ["operation"] = op.Name };
            foreach (JProperty property in plan.Properties())
                if (property.Name != "changed" && property.Name != "error" && property.Name != "code")
                    receipt[property.Name] = property.Value.DeepClone();
            receipt["mutationMode"] = "native-pattern-sdk";
            receipt["sdkOperation"] = op.SdkOperation;
            receipt["versionToken"] = versionToken;
            receipt["persisted"] = persisted;
            receipt["saved"] = persisted;
            receipt["patternReReadConfirmed"] = persisted;
            receipt["webFormProjectionConfirmed"] = persisted;
            receipt["rollbackOnFailure"] = rollbackOnFailure;
            receipt["lifecycleExecuted"] = false;
            receipt["specified"] = false;
            receipt["generated"] = false;
            receipt["built"] = false;
            return receipt;
        }

        // ---- add_table: request --------------------------------------------------------------

        internal sealed class AddTableRequest
        {
            internal string TableName;
            internal int NumberOfColumns;
            internal string TableType;
            internal bool? IncludeInPanel;
            internal string PanelTitle;
            internal JArray Children;
        }

        internal static JObject ValidateAddTableArgs(JObject args, out AddTableRequest request)
        {
            request = new AddTableRequest { TableName = args?["tableName"]?.ToString()?.Trim(), NumberOfColumns = 1, TableType = "Responsive" };
            if (string.IsNullOrWhiteSpace(request.TableName))
                return TableError("MissingTableName", "tableName is required for add_table.");
            if (!LayoutIdentifierPattern.IsMatch(request.TableName))
                return TableError("InvalidTableName", "tableName must be a GeneXus control identifier (letters, digits and underscore, not starting with a digit).");
            JToken columns = args?["numberOfColumns"];
            if (columns != null && columns.Type != JTokenType.Null)
            {
                if (!(columns.Type == JTokenType.Integer || (columns.Type == JTokenType.String && int.TryParse(columns.ToString(), out _)))
                    || columns.ToObject<int>() < 1)
                    return TableError("InvalidNumberOfColumns", "numberOfColumns must be an integer of at least 1.");
                request.NumberOfColumns = columns.ToObject<int>();
            }
            string tableType = args?["tableType"]?.ToString();
            if (!string.IsNullOrWhiteSpace(tableType))
            {
                if (!TryNormalizeTableType(tableType, out request.TableType, out string typeError))
                    return TableError("InvalidTableType", typeError);
            }
            JToken includeInPanel = args?["includeInPanel"];
            if (includeInPanel != null && includeInPanel.Type != JTokenType.Null)
            {
                if (includeInPanel.Type != JTokenType.Boolean)
                    return TableError("InvalidIncludeInPanel", "includeInPanel must be true or false.");
                request.IncludeInPanel = includeInPanel.Value<bool>();
            }
            string panelTitle = args?["panelTitle"]?.ToString();
            if (!string.IsNullOrWhiteSpace(panelTitle))
            {
                if (request.IncludeInPanel == false)
                    return TableError("InvalidPanelTitle", "panelTitle requires includeInPanel=true; a table outside a panel has no panel title.");
                request.IncludeInPanel = true;
                request.PanelTitle = panelTitle;
            }
            JToken children = args?["children"];
            if (children != null && children.Type != JTokenType.Null)
            {
                if (!(children is JArray array) || array.Any(c => !(c is JObject)))
                    return TableError("InvalidWwpChild", "children must be an array of typed child objects.");
                request.Children = array;
            }
            return ValidatePlacementArgs(args);
        }

        private static bool HasPlacement(JObject args) =>
            args?["position"] != null && args["position"].Type != JTokenType.Null
            || !string.IsNullOrWhiteSpace(args?["before"]?.ToString())
            || !string.IsNullOrWhiteSpace(args?["after"]?.ToString());

        private static JObject ValidatePlacementArgs(JObject args)
        {
            int given = (args?["position"] != null && args["position"].Type != JTokenType.Null ? 1 : 0)
                + (string.IsNullOrWhiteSpace(args?["before"]?.ToString()) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(args?["after"]?.ToString()) ? 0 : 1);
            if (given > 1)
                return TableError("ConflictingPlacement", "Pass only one of position, before or after.");
            if (args?["position"] != null && args["position"].Type != JTokenType.Null
                && (args["position"].Type != JTokenType.Integer || args["position"].Value<int>() < 0))
                return TableError("InvalidPosition", "position must be a zero-based integer index among the container's children.");
            return null;
        }

        // ---- add_table: XML plan -------------------------------------------------------------

        internal static JObject PlanAddTableXml(XDocument document, JObject args)
        {
            JObject invalid = ValidateAddTableArgs(args, out AddTableRequest request);
            if (invalid != null) return invalid;
            JObject containerError = ResolveDefaultFormTable(document, args, "TableContainerNotFound", "TableContainerAmbiguous",
                "add_table:", "the new table", "add_table", out XElement container);
            if (containerError != null) return containerError;

            HashSet<string> taken = TakenControlNames(document);
            if (taken.Contains(request.TableName))
                return TableError("TableAlreadyExists", "A control named '" + request.TableName + "' already exists in the PatternInstance; table names must be unique on the form.");

            string snapshot = document.ToString(SaveOptions.DisableFormatting);
            string containerPath = ContainerPath(container);
            string containerBefore = container.ToString(SaveOptions.DisableFormatting);
            var movedAttributes = new JArray();
            var sourceParents = new List<XElement>();
            XElement table = BuildTableElement(request, container.GetDefaultNamespace());
            try
            {
                foreach (JObject child in request.Children?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    table.Add(BuildXmlControl(child));
                JObject collision = CheckNewControlNames(table, taken, request.TableName);
                if (collision != null) return collision;
                // The table is still detached, so the scope search cannot see its children.
                JObject moveError = MoveFormAttributesIntoTab(table, FormScope(container), movedAttributes, sourceParents, maintainOrderedLists: true);
                if (moveError != null) { RestoreDocument(document, snapshot); return moveError; }
            }
            catch (WwpTabException ex) { RestoreDocument(document, snapshot); return TableError(ex.Code, ex.Message); }

            JObject placementError = ResolveInsertIndex(container, args, out int index);
            if (placementError != null) { RestoreDocument(document, snapshot); return placementError; }
            InsertChildAt(container, table, index);
            if (!PatternChildOrderEdit.TryInsert(container, table, out PatternChildOrderEdit.Change ordering, out string reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(containerPath, reason);
            }
            // Recorded once the document is final, so the verification can find each source
            // table by position rather than by a path an unnamed table would share.
            for (int i = 0; i < sourceParents.Count; i++)
                ((JObject)movedAttributes[i])["sourceIndexPath"] = new JArray(IndexPath(sourceParents[i]));

            return new JObject
            {
                ["changed"] = true,
                ["tableName"] = request.TableName,
                ["containerPath"] = containerPath,
                ["position"] = index,
                ["typedDiff"] = new JObject
                {
                    ["operation"] = AddTableOperation,
                    ["tableName"] = request.TableName,
                    ["containerPath"] = containerPath,
                    ["position"] = index,
                    ["before"] = containerBefore,
                    ["after"] = container.ToString(SaveOptions.DisableFormatting),
                    ["table"] = table.ToString(SaveOptions.DisableFormatting)
                },
                ["children"] = new JArray(table.Elements().Select(ProjectXmlControl)),
                ["movedAttributes"] = movedAttributes,
                ["childrenOrderedList"] = OrderingJson(ordering)
            };
        }

        private static XElement BuildTableElement(AddTableRequest request, XNamespace ns)
        {
            var table = new XElement(ns + "table", new XAttribute("name", request.TableName));
            SetPair(table, "type", request.TableType);
            SetPair(table, "numberOfColumns", request.NumberOfColumns.ToString(CultureInfo.InvariantCulture));
            if (request.IncludeInPanel.HasValue) SetPair(table, "includeInPanel", new JValue(request.IncludeInPanel.Value));
            if (request.PanelTitle != null) table.SetAttributeValue("panelTitle", request.PanelTitle);
            return table;
        }

        private static HashSet<string> TakenControlNames(XDocument document)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in document.Descendants())
            {
                string name = Attr(element, "name");
                if (name.Length > 0) taken.Add(name);
                string controlName = Attr(element, "controlName");
                if (controlName.Length > 0) taken.Add(controlName);
            }
            return taken;
        }

        // A created child (textBlock, variable, userAction, nested table) must not reuse a
        // name that is already on the form: a repeated control would be the IDE's problem to
        // report after the fact. Attribute children are moves and are checked by the mover.
        private static JObject CheckNewControlNames(XElement table, HashSet<string> taken, string tableName)
        {
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tableName };
            foreach (XElement created in table.Descendants().Where(e => !Is(e, "attribute")))
            {
                string identity = Attr(created, "name");
                if (identity.Length == 0) identity = Attr(created, "controlName");
                if (identity.Length == 0) continue;
                if (!requested.Add(identity))
                    return TableError("InvalidWwpChild", "'" + identity + "' is requested more than once in the same add_table call.");
                if (taken.Contains(identity))
                {
                    JObject error = TableError(Is(created, "table") ? "TableAlreadyExists" : "ControlNameAlreadyExists",
                        "A control named '" + identity + "' already exists in the PatternInstance; add_table creates new controls only.");
                    error["hint"] = "Use genexus_wwp action=move_control to move the existing control into the table.";
                    return error;
                }
            }
            return null;
        }

        private static JObject OrderedContainerUnsupported(string path, string reason)
        {
            JObject error = TableError("WwpOrderedContainerUnsupported",
                "The container '" + path + "' carries a childrenOrderedList the MCP cannot maintain (" + reason + "); no mutation was applied.");
            error["containerPath"] = path;
            error["hint"] = "The IDE hides children missing from childrenOrderedList, so the list is never rewritten by guess. Fix the list in the GeneXus IDE or choose another container.";
            return error;
        }

        private static JObject OrderingJson(PatternChildOrderEdit.Change change)
        {
            if (change == null || change.Before == null) return null;
            return new JObject
            {
                ["before"] = change.Before,
                ["after"] = change.After,
                ["entry"] = change.Entry,
                ["insertedBefore"] = change.InsertedBefore,
                ["changed"] = change.Changed
            };
        }

        private static void RestoreDocument(XDocument document, string snapshot)
        {
            document.Root.ReplaceWith(XDocument.Parse(snapshot, LoadOptions.PreserveWhitespace).Root);
        }

        private static void InsertChildAt(XElement container, XElement child, int index)
        {
            List<XElement> siblings = container.Elements().ToList();
            if (index >= siblings.Count) container.Add(child); else siblings[index].AddBeforeSelf(child);
        }

        // The label a sibling is addressed by for before/after: name, controlName, the field
        // name of an attribute reference, "Grid" for the unnamed grid, "ErrorViewer", or a
        // group table's title.
        private static string SiblingIdentity(XElement element)
        {
            string name = Attr(element, "name");
            if (name.Length > 0) return name;
            string controlName = Attr(element, "controlName");
            if (controlName.Length > 0) return controlName;
            string attribute = Attr(element, "attribute");
            if (attribute.Length > 0) { int dash = attribute.LastIndexOf('-'); return dash >= 0 ? attribute.Substring(dash + 1) : attribute; }
            if (Is(element, "grid")) return "Grid";
            if (Is(element, "errorViewer")) return "ErrorViewer";
            string title = Attr(element, "title");
            return title.Length > 0 ? title : string.Empty;
        }

        private static bool SiblingMatches(XElement element, string requested) =>
            string.Equals(SiblingIdentity(element), requested, StringComparison.OrdinalIgnoreCase)
            || (Is(element, "attribute") && AttributeReferenceMatches(Attr(element, "attribute"), requested));

        private static JObject ResolveInsertIndex(XElement container, JObject args, out int index)
        {
            List<XElement> siblings = container.Elements().ToList();
            index = siblings.Count;
            string before = args?["before"]?.ToString()?.Trim();
            string after = args?["after"]?.ToString()?.Trim();
            int? position = args?["position"]?.Type == JTokenType.Integer ? args["position"].Value<int>() : (int?)null;
            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            {
                string requested = string.IsNullOrWhiteSpace(before) ? after : before;
                int found = siblings.FindIndex(e => SiblingMatches(e, requested));
                if (found < 0)
                {
                    JObject error = TableError("PlacementSiblingNotFound",
                        "No child of '" + ContainerPath(container) + "' is addressed by '" + requested + "'.");
                    error["availableSiblings"] = new JArray(siblings.Select(SiblingIdentity).Where(s => s.Length > 0));
                    return error;
                }
                index = string.IsNullOrWhiteSpace(before) ? found + 1 : found;
            }
            else if (position.HasValue) index = ClampPosition(position, siblings.Count);
            return null;
        }

        // ---- add_table: native ---------------------------------------------------------------

        internal static JObject ApplyNativeAddTable(object part, JObject args, JObject plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TableError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject invalid = ValidateAddTableArgs(args, out AddTableRequest request);
            if (invalid != null) return invalid;
            JObject containerError = ResolveDefaultNativeFormTable(root, args, "TableContainerNotFound", "TableContainerAmbiguous",
                "add_table:", "the new table", "add_table", out object container);
            if (containerError != null) return containerError;
            if (Walk(root).Any(e => NativeNameMatches(e, request.TableName)))
                return TableError("TableAlreadyExists", "A native control named '" + request.TableName + "' already exists in the PatternInstance.");

            // Every ordering-list edit is checked against the native value before anything
            // moves, so a mismatch between the native tree and the persisted XML refuses
            // cleanly instead of being discovered halfway through the update.
            var orderingEdits = new List<KeyValuePair<object, JObject>>();
            JObject orderingError = CollectOrderingEdit(container, plan?["childrenOrderedList"] as JObject, orderingEdits);
            if (orderingError != null) return orderingError;
            foreach (JObject moved in (plan?["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                if (!(moved["childrenOrderedList"] is JObject change) || change["changed"]?.ToObject<bool?>() != true) continue;
                JObject sourceError = ResolveNativeContainer(root, moved["from"]?.ToString(), out object source);
                if (sourceError != null) { sourceError["code"] = "AttributeSourceUnresolved"; return sourceError; }
                orderingError = CollectOrderingEdit(source, change, orderingEdits);
                if (orderingError != null) return orderingError;
            }

            object scope = NativeFormScope(root, container);
            var requestedAttributes = new List<string>();
            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            int? insertIndex = null;
            Action mutation = () =>
            {
                object table = CreateNativeChild(container, "table");
                SetNativeAttribute(table, "name", request.TableName);
                SetNativePair(table, "type", request.TableType);
                SetNativeNumberPair(table, "numberOfColumns", request.NumberOfColumns);
                if (request.IncludeInPanel.HasValue) SetNativePair(table, "includeInPanel", request.IncludeInPanel.Value ? "True" : "False");
                if (request.PanelTitle != null) SetNativeAttribute(table, "panelTitle", request.PanelTitle);
                foreach (JObject child in request.Children?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    AttachNativeControl(table, child, scope, requestedAttributes);
                insertIndex = ResolveNativeInsertIndex(container, args, null);
                if (insertIndex.HasValue) ExecuteElementCommand(container, table, "InsertElementCommand", insertIndex.Value);
                else ExecuteElementCommand(container, table, "AddElementCommand", null);
                foreach (var edit in orderingEdits) SetNativeAttribute(edit.Key, "childrenOrderedList", edit.Value["after"].ToString());
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp add_table", mutation });
            else mutation();

            return new JObject
            {
                ["changed"] = true,
                ["tableName"] = request.TableName,
                ["sdkOperation"] = "CreateChildElement(table) + " + (insertIndex.HasValue ? "InsertElementCommand" : "AddElementCommand")
            };
        }

        private static bool NativeNameMatches(object element, string name) =>
            string.Equals(NativeAttribute(element, "name"), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NativeAttribute(element, "controlName"), name, StringComparison.OrdinalIgnoreCase);

        // The native list must be the one the plan was computed from; the plan's new value
        // is then applied verbatim.
        private static JObject CollectOrderingEdit(object container, JObject change, List<KeyValuePair<object, JObject>> edits)
        {
            if (change == null || change["changed"]?.ToObject<bool?>() != true) return null;
            string native = NativeAttributeOrXml(container, "childrenOrderedList");
            if (!OrderedListEquivalent(native, change["before"]?.ToString()))
                return TableError("WwpOrderingMetadataMismatch",
                    "The native childrenOrderedList of '" + NativeContainerLabel(container) + "' differs from the persisted PatternInstance; no mutation was applied.");
            edits.Add(new KeyValuePair<object, JObject>(container, change));
            return null;
        }

        private static bool OrderedListEquivalent(string left, string right) =>
            string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

        // numberOfColumns is declared as a number by the pattern; a string is the fallback for
        // a package that declares it as text. Either way the default twin follows.
        private static void SetNativeNumberPair(object element, string name, int value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            foreach (string attribute in new[] { name, "default" + char.ToUpperInvariant(name[0]) + name.Substring(1) })
            {
                bool applied;
                try { applied = PatternSemanticAttributeWriter.ApplySemanticAttributeObject(element, attribute, value, text); }
                catch { applied = false; }
                if (!applied) SetNativeAttribute(element, attribute, text);
            }
        }

        private static string NativeSiblingIdentity(object element)
        {
            string name = NativeAttribute(element, "name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
            string controlName = NativeAttribute(element, "controlName");
            if (!string.IsNullOrWhiteSpace(controlName)) return controlName;
            string attribute = NativeAttribute(element, "attribute");
            if (!string.IsNullOrWhiteSpace(attribute)) { int dash = attribute.LastIndexOf('-'); return dash >= 0 ? attribute.Substring(dash + 1) : attribute; }
            if (IsNativeGrid(element)) return "Grid";
            if (NativeIsKind(element, "errorViewer")) return "ErrorViewer";
            string title = NativeAttribute(element, "title");
            return string.IsNullOrWhiteSpace(title) ? string.Empty : title;
        }

        private static bool NativeSiblingMatches(object element, string requested) =>
            string.Equals(NativeSiblingIdentity(element), requested, StringComparison.OrdinalIgnoreCase)
            || (IsNativeAttribute(element) && AttributeReferenceMatches(NativeAttribute(element, "attribute"), requested));

        // Null means append. `excluded` is the element being moved within the same container.
        private static int? ResolveNativeInsertIndex(object container, JObject args, object excluded)
        {
            List<object> siblings = NativeChildren(container).Where(c => !ReferenceEquals(c, excluded)).ToList();
            string before = args?["before"]?.ToString()?.Trim();
            string after = args?["after"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(before) || !string.IsNullOrWhiteSpace(after))
            {
                string requested = string.IsNullOrWhiteSpace(before) ? after : before;
                int found = siblings.FindIndex(e => NativeSiblingMatches(e, requested));
                if (found < 0) throw new WwpTabException("PlacementSiblingNotFound", "No native child is addressed by '" + requested + "'.");
                int index = string.IsNullOrWhiteSpace(before) ? found + 1 : found;
                return index >= siblings.Count ? (int?)null : NativeChildren(container).IndexOf(siblings[index]);
            }
            if (args?["position"]?.Type == JTokenType.Integer)
            {
                int index = ClampPosition(args["position"].Value<int>(), siblings.Count);
                return index >= siblings.Count ? (int?)null : NativeChildren(container).IndexOf(siblings[index]);
            }
            return null;
        }

        private static bool NativeIsKind(object element, string kind) =>
            NativeType(element).Equals(kind, StringComparison.OrdinalIgnoreCase)
            || NativeElementXml(element)?.Name.LocalName.Equals(kind, StringComparison.OrdinalIgnoreCase) == true;

        // ---- add_table: verification ---------------------------------------------------------

        internal static JObject VerifyAddedTable(XDocument planned, XDocument persisted, JObject plan)
        {
            string tableName = plan["tableName"]?.ToString();
            string containerPath = plan["containerPath"]?.ToString();
            int index = plan["position"]?.ToObject<int?>() ?? -1;
            List<XElement> tables = persisted.Descendants().Where(e => Is(e, "table")
                && string.Equals(Attr(e, "name"), tableName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (tables.Count != 1)
                return NotConfirmed("The re-read PatternInstance contains table '" + tableName + "' " + tables.Count + " time(s) instead of once.");
            XElement table = tables[0];
            if (!string.Equals(ContainerPath(table.Parent), containerPath, StringComparison.OrdinalIgnoreCase))
                return NotConfirmed("The re-read table '" + tableName + "' is under '" + ContainerPath(table.Parent) + "' instead of '" + containerPath + "'.");
            if (table.Parent.Elements().ToList().IndexOf(table) != index)
                return NotConfirmed("The re-read table '" + tableName + "' is at child index " + table.Parent.Elements().ToList().IndexOf(table) + " instead of " + index + ".");
            XElement plannedTable = planned.Descendants().FirstOrDefault(e => Is(e, "table")
                && string.Equals(Attr(e, "name"), tableName, StringComparison.OrdinalIgnoreCase));
            if (plannedTable == null) return NotConfirmed("The planned PatternInstance has no table '" + tableName + "'.");
            if (!IsRequestedSubset(ProjectXmlControl(plannedTable), ProjectXmlControl(table)))
                return NotConfirmed("The re-read table '" + tableName + "' does not carry the requested attributes and typed children in order.");

            foreach (JObject moved in (plan["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = moved["attribute"]?.ToString();
                List<XElement> occurrences = FormTableAttributes(FormScope(table))
                    .Where(e => AttributeReferenceMatches(Attr(e, "attribute"), name)).ToList();
                if (occurrences.Count != 1 || !occurrences[0].Ancestors().Contains(table))
                    return NotConfirmed("Attribute '" + name + "' is on the form " + occurrences.Count
                        + " time(s) after the save instead of once inside table '" + tableName + "'.");
            }

            XElement plannedRest = StripForComparison(planned.Root, plannedTable);
            XElement persistedRest = StripForComparison(persisted.Root, table);
            if (!XNode.DeepEquals(plannedRest, persistedRest))
                return NotConfirmed("The persisted PatternInstance differs beyond the requested table; children, bindings, events or metadata of other elements changed.");
            var forbidden = new List<KeyValuePair<XElement, string>>();
            foreach (JObject moved in (plan["movedAttributes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                XElement source = ElementAtIndexPath(planned.Root, moved["sourceIndexPath"] as JArray);
                string identifier = moved["orderingIdentifier"]?.ToString();
                if (source != null && !string.IsNullOrEmpty(identifier))
                    forbidden.Add(new KeyValuePair<XElement, string>(source, identifier));
            }
            return VerifyOrderedLists(planned.Root, persisted.Root, plannedTable, table, requiredIdentifier: tableName, forbidden: forbidden);
        }

        private static XElement ElementAtIndexPath(XElement root, JArray path)
        {
            if (path == null) return null;
            XElement cursor = root;
            foreach (JToken step in path)
            {
                cursor = cursor.Elements().ElementAtOrDefault(step.ToObject<int>());
                if (cursor == null) return null;
            }
            return cursor;
        }

        private static JObject NotConfirmed(string message) => new JObject { ["confirmed"] = false, ["message"] = message };

        // The document with one element removed, every childrenOrderedList dropped (compared
        // separately), attributes sorted and formatting whitespace ignored.
        private static XElement StripForComparison(XElement root, XElement removed)
        {
            var clone = new XElement(root);
            if (removed != null)
            {
                List<int> path = IndexPath(removed);
                XElement cursor = clone;
                foreach (int step in path) cursor = cursor.Elements().ElementAt(step);
                cursor.Remove();
            }
            foreach (XElement element in clone.DescendantsAndSelf())
            {
                element.Attributes().Where(a => a.Name.LocalName.Equals("childrenOrderedList", StringComparison.OrdinalIgnoreCase)).ToList()
                    .ForEach(a => a.Remove());
                element.ReplaceAttributes(element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList());
                element.Nodes().OfType<XText>().Where(t => !(t is XCData) && string.IsNullOrWhiteSpace(t.Value)).ToList().ForEach(t => t.Remove());
            }
            return clone;
        }

        private static List<int> IndexPath(XElement element)
        {
            var path = new List<int>();
            for (XElement cursor = element; cursor.Parent != null; cursor = cursor.Parent)
                path.Insert(0, cursor.Parent.Elements().ToList().IndexOf(cursor));
            return path;
        }

        // Walks the planned and persisted trees in parallel (they are structurally equal once
        // the moved element is set aside) and checks every childrenOrderedList the plan wrote:
        // the persisted list must keep every planned entry, carry the required identifier on
        // the target container and no longer carry the moved identifier on the source.
        private static JObject VerifyOrderedLists(XElement plannedRoot, XElement persistedRoot, XElement plannedSkip, XElement persistedSkip,
            string requiredIdentifier, IList<KeyValuePair<XElement, string>> forbidden)
        {
            bool exact = true;
            var pairs = new List<KeyValuePair<XElement, XElement>>();
            if (!PairElements(plannedRoot, persistedRoot, plannedSkip, persistedSkip, pairs))
                return NotConfirmed("The persisted PatternInstance tree does not line up with the planned one.");
            foreach (var pair in pairs)
            {
                string plannedList = (string)pair.Key.Attribute("childrenOrderedList");
                if (plannedList == null) continue;
                string persistedList = (string)pair.Value.Attribute("childrenOrderedList");
                string label = ContainerPath(pair.Key);
                if (persistedList == null)
                    return NotConfirmed("The re-read container '" + label + "' lost its childrenOrderedList.");
                var persistedEntries = new HashSet<string>(PatternChildOrderEdit.Entries(persistedList), StringComparer.Ordinal);
                // The pattern may rewrite the new entry's level or type code on save; it must
                // still reference the identifier. Every other entry must survive verbatim.
                bool isTarget = ReferenceEquals(pair.Key, plannedSkip?.Parent) && requiredIdentifier != null;
                string lost = PatternChildOrderEdit.Entries(plannedList).FirstOrDefault(e => !persistedEntries.Contains(e)
                    && !(isTarget && PatternChildOrderEdit.Identifier(e).Equals(requiredIdentifier, StringComparison.OrdinalIgnoreCase)));
                if (lost != null)
                    return NotConfirmed("The re-read childrenOrderedList of '" + label + "' lost the entry '" + lost + "'.");
                if (isTarget && !PatternChildOrderEdit.References(persistedList, requiredIdentifier))
                    return NotConfirmed("The re-read childrenOrderedList of '" + label + "' does not reference '" + requiredIdentifier + "'; the IDE would hide it.");
                foreach (var rule in forbidden ?? new List<KeyValuePair<XElement, string>>())
                    if (ReferenceEquals(pair.Key, rule.Key) && PatternChildOrderEdit.References(persistedList, rule.Value))
                        return NotConfirmed("The re-read childrenOrderedList of '" + label + "' still references '" + rule.Value + "'.");
                if (!string.Equals(plannedList, persistedList, StringComparison.Ordinal)) exact = false;
            }
            return new JObject { ["confirmed"] = true, ["orderedListsExact"] = exact };
        }

        private static bool PairElements(XElement planned, XElement persisted, XElement plannedSkip, XElement persistedSkip,
            List<KeyValuePair<XElement, XElement>> pairs)
        {
            if (planned.Name != persisted.Name) return false;
            pairs.Add(new KeyValuePair<XElement, XElement>(planned, persisted));
            List<XElement> left = planned.Elements().Where(e => !ReferenceEquals(e, plannedSkip)).ToList();
            List<XElement> right = persisted.Elements().Where(e => !ReferenceEquals(e, persistedSkip)).ToList();
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!PairElements(left[i], right[i], plannedSkip, persistedSkip, pairs)) return false;
            return true;
        }

        internal static JObject VerifyAddedTableProjection(string webForm, JObject plan)
        {
            if (string.IsNullOrWhiteSpace(webForm)) return NotConfirmed("The projected WebForm could not be re-read.");
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch { return NotConfirmed("The projected WebForm is not valid XML."); }
            string tableName = plan["tableName"]?.ToString();
            bool targetPresent = FindWebFormNamedElement(document, tableName) != null;
            List<string> missing = (plan["children"] as JArray)?.OfType<JObject>()
                .Where(c => string.Equals(c["type"]?.ToString(), "textBlock", StringComparison.OrdinalIgnoreCase))
                .Select(c => c["controlName"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n) && FindWebFormNamedElement(document, n) == null).ToList() ?? new List<string>();
            bool confirmed = targetPresent && missing.Count == 0;
            return new JObject
            {
                ["confirmed"] = confirmed,
                ["targetPresent"] = targetPresent,
                ["missingControls"] = new JArray(missing),
                ["message"] = confirmed ? null : "The projected WebForm does not contain table '" + tableName + "'"
                    + (missing.Count == 0 ? "." : " with its text blocks (" + string.Join(", ", missing) + ").")
            };
        }

        // ---- move_control: request -----------------------------------------------------------

        internal sealed class MoveControlRequest
        {
            internal string ControlName;
            internal string Kind;
            internal string TableName;
        }

        internal static JObject ValidateMoveControlArgs(JObject args, out MoveControlRequest request)
        {
            request = new MoveControlRequest
            {
                ControlName = args?["controlName"]?.ToString()?.Trim(),
                Kind = args?["controlKind"]?.ToString()?.Trim(),
                TableName = args?["tableName"]?.ToString()?.Trim()
            };
            if (string.IsNullOrWhiteSpace(request.ControlName))
                return TableError("MissingControlName", "controlName is required for move_control: the textBlock controlName, variable name or attribute name to move.");
            if (string.IsNullOrWhiteSpace(request.TableName))
                return TableError("MissingTableName", "tableName is required for move_control: the destination table (name or path of named ancestors).");
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                string requestedKind = request.Kind;
                string kind = MovableControlKinds.FirstOrDefault(k => k.Equals(requestedKind, StringComparison.OrdinalIgnoreCase));
                if (kind == null) return TableError("InvalidControlKind", "controlKind must be textBlock, variable or attribute.");
                request.Kind = kind;
            }
            else request.Kind = null;
            return ValidatePlacementArgs(args);
        }

        private static bool ControlMatches(XElement element, MoveControlRequest request)
        {
            if (element.Parent == null || !Is(element.Parent, "table")) return false;
            string kind = MovableControlKinds.FirstOrDefault(k => Is(element, k));
            if (kind == null || (request.Kind != null && !kind.Equals(request.Kind, StringComparison.Ordinal))) return false;
            switch (kind)
            {
                case "textBlock": return string.Equals(Attr(element, "controlName"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                case "variable": return string.Equals(Attr(element, "name"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                default: return AttributeReferenceMatches(Attr(element, "attribute"), request.ControlName);
            }
        }

        private static string ControlKind(XElement element) => MovableControlKinds.FirstOrDefault(k => Is(element, k));

        private static string ControlIdentity(XElement element)
        {
            switch (ControlKind(element))
            {
                case "textBlock": return Attr(element, "controlName");
                case "variable": return Attr(element, "name");
                default: return Attr(element, "attribute");
            }
        }

        private static JObject DescribeControl(XElement element) => new JObject
        {
            ["type"] = ControlKind(element),
            ["identity"] = ControlIdentity(element),
            ["path"] = ContainerPath(element.Parent)
        };

        // ---- move_control: XML plan ----------------------------------------------------------

        internal static JObject PlanMoveControlXml(XDocument document, JObject args)
        {
            JObject invalid = ValidateMoveControlArgs(args, out MoveControlRequest request);
            if (invalid != null) return invalid;

            List<XElement> candidates = document.Descendants().Where(e => ControlMatches(e, request)).ToList();
            if (candidates.Count == 0)
            {
                JObject notFound = TableError("ControlNotFound",
                    "No textBlock, variable or attribute addressed by '" + request.ControlName + "' sits in a form table of the PatternInstance.");
                notFound["availableControls"] = new JArray(document.Descendants()
                    .Where(e => e.Parent != null && Is(e.Parent, "table") && ControlKind(e) != null)
                    .Take(50).Select(DescribeControl));
                return notFound;
            }
            if (candidates.Count > 1)
            {
                JObject ambiguous = TableError("ControlAmbiguous",
                    "'" + request.ControlName + "' addresses more than one control; pass controlKind (textBlock|variable|attribute).");
                ambiguous["matchingControls"] = new JArray(candidates.Select(DescribeControl));
                return ambiguous;
            }
            XElement element = candidates[0];
            XElement source = element.Parent;

            List<XElement> targets = FindFormContainers(document, request.TableName).ToList();
            if (targets.Count > 1)
            {
                JObject ambiguous = TableError("WwpTableAmbiguous",
                    "tableName '" + request.TableName + "' matched more than one container; qualify it with a path of named ancestors.");
                ambiguous["matchingContainers"] = new JArray(targets.Select(DescribeFormContainer));
                return ambiguous;
            }
            if (targets.Count == 0)
            {
                JObject notFound = TableError("WwpTableNotFound", "No table addressed by '" + request.TableName + "' was found in the PatternInstance.");
                notFound["availablePaths"] = new JArray(GetFormActionContainers(document).Where(e => Is(e, "table"))
                    .Select(ContainerPath).Where(p => p.Length > 0).Distinct());
                return notFound;
            }
            XElement target = targets[0];
            if (!Is(target, "table"))
                return TableError("MoveTargetNotTable", "'" + request.TableName + "' is a " + target.Name.LocalName + ", not a table; move_control only moves controls into tables.");
            if (ReferenceEquals(target, source) && !HasPlacement(args))
                return TableError("ControlAlreadyInTable", "'" + request.ControlName + "' is already in '" + ContainerPath(target) + "'; pass position, before or after to reorder it.");

            string snapshot = document.ToString(SaveOptions.DisableFormatting);
            string kind = ControlKind(element), identity = ControlIdentity(element);
            string fromPath = ContainerPath(source), toPath = ContainerPath(target);
            string sourceBefore = source.ToString(SaveOptions.DisableFormatting);
            string targetBefore = target.ToString(SaveOptions.DisableFormatting);
            string elementXml = element.ToString(SaveOptions.DisableFormatting);

            if (!PatternChildOrderEdit.TryRemove(source, element, out PatternChildOrderEdit.Change removal, out string reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(fromPath, reason);
            }
            element.Remove();
            JObject placementError = ResolveInsertIndex(target, args, out int index);
            if (placementError != null) { RestoreDocument(document, snapshot); return placementError; }
            InsertChildAt(target, element, index);
            if (!PatternChildOrderEdit.TryInsert(target, element, out PatternChildOrderEdit.Change insertion, out reason))
            {
                RestoreDocument(document, snapshot);
                return OrderedContainerUnsupported(toPath, reason);
            }
            if (ReferenceEquals(source, target))
            {
                // One container, one list: the reposition is reported as a single change.
                removal = new PatternChildOrderEdit.Change { Before = removal.Before, After = insertion.After, Entry = insertion.Entry, InsertedBefore = insertion.InsertedBefore };
                insertion = removal;
            }

            var control = new JObject
            {
                ["type"] = kind, ["identity"] = identity, ["from"] = fromPath, ["to"] = toPath, ["position"] = index,
                ["orderingIdentifier"] = PatternChildOrderReconciler.GetIdentifier(element, target),
                ["sourceIndexPath"] = new JArray(IndexPath(source))
            };
            return new JObject
            {
                ["changed"] = true,
                ["control"] = control,
                ["movedControls"] = new JArray(control.DeepClone()),
                ["typedDiff"] = new JObject
                {
                    ["operation"] = MoveControlOperation,
                    ["control"] = control.DeepClone(),
                    ["sourceBefore"] = sourceBefore,
                    ["sourceAfter"] = source.ToString(SaveOptions.DisableFormatting),
                    ["targetBefore"] = targetBefore,
                    ["targetAfter"] = target.ToString(SaveOptions.DisableFormatting),
                    ["element"] = elementXml
                },
                ["childrenOrderedList"] = new JObject
                {
                    ["source"] = OrderingJson(removal),
                    ["target"] = OrderingJson(insertion),
                    ["sameContainer"] = ReferenceEquals(source, target)
                }
            };
        }

        // ---- move_control: native ------------------------------------------------------------

        private static bool NativeControlMatches(object element, object parent, MoveControlRequest request)
        {
            if (parent == null || !IsNativeTable(parent)) return false;
            string kind = MovableControlKinds.FirstOrDefault(k => NativeIsKind(element, k));
            if (kind == null || (request.Kind != null && !kind.Equals(request.Kind, StringComparison.Ordinal))) return false;
            switch (kind)
            {
                case "textBlock": return string.Equals(NativeAttribute(element, "controlName"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                case "variable": return string.Equals(NativeAttribute(element, "name"), request.ControlName, StringComparison.OrdinalIgnoreCase);
                default: return AttributeReferenceMatches(NativeAttribute(element, "attribute"), request.ControlName);
            }
        }

        internal static JObject ApplyNativeMoveControl(object part, JObject args, JObject plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return TableError("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject invalid = ValidateMoveControlArgs(args, out MoveControlRequest request);
            if (invalid != null) return invalid;

            List<KeyValuePair<object, object>> candidates = WalkWithParent(root, null)
                .Where(pair => NativeControlMatches(pair.Key, pair.Value, request)).ToList();
            if (candidates.Count == 0) return TableError("ControlNotFound", "No native control addressed by '" + request.ControlName + "' sits in a form table.");
            if (candidates.Count > 1) return TableError("ControlAmbiguous", "'" + request.ControlName + "' addresses more than one native control; pass controlKind.");
            object element = candidates[0].Key, source = candidates[0].Value;

            JObject targetError = ResolveNativeContainer(root, request.TableName, out object target);
            if (targetError != null)
            {
                targetError["code"] = targetError["code"]?.ToString() == "FormActionContainerAmbiguous" ? "WwpTableAmbiguous" : "WwpTableNotFound";
                return targetError;
            }
            if (!IsNativeTable(target)) return TableError("MoveTargetNotTable", "'" + request.TableName + "' is not a native table.");
            if (ReferenceEquals(source, target) && !HasPlacement(args))
                return TableError("ControlAlreadyInTable", "'" + request.ControlName + "' is already in the destination table.");

            JObject lists = plan?["childrenOrderedList"] as JObject;
            var orderingEdits = new List<KeyValuePair<object, JObject>>();
            if (ReferenceEquals(source, target))
            {
                JObject error = CollectOrderingEdit(target, lists?["target"] as JObject, orderingEdits);
                if (error != null) return error;
            }
            else
            {
                JObject error = CollectOrderingEdit(source, lists?["source"] as JObject, orderingEdits)
                    ?? CollectOrderingEdit(target, lists?["target"] as JObject, orderingEdits);
                if (error != null) return error;
            }

            MethodInfo executeUpdate = part.GetType().GetMethod("ExecuteUpdate",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(Action) }, null);
            int? insertIndex = null;
            Action mutation = () =>
            {
                insertIndex = ResolveNativeInsertIndex(target, args, element);
                ExecuteElementCommand(source, element, "RemoveElementCommand", NativeChildren(source).IndexOf(element));
                if (insertIndex.HasValue) ExecuteElementCommand(target, element, "InsertElementCommand", insertIndex.Value);
                else ExecuteElementCommand(target, element, "AddElementCommand", null);
                foreach (var edit in orderingEdits) SetNativeAttribute(edit.Key, "childrenOrderedList", edit.Value["after"].ToString());
            };
            if (executeUpdate != null) executeUpdate.Invoke(part, new object[] { "genexus_wwp move_control", mutation });
            else mutation();

            return new JObject
            {
                ["changed"] = true,
                ["sdkOperation"] = "RemoveElementCommand + " + (insertIndex.HasValue ? "InsertElementCommand" : "AddElementCommand")
            };
        }

        // ---- move_control: verification ------------------------------------------------------

        internal static JObject VerifyMovedControl(XDocument planned, XDocument persisted, JObject plan)
        {
            JObject control = plan["control"] as JObject ?? new JObject();
            var request = new MoveControlRequest { ControlName = control["identity"]?.ToString(), Kind = control["type"]?.ToString() };
            if (request.Kind == "attribute")
            {
                int dash = request.ControlName?.LastIndexOf('-') ?? -1;
                if (dash >= 0) request.ControlName = request.ControlName.Substring(dash + 1);
            }
            string toPath = control["to"]?.ToString();
            int index = control["position"]?.ToObject<int?>() ?? -1;

            List<XElement> found = persisted.Descendants().Where(e => ControlMatches(e, request)).ToList();
            if (found.Count != 1)
                return NotConfirmed("The re-read PatternInstance contains the " + request.Kind + " '" + control["identity"] + "' " + found.Count + " time(s) instead of once.");
            XElement element = found[0];
            if (!string.Equals(ContainerPath(element.Parent), toPath, StringComparison.OrdinalIgnoreCase))
                return NotConfirmed("The re-read control is under '" + ContainerPath(element.Parent) + "' instead of '" + toPath + "'.");
            if (element.Parent.Elements().ToList().IndexOf(element) != index)
                return NotConfirmed("The re-read control is at child index " + element.Parent.Elements().ToList().IndexOf(element) + " instead of " + index + ".");
            XElement plannedElement = planned.Descendants().FirstOrDefault(e => ControlMatches(e, request));
            if (plannedElement == null) return NotConfirmed("The planned PatternInstance has no control '" + control["identity"] + "'.");
            if (!IsRequestedSubset(ProjectXmlControl(plannedElement), ProjectXmlControl(element)))
                return NotConfirmed("The re-read control lost attributes or children it had before the move.");

            XElement plannedRest = StripForComparison(planned.Root, plannedElement);
            XElement persistedRest = StripForComparison(persisted.Root, element);
            if (!XNode.DeepEquals(plannedRest, persistedRest))
                return NotConfirmed("The persisted PatternInstance differs beyond the moved control; other elements changed.");

            string orderingIdentifier = control["orderingIdentifier"]?.ToString();
            XElement plannedSource = ElementAtIndexPath(planned.Root, control["sourceIndexPath"] as JArray);
            var forbidden = new List<KeyValuePair<XElement, string>>();
            if (plannedSource != null && !ReferenceEquals(plannedSource, plannedElement.Parent) && !string.IsNullOrEmpty(orderingIdentifier))
                forbidden.Add(new KeyValuePair<XElement, string>(plannedSource, orderingIdentifier));
            return VerifyOrderedLists(planned.Root, persisted.Root, plannedElement, element,
                requiredIdentifier: orderingIdentifier, forbidden: forbidden);
        }

        internal static JObject VerifyMovedControlProjection(string webForm, JObject plan)
        {
            if (string.IsNullOrWhiteSpace(webForm)) return NotConfirmed("The projected WebForm could not be re-read.");
            XDocument document;
            try { document = XDocument.Parse(webForm, LoadOptions.PreserveWhitespace); }
            catch { return NotConfirmed("The projected WebForm is not valid XML."); }
            JObject control = plan["control"] as JObject ?? new JObject();
            string identity = control["identity"]?.ToString();
            if (control["type"]?.ToString() == "attribute" && identity != null && identity.LastIndexOf('-') >= 0)
                identity = identity.Substring(identity.LastIndexOf('-') + 1);
            bool present = FindWebFormNamedElement(document, identity) != null;
            // A text block keeps its controlName in the WebForm; a variable or attribute may
            // render under a customised control name, so its presence is reported, not required.
            bool required = control["type"]?.ToString() == "textBlock";
            return new JObject
            {
                ["confirmed"] = present || !required,
                ["targetPresent"] = present,
                ["message"] = present || !required ? null : "The projected WebForm does not contain the moved text block '" + identity + "'."
            };
        }
    }
}
