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

        public WhatsAppControllerV2(IWhatsAppServiceV2 whatsAppService)
        {
            _whatsAppService = whatsAppService;
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
}


//WORKING !!!!!!!

//using Microsoft.AspNetCore.Mvc;
//using System.Threading;
//using System.Threading.Tasks;
//using WhatsAppApi.Services;

//namespace WhatsAppApi.Controllers
//{
//    [ApiController]
//    [Route("v2/[controller]")]
//    public class WhatsAppControllerV2 : ControllerBase
//    {
//        private readonly IWhatsAppServiceV2 _whatsAppService;

//        public WhatsAppControllerV2(IWhatsAppServiceV2 whatsAppService)
//        {
//            _whatsAppService = whatsAppService;
//        }

//        [HttpPost("startSession")]
//        public async Task<IActionResult> StartSession([FromBody] StartSessionRequest request)
//        {
//            await _whatsAppService.StartSessionAsync(request.SessionName, CancellationToken.None);
//            return Ok(new { Status = $"Session {request.SessionName} started" });
//        }

//        [HttpPost("stopSession")]
//        public async Task<IActionResult> StopSession([FromBody] StopSessionRequest request)
//        {
//            await _whatsAppService.StopSessionAsync(request.SessionName, CancellationToken.None);
//            return Ok(new { Status = $"Session {request.SessionName} stopped" });
//        }

//        [HttpPost("sendMessage")]
//        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequestV2 request)
//        {
//            await _whatsAppService.SendMessage(request.SessionName, request.RemoteJid, request.Message);
//            return Ok(new { Status = "Message sent" });
//        }

//        [HttpGet("getQRCode")]
//        public IActionResult GetQRCode([FromQuery] string sessionName)
//        {
//            var qrCodeBase64 = _whatsAppService.GetQRCode(sessionName);
//            if (string.IsNullOrEmpty(qrCodeBase64))
//            {
//                return NotFound(new { Message = "QR code not available" });
//            }
//            return Ok(new { QrCodeBase64 = qrCodeBase64 });
//        }

//        [HttpGet("getAsciiQRCode")]
//        public IActionResult GetAsciiQRCode([FromQuery] string sessionName)
//        {
//            var asciiQrCode = _whatsAppService.GetAsciiQRCode(sessionName);
//            if (string.IsNullOrEmpty(asciiQrCode))
//            {
//                return NotFound(new { Message = "QR code not available" });
//            }
//            return Ok(new { AsciiQrCode = asciiQrCode });
//        }

//        [HttpGet("connectionStatus")]
//        public IActionResult GetConnectionStatus([FromQuery] string sessionName)
//        {
//            var isConnected = _whatsAppService.IsConnected(sessionName);
//            return Ok(new { IsConnected = isConnected });
//        }

//        [HttpGet("activeSessions")]
//        public IActionResult GetActiveSessions()
//        {
//            var sessions = _whatsAppService.GetActiveSessions();
//            return Ok(new { Sessions = sessions });
//        }
//    }

//    public class StartSessionRequest
//    {
//        public string SessionName { get; set; }
//    }

//    public class StopSessionRequest
//    {
//        public string SessionName { get; set; }
//    }

//    public class SendMessageRequestV2
//    {
//        public string SessionName { get; set; }
//        public string RemoteJid { get; set; }
//        public string Message { get; set; }
//    }
//}
