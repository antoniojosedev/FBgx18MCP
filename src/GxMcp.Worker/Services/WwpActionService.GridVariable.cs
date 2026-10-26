using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // add_grid_variable (#638): preview a presentation-variable column keyed by name and typed from its declaration
    // or explicit arguments. Writes remain blocked until the requested column can be verified in the projected WebForm.
    public sealed partial class WwpActionService
    {
        private string RunAddGridVariableOperation(string target, KBObject requestedObject, KBObject instance,
            string xml, string versionToken, JObject args)
        {
            string expectedVersion = args?["baseVersion"]?.ToString() ?? args?["expectedVersion"]?.ToString() ?? args?["versionToken"]?.ToString();
            bool dryRun = args?["dryRun"]?.ToObject<bool?>() == true;
            bool rollbackOnFailure = args?["rollbackOnFailure"]?.ToObject<bool?>() ?? true;
            string variableName = (args?["variable"]?.ToString() ?? args?["variableName"]?.ToString())?.Trim().TrimStart('&');

            KBObject previewHost = WwpProjectionHelper.ResolveHostParent(instance, _objects, xml);
            DeclaredVariable previewDeclared = FindDeclaredVariable(new[] { previewHost, requestedObject, instance }, variableName);
            XDocument beforeDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            XDocument previewDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            JObject previewMutation = ApplyAddGridVariableXml(previewDocument, args, previewDeclared);
            if (previewMutation["error"] != null)
                return McpResponse.Err(code: previewMutation["code"]?.ToString() ?? "WwpGridVariableInvalid",
                    message: previewMutation["error"].ToString(), target: target, extra: previewMutation);
            if (!IsExpectedVersion(expectedVersion, versionToken))
                return BuildWwpStaleObject(target, expectedVersion, versionToken,
                    "The WorkWithPlus PatternInstance changed after the caller's read; no grid variable was added.");

            JObject diff = new JObject { ["before"] = Project(beforeDocument), ["after"] = Project(previewDocument), ["column"] = previewMutation };
            if (dryRun)
                return McpResponse.Ok(target: target, code: "DryRun", result: new JObject
                {
                    ["instance"] = instance.Name,
                    ["operation"] = "add_grid_variable",
                    ["diff"] = diff,
                    ["versionToken"] = versionToken,
                    ["saved"] = false,
                    ["persisted"] = false,
                    ["rollbackOnFailure"] = rollbackOnFailure,
                    ["declaredVariable"] = previewDeclared?.ToJson(),
                    ["mutationMode"] = "native-pattern-sdk",
                    ["sdkOperation"] = "PatternInstance.RootElement + CreateChildElement(gridVariable) + AddElementCommand/InsertElementCommand"
                });

            return McpResponse.Err(code: "WwpProjectionVerificationUnavailable",
                message: "add_grid_variable writes are disabled because the requested variable cannot yet be verified in the projected WebForm. Use dryRun to inspect the plan; no changes were made.",
                target: target, extra: new JObject { ["persisted"] = false, ["saved"] = false });
        }

        internal static DeclaredVariable FindDeclaredVariable(IEnumerable<KBObject> hosts, string name)
            => string.IsNullOrWhiteSpace(name) ? null
                : DeclaredVariables(hosts).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        // The native twin of the XML grid is the grid with the same ordinal among the grids of the instance in document
        // order; the name and the number of columns are compared before it is trusted.
        private static object ResolveNativeGrid(object root, XDocument document, XElement xmlGrid, out JObject error)
        {
            error = null;
            List<XElement> xmlGrids = document.Descendants().Where(e => Is(e, "grid")).ToList();
            List<object> nativeGrids = Walk(root).Where(IsNativeGrid).ToList();
            int ordinal = xmlGrids.IndexOf(xmlGrid);
            if (ordinal < 0 || nativeGrids.Count != xmlGrids.Count)
            {
                error = Error("WwpGridNotResolvable", "The grid could not be matched to a native PatternInstance element ("
                    + xmlGrids.Count + " in the XML, " + nativeGrids.Count + " native).");
                return null;
            }
            object grid = nativeGrids[ordinal];
            if (!string.Equals(NativeAttribute(grid, "name") ?? string.Empty, Attr(xmlGrid, "name"), StringComparison.OrdinalIgnoreCase)
                || NativeChildren(grid).Count(IsNativeGridColumn) != xmlGrid.Elements().Count(e => Is(e, "gridAttribute") || Is(e, "gridVariable")))
            {
                error = Error("WwpGridNotResolvable", "The native grid does not match the XML grid it was resolved from.");
                return null;
            }
            return grid;
        }

        private static bool IsNativeGridColumn(object element)
        {
            if (element == null) return false;
            // The element types are WPGridVariable, TrnGridAttribute, ...: a column, not a gridVariableBasedOn placeholder.
            string type = NativeType(element);
            return type.EndsWith("GridVariable", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("GridAttribute", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NativeColumnIdentifies(object column, string identity)
            => string.Equals(NativeAttribute(column, "name"), identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NativeAttribute(column, "attribute"), identity, StringComparison.OrdinalIgnoreCase);

        // The value a native column attribute takes: booleans and sizes are typed (a text value fails when the instance
        // is loaded), a domain is the KB object itself, everything else is text.
        private static object ToNativeColumnValue(XAttribute attribute, KBObject basedOn)
        {
            string name = attribute.Name.LocalName;
            if (name == "readOnly") return attribute.Value == "True";
            if (name.StartsWith("basic", StringComparison.Ordinal) && name != "basicType")
                return int.Parse(attribute.Value, CultureInfo.InvariantCulture);
            if (name == "domain" && basedOn != null) return basedOn;
            return attribute.Value;
        }

        internal static JObject ApplyNativeAddGridVariable(object part, XDocument before, JObject args, GridVariablePlan plan)
        {
            object root = GetProperty(part, "RootElement");
            if (root == null) return Error("WwpNativeRootUnavailable", "PatternInstance RootElement is unavailable.");
            JObject gridError = FindRequestedGrid(before, args?["gridPath"]?.ToString(), out XElement xmlGrid);
            if (gridError != null) return gridError;
            object grid = ResolveNativeGrid(root, before, xmlGrid, out JObject resolveError);
            if (resolveError != null) return resolveError;
            if (NativeChildren(grid).Any(c => IsNativeGridColumn(c) && NativeColumnIdentifies(c, plan.Name)))
                return Error("GridVariableAlreadyExists", "Grid variable '" + plan.Name + "' already exists.");

            int? index = null;
            string beforeIdentity = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(beforeIdentity))
            {
                List<object> children = NativeChildren(grid);
                object anchor = children.FirstOrDefault(c => IsNativeGridColumn(c) && NativeColumnIdentifies(c, beforeIdentity));
                if (anchor == null) return Error("GridColumnBeforeNotFound", "Before-column '" + beforeIdentity + "' was not found.");
                index = children.IndexOf(anchor);
            }

            Action mutation = () =>
            {
                object variable = CreateNativeChild(grid, "gridVariable");
                foreach (XAttribute attribute in plan.Column.Attributes())
                {
                    if (!PatternSemanticAttributeWriter.ApplySemanticAttributeObject(variable, attribute.Name.LocalName,
                            ToNativeColumnValue(attribute, plan.BasedOnObject)))
                        throw new WwpTabException("WwpAttributeRejected", "WorkWithPlus rejected variable property '" + attribute.Name.LocalName + "'.");
                }
                // AddElementCommand appends; a position is InsertElementCommand's (as in add_table and the form actions).
                ExecuteElementCommand(grid, variable, index.HasValue ? "InsertElementCommand" : "AddElementCommand", index);
            };
            MethodInfo update = part.GetType().GetMethod("ExecuteUpdate", new[] { typeof(string), typeof(Action) });
            if (update != null) update.Invoke(part, new object[] { "genexus_wwp add_grid_variable", mutation });
            else mutation();
            return new JObject { ["changed"] = true };
        }

        /// <summary>
        /// The re-read grid holds the column with every requested attribute, where it was asked to be, and every other child
        /// of the grid exactly as it was.
        /// </summary>
        internal static JObject VerifyAddGridVariable(XDocument before, XDocument persisted, JObject args, GridVariablePlan plan)
        {
            string gridPath = args?["gridPath"]?.ToString();
            if (FindRequestedGrid(before, gridPath, out XElement beforeGrid) != null
                || FindRequestedGrid(persisted, gridPath, out XElement afterGrid) != null)
                return Error("WwpGridVariableNotPersisted", "The re-read PatternInstance no longer has the requested grid.");

            List<XElement> afterChildren = afterGrid.Elements().ToList();
            List<XElement> added = afterChildren.Where(e => Is(e, "gridVariable") && Attr(e, "name").Equals(plan.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (added.Count != 1)
                return Error("WwpGridVariableNotPersisted", "The re-read grid holds " + added.Count + " columns named '" + plan.Name + "' instead of one.");
            XElement column = added[0];
            foreach (XAttribute requested in plan.Column.Attributes())
            {
                string persistedValue = Attr(column, requested.Name.LocalName);
                // The engine does not store a value that is the pattern's default (Numeric for basicType, Based on for dataType).
                if (persistedValue.Length == 0
                    && ((requested.Name.LocalName == "basicType" && IsDefaultBasicType(requested.Value))
                        || (requested.Name.LocalName == "dataType" && requested.Value == "Based on")))
                    continue;
                bool same = requested.Name.LocalName == "domain"
                    ? string.Equals(persistedValue, requested.Value, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(persistedValue, requested.Value, StringComparison.Ordinal);
                if (!same)
                    return Error("WwpGridVariableNotPersisted", "The re-read column lost or changed '" + requested.Name.LocalName
                        + "' (requested '" + requested.Value + "', persisted '" + persistedValue + "').");
            }

            List<XElement> beforeChildren = beforeGrid.Elements().ToList();
            int expectedIndex = beforeChildren.Count;
            string beforeIdentity = args?["before"]?.ToString();
            if (!string.IsNullOrWhiteSpace(beforeIdentity))
            {
                XElement anchor = FindGridColumn(beforeGrid, beforeIdentity);
                expectedIndex = anchor == null ? expectedIndex : beforeChildren.IndexOf(anchor);
            }
            if (afterChildren.IndexOf(column) != expectedIndex)
                return Error("WwpGridVariableNotPersisted", "The re-read column is at position " + afterChildren.IndexOf(column)
                    + " instead of " + expectedIndex + ".");

            List<XElement> others = afterChildren.Where(e => !ReferenceEquals(e, column)).ToList();
            if (others.Count != beforeChildren.Count || others.Where((e, i) => !XNode.DeepEquals(e, beforeChildren[i])).Any())
                return Error("WwpGridVariableNotPersisted", "The re-read grid changed or reordered a column that was not part of the request.");
            return new JObject { ["confirmed"] = true };
        }
    }
}
