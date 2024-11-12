using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using WhatsAppApi.Services;

namespace WhatsAppApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WhatsAppController : ControllerBase
    {
        private readonly IWhatsAppService _whatsAppService;

        public WhatsAppController(IWhatsAppService whatsAppService)
        {
            _whatsAppService = whatsAppService;
        }

        [HttpPost("sendMessage")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
        {
            await _whatsAppService.SendMessage(request.RemoteJid, request.Message);
            return Ok(new { Status = "Message sent" });
        }

        [HttpGet("getQRCode")]
        public IActionResult GetQRCode()
        {
            var qrCodeBase64 = _whatsAppService.GetCurrentQRCode();
            if (string.IsNullOrEmpty(qrCodeBase64))
            {
                return NotFound(new { Message = "QR code not available" });
            }
            return Ok(new { QrCodeBase64 = qrCodeBase64 });
        }
        [HttpGet("getAsciiQRCode")]
        public IActionResult GetAsciiQRCode()
        {
            var asciiQrCode = _whatsAppService.GetCurrentAsciiQRCode();
            if (string.IsNullOrEmpty(asciiQrCode))
            {
                return NotFound(new { Message = "QR code not available" });
            }
            return Ok(new { AsciiQrCode = asciiQrCode });
        }

        [HttpGet("connectionStatus")]
        public IActionResult GetConnectionStatus()
        {
            var isConnected = _whatsAppService.IsConnected;
            return Ok(new { IsConnected = isConnected });
        }
        // Add more endpoints as needed
    }

    //public class SendMessageRequest
    //{
    //    public string RemoteJid { get; set; }
    //    public string Message { get; set; }
    //}
}
