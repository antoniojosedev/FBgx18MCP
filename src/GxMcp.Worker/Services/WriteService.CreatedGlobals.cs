using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Parsers;

namespace GxMcp.Worker.Services
{
    // issue #592 — a Structure write that adds a new attribute first saves a
    // global Attribute object, then the Transaction. The post-save verification
    // and the automatic rollback only see the Transaction's DSL, so a verified
    // rollback used to leave that global in the Knowledge Base as an unreported
    // orphan. The write now carries the globals it created on its receipt, and
    // the rollback undoes them under the guards in RollbackGlobalCleanup.
    public partial class WriteService
    {
        /// <summary>Receipt field listing the globals a Structure write created.</summary>
        internal const string CreatedGlobalsField = "createdGlobals";

        internal static void AttachCreatedGlobals(JObject payload, IReadOnlyList<TransactionDslParser.CreatedGlobalIdentity> created)
        {
            if (payload == null || created == null || created.Count == 0) return;
            payload[CreatedGlobalsField] = new JArray(created.Where(c => c != null).Select(c => new JObject
            {
                ["name"] = c.Name,
                ["guid"] = c.Guid == Guid.Empty ? null : c.Guid.ToString()
            }));
        }

        /// <summary>
        /// The globals recorded on a write envelope, whether the field is still
        /// on the ok payload (<c>result</c>) or already at the top level.
        /// </summary>
        internal static IReadOnlyList<TransactionDslParser.CreatedGlobalIdentity> ReadCreatedGlobals(JObject response)
        {
            var list = new List<TransactionDslParser.CreatedGlobalIdentity>();
            var items = response?[CreatedGlobalsField] as JArray ?? response?["result"]?[CreatedGlobalsField] as JArray;
            if (items == null) return list;
            foreach (var item in items.OfType<JObject>())
            {
                string name = item["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                Guid guid;
                if (!Guid.TryParse(item["guid"]?.ToString(), out guid)) guid = Guid.Empty;
                list.Add(new TransactionDslParser.CreatedGlobalIdentity(name, guid));
            }
            return list;
        }

        /// <summary>
        /// Undoes the globals the reverted write created and records the outcome
        /// under <c>rollback.createdGlobals</c>. Runs only against a rollback the
        /// receipt already reports as <c>rolledBack</c> and <c>verified</c>; those
        /// two fields keep describing the Structure restore itself. Pure given the
        /// store, so the guards are asserted without a Knowledge Base.
        /// </summary>
        internal static void ApplyCreatedGlobalRollback(JObject response, ICreatedGlobalStore store)
        {
            if (response == null) return;
            var created = ReadCreatedGlobals(response);
            if (created.Count == 0) return;
            var rollback = response["rollback"] as JObject;
            if (rollback == null) return;
            bool verified = rollback["rolledBack"]?.Value<bool?>() == true
                && rollback["verified"]?.Value<bool?>() == true;
            rollback[CreatedGlobalsField] = RollbackGlobalCleanup.Run(created, store, verified);
        }

        private void FinishCreatedGlobalRollback(JObject response, string target, string typeFilter)
        {
            IReadOnlyList<TransactionDslParser.CreatedGlobalIdentity> created;
            try { created = ReadCreatedGlobals(response); }
            catch (Exception ex)
            {
                Logger.Warn("[ROLLBACK-GLOBALS] the created-globals ledger of " + target + " could not be read; no cleanup was attempted: " + ex.Message);
                return;
            }
            if (created.Count == 0) return;
            try
            {
                ApplyCreatedGlobalRollback(response, new SdkCreatedGlobalStore(_objectService, target, typeFilter));
            }
            catch (Exception ex)
            {
                // The cleanup must never mask the rollback receipt. Record every
                // global as kept with the failure instead of dropping the ledger.
                Logger.Warn("[ROLLBACK-GLOBALS] cleanup failed for " + target + ": " + ex.Message);
                var rollback = response["rollback"] as JObject;
                if (rollback != null && rollback[CreatedGlobalsField] == null)
                    rollback[CreatedGlobalsField] = RollbackGlobalCleanup.Run(created, null, false);
            }
        }

        /// <summary>
        /// The live-SDK store: object resolution by GUID, the incoming-reference
        /// walk the delete tool uses, and the Worker's own object-delete path.
        /// </summary>
        private sealed class SdkCreatedGlobalStore : ICreatedGlobalStore
        {
            private readonly ObjectService _objects;
            private readonly string _target;
            private readonly string _typeFilter;

            internal SdkCreatedGlobalStore(ObjectService objects, string target, string typeFilter)
            {
                _objects = objects;
                _target = target;
                _typeFilter = typeFilter;
            }

            public Guid? TargetGuid
            {
                get
                {
                    try { return _objects?.FindObject(_target, _typeFilter)?.Guid; }
                    catch (Exception ex)
                    {
                        Logger.Warn("[ROLLBACK-GLOBALS] target " + _target + " could not be resolved: " + ex.Message);
                        return null;
                    }
                }
            }

            private global::Artech.Architecture.Common.Objects.KnowledgeBase Kb
            {
                get
                {
                    var kb = _objects?.GetKbService()?.GetKB() as global::Artech.Architecture.Common.Objects.KnowledgeBase;
                    if (kb == null) throw new InvalidOperationException("No Knowledge Base is open.");
                    return kb;
                }
            }

            private global::Artech.Architecture.Common.Objects.KBObject ResolveObject(Guid guid)
            {
                return Kb.DesignModel.Objects.Get(guid);
            }

            public ResolvedGlobal Resolve(Guid guid)
            {
                var obj = ResolveObject(guid);
                if (obj == null) return null;
                return new ResolvedGlobal
                {
                    Guid = obj.Guid,
                    Name = obj.Name,
                    TypeName = obj.TypeDescriptor?.Name
                };
            }

            public ReferenceProbe FindReferences(Guid guid)
            {
                var obj = ResolveObject(guid);
                if (obj == null) throw new InvalidOperationException("The global Attribute no longer resolves in the model.");
                bool truncated;
                JArray references = ObjectService.CollectIncomingReferences(obj, Kb, out truncated);
                var probe = new ReferenceProbe { Truncated = truncated };
                foreach (var item in references.OfType<JObject>())
                {
                    Guid from;
                    if (!Guid.TryParse(item["guid"]?.ToString(), out from)) from = Guid.Empty;
                    probe.References.Add(new ReferencingObject
                    {
                        Name = item["name"]?.ToString() ?? item["nativeKey"]?.ToString(),
                        TypeName = item["type"]?.ToString(),
                        Guid = from
                    });
                }
                return probe;
            }

            public JObject Delete(ResolvedGlobal global)
            {
                // DeleteObject resolves its target by name. Refuse when that name no
                // longer maps to the GUID this write created, so a homonym created
                // meanwhile is never the one deleted.
                var byName = _objects.FindObject(global.Name, "Attribute");
                if (byName == null || byName.Guid != global.Guid)
                {
                    return JObject.Parse(Models.McpResponse.Err(
                        code: "IdentityMismatch",
                        message: "The attribute name no longer resolves to the global this write created.",
                        target: global.Name));
                }
                return JObject.Parse(_objects.DeleteObject(
                    global.Name, "Attribute", confirm: true, dryRun: false,
                    expectedVersion: ComputeVersionToken(byName)));
            }
        }
    }
}
