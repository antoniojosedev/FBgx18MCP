using System;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;
using Artech.Architecture.Common.Descriptors;
using Artech.Genexus.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ForgeService
    {
        private readonly KbService _kbService;

        public ForgeService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string Scaffold(string type, string name, JObject properties)
        {
            try
            {
                var kb = _kbService.GetKB();
                Logger.Info(string.Format("Scaffolding new {0}: {1}", type, name));
                Logger.Info("[DEBUG-SCAFFOLD] Step 1: Init");

                KBObject obj = null;
                if (type.Equals("Procedure", StringComparison.OrdinalIgnoreCase) || type.Equals("Prc", StringComparison.OrdinalIgnoreCase))
                {
                    obj = KBObject.Create(kb.DesignModel, KBObjectDescriptor.Get<Procedure>().Id);
                }
                else if (type.Equals("Transaction", StringComparison.OrdinalIgnoreCase) || type.Equals("Trn", StringComparison.OrdinalIgnoreCase))
                {
                    obj = KBObject.Create(kb.DesignModel, KBObjectDescriptor.Get<Transaction>().Id);
                }
                else
                {
                    return "{\"status\":\"Error\",\"error\":\"Scaffold for type '" + type + "' not implemented.\"}";
                }

                Logger.Info("[DEBUG-SCAFFOLD] Step 2: Instantiated");
                obj.Name = name;
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 3: Name set: " + name);
                obj.Description = (properties != null && properties["description"] != null) 
                    ? properties["description"].ToString() 
                    : ("Created by MCP: " + name);
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 4: Description set");
                // Add default logic based on properties
                if (obj is Procedure prc)
                {
                    var sourcePart = prc.Parts.Get<SourcePart>();
                    if (sourcePart != null)
                    {
                        string codeParams = (properties != null && properties["code"] != null) ? properties["code"].ToString() : "";
                        sourcePart.Source = "// Template created by MCP\r\n" + codeParams;
                    }
                }
                
                Logger.Info("[DEBUG-SCAFFOLD] Step 5: Before Save");
                obj.EnsureSave();
                Logger.Info($"Scaffold complete: {name}");
                return "{\"status\":\"Success\", \"name\":\"" + name + "\", \"guid\":\"" + obj.Guid + "\"}";
            }
            catch (Exception ex)
            {
                Logger.Error($"Scaffold Error: {ex.Message}");
                return "{\"status\":\"Error\",\"error\":\"" + CommandDispatcher.EscapeJsonString(ex.ToString()) + "\"}";
            }
        }
    }
}
