using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace WhatsAppApi.Services
{
    public class WhatsAppHostedService : IHostedService
    {
        private readonly IWhatsAppService _whatsAppService;

        public WhatsAppHostedService(IWhatsAppService whatsAppService)
        {
            _whatsAppService = whatsAppService;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _whatsAppService.StartAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _whatsAppService.StopAsync(cancellationToken);
        }
    }
}
