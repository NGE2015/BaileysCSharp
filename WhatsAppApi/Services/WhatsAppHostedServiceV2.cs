﻿using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WhatsAppApi.Services;

namespace WhatsAppApi.Services
{
    public class WhatsAppHostedServiceV2 : IHostedService, IDisposable
    {
        private readonly IWhatsAppServiceV2 _whatsAppService;
        private Timer _timer;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(10); // Adjust as needed
        private readonly TimeSpan _inactiveThreshold = TimeSpan.FromHours(720);  // Adjust as needed

        public WhatsAppHostedServiceV2(IWhatsAppServiceV2 whatsAppService)
        {
            _whatsAppService = whatsAppService;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Start a timer to perform cleanup tasks periodically
            _timer = new Timer(DoWork, null, TimeSpan.Zero, _cleanupInterval);
            return Task.CompletedTask;
        }

        private void DoWork(object state)
        {
            var now = DateTime.UtcNow;
            foreach (var sessionName in _whatsAppService.GetActiveSessions())
            {
                if (_whatsAppService is WhatsAppServiceV2 service)
                {
                    if (service.TryGetSessionData(sessionName, out var sessionData))
                    {
                        var inactivity = now - sessionData.LastActivity;
                        if (inactivity > _inactiveThreshold)
                        {
                            // Stop the session due to inactivity
                            _whatsAppService.StopSessionAsync(sessionName, CancellationToken.None).Wait();
                            Console.WriteLine($"Session {sessionName} stopped due to inactivity.");
                        }
                    }
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Stop the timer when the application is shutting down
            _timer?.Change(Timeout.Infinite, 0);

            // Gracefully stop all active sessions
            var activeSessions = _whatsAppService.GetActiveSessions().ToList();
            var stopTasks = activeSessions.Select(sessionName =>
                _whatsAppService.StopSessionAsync(sessionName, cancellationToken));

            try
            {
                await Task.WhenAll(stopTasks);
            }
            catch (Exception ex)
            {
                // Log but don't throw during shutdown
                Console.WriteLine($"Error during graceful shutdown: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            if (_whatsAppService is IDisposable disposableService)
            {
                disposableService.Dispose();
            }
        }
    }
}
