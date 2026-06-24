using WhatsAppApi.Controllers;

namespace WhatsAppApi.Middleware
{
    public class DashboardAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _config;

        private static readonly string[] ProtectedPrefixes =
        [
            "/api/logs"
        ];

        public DashboardAuthMiddleware(RequestDelegate next, IConfiguration config)
        {
            _next = next;
            _config = config;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value?.ToLower() ?? "";

            if (ProtectedPrefixes.Any(p => path.StartsWith(p)))
            {
                var configured = _config["Dashboard:Password"] ?? "";
                if (!string.IsNullOrEmpty(configured))
                {
                    var expected = DashboardController.ComputeToken(configured);
                    if (context.Request.Cookies["dash_tok"] != expected)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"message\":\"Dashboard authentication required\"}");
                        return;
                    }
                }
            }

            await _next(context);
        }
    }
}
