namespace WhatsAppApi.Configuration
{
    public class RateLimitingOptions
    {
        public const string SectionName = "RateLimiting";
        
        public int MaxRequestsPerMinute { get; set; } = 30;
        public int MaxRequestsPerHour { get; set; } = 300;
        public int MaxQRRequestsPerMinute { get; set; } = 5;
        public int MaxSessionOperationsPerMinute { get; set; } = 1000; // High limit for development testing
        public int MaxConnectionStatusPerMinute { get; set; } = 20; // New limit for connection status checks
    }
}