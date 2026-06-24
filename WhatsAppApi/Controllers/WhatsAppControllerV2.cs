using BaileysCSharp.Core.Events;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
using WhatsAppApi.Helper;
using WhatsAppApi.Services;

namespace WhatsAppApi.Controllers
{
    [ApiController]
    [Route("v2/[controller]")]
    public class WhatsAppControllerV2 : ControllerBase
    {
        private readonly IWhatsAppServiceV2 _whatsAppService;
        private readonly IWebHostEnvironment _env;

        public WhatsAppControllerV2(IWhatsAppServiceV2 whatsAppService, IWebHostEnvironment env)
        {
            _whatsAppService = whatsAppService;
            _env = env;
        }

        [HttpPost("startSession")]
        public async Task<IActionResult> StartSession([FromBody] StartSessionRequest request)
        {
            await _whatsAppService.StartSessionAsync(request.SessionName, CancellationToken.None);
            return Ok(new { Status = $"Session {request.SessionName} started" });
        }

        [HttpPost("stopSession")]
        public async Task<IActionResult> StopSession([FromBody] StopSessionRequest request)
        {
            await _whatsAppService.StopSessionAsync(request.SessionName, CancellationToken.None);
            return Ok(new { Status = $"Session {request.SessionName} stopped" });
        }

        [HttpPost("sendMessage")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionName) || string.IsNullOrEmpty(request.RemoteJid) || string.IsNullOrEmpty(request.Message))
                {
                    return BadRequest(new { Message = "SessionName, RemoteJid, and Message are required" });
                }

                await _whatsAppService.SendMessage(request.SessionName, request.RemoteJid, request.Message);
                return Ok(new { Status = "Message sent" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Failed to send message", Error = ex.Message });
            }
        }
        [HttpPost("sendMedia")]
        public async Task<IActionResult> SendMedia([FromBody] SendMediaRequest req)
        {
            // —— LOGGING START ——
            try
            {
                var logDir = Path.Combine(_env.ContentRootPath, "logs");
                Directory.CreateDirectory(logDir);

                var logFile = Path.Combine(logDir, "sendmedia.log");
                var now = DateTime.UtcNow.ToString("o");

                // just the length of the array; don’t clutter your log with the whole blob!
                var length = req.MediaBytes?.Length ?? 0;

                var line = $"{now}  SESSION={req.SessionName}  JID={req.RemoteJid}  MIME={req.MimeType}  BYTES={length}\n";
                await System.IO.File.AppendAllTextAsync(logFile, line);
            }
            catch
            {
                // swallow any logging errors so you don't break the happy path
            }
            // —— LOGGING END ——

            await _whatsAppService.SendMediaAsync(
                req.SessionName,
                req.RemoteJid,
                req.MediaBytes,
                req.MimeType,
                req.Caption
            );
            return Ok(new { Status = "Media sent" });
        }
        [HttpGet("getAsciiQRCode")]
        public async Task<IActionResult> GetAsciiQRCode([FromQuery] string sessionName, [FromQuery] int timeout = 10)
        {
            if (string.IsNullOrEmpty(sessionName))
            {
                return BadRequest(new { Message = "Session name is required" });
            }

            // Validate timeout parameter
            if (timeout < 1 || timeout > 60)
            {
                return BadRequest(new { Message = "Timeout must be between 1 and 60 seconds" });
            }

            try
            {
                // Check if session exists first
                if (!_whatsAppService.TryGetSessionData(sessionName, out _))
                {
                    return NotFound(new { Message = $"Session '{sessionName}' not found" });
                }

                // Use the new waiting mechanism
                var asciiQrCode = await _whatsAppService.GetAsciiQRCodeWithWaitAsync(sessionName, timeout);
                
                if (string.IsNullOrEmpty(asciiQrCode))
                {
                    // Check if session is connected (which means no QR needed)
                    if (_whatsAppService.IsConnected(sessionName))
                    {
                        return Ok(new { Message = "Session is already connected, no QR code needed", IsConnected = true });
                    }
                    
                    // Return timeout status
                    return StatusCode(408, new { Message = $"Request timeout: QR code not ready within {timeout} seconds" });
                }
                
                return Ok(new { AsciiQrCode = asciiQrCode, IsConnected = false });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Internal server error", Details = ex.Message });
            }
        }

        [HttpPost("forceRegenerateQRCode")]
        public async Task<IActionResult> ForceRegenerateQRCode([FromBody] ForceRegenerateQRRequest request)
        {
            if (string.IsNullOrEmpty(request.SessionName))
            {
                return BadRequest(new { Message = "Session name is required" });
            }

            try
            {
                var asciiQrCode = await _whatsAppService.ForceRegenerateQRCodeAsync(request.SessionName, CancellationToken.None);
                if (string.IsNullOrEmpty(asciiQrCode))
                {
                    return NotFound(new { Message = "Unable to generate new QR code" });
                }
                return Ok(new { AsciiQrCode = asciiQrCode });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = $"Error regenerating QR code: {ex.Message}" });
            }
        }

        [HttpGet("connectionStatus")]
        public IActionResult GetConnectionStatus([FromQuery] string sessionName)
        {
            var isConnected = _whatsAppService.IsConnected(sessionName);
            return Ok(new { IsConnected = isConnected });
        }

        [HttpGet("activeSessions")]
        public IActionResult GetActiveSessions()
        {
            var sessions = _whatsAppService.GetActiveSessions();
            return Ok(new { Sessions = sessions });
        }

        [HttpGet("allDiagnostics")]
        public IActionResult GetAllDiagnostics()
        {
            var results = new List<object>();
            foreach (var sessionName in _whatsAppService.GetActiveSessions())
            {
                if (_whatsAppService.TryGetSessionData(sessionName, out var sd))
                    results.Add(BuildDiagnostic(sessionName, sd));
            }
            var waVersion = WaBuildHelper.LastResolvedVersion is { } v
                ? string.Join(".", v)
                : "unknown";
            return Ok(new { sessions = results, waVersion });
        }

        private static object BuildDiagnostic(string sessionName, WhatsAppServiceV2.SessionData sd)
        {
            string state, why, action;

            if (sd.IsConnected)
            {
                state = "connected";
                why = "Connected and working normally.";
                action = "";
            }
            else if (!string.IsNullOrEmpty(sd.QRCode))
            {
                var elapsed = sd.QRSessionStartTime != DateTime.MinValue
                    ? (DateTime.UtcNow - sd.QRSessionStartTime).TotalMinutes : 0;
                state = "qr_pending";
                why = $"Waiting for QR code scan ({elapsed:F1} min / {sd.MaxQRSessionDuration.TotalMinutes:F0} min max).";
                action = "Open WhatsApp on your phone → Settings → Linked Devices → Add Device → scan the QR code shown below.";
            }
            else if (sd.LastDisconnectReason == DisconnectReason.LoggedOut)
            {
                state = "logged_out";
                why = "The phone explicitly logged out this session. The stored credentials are no longer valid.";
                action = "Use Logoff to delete the session, then start a new session and scan a fresh QR code.";
            }
            else if (sd.LastDisconnectReason == DisconnectReason.BadSession)
            {
                state = "bad_session";
                why = "Session credentials were rejected by WhatsApp as corrupt or invalid.";
                action = "Delete the session permanently and re-authenticate with a new QR code.";
            }
            else if (sd.LastDisconnectReason == DisconnectReason.RestartRequired)
            {
                state = "restart_required";
                why = "WhatsApp signalled that a restart is required (protocol update or app update needed).";
                action = "Stop and restart the session. If it persists, check if a new WhatsApp Web version is available.";
            }
            else if (sd.ReconnectAttempts > 0)
            {
                state = "reconnecting";
                why = $"Disconnected ({sd.LastDisconnectReason}). Auto-reconnection attempt #{sd.ReconnectAttempts} in progress.";
                action = "Wait for automatic reconnection. If it keeps failing after 5+ attempts, check the logs for the root cause.";
            }
            else if (sd.LastDisconnectionTime != DateTime.MinValue)
            {
                state = "disconnected";
                why = $"Disconnected ({sd.LastDisconnectReason}) at {sd.LastDisconnectionTime:HH:mm:ss} UTC. Preparing to reconnect.";
                action = "Wait for automatic reconnection. If QR code does not appear within 30 s, restart the session.";
            }
            else
            {
                state = "connecting";
                why = "Connecting to WhatsApp servers. QR code generation in progress.";
                action = "Wait up to 30 seconds. If no QR appears, there may be a WhatsApp version mismatch — check the logs.";
            }

            return new
            {
                sessionName,
                state,
                isConnected = sd.IsConnected,
                hasQrCode = !string.IsNullOrEmpty(sd.QRCode),
                rawQrData = sd.RawQrData,
                why,
                action,
                lastDisconnectReason = sd.LastDisconnectReason.ToString(),
                lastDisconnectionTime = sd.LastDisconnectionTime == DateTime.MinValue
                    ? null : (object)sd.LastDisconnectionTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                reconnectAttempts = sd.ReconnectAttempts,
                lastActivity = sd.LastActivity == default ? "Never"
                    : sd.LastActivity.ToString("yyyy-MM-dd HH:mm:ss") + " UTC",
                qrElapsedMinutes = sd.QRSessionStartTime != DateTime.MinValue
                    ? Math.Round((DateTime.UtcNow - sd.QRSessionStartTime).TotalMinutes, 1) : 0,
                qrMaxMinutes = sd.MaxQRSessionDuration.TotalMinutes
            };
        }

        [HttpPost("logoff")]
        public async Task<IActionResult> LogoffSession([FromBody] LogoffSessionRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SessionName))
                {
                    return BadRequest(new { Message = "Session name is required" });
                }

                // Use DeleteSessionPermanentlyAsync to completely remove session and files
                await _whatsAppService.DeleteSessionPermanentlyAsync(request.SessionName, CancellationToken.None);
                return Ok(new { Status = $"Session {request.SessionName} logged off and files deleted" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "Failed to logoff session", Error = ex.Message });
            }
        }
    }

    public class StartSessionRequest
    {
        public string SessionName { get; set; }
    }

    public class StopSessionRequest
    {
        public string SessionName { get; set; }
    }

    public class SendMessageRequest
    {
        public string SessionName { get; set; }
        public string RemoteJid { get; set; }
        public string Message { get; set; }
    }
    /// <summary>
    /// POST v2/WhatsApp/sendMedia
    /// {
    ///   "sessionName": "mySession",
    ///   "remoteJid": "2779xxxxxxx@s.whatsapp.net",
    ///   "mediaBytes": "<base64 binary map>",
    ///   "mimeType": "image/jpeg",
    ///   "caption": "Here's your picture!"
    /// }
    /// </summary>
    public class SendMediaRequest
    {
        public string SessionName { get; set; }
        public string RemoteJid { get; set; }
        public byte[] MediaBytes { get; set; }
        public string MimeType { get; set; }
        public string Caption { get; set; }
    }

    public class ForceRegenerateQRRequest
    {
        public string SessionName { get; set; }
    }

    public class LogoffSessionRequest
    {
        public string SessionName { get; set; }
    }
}