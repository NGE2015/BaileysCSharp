using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using WhatsAppApi.Configuration;

namespace WhatsAppApi.Middleware
{
    public class RateLimitingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RateLimitingMiddleware> _logger;
        private readonly ConcurrentDictionary<string, ClientRateLimit> _clientLimits = new();
        private readonly Timer _cleanupTimer;
        private readonly DetailedLoggingOptions _loggingOptions;
        private readonly RateLimitingOptions _rateLimitOptions;

        public RateLimitingMiddleware(
            RequestDelegate next, 
            ILogger<RateLimitingMiddleware> logger, 
            IOptionsMonitor<DetailedLoggingOptions> loggingOptions,
            IOptionsMonitor<RateLimitingOptions> rateLimitOptions)
        {
            _next = next;
            _logger = logger;
            _loggingOptions = loggingOptions.CurrentValue;
            _rateLimitOptions = rateLimitOptions.CurrentValue;
            
            // Cleanup expired entries every 5 minutes
            _cleanupTimer = new Timer(CleanupExpiredEntries, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            
            if (_loggingOptions.Enabled && _loggingOptions.LogRateLimit)
            {
                _logger.LogInformation("Rate limiting middleware initialized with limits: {MaxPerMinute}/min, {MaxPerHour}/hour, QR: {MaxQRPerMinute}/min", 
                    _rateLimitOptions.MaxRequestsPerMinute, _rateLimitOptions.MaxRequestsPerHour, _rateLimitOptions.MaxQRRequestsPerMinute);
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var clientId = GetClientIdentifier(context);
            var endpoint = GetEndpointType(context.Request.Path);

            if (await IsRateLimited(clientId, endpoint, context))
            {
                return; // Response already sent
            }

            await _next(context);
        }

        private string GetClientIdentifier(HttpContext context)
        {
            // Try to get session name from request for tenant-based limiting
            var sessionName = GetSessionNameFromRequest(context);
            if (!string.IsNullOrEmpty(sessionName))
            {
                return $"session:{sessionName}";
            }

            // Fall back to IP-based limiting
            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            return !string.IsNullOrEmpty(forwarded) 
                ? forwarded.Split(',')[0].Trim()
                : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        private string GetSessionNameFromRequest(HttpContext context)
        {
            // Check query string first
            if (context.Request.Query.TryGetValue("sessionName", out var queryValue))
                return queryValue.FirstOrDefault();

            // For POST requests, we'd need to read the body, but that's complex
            // For now, rely on IP-based limiting for POST endpoints
            return null;
        }

        private EndpointType GetEndpointType(PathString path)
        {
            var pathValue = path.Value?.ToLower() ?? "";

            if (pathValue.Contains("getasciiQRcode"))
                return EndpointType.QRCode;
            
            if (pathValue.Contains("startsession") || pathValue.Contains("stopsession"))
                return EndpointType.SessionOperation;
            
            if (pathValue.Contains("sendmessage") || pathValue.Contains("sendmedia"))
                return EndpointType.Messaging;

            return EndpointType.General;
        }

        private async Task<bool> IsRateLimited(string clientId, EndpointType endpoint, HttpContext context)
        {
            var clientLimit = _clientLimits.GetOrAdd(clientId, _ => new ClientRateLimit());
            var now = DateTime.UtcNow;

            lock (clientLimit)
            {
                // Clean old entries
                clientLimit.Requests.RemoveAll(r => now - r > TimeSpan.FromHours(1));

                var recentRequests = clientLimit.Requests.Where(r => now - r <= TimeSpan.FromMinutes(1)).Count();
                var hourlyRequests = clientLimit.Requests.Count;

                // Endpoint-specific limits from configuration
                var minuteLimit = endpoint switch
                {
                    EndpointType.QRCode => _rateLimitOptions.MaxQRRequestsPerMinute,
                    EndpointType.SessionOperation => _rateLimitOptions.MaxSessionOperationsPerMinute,
                    _ => _rateLimitOptions.MaxRequestsPerMinute
                };

                // Check rate limits
                if (recentRequests >= minuteLimit)
                {
                    if (_loggingOptions.Enabled && _loggingOptions.LogRateLimit)
                    {
                        _logger.LogWarning("Rate limit exceeded for client {ClientId} on {Endpoint}: {RecentRequests} requests in last minute (limit: {MinuteLimit})", 
                            clientId, endpoint, recentRequests, minuteLimit);
                    }
                    return SendRateLimitResponse(context, $"Too many {endpoint} requests. Limit: {minuteLimit}/minute");
                }

                if (hourlyRequests >= _rateLimitOptions.MaxRequestsPerHour)
                {
                    if (_loggingOptions.Enabled && _loggingOptions.LogRateLimit)
                    {
                        _logger.LogWarning("Hourly rate limit exceeded for client {ClientId}: {HourlyRequests} requests (limit: {MaxPerHour})", 
                            clientId, hourlyRequests, _rateLimitOptions.MaxRequestsPerHour);
                    }
                    return SendRateLimitResponse(context, $"Hourly request limit exceeded. Limit: {_rateLimitOptions.MaxRequestsPerHour}/hour");
                }

                // Add current request
                clientLimit.Requests.Add(now);
                return false; // Not rate limited
            }
        }

        private bool SendRateLimitResponse(HttpContext context, string message)
        {
            context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            context.Response.Headers.Add("Retry-After", "60"); // Suggest retry after 60 seconds
            
            var response = new
            {
                error = "Rate limit exceeded",
                message = message,
                retryAfter = 60
            };

            context.Response.ContentType = "application/json";
            var json = System.Text.Json.JsonSerializer.Serialize(response);
            context.Response.WriteAsync(json);
            
            return true; // Rate limited
        }

        private void CleanupExpiredEntries(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredClients = new List<string>();

                foreach (var kvp in _clientLimits)
                {
                    var clientLimit = kvp.Value;
                    lock (clientLimit)
                    {
                        // Remove requests older than 1 hour
                        clientLimit.Requests.RemoveAll(r => now - r > TimeSpan.FromHours(1));
                        
                        // If no recent requests, mark client for removal
                        if (!clientLimit.Requests.Any())
                        {
                            expiredClients.Add(kvp.Key);
                        }
                    }
                }

                // Remove expired clients
                foreach (var clientId in expiredClients)
                {
                    _clientLimits.TryRemove(clientId, out _);
                }

                if (_loggingOptions.Enabled && _loggingOptions.LogRateLimit)
                {
                    _logger.LogDebug("Rate limiting cleanup: {ExpiredClients} expired clients removed, {ActiveClients} active clients", 
                        expiredClients.Count, _clientLimits.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during rate limiting cleanup");
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
    }

    public class ClientRateLimit
    {
        public List<DateTime> Requests { get; } = new List<DateTime>();
    }

    public enum EndpointType
    {
        General,
        QRCode,
        SessionOperation,
        Messaging
    }
}