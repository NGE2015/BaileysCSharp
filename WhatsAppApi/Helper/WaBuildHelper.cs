using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Linq;
using System.Text.RegularExpressions;

namespace WhatsAppApi.Helper      // ← match your folder/namespace
{
    // JSON returned by https://web.whatsapp.com/check-update
    public sealed class WaBuildResponse
    {
        [JsonPropertyName("currentVersion")]
        public string CurrentVersion { get; set; } = default!;
    }
    internal sealed class WppConnectFeed
    {
        [JsonPropertyName("current_version")]
        public string CurrentVersion { get; set; } = default!;
    }
    /// <summary>
    /// Ask WhatsApp Web for the newest build (e.g. "2.3000.1023373029")
    /// and convert it to the uint[] format SocketConfig expects.
    /// </summary>
    public static class WaBuildHelper
        {
            // last known good build (updated 2026-01-08)
            // Current version: 2.3000.1031772734-alpha
            private static readonly uint[] Fallback = { 2, 3000, 1031772734 };

        public static async Task<uint[]> GetLatestAlphaAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

            try
            {
                var html = await http.GetStringAsync("https://wppconnect.io/whatsapp-versions/");
                // Simple and robust regex: match version numbers directly (e.g., "2.3000.1031772734-alpha")
                // This pattern looks for the version between > and < tags, which is reliable across HTML changes
                var match = Regex.Match(html,
                    @">([0-9]+\.[0-9]+\.[0-9]+-alpha)<",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                    return match.Groups[1].Value
                                .Split('.')
                                .Select(uint.Parse)
                                .ToArray();
            }
            catch
            {
                // network error, HTML format changed, etc.
            }

            return Fallback;
        }
    }
}
