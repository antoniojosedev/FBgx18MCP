using Newtonsoft.Json.Linq;
namespace GxMcp.Gateway.Routers
{
    public class SearchRouter : IMcpModuleRouter
    {
        public string ModuleName => "Search";

        public object? ConvertToolCall(string toolName, JObject? args)
        {
            switch (toolName)
            {
                case "genexus_query":
                case "genexus_search":
                    string q = args?["query"]?.ToString() ?? args?["filter"]?.ToString() ?? "";
                    return new
                    {
                        module = "Search",
                        action = "Query",
                        target = q,
                        limit = args?["limit"]?.ToObject<int?>() ?? 50,
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        domainFilter = args?["domainFilter"]?.ToString(),
                        exactMatch = args?["exactMatch"]?.ToObject<bool?>() ?? false,
                        inline_read_top = args?["inline_read_top"]?.ToObject<int?>() ?? 0,
                    };
                case "genexus_search_source":
                    return new
                    {
                        module = "Search",
                        action = "SearchSource",
                        target = "",
                        callee = args?["callee"]?.ToString(),
                        pattern = args?["pattern"]?.ToString(),
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        caseSensitive = args?["caseSensitive"]?.ToObject<bool?>() ?? false,
                        includeComments = args?["includeComments"]?.ToObject<bool?>() ?? false,
                        maxResults = args?["maxResults"]?.ToObject<int?>() ?? 50,
                        scope = args?["scope"],
                        argMatches = args?["argMatches"]
                    };
                case "genexus_list_objects":
                    return new
                    {
                        module = "List",
                        action = "Objects",
                        target = args?["filter"]?.ToString() ?? "",
                        limit = args?["limit"]?.ToObject<int?>() ?? 100,
                        offset = args?["offset"]?.ToObject<int?>() ?? 0,
                        parent = args?["parent"]?.ToString(),
                        parentPath = args?["parentPath"]?.ToString(),
                        typeFilter = args?["typeFilter"]?.ToString() ?? args?["type"]?.ToString(),
                        verbose = args?["verbose"]?.ToObject<bool?>() ?? false,
                        inline_read_top = args?["inline_read_top"]?.ToObject<int?>() ?? 0,
                    };
                default:
                    return null;
            }
        }
    }
}
