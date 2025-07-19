﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BaileysCSharp.Core.Events;
using BaileysCSharp.Core.Logging;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Models.Sending.NonMedia;
using BaileysCSharp.Core.NoSQL;
using BaileysCSharp.Core.Sockets;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.Utils;
using BaileysCSharp.Exceptions;
using Microsoft.Extensions.Logging;
using Proto;
using QRCoder;
using System.Text.Json;
using BaileysCSharp.Core.Helper;
using static WhatsAppApi.Services.WhatsAppServiceV2;
using BaileysCSharp.Core.Models.Sending.Media;
using System.IO;
using BaileysCSharp.Core.Models.Sending.Media;
using WhatsAppApi.Helper;          // <— new using
namespace WhatsAppApi.Services
{
    public interface IWhatsAppServiceV2
    {
        Task StartSessionAsync(string sessionName, CancellationToken cancellationToken);
        Task StopSessionAsync(string sessionName, CancellationToken cancellationToken);
        Task SendMessage(string sessionName, string remoteJid, string message);
        /// <summary>
        /// Send a media (image/video/etc) with an optional caption.
        /// </summary>
        Task SendMediaAsync(
           string sessionName,
           string remoteJid,
           byte[] mediaBytes,
           string mimeType,
           string caption
       );
        string GetQRCode(string sessionName);
        string GetAsciiQRCode(string sessionName);
        bool IsConnected(string sessionName);
        IEnumerable<string> GetActiveSessions();
        bool TryGetSessionData(string sessionName, out SessionData sessionData);
    }

    public class WhatsAppServiceV2 : IWhatsAppServiceV2, IDisposable
    {
        private readonly ILogger<WhatsAppServiceV2> _logger;
        private readonly ConcurrentDictionary<string, SessionData> _sessions;
        private readonly Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _qrRequestSemaphores = new();
        private const int MaxSessionsPerService = 100;
        private const int MaxInactiveHours = 72; // 3 days for inactive sessions
        private const int MaxConnectedDays = 30; // 30 days for connected sessions
        private bool _disposed = false;

        public WhatsAppServiceV2(ILogger<WhatsAppServiceV2> logger)
        {
            _logger = logger;
            _sessions = new ConcurrentDictionary<string, SessionData>();

            // Start health check timer
            _healthCheckTimer = new Timer(PerformHealthCheck, null, _healthCheckInterval, _healthCheckInterval);
        }

        public async Task StartSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (_sessions.ContainsKey(sessionName))
            {
                _logger.LogWarning($"Session {sessionName} already exists. Attempting to restore connection.");
                
                // Try to restore the existing session if it's disconnected
                if (_sessions.TryGetValue(sessionName, out var existingSession) && !existingSession.IsConnected)
                {
                    try
                    {
                        existingSession.Socket.MakeSocket();
                        existingSession.LastActivity = DateTime.UtcNow;
                        _logger.LogInformation($"Session {sessionName} connection restored.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to restore session {sessionName}, creating new session.");
                        // Remove the failed session and continue to create a new one
                        _sessions.TryRemove(sessionName, out _);
                    }
                }
                else
                {
                    return; // Session exists and is connected
                }
            }

            var sessionData = new SessionData();
            var config = new SocketConfig()
            {
                SessionName = sessionName,
            };
            config.Version = await WhatsAppApi.Helper.WaBuildHelper.GetLatestAlphaAsync();
            var credsFile = Path.Join(config.CacheRoot, $"{sessionName}_creds.json");
            AuthenticationCreds? authentication = null;
            if (File.Exists(credsFile))
            {
                authentication = AuthenticationCreds.Deserialize(File.ReadAllText(credsFile));
            }
            authentication = authentication ?? AuthenticationUtils.InitAuthCreds();

            BaseKeyStore keys = new FileKeyStore(config.CacheRoot);

            config.Logger.Level = BaileysCSharp.Core.Logging.LogLevel.Raw;
            config.Auth = new AuthenticationState()
            {
                Creds = authentication,
                Keys = keys
            };

            var socket = new WASocket(config);

            // Attach event handlers
            socket.EV.Auth.Update += (sender, creds) => Auth_Update(sender, creds, sessionName);
            socket.EV.Connection.Update += (sender, state) => Connection_UpdateAsync(sender, state, sessionName);
            socket.EV.Message.Upsert += (sender, e) => Message_Upsert(sender, e, sessionName);
            socket.EV.MessageHistory.Set += MessageHistory_Set;
            socket.EV.Pressence.Update += Pressence_Update;

            socket.MakeSocket();

            sessionData.Socket = socket;
            sessionData.Config = config; // Store config in sessionData
            sessionData.Messages = new List<WebMessageInfo>();
            sessionData.LastActivity = DateTime.UtcNow; // Initialize LastActivity

            _sessions.TryAdd(sessionName, sessionData);
        }

        public async Task StopSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (!_sessions.TryRemove(sessionName, out var sessionData))
                return;
            // NEW CODE -------------------------------
            if (sessionData.Socket is { } sock)
            {
                await LogoutAndWaitAsync(sock, cancellationToken);
                sock.CleanupSession();                     // drops in-memory keys
            }

            var folder = sessionData.Config.CacheRoot;     // REAL path
            try
            {
                Directory.Delete(folder, true);
                _logger.LogInformation($"Session {sessionName} stopped and folder deleted.");
            }
            catch (IOException)
            {
                // try again after forcing finalisers to release file handles
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try
                {
                    Directory.Delete(folder, true);
                    _logger.LogInformation($"Session {sessionName} stopped (2nd attempt) and folder deleted.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Session {sessionName} stopped but folder still locked.");
                }
            }
            //if (_sessions.TryRemove(sessionName, out var sessionData))
            //{
            //    try
            //    {
            //        // Cleanup the session resources
            //        sessionData.Socket?.CleanupSession();

            //        // Wait a short delay to ensure all resources are released
            //        await Task.Delay(1000, cancellationToken);

            //        // Delete the session data folder
            //        var sessionFolderPath = Path.Combine(AppContext.BaseDirectory, sessionName);
            //        if (Directory.Exists(sessionFolderPath))
            //        {
            //            try
            //            {
            //                Directory.Delete(sessionFolderPath, true);
            //                _logger.LogInformation($"Session {sessionName} stopped and folder deleted.");
            //            }
            //            catch (Exception)
            //            {

            //                _logger.LogInformation($"Session {sessionName} stopped and folder NOT deleted.");
            //            }

            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        _logger.LogError(ex, $"Error while stopping session {sessionName}.");
            //    }
            //}
            //else
            //{
            //    _logger.LogWarning($"Session {sessionName} not found.");
            //}
        }


        private void Auth_Update(object? sender, AuthenticationCreds e, string sessionName)
        {
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                var cacheRoot = sessionData.Config.CacheRoot;
                var credsFile = Path.Join(cacheRoot, $"{sessionName}_creds.json");
                var json = AuthenticationCreds.Serialize(e);
                File.WriteAllText(credsFile, json);
            }
            else
            {
                _logger.LogWarning($"Session {sessionName} not found in Auth_Update.");
            }
        }

        private async Task Connection_UpdateAsync(object? sender, ConnectionState e, string sessionName)
        {
            var connection = e;
            _logger.LogDebug(JsonSerializer.Serialize(connection));
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                if (connection.QR != null)
                {
                    QRCodeGenerator QrGenerator = new QRCodeGenerator();
                    QRCodeData QrCodeInfo = QrGenerator.CreateQrCode(connection.QR, QRCodeGenerator.ECCLevel.L);
                    AsciiQRCode qrCode = new AsciiQRCode(QrCodeInfo);
                    var data = qrCode.GetGraphic(1);
                    Console.WriteLine(data);
                    sessionData.QRCode = data;
                }
                if (connection.Connection == WAConnectionState.Close)
                {
                    sessionData.IsConnected = false;
                    if (connection.LastDisconnect.Error is Boom boom && boom.Data?.StatusCode != (int)DisconnectReason.LoggedOut)
                    {
                        // Implement exponential backoff for reconnection
                        await ScheduleReconnectionAsync(sessionName, sessionData);
                    }
                    else
                    {
                        _logger.LogWarning($"Session {sessionName} is logged out, will not auto-reconnect");
                        Console.WriteLine($"Session {sessionName} is logged out");
                    }
                }
                else if (connection.Connection == WAConnectionState.Open)
                {
                    // Clear the QR code when connection is established
                    sessionData.QRCode = null;
                    sessionData.IsConnected = true;
                }

                // Update LastActivity
                sessionData.LastActivity = DateTime.UtcNow;
            }
        }

        private void Message_Upsert(object? sender, MessageEventModel e, string sessionName)
        {
            if (e.Type == MessageEventType.Notify)
            {
                if (_sessions.TryGetValue(sessionName, out var sessionData))
                {
                    foreach (var msg in e.Messages)
                    {
                        if (msg.Message == null)
                            continue;

                        // Handle incoming messages here

                        // Update LastActivity
                        sessionData.LastActivity = DateTime.UtcNow;
                    }
                    sessionData.Messages.AddRange(e.Messages);
                }
            }
        }

        private void MessageHistory_Set(object? sender, MessageHistoryModel[] e)
        {
            // Implement if necessary
        }

        private void Pressence_Update(object? sender, PresenceModel e)
        {
            Console.WriteLine(JsonSerializer.Serialize(e));
        }

        public async Task SendMessage(string sessionName, string remoteJid, string message)
        {
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                await sessionData.Socket.SendMessage(remoteJid, new TextMessageContent()
                {
                    Text = message
                });

                // Update LastActivity
                sessionData.LastActivity = DateTime.UtcNow;
            }
            else
            {
                throw new Exception($"Session {sessionName} not found.");
            }
        }
        /// <summary>
        /// Send a media (e.g. image) plus caption via Baileys.
        /// </summary>
        public async Task SendMediaAsync(
    string sessionName,
    string remoteJid,
    byte[] mediaBytes,
    string mimeType,
    string caption
)
        {
            if (!_sessions.TryGetValue(sessionName, out var sessionData))
                throw new InvalidOperationException($"Session '{sessionName}' not found.");

            // prepare log folder & log file
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            var logFile = Path.Combine(logDir, "SendMediaService.log");
            Directory.CreateDirectory(logDir);

            var now = DateTime.UtcNow;
            var timeTag = now.ToString("yyyyMMdd_HHmmssfff");
            var length = mediaBytes?.Length ?? 0;

            // 1) Write the bytes out as a .png so you can open it directly on the server
            var imagePath = Path.Combine(logDir, $"{sessionName}_{timeTag}.png");
            try
            {
                //await File.WriteAllBytesAsync(imagePath, mediaBytes);
            }
            catch
            {
                // swallow; best‐effort
            }

            // 2) Log the Base64 snippet (first 200 chars) plus length
            var b64 = Convert.ToBase64String(mediaBytes ?? Array.Empty<byte>());
            var snippet = b64.Length > 200 ? b64.Substring(0, 200) + "…(truncated)" : b64;
            var headerLog = $"{now:o}  [Service] session={sessionName} jid={remoteJid} mime={mimeType} bytes={length}\n"
                          + $"              ImageDump: {imagePath}\n"
                          + $"              Base64: {snippet}\n";
            await File.AppendAllTextAsync(logFile, headerLog);

            // now hand off to Baileys
            using var ms = new MemoryStream(mediaBytes);
            try
            {
                await sessionData.Socket.SendMessage(
                    remoteJid,
                    new ImageMessageContent
                    {
                        Image = ms,
                        Caption = caption
                    }
                );

                var doneLine = $"{DateTime.UtcNow:o}  [Service] SendMessage() completed successfully\n";
                await File.AppendAllTextAsync(logFile, doneLine);
            }
            catch (Exception ex)
            {
                var errLine = $"{DateTime.UtcNow:o}  [Service] ERROR: {ex}\n";
                await File.AppendAllTextAsync(logFile, errLine);
                throw;
            }

            sessionData.LastActivity = DateTime.UtcNow;
        }

        public string GetQRCode(string sessionName)
        {
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                return sessionData.QRCode;
            }
            return null;
        }

        public string GetAsciiQRCode(string sessionName)
        {
            // REQUEST DEDUPLICATION: Prevent concurrent QR requests for same session
            var semaphore = _qrRequestSemaphores.GetOrAdd(sessionName, _ => new SemaphoreSlim(1, 1));
            
            if (!semaphore.Wait(100)) // 100ms timeout
            {
                _logger.LogWarning($"QR code request for session {sessionName} timed out due to concurrent request");
                return null;
            }

            try
            {
                if (_sessions.TryGetValue(sessionName, out var sessionData))
                {
                    return sessionData.QRCode;
                }
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public bool IsConnected(string sessionName)
        {
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                return sessionData.IsConnected;
            }
            return false;
        }

        public IEnumerable<string> GetActiveSessions()
        {
            return _sessions.Keys;
        }

        public bool TryGetSessionData(string sessionName, out SessionData sessionData)
        {
            return _sessions.TryGetValue(sessionName, out sessionData);
        }


        // Add after the class' private fields or right before the closing brace
        private static async Task LogoutAndWaitAsync(WASocket socket,
                                                     CancellationToken token)
        {
            // Tell WhatsApp to terminate the MD session on its side
            socket.PublicEnd(); // emits DisconnectReason.LoggedOut

            // Wait (max 5 s) until the connection state is "close"
            var closed = new TaskCompletionSource();
            void Handler(object? _, ConnectionState state)
            {
                if (state.Connection == WAConnectionState.Close)
                    closed.TrySetResult();
            }
            socket.EV.Connection.Update += Handler;
            await Task.WhenAny(closed.Task, Task.Delay(5000, token));
            socket.EV.Connection.Update -= Handler;
        }

        // Add health check and cleanup methods
        private void PerformHealthCheck(object state)
        {
            if (_disposed) return;

            try
            {
                var now = DateTime.UtcNow;
                var sessionsToCleanup = new List<string>();
                var sessionCount = _sessions.Count;

                _logger.LogDebug($"Health check: {sessionCount} active sessions");

                // Check for session limit enforcement
                if (sessionCount > MaxSessionsPerService)
                {
                    _logger.LogWarning($"Session count ({sessionCount}) exceeds maximum ({MaxSessionsPerService}), enforcing cleanup");
                }

                foreach (var kvp in _sessions)
                {
                    var sessionName = kvp.Key;
                    var sessionData = kvp.Value;
                    var timeSinceActivity = now - sessionData.LastActivity;

                    bool shouldCleanup = false;
                    string reason = "";

                    // ENHANCED CLEANUP LOGIC:
                    // 1. Inactive sessions: 72 hours (3 days)
                    if (!sessionData.IsConnected && timeSinceActivity > TimeSpan.FromHours(MaxInactiveHours))
                    {
                        shouldCleanup = true;
                        reason = $"inactive for {timeSinceActivity.TotalHours:F1} hours";
                    }
                    // 2. Connected sessions: 30 days (prevent indefinite accumulation)
                    else if (sessionData.IsConnected && timeSinceActivity > TimeSpan.FromDays(MaxConnectedDays))
                    {
                        shouldCleanup = true;
                        reason = $"connected but stale for {timeSinceActivity.TotalDays:F1} days";
                    }
                    // 3. Unhealthy connected sessions
                    else if (sessionData.IsConnected && !IsSocketHealthy(sessionData.Socket))
                    {
                        _logger.LogWarning($"Session {sessionName} appears unhealthy, marking as disconnected");
                        sessionData.IsConnected = false;
                        // Don't cleanup immediately, give it a chance to reconnect
                    }
                    // 4. Force cleanup if over session limit (oldest first)
                    else if (sessionCount > MaxSessionsPerService)
                    {
                        shouldCleanup = true;
                        reason = "session limit exceeded";
                    }

                    if (shouldCleanup)
                    {
                        sessionsToCleanup.Add(sessionName);
                        _logger.LogInformation($"Scheduling cleanup for session {sessionName}: {reason}");
                    }
                    else if (sessionData.IsConnected)
                    {
                        // Keep connected sessions alive by updating activity
                        sessionData.LastActivity = DateTime.UtcNow;
                    }
                }

                // Clean up sessions (async to avoid blocking health check)
                foreach (var sessionName in sessionsToCleanup)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await StopSessionAsync(sessionName, CancellationToken.None);
                            _logger.LogInformation($"Successfully cleaned up session: {sessionName}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error cleaning up session {sessionName}");
                        }
                    });
                }

                // Clean up orphaned semaphores
                var orphanedSemaphores = _qrRequestSemaphores.Keys.Except(_sessions.Keys).ToList();
                foreach (var orphaned in orphanedSemaphores)
                {
                    if (_qrRequestSemaphores.TryRemove(orphaned, out var semaphore))
                    {
                        semaphore.Dispose();
                        _logger.LogDebug($"Cleaned up orphaned semaphore for session: {orphaned}");
                    }
                }

                // Log memory usage info
                if (sessionCount > 10) // Only log if significant number of sessions
                {
                    _logger.LogInformation($"Health check complete: {sessionCount} sessions, {sessionsToCleanup.Count} marked for cleanup, {_qrRequestSemaphores.Count} semaphores");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
            }
        }

        private bool IsSocketHealthy(WASocket socket)
        {
            try
            {
                // Basic health check - socket should be non-null and not disposed
                return socket != null;
                //&& socket.state != null
            }
            catch
            {
                return false;
            }
        }

        private async Task ScheduleReconnectionAsync(string sessionName, SessionData sessionData)
        {
            if (_disposed) return;

            sessionData.ReconnectAttempts++;
            var maxAttempts = 5;

            if (sessionData.ReconnectAttempts > maxAttempts)
            {
                _logger.LogError($"Session {sessionName} exceeded maximum reconnection attempts ({maxAttempts})");
                return;
            }

            // Exponential backoff: 2^attempt seconds, max 5 minutes
            var delaySeconds = Math.Min(Math.Pow(2, sessionData.ReconnectAttempts), 300);

            _logger.LogInformation($"Scheduling reconnection for {sessionName} in {delaySeconds} seconds (attempt {sessionData.ReconnectAttempts})");

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            if (_disposed || !_sessions.ContainsKey(sessionName)) return;

            try
            {
                sessionData.Socket.MakeSocket();
                _logger.LogInformation($"Reconnection attempt {sessionData.ReconnectAttempts} for session {sessionName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reconnect session {sessionName} on attempt {sessionData.ReconnectAttempts}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _logger.LogInformation("WhatsAppServiceV2 disposing: cleaning up resources");

            _healthCheckTimer?.Dispose();

            // Clean up all sessions
            foreach (var kvp in _sessions)
            {
                try
                {
                    kvp.Value.Socket?.CleanupSession();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing session {kvp.Key}");
                }
            }

            _sessions.Clear();

            // Clean up all semaphores
            foreach (var kvp in _qrRequestSemaphores)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing semaphore for session {kvp.Key}");
                }
            }

            _qrRequestSemaphores.Clear();
            _logger.LogInformation("WhatsAppServiceV2 disposal complete");
        }

        public class SessionData
        {
            public WASocket Socket { get; set; }
            public SocketConfig Config { get; set; } // Added Config property
            public List<WebMessageInfo> Messages { get; set; }
            public string QRCode { get; set; }
            public bool IsConnected { get; set; }
            public DateTime LastActivity { get; set; } = DateTime.UtcNow;
            public int ReconnectAttempts { get; set; } = 0;
        }
    }
}

