using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
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
            await _whatsAppService.SendMessage(request.SessionName, request.RemoteJid, request.Message);
            return Ok(new { Status = "Message sent" });
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
        public IActionResult GetAsciiQRCode([FromQuery] string sessionName)
        {
            var asciiQrCode = _whatsAppService.GetAsciiQRCode(sessionName);
            if (string.IsNullOrEmpty(asciiQrCode))
            {
                return NotFound(new { Message = "QR code not available" });
            }
            return Ok(new { AsciiQrCode = asciiQrCode });
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
}