namespace WhatsAppApi.Configuration
{
    public class DetailedLoggingOptions
    {
        public const string SectionName = "DetailedLogging";
        
        public bool Enabled { get; set; } = true;
        public bool LogConnections { get; set; } = true;
        public bool LogMessages { get; set; } = true;
        public bool LogQRGeneration { get; set; } = true;
        public bool LogRateLimit { get; set; } = true;
        public bool LogSessionEvents { get; set; } = true;
        public bool LogErrorDetails { get; set; } = true;
    }
}