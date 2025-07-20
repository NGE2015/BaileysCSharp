using Microsoft.Extensions.Options;
using WhatsAppApi.Configuration;

namespace WhatsAppApi.Services
{
    public interface ILoggingService
    {
        void LogConnection(string message, params object[] args);
        void LogMessage(string message, params object[] args);
        void LogQRGeneration(string message, params object[] args);
        void LogSessionEvent(string message, params object[] args);
        void LogError(Exception ex, string message, params object[] args);
        void LogDebug(string message, params object[] args);
        void LogInformation(string message, params object[] args);
        void LogWarning(string message, params object[] args);
    }

    public class LoggingService : ILoggingService
    {
        private readonly ILogger<LoggingService> _logger;
        private readonly DetailedLoggingOptions _options;

        public LoggingService(ILogger<LoggingService> logger, IOptionsMonitor<DetailedLoggingOptions> options)
        {
            _logger = logger;
            _options = options.CurrentValue;
        }

        public void LogConnection(string message, params object[] args)
        {
            if (_options.Enabled && _options.LogConnections)
            {
                _logger.LogInformation($"[CONNECTION] {message}", args);
            }
        }

        public void LogMessage(string message, params object[] args)
        {
            if (_options.Enabled && _options.LogMessages)
            {
                _logger.LogInformation($"[MESSAGE] {message}", args);
            }
        }

        public void LogQRGeneration(string message, params object[] args)
        {
            if (_options.Enabled && _options.LogQRGeneration)
            {
                _logger.LogInformation($"[QR_CODE] {message}", args);
            }
        }

        public void LogSessionEvent(string message, params object[] args)
        {
            if (_options.Enabled && _options.LogSessionEvents)
            {
                _logger.LogInformation($"[SESSION] {message}", args);
            }
        }

        public void LogError(Exception ex, string message, params object[] args)
        {
            if (_options.Enabled && _options.LogErrorDetails)
            {
                _logger.LogError(ex, $"[ERROR] {message}", args);
            }
            else
            {
                _logger.LogError($"[ERROR] {message}", args);
            }
        }

        public void LogDebug(string message, params object[] args)
        {
            if (_options.Enabled)
            {
                _logger.LogDebug($"[DEBUG] {message}", args);
            }
        }

        public void LogInformation(string message, params object[] args)
        {
            if (_options.Enabled)
            {
                _logger.LogInformation($"[INFO] {message}", args);
            }
        }

        public void LogWarning(string message, params object[] args)
        {
            if (_options.Enabled)
            {
                _logger.LogWarning($"[WARNING] {message}", args);
            }
        }
    }
}