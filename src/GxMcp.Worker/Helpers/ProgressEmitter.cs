using System;
using Newtonsoft.Json;

namespace GxMcp.Worker.Helpers
{
    public static class ProgressEmitter
    {
        public static void Emit(int progress, int total, string message = null)
        {
            string token = ProgressContext.CurrentToken;
            if (string.IsNullOrWhiteSpace(token)) return;

            var envelope = new
            {
                jsonrpc = "2.0",
                method = "notifications/progress",
                @params = new
                {
                    progressToken = token,
                    progress = progress,
                    total = total,
                    message = message ?? string.Empty
                }
            };

            try
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(envelope));
                Console.Out.Flush();
            }
            catch
            {
                // stdout might be closed during shutdown — silently drop.
            }
        }

        public static void Emit(string token, int progress, int total, string message = null)
        {
            using (ProgressContext.Use(token))
            {
                Emit(progress, total, message);
            }
        }
    }
}
