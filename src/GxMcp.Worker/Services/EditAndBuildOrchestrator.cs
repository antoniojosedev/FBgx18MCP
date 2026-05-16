using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public interface IWriteServiceFacade
    {
        string WriteObject(string target, JObject args);
    }

    public interface IAnalyzeServiceFacade
    {
        string ImpactAnalysis(string target, bool waitForIndex, int waitTimeoutMs);
    }

    public interface IBuildServiceFacade
    {
        string Build(string action, string target, string includeCallees, int buildPlanCap);
    }

    public class EditAndBuildOrchestrator
    {
        private readonly IWriteServiceFacade _write;
        private readonly IAnalyzeServiceFacade _analyze;
        private readonly IBuildServiceFacade _build;

        public EditAndBuildOrchestrator(IWriteServiceFacade write, IAnalyzeServiceFacade analyze, IBuildServiceFacade build)
        {
            _write = write;
            _analyze = analyze;
            _build = build;
        }

        public string Orchestrate(JObject args)
        {
            string target = args?["name"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(target))
            {
                return JsonConvert.SerializeObject(new
                {
                    status = "Error",
                    error = "name is required"
                });
            }

            string includeCallees = args?["buildIncludeCallees"]?.ToString() ?? "direct";
            int buildPlanCap = args?["buildPlanCap"]?.ToObject<int?>() ?? 200;
            bool waitForIndex = args?["waitForIndex"]?.ToObject<bool?>() ?? true;
            int waitTimeoutMs = args?["waitTimeoutMs"]?.ToObject<int?>() ?? 30000;

            string editRaw = _write.WriteObject(target, args);
            var edit = JObject.Parse(editRaw);
            if (!string.Equals(edit["status"]?.ToString(), "Ok", StringComparison.OrdinalIgnoreCase))
            {
                edit["phase"] = "edit";
                return edit.ToString();
            }

            string impactRaw = _analyze.ImpactAnalysis(target, waitForIndex, waitTimeoutMs);
            var impact = JObject.Parse(impactRaw);
            var callers = impact["callers"] as JArray ?? new JArray();

            JObject buildResult;
            if (callers.Count == 0)
            {
                buildResult = new JObject
                {
                    ["status"] = "Skipped",
                    ["reason"] = "No callers to rebuild."
                };
            }
            else
            {
                string targetList = string.Join(",", callers);
                string buildRaw = _build.Build("Build", targetList, includeCallees, buildPlanCap);
                buildResult = JObject.Parse(buildRaw);
            }

            return new JObject
            {
                ["status"] = "Ok",
                ["target"] = target,
                ["edit"] = edit,
                ["impact"] = impact,
                ["build"] = buildResult
            }.ToString();
        }
    }
}
