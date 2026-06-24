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
            // last known good build (updated 2026-06-24)
            // Current version: 2.3000.1042026337-alpha
            private static readonly uint[] Fallback = { 2, 3000, 1042026337 };

            public static uint[] LastResolvedVersion { get; private set; } = Fallback;

        public static async Task<uint[]> GetLatestAlphaAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            try
            {
                var html = await http.GetStringAsync("https://wppconnect.io/whatsapp-versions/");
                // Capture only the numeric part before "-alpha" so uint.Parse succeeds.
                // Previous bug: the -alpha suffix was included in group 1, causing FormatException.
                var match = Regex.Match(html,
                    @">([0-9]+\.[0-9]+\.[0-9]+)-alpha<",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    var parts = match.Groups[1].Value.Split('.');
                    if (parts.Length == 3 &&
                        uint.TryParse(parts[0], out var major) &&
                        uint.TryParse(parts[1], out var minor) &&
                        uint.TryParse(parts[2], out var patch))
                    {
                        LastResolvedVersion = new[] { major, minor, patch };
                        return LastResolvedVersion;
                    }
                }
            }
            catch
            {
                // network error, HTML format changed, etc.
            }

            LastResolvedVersion = Fallback;
            return Fallback;
        }
    }
}
