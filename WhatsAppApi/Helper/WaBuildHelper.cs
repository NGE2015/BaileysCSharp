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
            // last known good build (updated 2025-05-31)
            private static readonly uint[] Fallback = { 2, 3000, 1023373029 };

        public static async Task<uint[]> GetLatestAlphaAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

            try
            {
                var html = await http.GetStringAsync("https://wppconnect.io/whatsapp-versions/");
                // grabs "2.3000.1023373029-alpha"
                var match = Regex.Match(html,
                    @"Current Version\s*</[^>]+>\s*([0-9.]+)-alpha",
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
