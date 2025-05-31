using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaileysCSharp.Core.Helper
{
    // POCO that matches the JSON WhatsApp returns
    public sealed class WaBuildResponse
    {
        [JsonPropertyName("currentVersion")]
        public string CurrentVersion { get; set; } = default!;
    }

    public static class WaBuildHelper
    {
        /// <summary>
        /// Downloads the most recent Web build (e.g. "2.3000.1023373029")
        /// and converts it to the uint[] Baileys expects.
        /// </summary>
        public static async Task<uint[]> GetLatestWaWebBuildAsync()
        {
            using var http = new HttpClient();
            var json = await http.GetStringAsync(
                "https://web.whatsapp.com/check-update?version=2&platform=web");

            var latest = JsonSerializer.Deserialize<WaBuildResponse>(json)!;

            return latest.CurrentVersion
                         .Split('.')
                         .Select(uint.Parse)
                         .ToArray();
        }
    }
}
