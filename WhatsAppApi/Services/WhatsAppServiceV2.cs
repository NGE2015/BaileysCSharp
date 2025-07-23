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
using Microsoft.Extensions.Configuration;  // <— new using
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
        Task<string> GetAsciiQRCodeWithWaitAsync(string sessionName, int timeoutSeconds = 10);
        bool IsSessionReady(string sessionName);
        Task<string> ForceRegenerateQRCodeAsync(string sessionName, CancellationToken cancellationToken);
        bool IsConnected(string sessionName);
        IEnumerable<string> GetActiveSessions();
        bool TryGetSessionData(string sessionName, out SessionData sessionData);
        Task DeleteSessionPermanentlyAsync(string sessionName, CancellationToken cancellationToken);
    }

    public class WhatsAppServiceV2 : IWhatsAppServiceV2, IDisposable
    {
        private readonly ILogger<WhatsAppServiceV2> _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, SessionData> _sessions;
        private readonly Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromMinutes(5);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _qrRequestSemaphores = new();
        private const int MaxSessionsPerService = 100;
        private const int MaxInactiveHours = 72; // 3 days for inactive sessions
        private const int MaxConnectedDays = 30; // 30 days for connected sessions
        private bool _disposed = false;

        public WhatsAppServiceV2(ILogger<WhatsAppServiceV2> logger, HttpClient httpClient, IConfiguration configuration)
        {
            _logger = logger;
            _httpClient = httpClient;
            _configuration = configuration;
            _sessions = new ConcurrentDictionary<string, SessionData>();

            // Start health check timer
            _healthCheckTimer = new Timer(PerformHealthCheck, null, _healthCheckInterval, _healthCheckInterval);
            
            // Auto-restore existing sessions on startup
            _ = Task.Run(RestoreExistingSessionsAsync);
        }

        public async Task StartSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Starting session: {sessionName}");
            
            if (_sessions.ContainsKey(sessionName))
            {
                _logger.LogWarning($"Session {sessionName} already exists. Attempting to restore connection.");
                
                // Try to restore the existing session if it's disconnected
                if (_sessions.TryGetValue(sessionName, out var existingSession) && !existingSession.IsConnected)
                {
                    try
                    {
                        _logger.LogDebug($"Attempting to restore disconnected session: {sessionName}");
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
                    _logger.LogInformation($"Session {sessionName} already exists and is connected");
                    return; // Session exists and is connected
                }
            }

            var sessionData = new SessionData();
            var config = new SocketConfig()
            {
                SessionName = sessionName,
            };
            config.Version = await WhatsAppApi.Helper.WaBuildHelper.GetLatestAlphaAsync();
            
            // Enhanced credential file discovery with migration support
            var credsFile = FindOrMigrateCredentialsFile(sessionName, config.CacheRoot);
            AuthenticationCreds? authentication = null;
            if (!string.IsNullOrEmpty(credsFile) && File.Exists(credsFile))
            {
                try
                {
                    authentication = AuthenticationCreds.Deserialize(File.ReadAllText(credsFile));
                    _logger.LogInformation($"Successfully loaded existing credentials for session {sessionName} from {credsFile}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to deserialize credentials for session {sessionName}, creating new credentials");
                }
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

            _logger.LogDebug($"Making socket connection for session: {sessionName}");
            socket.MakeSocket();

            sessionData.Socket = socket;
            sessionData.Config = config; // Store config in sessionData
            sessionData.Messages = new List<WebMessageInfo>();
            sessionData.LastActivity = DateTime.UtcNow; // Initialize LastActivity

            _sessions.TryAdd(sessionName, sessionData);
            _logger.LogInformation($"Session {sessionName} initialized and added to active sessions");
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

            // Don't delete session folder for persistence - only cleanup in-memory resources
            // Session files (credentials, keys) should persist across service restarts
            var folder = sessionData.Config.CacheRoot;
            _logger.LogInformation($"Session {sessionName} stopped. Session files preserved at: {folder}");
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

        /// <summary>
        /// Permanently deletes a session including all files - use with caution
        /// This is for when a session truly needs to be removed completely
        /// </summary>
        public async Task DeleteSessionPermanentlyAsync(string sessionName, CancellationToken cancellationToken)
        {
            _logger.LogWarning($"Permanently deleting session {sessionName} and all its files");
            
            // First stop the session (but this preserves files now)
            await StopSessionAsync(sessionName, cancellationToken);
            
            // Now delete the actual files
            var baseCacheRoot = "/home/RubyManager/web/whatsapp.rubymanager.app/sessions";
            var sessionFolder = Path.Combine(baseCacheRoot, sessionName);
            
            if (Directory.Exists(sessionFolder))
            {
                try
                {
                    Directory.Delete(sessionFolder, true);
                    _logger.LogInformation($"Session {sessionName} permanently deleted from {sessionFolder}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to permanently delete session {sessionName} from {sessionFolder}");
                    throw;
                }
            }
            else
            {
                _logger.LogWarning($"Session folder {sessionFolder} does not exist for permanent deletion");
            }
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
                    _logger.LogInformation($"Generating QR code for session {sessionName}");
                    QRCodeGenerator QrGenerator = new QRCodeGenerator();
                    QRCodeData QrCodeInfo = QrGenerator.CreateQrCode(connection.QR, QRCodeGenerator.ECCLevel.L);
                    AsciiQRCode qrCode = new AsciiQRCode(QrCodeInfo);
                    var data = qrCode.GetGraphic(1);
                    Console.WriteLine(data);
                    sessionData.QRCode = data;
                    _logger.LogInformation($"QR code generated and stored for session {sessionName}");
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
                    _logger.LogInformation($"Session {sessionName} connected successfully");
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

                        // Save incoming messages to CRM asynchronously (fire-and-forget)
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await SaveMessageToCrmAsync(sessionName, msg);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"Failed to save message to CRM for session {sessionName}");
                            }
                        });

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

        /// <summary>
        /// Gets ASCII QR code with waiting mechanism for session initialization
        /// </summary>
        /// <param name="sessionName">Name of the session</param>
        /// <param name="timeoutSeconds">Maximum time to wait for session readiness (default 10 seconds)</param>
        /// <returns>ASCII QR code string or null if timeout/not available</returns>
        public async Task<string> GetAsciiQRCodeWithWaitAsync(string sessionName, int timeoutSeconds = 10)
        {
            _logger.LogDebug($"GetAsciiQRCodeWithWaitAsync called for session {sessionName} with timeout {timeoutSeconds}s");

            // REQUEST DEDUPLICATION: Prevent concurrent QR requests for same session
            var semaphore = _qrRequestSemaphores.GetOrAdd(sessionName, _ => new SemaphoreSlim(1, 1));
            
            if (!await semaphore.WaitAsync(5000)) // 5 second semaphore timeout
            {
                _logger.LogWarning($"QR code request for session {sessionName} timed out due to concurrent request");
                return null;
            }

            try
            {
                var startTime = DateTime.UtcNow;
                var maxWaitTime = TimeSpan.FromSeconds(timeoutSeconds);
                
                while (DateTime.UtcNow - startTime < maxWaitTime)
                {
                    // Check if session exists
                    if (!_sessions.TryGetValue(sessionName, out var sessionData))
                    {
                        _logger.LogDebug($"Session {sessionName} not found, waiting...");
                        await Task.Delay(500); // Wait 500ms before next check
                        continue;
                    }

                    // Check if session is connected (no QR needed)
                    if (sessionData.IsConnected)
                    {
                        _logger.LogInformation($"Session {sessionName} is already connected, no QR code needed");
                        return null; // Connected session doesn't need QR
                    }

                    // Check if QR code is available
                    if (!string.IsNullOrEmpty(sessionData.QRCode))
                    {
                        _logger.LogInformation($"QR code ready for session {sessionName} after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
                        return sessionData.QRCode;
                    }

                    _logger.LogDebug($"Session {sessionName} exists but QR not ready yet, continuing to wait...");
                    await Task.Delay(500); // Wait 500ms before next check
                }

                _logger.LogWarning($"Timeout waiting for QR code for session {sessionName} after {timeoutSeconds} seconds");
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Checks if a session is ready for QR code retrieval or is already connected
        /// </summary>
        /// <param name="sessionName">Name of the session to check</param>
        /// <returns>True if session is ready (has QR or is connected), false otherwise</returns>
        public bool IsSessionReady(string sessionName)
        {
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                // Session is ready if it's connected OR has a QR code available
                return sessionData.IsConnected || !string.IsNullOrEmpty(sessionData.QRCode);
            }
            return false;
        }

        public async Task<string> ForceRegenerateQRCodeAsync(string sessionName, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Force regenerating QR code for session {sessionName}");

            // REQUEST DEDUPLICATION: Prevent concurrent QR requests for same session
            var semaphore = _qrRequestSemaphores.GetOrAdd(sessionName, _ => new SemaphoreSlim(1, 1));
            
            if (!await semaphore.WaitAsync(5000, cancellationToken)) // 5 second timeout
            {
                _logger.LogWarning($"Force QR regeneration for session {sessionName} timed out due to concurrent request");
                return null;
            }

            try
            {
                // Step 1: Check if session exists and is not connected
                if (_sessions.TryGetValue(sessionName, out var sessionData))
                {
                    if (sessionData.IsConnected)
                    {
                        _logger.LogWarning($"Cannot regenerate QR for session {sessionName} - already connected");
                        return null;
                    }

                    // Step 2: Clear existing QR code to force regeneration
                    sessionData.QRCode = null;
                    _logger.LogDebug($"Cleared existing QR code for session {sessionName}");

                    // Step 3: Force socket reconnection to generate new QR
                    try
                    {
                        // Disconnect and reconnect the socket to trigger new QR generation
                        sessionData.Socket?.CleanupSession();
                        await Task.Delay(500, cancellationToken); // Brief delay for cleanup

                        // Recreate the socket to trigger fresh QR generation
                        sessionData.Socket.MakeSocket();
                        _logger.LogDebug($"Triggered socket reconnection for session {sessionName}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to reconnect socket for session {sessionName}");
                        // Try stopping and starting the session completely
                        await StopSessionAsync(sessionName, cancellationToken);
                        await StartSessionAsync(sessionName, cancellationToken);
                    }
                }
                else
                {
                    // Session doesn't exist, create it fresh
                    _logger.LogInformation($"Session {sessionName} not found, creating new session for QR generation");
                    await StartSessionAsync(sessionName, cancellationToken);
                }

                // Step 4: Wait for new QR code to be generated (with timeout)
                var maxWaitTime = TimeSpan.FromSeconds(15);
                var startTime = DateTime.UtcNow;
                
                while (DateTime.UtcNow - startTime < maxWaitTime)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (_sessions.TryGetValue(sessionName, out var currentSessionData))
                    {
                        if (!string.IsNullOrEmpty(currentSessionData.QRCode))
                        {
                            _logger.LogInformation($"New QR code generated for session {sessionName}");
                            return currentSessionData.QRCode;
                        }

                        if (currentSessionData.IsConnected)
                        {
                            _logger.LogInformation($"Session {sessionName} connected while waiting for QR - no QR needed");
                            return null;
                        }
                    }

                    await Task.Delay(500, cancellationToken);
                }

                _logger.LogWarning($"Timeout waiting for QR code generation for session {sessionName}");
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

        /// <summary>
        /// Saves incoming message data to the CRM system asynchronously without blocking message processing
        /// </summary>
        private async Task SaveMessageToCrmAsync(string sessionName, WebMessageInfo messageInfo)
        {
            try
            {
                // Only process incoming messages (not outgoing)
                if (messageInfo.Key?.FromMe == true)
                {
                    return; // Skip outgoing messages
                }

                // Extract message data
                var senderPhone = ExtractPhoneNumber(messageInfo.Key?.RemoteJid);
                var messageContent = ExtractMessageContent(messageInfo.Message);
                var messageType = GetMessageType(messageInfo.Message);
                var messageId = messageInfo.Key?.Id;
                var remoteJid = messageInfo.Key?.RemoteJid;
                var timestamp = messageInfo.MessageTimestamp > 0 ? (long)messageInfo.MessageTimestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var receivedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-ddTHH:mm:ssZ");

                // Skip if no content to save
                if (string.IsNullOrEmpty(messageContent) || string.IsNullOrEmpty(senderPhone))
                {
                    _logger.LogDebug($"Skipping message save - missing content or sender for session {sessionName}");
                    return;
                }

                // Prepare payload for CRM API
                var payload = new
                {
                    clientExternalId = sessionName, // Using session name as tenant ID
                    senderPhone = senderPhone,
                    messageContent = messageContent,
                    messageType = messageType,
                    remoteJid = remoteJid,
                    messageId = messageId,
                    receivedAt = receivedAt
                };

                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                _logger.LogDebug($"Sending message to CRM for session {sessionName}: {senderPhone} - {messageContent.Substring(0, Math.Min(50, messageContent.Length))}...");

                // Send to CRM API with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var crmBaseUrl = _configuration["CrmEndpoint:BaseUrl"] ?? "https://whatsapp.rubymanager.app";
                var crmUrl = $"{crmBaseUrl}/api/whatsappmessagehistory/saveMessage";
                var response = await _httpClient.PostAsync(crmUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug($"Successfully saved message to CRM for session {sessionName}, message ID: {messageId}");
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"CRM API returned {response.StatusCode} for session {sessionName}: {responseBody}");
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning($"CRM API call timed out for session {sessionName}");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, $"Network error sending message to CRM for session {sessionName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error saving message to CRM for session {sessionName}");
            }
        }

        /// <summary>
        /// Extracts phone number from WhatsApp JID format
        /// </summary>
        private string ExtractPhoneNumber(string remoteJid)
        {
            if (string.IsNullOrEmpty(remoteJid))
                return null;

            // Extract phone number from formats like "1234567890@s.whatsapp.net"
            var atIndex = remoteJid.IndexOf('@');
            return atIndex > 0 ? remoteJid.Substring(0, atIndex) : remoteJid;
        }

        /// <summary>
        /// Extracts text content from WhatsApp message
        /// </summary>
        private string ExtractMessageContent(Message message)
        {
            if (message == null)
                return null;

            // Text message
            if (!string.IsNullOrEmpty(message.Conversation))
                return message.Conversation;

            // Extended text message
            if (message.ExtendedTextMessage?.Text != null)
                return message.ExtendedTextMessage.Text;

            // Image with caption
            if (message.ImageMessage?.Caption != null)
                return message.ImageMessage.Caption;

            // Video with caption
            if (message.VideoMessage?.Caption != null)
                return message.VideoMessage.Caption;

            // Document with caption
            if (message.DocumentMessage?.Caption != null)
                return message.DocumentMessage.Caption;

            // For media messages without captions, return a descriptive message
            if (message.ImageMessage != null)
                return "[Image]";

            if (message.VideoMessage != null)
                return "[Video]";

            if (message.AudioMessage != null)
                return "[Audio]";

            if (message.DocumentMessage != null)
                return $"[Document: {message.DocumentMessage.Title ?? "Unknown"}]";

            if (message.LocationMessage != null)
                return "[Location]";

            if (message.ContactMessage != null)
                return $"[Contact: {message.ContactMessage.DisplayName ?? "Unknown"}]";

            return "[Unknown message type]";
        }

        /// <summary>
        /// Determines the message type for CRM storage
        /// </summary>
        private string GetMessageType(Message message)
        {
            if (message == null)
                return "unknown";

            if (!string.IsNullOrEmpty(message.Conversation) || message.ExtendedTextMessage != null)
                return "text";

            if (message.ImageMessage != null)
                return "image";

            if (message.VideoMessage != null)
                return "video";

            if (message.AudioMessage != null)
                return "audio";

            if (message.DocumentMessage != null)
                return "document";

            if (message.LocationMessage != null)
                return "location";

            if (message.ContactMessage != null)
                return "contact";

            return "unknown";
        }

        /// <summary>
        /// Automatically restore sessions from existing credential files on service startup.
        /// This allows WhatsApp sessions to survive service restarts.
        /// </summary>
        private async Task RestoreExistingSessionsAsync()
        {
            try
            {
                _logger.LogInformation("Scanning for existing WhatsApp sessions to restore...");
                
                // Wait a short delay to ensure service is fully initialized
                await Task.Delay(2000);
                
                // Get the base cache directory (where session folders are stored)
                // Use fixed session storage path to match SocketConfig
                var baseCacheRoot = "/home/RubyManager/web/whatsapp.rubymanager.app/sessions";
                
                if (!Directory.Exists(baseCacheRoot))
                {
                    _logger.LogDebug($"Base cache directory does not exist: {baseCacheRoot}");
                    return;
                }
                
                _logger.LogInformation($"Scanning for sessions in directory: {baseCacheRoot}");
                
                // Find all credential files in session subdirectories
                var credentialFiles = Directory.GetFiles(baseCacheRoot, "*_creds.json", SearchOption.AllDirectories);
                
                if (credentialFiles.Length == 0)
                {
                    _logger.LogInformation("No existing WhatsApp sessions found to restore");
                    return;
                }
                
                _logger.LogInformation($"Found {credentialFiles.Length} existing WhatsApp sessions to restore");
                
                // Log all found credential files for debugging
                foreach (var file in credentialFiles)
                {
                    _logger.LogDebug($"Found credential file: {file}");
                }
                
                // Restore each session found
                var restorationTasks = new List<Task>();
                
                foreach (var credFile in credentialFiles)
                {
                    try
                    {
                        // Extract session name from filename: "sessionName_creds.json"
                        var fileName = Path.GetFileNameWithoutExtension(credFile);
                        if (fileName.EndsWith("_creds"))
                        {
                            var sessionName = fileName.Substring(0, fileName.Length - "_creds".Length);
                            
                            // Also try to get session name from parent directory (more reliable)
                            var parentDirName = Path.GetFileName(Path.GetDirectoryName(credFile));
                            if (!string.IsNullOrEmpty(parentDirName) && parentDirName == sessionName)
                            {
                                // This confirms the session name matches the directory structure
                                _logger.LogDebug($"Found session credential file: {credFile} for session: {sessionName}");
                            }
                            
                            if (!string.IsNullOrEmpty(sessionName))
                            {
                                _logger.LogInformation($"Restoring WhatsApp session: {sessionName}");
                                
                                // Restore session asynchronously with delay to avoid overwhelming
                                var sessionRestorationTask = Task.Run(async () =>
                                {
                                    try
                                    {
                                        // Add small delay between session restorations to prevent rate limiting
                                        var delayMs = Random.Shared.Next(500, 2000);
                                        _logger.LogDebug($"Waiting {delayMs}ms before restoring session: {sessionName}");
                                        await Task.Delay(delayMs);
                                        
                                        _logger.LogInformation($"Starting restoration for session: {sessionName}");
                                        await StartSessionAsync(sessionName, CancellationToken.None);
                                        _logger.LogInformation($"Successfully restored session: {sessionName}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, $"Failed to restore session {sessionName}: {ex.Message}");
                                    }
                                });
                                
                                restorationTasks.Add(sessionRestorationTask);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error processing credential file {credFile}: {ex.Message}");
                    }
                }
                
                // Wait for all restoration attempts to complete (with timeout)
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
                var restorationTask = Task.WhenAll(restorationTasks);
                
                var completedTask = await Task.WhenAny(restorationTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("Session restoration timed out after 5 minutes");
                }
                else
                {
                    _logger.LogInformation($"Session restoration completed. Active sessions: {_sessions.Count}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session restoration on startup");
            }
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

        /// <summary>
        /// Finds credentials file in current or legacy locations, migrating if necessary
        /// </summary>
        private string FindOrMigrateCredentialsFile(string sessionName, string newCacheRoot)
        {
            var primaryCredsFile = Path.Join(newCacheRoot, $"{sessionName}_creds.json");
            
            // If file already exists in new location, use it
            if (File.Exists(primaryCredsFile))
            {
                _logger.LogDebug($"Using existing credentials from new location: {primaryCredsFile}");
                return primaryCredsFile;
            }

            // Search for credentials in potential legacy locations
            var assemblyRoot = Path.GetDirectoryName(typeof(BaileysCSharp.Core.BaseSocket).Assembly.Location);
            var legacyLocations = new[]
            {
                Path.Join(assemblyRoot, sessionName, $"{sessionName}_creds.json"),
                Path.Join("/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp", sessionName, $"{sessionName}_creds.json"),
                Path.Join(AppContext.BaseDirectory, sessionName, $"{sessionName}_creds.json"),
                // Check if files exist in current working directory structure
                Path.Join(Environment.CurrentDirectory, sessionName, $"{sessionName}_creds.json")
            };

            foreach (var legacyPath in legacyLocations)
            {
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        _logger.LogInformation($"Found credentials in legacy location: {legacyPath}");
                        _logger.LogInformation($"Migrating credentials to new location: {primaryCredsFile}");
                        
                        // Ensure new directory exists
                        Directory.CreateDirectory(Path.GetDirectoryName(primaryCredsFile));
                        
                        // Copy credential file
                        File.Copy(legacyPath, primaryCredsFile, overwrite: true);
                        
                        // Try to migrate entire session directory if possible
                        var legacySessionDir = Path.GetDirectoryName(legacyPath);
                        var newSessionDir = Path.GetDirectoryName(primaryCredsFile);
                        
                        if (legacySessionDir != newSessionDir && Directory.Exists(legacySessionDir))
                        {
                            foreach (var file in Directory.GetFiles(legacySessionDir))
                            {
                                var fileName = Path.GetFileName(file);
                                var destFile = Path.Join(newSessionDir, fileName);
                                
                                if (!File.Exists(destFile))
                                {
                                    File.Copy(file, destFile);
                                    _logger.LogDebug($"Migrated session file: {fileName}");
                                }
                            }
                            
                            // Migrate subdirectories (keys, state, etc.)
                            foreach (var dir in Directory.GetDirectories(legacySessionDir))
                            {
                                var dirName = Path.GetFileName(dir);
                                var destDir = Path.Join(newSessionDir, dirName);
                                
                                if (!Directory.Exists(destDir))
                                {
                                    CopyDirectory(dir, destDir);
                                    _logger.LogDebug($"Migrated session directory: {dirName}");
                                }
                            }
                        }
                        
                        _logger.LogInformation($"Successfully migrated session {sessionName} from {legacyPath} to {primaryCredsFile}");
                        return primaryCredsFile;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to migrate credentials from {legacyPath} to {primaryCredsFile}");
                        // Continue trying other locations
                    }
                }
            }

            _logger.LogInformation($"No existing credentials found for session {sessionName}, will create new ones");
            return primaryCredsFile; // Return the target path for new credentials
        }

        /// <summary>
        /// Recursively copy directory contents
        /// </summary>
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var destFile = Path.Join(destDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }
            
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var destSubDir = Path.Join(destDir, dirName);
                CopyDirectory(dir, destSubDir);
            }
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

