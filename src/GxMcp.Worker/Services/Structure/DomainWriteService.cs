using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Artech.Genexus.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services.Structure
{
    // issue #39 follow-up: edit the enum values (and optionally base type) of an EXISTING Domain.
    // Domain creation already accepts enumValues; this closes the edit-after gap. ApplyEnumValues
    // replaces the whole enum set, so callers pass the full desired list.
    public class DomainWriteService
    {
        private readonly ObjectService _objectService;

        public DomainWriteService(ObjectService objectService)
        {
            _objectService = objectService;
        }

        // payload = { enumValues:[{name,value,description?}], dataType?, length?, decimals?, signed? }
        public string SetDomainProperties(string domainName, string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domainName)) return Models.McpResponse.Err(
                    code: "InvalidDomain", message: "Domain name (name) is required.", target: domainName);
                if (string.IsNullOrWhiteSpace(payload)) return Models.McpResponse.Err(
                    code: "InvalidPayload", message: "payload is required.",
                    hint: "e.g. { \"enumValues\": [{\"name\":\"Active\",\"value\":\"A\"}] }.",
                    target: domainName);

                // GetKB() is dynamic; type the model so EnsureSave binds statically.
                Artech.Architecture.Common.Objects.KBModel model =
                    _objectService.GetKbService().GetKB().DesignModel;
                var domain = _objectService.FindObject(domainName) as Domain;
                if (domain == null) return Models.McpResponse.Err(
                    code: "DomainNotFound",
                    message: $"Domain '{domainName}' not found.",
                    hint: "Create it first with genexus_create type=Domain.",
                    target: domainName);

                var json = JObject.Parse(payload);
                var applied = new JArray();

                using (var sdkTrans = model.KB.BeginTransaction())
                {
                    try
                    {
                        string dataType = json["dataType"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(dataType))
                        {
                            int? len = json["length"]?.ToObject<int?>();
                            int? dec = json["decimals"]?.ToObject<int?>();
                            bool? signed = json["signed"]?.ToObject<bool?>();
                            if (DomainPropertyApplier.ApplyPrimitive(domain, dataType, len, dec, signed))
                                applied.Add("dataType");
                            else { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "DomainTypeFailed",
                                message: $"Could not apply dataType '{dataType}'.",
                                hint: "Use a canonical type: Character, VarChar, Numeric, Date, DateTime, Boolean, etc.",
                                target: domainName); }
                        }

                        var enumArr = json["enumValues"] as JArray;
                        if (enumArr != null)
                        {
                            bool isStringFamily = IsStringDomain(domain);
                            var specs = new List<DomainEnumValueSpec>();
                            foreach (var item in enumArr)
                            {
                                if (!(item is JObject jo)) continue;
                                string name = jo["name"]?.ToString();
                                if (string.IsNullOrEmpty(name)) continue;
                                string val = jo["value"]?.ToString();
                                specs.Add(new DomainEnumValueSpec
                                {
                                    Name = name,
                                    Value = isStringFamily ? QuoteCharEnumValue(val) : val,
                                    Description = jo["description"]?.ToString()
                                });
                            }
                            int n = DomainPropertyApplier.ApplyEnumValues(domain, specs);
                            if (n < 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                                code: "EnumWriteFailed",
                                message: "Could not write EnumValues — SDK helper not resolvable.",
                                target: domainName); }
                            applied.Add("enumValues");
                        }

                        if (applied.Count == 0) { try { sdkTrans.Rollback(); } catch { } return Models.McpResponse.Err(
                            code: "NoPropertiesToApply",
                            message: "payload contained no recognized domain properties.",
                            hint: "Recognized: enumValues, dataType (+length/decimals/signed).",
                            target: domainName); }

                        domain.EnsureSave();
                        sdkTrans.Commit();
                        return Models.McpResponse.Ok(
                            target: domainName,
                            code: "DomainUpdated",
                            result: new JObject { ["domain"] = domain.Name, ["applied"] = applied });
                    }
                    catch (Exception ex)
                    {
                        try { sdkTrans.Rollback(); } catch { }
                        return Models.McpResponse.Err(
                            code: "DomainUpdateFailed",
                            message: ex.Message,
                            hint: "Check the worker log for the SDK stack trace.",
                            target: domainName,
                            extra: new JObject { ["stackTrace"] = ex.StackTrace });
                    }
                }
            }
            catch (Exception ex)
            {
                return Models.McpResponse.Err(
                    code: "DomainUpdateFailed",
                    message: ex.Message,
                    hint: "Ensure the domain exists and payload is valid JSON.",
                    target: domainName);
            }
        }

        // Character-family domains store enum values as quoted literals ("A"); numeric/date stay bare.
        private static bool IsStringDomain(Domain domain)
        {
            try
            {
                string t = domain.Type.ToString();
                return t.IndexOf("Char", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("VarChar", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static string QuoteCharEnumValue(string v)
        {
            if (v == null) return null;
            v = v.Trim();
            if (v.Length >= 2 && v.StartsWith("\"") && v.EndsWith("\"")) return v; // already quoted
            return "\"" + v.Trim('"') + "\"";
        }
    }
}
