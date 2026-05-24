using System;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Objects;
using Newtonsoft.Json.Linq;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class VersionControlService
    {
        private readonly KbService _kbService;

        public VersionControlService(KbService kbService)
        {
            _kbService = kbService;
        }

        public string GetPendingChanges()
        {
            try
            {
                dynamic kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\",\"error\": \"No KB open\"}";

                var result = new JObject();
                
                // Use dynamic to access VersionControl property safely at runtime
                bool hasVC = false;
                try { hasVC = (kb.VersionControl != null); } catch { }
                
                result["connected"] = hasVC;
                
                if (hasVC)
                {
                    result["serverUrl"] = kb.VersionControl.ServerUrl;
                    
                    var pending = new JArray();
                    foreach (dynamic change in kb.VersionControl.GetPendingChanges())
                    {
                        pending.Add(new JObject {
                            ["name"] = change.Name,
                            ["type"] = change.TypeDescriptor.Name,
                            ["action"] = change.Action.ToString()
                        });
                    }
                    result["pendingChanges"] = pending;
                }

                return result.ToString();
            }
            catch (Exception ex)
            {
                return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}";
            }
        }

        public string Update()
        {
            try
            {
                dynamic kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\",\"error\": \"No KB open\"}";

                kb.VersionControl.Update();
                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex) { return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }

        public string Commit(string message)
        {
            try
            {
                dynamic kb = _kbService.GetKB();
                if (kb == null) return "{\"status\":\"Error\",\"error\": \"No KB open\"}";

                kb.VersionControl.Commit(message);
                return "{\"status\": \"Success\"}";
            }
            catch (Exception ex) { return "{\"status\":\"Error\",\"error\": \"" + CommandDispatcher.EscapeJsonString(ex.Message) + "\"}"; }
        }
    }
}
