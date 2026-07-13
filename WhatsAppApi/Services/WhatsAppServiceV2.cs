using System;
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
using WhatsAppApi.Helper;
using Microsoft.Extensions.Configuration;
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
        private readonly int _qrSessionTimeoutMinutes;
        private bool _disposed = false;

        public WhatsAppServiceV2(ILogger<WhatsAppServiceV2> logger, HttpClient httpClient, IConfiguration configuration)
        {
            _logger = logger;
            _httpClient = httpClient;
            _configuration = configuration;
            _sessions = new ConcurrentDictionary<string, SessionData>();
            
            // Read QR session timeout from configuration (default: 10 minutes)
            _qrSessionTimeoutMinutes = _configuration.GetValue<int>("WhatsAppSettings:QRSessionTimeoutMinutes", 10);

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

            var sessionData = new SessionData()
            {
                MaxQRSessionDuration = TimeSpan.FromMinutes(_qrSessionTimeoutMinutes)
            };
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
            _logger.LogDebug($"Attaching event handlers for session {sessionName}");
            socket.EV.Auth.Update += (sender, creds) => Auth_Update(sender, creds, sessionName);
            socket.EV.Connection.Update += (sender, state) => Connection_UpdateAsync(sender, state, sessionName);
            socket.EV.Message.Upsert += (sender, e) => Message_Upsert(sender, e, sessionName);
            socket.EV.MessageHistory.Set += MessageHistory_Set;
            socket.EV.Pressence.Update += Pressence_Update;
            _logger.LogDebug($"Event handlers attached successfully for session {sessionName}");

            // Subscribe to phone number extraction events from MessageDecoder
            // This allows us to cache caller_pn and sender_pn for later use
            BaileysCSharp.Core.MessageDecoder.OnCallerPhoneNumberExtracted += (messageId, phoneNumber) =>
            {
                if (_sessions.TryGetValue(sessionName, out var sd))
                {
                    if (messageId.EndsWith(":sender"))
                    {
                        sd.SenderPhoneNumberCache.TryAdd(messageId.Replace(":sender", ""), phoneNumber);
                        _logger.LogInformation($"[CALLER_PN_CACHE] Cached sender phone number - MsgId: {messageId}, PN: {phoneNumber}");
                    }
                    else
                    {
                        sd.CallerPhoneNumberCache.TryAdd(messageId, phoneNumber);
                        _logger.LogInformation($"[CALLER_PN_CACHE] Cached caller phone number - MsgId: {messageId}, PN: {phoneNumber}");
                    }
                }
            };

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
            _logger.LogInformation($"Auth_Update called for session {sessionName} - updating credentials");
            
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                var cacheRoot = sessionData.Config.CacheRoot;
                var credsFile = Path.Join(cacheRoot, $"{sessionName}_creds.json");
                var json = AuthenticationCreds.Serialize(e);
                File.WriteAllText(credsFile, json);
                
                _logger.LogInformation($"Successfully updated credentials for session {sessionName} at {credsFile}");
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
                    // Initialize QR session start time on first QR generation
                    if (sessionData.QRSessionStartTime == DateTime.MinValue)
                    {
                        sessionData.QRSessionStartTime = DateTime.UtcNow;
                        _logger.LogInformation($"Starting QR session for {sessionName}, will timeout after {sessionData.MaxQRSessionDuration.TotalMinutes} minutes");
                    }

                    // Check if QR session has exceeded maximum duration
                    var qrSessionDuration = DateTime.UtcNow - sessionData.QRSessionStartTime;
                    if (qrSessionDuration > sessionData.MaxQRSessionDuration)
                    {
                        _logger.LogWarning($"QR session timeout reached for {sessionName} after {qrSessionDuration.TotalMinutes:F1} minutes, stopping QR generation");
                        sessionData.QRCode = null;
                        sessionData.IsConnected = false;
                        
                        // Stop the session to prevent further QR generation
                        _ = Task.Run(async () => await StopSessionAsync(sessionName, CancellationToken.None));
                        return;
                    }

                    _logger.LogInformation($"Generating QR code for session {sessionName} (session time: {qrSessionDuration.TotalMinutes:F1}min/{sessionData.MaxQRSessionDuration.TotalMinutes}min)");
                    sessionData.RawQrData = connection.QR; // store raw string for visual rendering on dashboard
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
                    sessionData.LastDisconnectionTime = DateTime.UtcNow;
                    
                    // Extract disconnect reason for enhanced reconnection logic
                    if (connection.LastDisconnect.Error is Boom boomError && boomError.Data?.StatusCode != null)
                    {
                        sessionData.LastDisconnectReason = (DisconnectReason)boomError.Data.StatusCode;
                        _logger.LogInformation($"Session {sessionName} disconnected with reason: {sessionData.LastDisconnectReason} ({boomError.Data.StatusCode})");
                        
                        // Enhanced reconnection with disconnect reason awareness (only if not logged out)
                        if (boomError.Data.StatusCode != (int)DisconnectReason.LoggedOut)
                        {
                            _logger.LogInformation($"Initiating reconnection for session {sessionName} due to {sessionData.LastDisconnectReason}");
                            await ScheduleReconnectionAsync(sessionName, sessionData);
                        }
                        else
                        {
                            _logger.LogWarning($"Session {sessionName} is logged out, will not auto-reconnect");
                            Console.WriteLine($"Session {sessionName} is logged out");
                            sessionData.LastDisconnectReason = DisconnectReason.LoggedOut;
                        }
                    }
                    else
                    {
                        sessionData.LastDisconnectReason = DisconnectReason.None;
                    }
                }
                else if (connection.Connection == WAConnectionState.Open)
                {
                    _logger.LogInformation($"Session {sessionName} connected successfully");
                    // Clear the QR code when connection is established
                    sessionData.QRCode = null;
                    sessionData.IsConnected = true;
                    // Fix 1: reset attempt counter so a post-pairing Close starts from 0
                    sessionData.ReconnectAttempts = 0;
                    // Fix 2: reset QR timer so if reconnection needs a new QR the 10-min
                    // window starts fresh (without this a QR scanned at t=8.5min would
                    // cause the next MakeSocket() QR to time-out immediately)
                    sessionData.QRSessionStartTime = DateTime.MinValue;
                    // Fix 3: cancel any sleeping reconnect chain so the pairing-disconnect
                    // Close event that follows a QR scan can acquire the lock and start a
                    // fresh chain — otherwise it gets dropped and the stale chain retries
                    // with wrong context until it exhausts attempts
                    sessionData.ReconnectCts.Cancel();
                    sessionData.ReconnectCts = new CancellationTokenSource();
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

                        // Log incoming message details for debugging phone number transformations
                        _logger.LogInformation($"[PHONE_NUMBER_TRACE] Incoming message - Session: {sessionName}, RemoteJid: {msg.Key?.RemoteJid}, FromMe: {msg.Key?.FromMe}, MessageId: {msg.Key?.Id}");

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
                // Add connection health check before sending
                if (!sessionData.IsConnected)
                {
                    _logger.LogWarning($"Session {sessionName} is not connected, cannot send message");
                    throw new Exception($"Session {sessionName} is not connected.");
                }

                // Detect format: LID or Phone
                bool isLidFormat = !string.IsNullOrEmpty(remoteJid) && remoteJid.Contains("@lid");
                string formatType = isLidFormat ? "LID" : "PHONE";
                string finalRemoteJid = remoteJid;

                _logger.LogInformation($"[FORMAT_DETECTION] Attempting to send message to {remoteJid} - Format: {formatType} via session {sessionName}");

                // ===== NEW: Try to resolve @lid to real phone number using cached caller_pn =====
                if (isLidFormat)
                {
                    var lidMatches = sessionData.CallerPhoneNumberCache.Where(kvp => kvp.Value != null).ToList();
                    _logger.LogInformation($"[LID_RESOLUTION] Checking {lidMatches.Count} cached phone numbers for @lid reply target");

                    // Look for a recent message from this LID in the cache
                    foreach (var cachedEntry in lidMatches.OrderByDescending(x => x.Key).Take(5))
                    {
                        _logger.LogDebug($"[LID_RESOLUTION] Available cached PN for potential match: MsgId={cachedEntry.Key}, PN={cachedEntry.Value}");
                    }
                }
                // ===== END: LID resolution attempt =====

                try
                {
                    // Add timeout to prevent indefinite hanging
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                    await sessionData.Socket.SendMessage(finalRemoteJid, new TextMessageContent()
                    {
                        Text = message
                    }).WaitAsync(cts.Token);

                    _logger.LogInformation($"[FORMAT_DETECTION] Message sent successfully to {finalRemoteJid} (Format: {formatType}) via session {sessionName}");

                    // Update LastActivity
                    sessionData.LastActivity = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    // If sending to LID format fails, try to resolve to phone format
                    if (isLidFormat)
                    {
                        _logger.LogWarning($"[FORMAT_FALLBACK] Failed to send to LID format {remoteJid}: {ex.Message}. Attempting fallback...");

                        // Try to resolve LID to phone number for fallback
                        var resolvedPhone = ResolveLidToPhoneNumber(sessionData, remoteJid);
                        if (!string.IsNullOrEmpty(resolvedPhone))
                        {
                            _logger.LogInformation($"[FORMAT_FALLBACK] Resolved LID {remoteJid} to phone {resolvedPhone}, retrying send...");

                            try
                            {
                                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                string phoneJid = JidUtils.JidEncode(resolvedPhone, "s.whatsapp.net");

                                await sessionData.Socket.SendMessage(phoneJid, new TextMessageContent()
                                {
                                    Text = message
                                }).WaitAsync(cts2.Token);

                                _logger.LogInformation($"[FORMAT_FALLBACK] Message sent successfully using phone format: {phoneJid}");
                                sessionData.LastActivity = DateTime.UtcNow;
                                return; // Success with fallback
                            }
                            catch (Exception fallbackEx)
                            {
                                _logger.LogError(fallbackEx, $"[FORMAT_FALLBACK] Also failed with phone format: {fallbackEx.Message}");
                            }
                        }
                    }

                    _logger.LogError(ex, $"Failed to send message to {remoteJid} (Format: {formatType}) via session {sessionName}: {ex.Message}");

                    // Mark session as disconnected if send fails
                    sessionData.IsConnected = false;
                    throw;
                }
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

            // Add connection health check before sending
            if (!sessionData.IsConnected)
            {
                _logger.LogWarning($"Session {sessionName} is not connected, cannot send media");
                throw new Exception($"Session {sessionName} is not connected.");
            }

            // Detect format: LID or Phone
            bool isLidFormat = !string.IsNullOrEmpty(remoteJid) && remoteJid.Contains("@lid");
            string formatType = isLidFormat ? "LID" : "PHONE";

            var length = mediaBytes?.Length ?? 0;
            _logger.LogInformation($"[FORMAT_DETECTION] Attempting to send media to {remoteJid} - Format: {formatType} via session {sessionName}, size: {length} bytes, type: {mimeType}");

            // now hand off to Baileys
            using var ms = new MemoryStream(mediaBytes);
            try
            {
                // Add timeout to prevent indefinite hanging
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await sessionData.Socket.SendMessage(
                    remoteJid,
                    new ImageMessageContent
                    {
                        Image = ms,
                        Caption = caption
                    }
                ).WaitAsync(cts.Token);

                _logger.LogInformation($"[FORMAT_DETECTION] Media sent successfully to {remoteJid} (Format: {formatType}) via session {sessionName}");
                sessionData.LastActivity = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // If sending to LID format fails, try to resolve to phone format
                if (isLidFormat)
                {
                    _logger.LogWarning($"[FORMAT_FALLBACK] Failed to send media to LID format {remoteJid}: {ex.Message}. Attempting fallback...");

                    // Try to resolve LID to phone number for fallback
                    var resolvedPhone = ResolveLidToPhoneNumber(sessionData, remoteJid);
                    if (!string.IsNullOrEmpty(resolvedPhone))
                    {
                        _logger.LogInformation($"[FORMAT_FALLBACK] Resolved LID {remoteJid} to phone {resolvedPhone}, retrying media send...");

                        try
                        {
                            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            string phoneJid = JidUtils.JidEncode(resolvedPhone, "s.whatsapp.net");
                            using var ms2 = new MemoryStream(mediaBytes);

                            await sessionData.Socket.SendMessage(
                                phoneJid,
                                new ImageMessageContent
                                {
                                    Image = ms2,
                                    Caption = caption
                                }
                            ).WaitAsync(cts2.Token);

                            _logger.LogInformation($"[FORMAT_FALLBACK] Media sent successfully using phone format: {phoneJid}");
                            sessionData.LastActivity = DateTime.UtcNow;
                            return; // Success with fallback
                        }
                        catch (Exception fallbackEx)
                        {
                            _logger.LogError(fallbackEx, $"[FORMAT_FALLBACK] Also failed with phone format: {fallbackEx.Message}");
                        }
                    }
                }

                _logger.LogError(ex, $"Failed to send media to {remoteJid} (Format: {formatType}) via session {sessionName}: {ex.Message}");

                // Mark session as disconnected if send fails
                sessionData.IsConnected = false;
                throw;
            }
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
                var remoteJid = messageInfo.Key?.RemoteJid;
                var messageId = messageInfo.Key?.Id;
                _logger.LogInformation($"[PHONE_NUMBER_TRACE] SaveMessageToCrmAsync - Raw RemoteJid from WhatsApp: {remoteJid}");

                // ===== NEW: Try to get cached caller phone number first =====
                var senderPhone = ExtractPhoneNumber(remoteJid);
                if (_sessions.TryGetValue(sessionName, out var sessionData) && sessionData.CallerPhoneNumberCache.TryRemove(messageId, out var cachedCallerPn))
                {
                    senderPhone = cachedCallerPn;
                    _logger.LogInformation($"[CALLER_PN_RESOLUTION] SUCCESS - Used cached caller_pn from message attributes for MsgId: {messageId}, PN: {senderPhone}");
                }
                else if (remoteJid?.EndsWith("@lid") == true)
                {
                    _logger.LogWarning($"[CALLER_PN_RESOLUTION] WARNING - @lid message but no cached caller_pn found for MsgId: {messageId}, falling back to LID extraction");
                }
                // ===== END: Caller phone number resolution =====

                var messageContent = ExtractMessageContent(messageInfo.Message);
                var messageType = GetMessageType(messageInfo.Message);
                var timestamp = messageInfo.MessageTimestamp > 0 ? (long)messageInfo.MessageTimestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var receivedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToString("yyyy-MM-ddTHH:mm:ssZ");

                _logger.LogInformation($"[PHONE_NUMBER_TRACE] Message details - Extracted Phone: {senderPhone}, MessageType: {messageType}, MessageId: {messageId}");

                // ============================================
                // DIAGNOSTIC LOGGING: Extract ALL available contact info from message
                // ============================================
                var isLidFormat = remoteJid?.EndsWith("@lid") ?? false;
                var pushName = messageInfo.PushName ?? "NOT_PROVIDED";
                var verifiedBizName = messageInfo.VerifiedBizName ?? "NOT_PROVIDED";
                var participant = messageInfo.Participant ?? "NOT_PROVIDED";
                var botInvokerJid = messageInfo.BotMessageInvokerJid ?? "NOT_PROVIDED";

                // Log comprehensive message contact data
                var contentPreview = (string.IsNullOrEmpty(messageContent) ? "EMPTY" : messageContent.Substring(0, Math.Min(80, messageContent.Length)));
                _logger.LogInformation($"[CALLER_PN_DIAGNOSTIC] isLid={isLidFormat}, remoteJid={remoteJid}, senderPhone={senderPhone}, pushName={pushName}, messageId={messageId}, content={contentPreview}");

                // Skip if no content to save
                if (string.IsNullOrEmpty(messageContent) || string.IsNullOrEmpty(senderPhone))
                {
                    _logger.LogDebug($"Skipping message save - missing content or sender for session {sessionName}");
                    return;
                }

                // Extract contact name for database and bot
                var contactName = messageInfo.PushName ?? "Unknown Contact";

                // Prepare payload for CRM API and Bot Webhook
                // This same payload is sent to both systems
                var payload = new
                {
                    clientExternalId = sessionName, // Using session name as tenant ID
                    senderPhone = senderPhone,
                    contactName = contactName,      // Contact display name (new field)
                    messageContent = messageContent,
                    messageType = messageType,
                    remoteJid = remoteJid,
                    replyTarget = remoteJid, // IMPORTANT: Use this exact value when sending message back via API
                    messageId = messageId,
                    receivedAt = receivedAt
                };

                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                // Log the payload with contact name included
                _logger.LogInformation($"[CONTACT_NAME] Including contact information - Phone: {senderPhone}, Name: {contactName}, MessageId: {messageId}");
                _logger.LogInformation($"[PHONE_NUMBER_TRACE] CRM Payload - Full JSON: {jsonPayload}");
                _logger.LogDebug($"Sending message to CRM for session {sessionName}: {senderPhone} ({contactName}) - {messageContent.Substring(0, Math.Min(50, messageContent.Length))}...");

                // Send to CRM API with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var crmBaseUrl = _configuration["CrmEndpoint:BaseUrl"] ?? "https://whatsapp.rubymanager.app";
                var crmUrl = $"{crmBaseUrl}/api/whatsappmessagehistory/saveMessage";

                _logger.LogInformation($"[PHONE_NUMBER_TRACE] CRM API - URL: {crmUrl}");

                var response = await _httpClient.PostAsync(crmUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"[PHONE_NUMBER_TRACE] CRM API SUCCESS (200) for session {sessionName}, message ID: {messageId}");
                    _logger.LogInformation($"[PHONE_NUMBER_TRACE] CRM API - Response body: {responseBody}");
                    _logger.LogDebug($"Successfully saved message to CRM for session {sessionName}, message ID: {messageId}");

                    // Log successful resolution summary for verification
                    if (isLidFormat)
                    {
                        _logger.LogInformation($"[CALLER_PN_SUCCESS] Successfully resolved @lid message - original={remoteJid}, resolved={senderPhone}, messageId={messageId}, session={sessionName}, status={response.StatusCode}");
                    }
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"[PHONE_NUMBER_TRACE] CRM API ERROR ({response.StatusCode}) for session {sessionName}");
                    _logger.LogWarning($"[PHONE_NUMBER_TRACE] CRM API - Error response: {responseBody}");
                    _logger.LogWarning($"CRM API returned {response.StatusCode} for session {sessionName}: {responseBody}");
                }

                // Call RubyManagerBot webhook asynchronously (fire-and-forget)
                // This allows the webhook to handle unknown numbers independently
                _ = Task.Run(async () => await CallRubyManagerBotWebhookAsync(sessionName, payload));
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
        /// Calls RubyManagerBot webhook with message data for processing unknown numbers
        /// This is a fire-and-forget call that doesn't block message processing
        /// </summary>
        private async Task CallRubyManagerBotWebhookAsync(string sessionName, object messagePayload)
        {
            try
            {
                // Check if webhook is enabled
                var webhookEnabled = _configuration["RubyManagerBotEndpoint:Enabled"];
                if (webhookEnabled?.ToLower() != "true")
                {
                    _logger.LogDebug($"RubyManagerBot webhook is disabled for session {sessionName}");
                    return;
                }

                var botBaseUrl = _configuration["RubyManagerBotEndpoint:BaseUrl"] ?? "http://localhost:5000";
                var botEndpoint = _configuration["RubyManagerBotEndpoint:Endpoint"] ?? "/api/bot/webhook";
                var botTimeoutSeconds = int.TryParse(_configuration["RubyManagerBotEndpoint:TimeoutSeconds"], out var timeout) ? timeout : 10;

                var webhookUrl = $"{botBaseUrl}{botEndpoint}";

                var jsonPayload = JsonSerializer.Serialize(messagePayload);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                // Log bot webhook with contact information
                var contactNameFromPayload = messagePayload.GetType().GetProperty("contactName")?.GetValue(messagePayload);
                var senderPhoneFromPayload = messagePayload.GetType().GetProperty("senderPhone")?.GetValue(messagePayload);
                _logger.LogInformation($"[CONTACT_NAME] Bot webhook receiving contact info - Phone: {senderPhoneFromPayload}, Name: {contactNameFromPayload}");
                _logger.LogInformation($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook - URL: {webhookUrl}");
                _logger.LogInformation($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Payload being sent: {jsonPayload}");
                _logger.LogInformation($"[BOT_INSTRUCTION] When sending a message back, use the 'replyTarget' field: {messagePayload.GetType().GetProperty("replyTarget")?.GetValue(messagePayload)}");

                // Call webhook with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(botTimeoutSeconds));
                var response = await _httpClient.PostAsync(webhookUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook SUCCESS (200) for session {sessionName}");
                    _logger.LogInformation($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Response body: {responseBody}");
                    _logger.LogDebug($"RubyManagerBot webhook call successful for session {sessionName}");
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook ERROR ({response.StatusCode}) for session {sessionName}");
                    _logger.LogWarning($"[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Error response: {responseBody}");
                    _logger.LogWarning($"RubyManagerBot webhook returned {response.StatusCode} for session {sessionName}: {responseBody}");
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning($"RubyManagerBot webhook call timed out for session {sessionName}");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, $"Network error calling RubyManagerBot webhook for session {sessionName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error calling RubyManagerBot webhook for session {sessionName}");
            }
        }

        /// <summary>
        /// Alerts the tenant admin (via the CRM) that this session's WhatsApp connection has been
        /// down long enough to exhaust fast reconnection attempts. Fire-and-forget, mirrors
        /// CallRubyManagerBotWebhookAsync's pattern. Called at most once per down-episode.
        /// </summary>
        private async Task NotifyConnectionDownAsync(string sessionName, SessionData sessionData)
        {
            try
            {
                var crmBaseUrl = _configuration["CrmEndpoint:BaseUrl"] ?? "https://whatsapp.rubymanager.app";
                var alertUrl = $"{crmBaseUrl}/api/whatsappconnection/connection-alert";

                var payload = new
                {
                    clientExternalId = sessionName,
                    disconnectedSince = sessionData.LastDisconnectionTime,
                    lastDisconnectReason = sessionData.LastDisconnectReason.ToString()
                };
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.PostAsync(alertUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Connection-down alert sent for session {sessionName}");
                }
                else
                {
                    _logger.LogWarning($"Connection-down alert endpoint returned {response.StatusCode} for session {sessionName}");
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning($"Connection-down alert call timed out for session {sessionName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send connection-down alert for session {sessionName}");
            }
        }

        /// <summary>
        /// Alerts the tenant admin (via the CRM) that this session can no longer self-heal and a
        /// human must scan a fresh QR code from the dashboard. Distinct from NotifyConnectionDownAsync:
        /// that one means "temporarily down, retrying"; this one means "credentials were wiped, action
        /// required". Fire-and-forget. If the CRM endpoint does not exist yet, the failure is logged
        /// but the code path is in place for when it does.
        /// </summary>
        private async Task NotifyQRScanNeededAsync(string sessionName, SessionData sessionData)
        {
            const string dashboardUrl = "https://whatsapp.rubymanager.app/status.html";
            try
            {
                var crmBaseUrl = _configuration["CrmEndpoint:BaseUrl"] ?? "https://whatsapp.rubymanager.app";
                var alertUrl = $"{crmBaseUrl}/api/whatsappconnection/qr-scan-needed";

                var payload = new
                {
                    clientExternalId = sessionName,
                    disconnectedSince = sessionData.LastDisconnectionTime,
                    dashboardUrl = dashboardUrl,
                    message = "Your WhatsApp session needs to be reconnected. Please scan the QR code at the dashboard."
                };
                var jsonPayload = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.PostAsync(alertUrl, content, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"QR-scan-needed alert sent for session {sessionName}");
                }
                else
                {
                    _logger.LogWarning($"QR-scan-needed alert endpoint returned {response.StatusCode} for session {sessionName}. Tenant must scan a fresh QR at {dashboardUrl}");
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning($"QR-scan-needed alert call timed out for session {sessionName}. Tenant must scan a fresh QR at {dashboardUrl}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to send QR-scan-needed alert for session {sessionName}. Tenant must scan a fresh QR at {dashboardUrl}");
            }
        }

        /// <summary>
        /// Extracts phone number from WhatsApp JID format
        /// </summary>
        private string ExtractPhoneNumber(string remoteJid)
        {
            if (string.IsNullOrEmpty(remoteJid))
            {
                _logger.LogDebug("[PHONE_NUMBER_TRACE] ExtractPhoneNumber - Input is null or empty");
                return null;
            }

            // Extract phone number from formats like "1234567890@s.whatsapp.net"
            var atIndex = remoteJid.IndexOf('@');
            var extractedPhone = atIndex > 0 ? remoteJid.Substring(0, atIndex) : remoteJid;
            _logger.LogInformation($"[PHONE_NUMBER_TRACE] Phone extraction - Input JID: {remoteJid} => Extracted: {extractedPhone}");
            return extractedPhone;
        }

        /// <summary>
        /// Resolves a LID (@lid) format JID back to a phone number using local contact store
        /// Returns null if contact not found
        /// </summary>
        private string ResolveLidToPhoneNumber(SessionData sessionData, string lidJid)
        {
            try
            {
                // Extract LID number from JID (e.g., "171601257582835@lid" → "171601257582835")
                var lidNumber = ExtractPhoneNumber(lidJid);

                // Get all contacts from socket
                var allContacts = sessionData.Socket.GetAllContact();

                // Find contact with matching LID property
                var contact = allContacts.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.LID) && c.LID == lidNumber);

                if (contact != null && !string.IsNullOrEmpty(contact.PhoneNumber))
                {
                    _logger.LogInformation($"[LID_RESOLUTION] Found contact: LID={lidNumber}, Phone={contact.PhoneNumber}");
                    return contact.PhoneNumber;
                }

                // Fallback: try to find by checking contact ID
                var matchedContact = allContacts.FirstOrDefault(c =>
                    JidUtils.JidDecode(c.ID)?.User == lidNumber);

                if (matchedContact != null)
                {
                    _logger.LogInformation($"[LID_RESOLUTION] Matched by decoded JID");
                    return matchedContact.PhoneNumber;
                }

                _logger.LogWarning($"[LID_RESOLUTION] Could not resolve LID {lidNumber} to phone number - contact may be new");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error resolving LID {lidJid} to phone number");
                return null;
            }
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
                    // 3. Expired QR sessions (prevent infinite QR generation)
                    else if (!sessionData.IsConnected && 
                             sessionData.QRSessionStartTime != DateTime.MinValue &&
                             now - sessionData.QRSessionStartTime > sessionData.MaxQRSessionDuration)
                    {
                        shouldCleanup = true;
                        reason = $"QR session expired after {(now - sessionData.QRSessionStartTime).TotalMinutes:F1} minutes";
                    }
                    // 4. Unhealthy connected sessions
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

        /// <summary>
        /// Runs the full reconnection lifecycle for a session: fast backoff attempts, then (if
        /// those are exhausted) an indefinite slow retry with a one-time tenant alert. Guarded by
        /// SessionData.ReconnectLock so at most one chain is ever in flight per session - a socket
        /// recreated mid-attempt that itself fails fast must NOT spawn a second concurrent chain
        /// (this was the cause of the multi-per-minute disconnect storms seen in production logs).
        /// </summary>
        private async Task ScheduleReconnectionAsync(string sessionName, SessionData sessionData)
        {
            if (_disposed) return;

            if (!sessionData.ReconnectLock.Wait(0))
            {
                // A chain is already running. Remember that a new Close was dropped so the
                // chain's finally block can start a fresh chain once the lock is released.
                sessionData.PendingReconnectNeeded = true;
                _logger.LogDebug($"Reconnection already in progress for {sessionName}, queuing reconnect for after current chain exits");
                return;
            }

            sessionData.PendingReconnectNeeded = false;
            // Capture the CTS that was current when we acquired the lock. If Open fires during
            // our delay it cancels this CTS and replaces sessionData.ReconnectCts with a new one,
            // so the cancellation only affects this chain.
            var cts = sessionData.ReconnectCts;

            try
            {
                // Snapshot the disconnect reason that triggered THIS chain. A socket recreated
                // mid-chain (FullSocketRecreation) can itself fail and fire a Close event whose
                // handler overwrites sessionData.LastDisconnectReason - if we re-read that field
                // each iteration the attempt cap and delay formula would silently switch reasons
                // from attempt 4 onward. Freeze both here and use them throughout the loop.
                var originalReason = sessionData.LastDisconnectReason;
                var maxAttempts = GetMaxAttemptsForDisconnectReason(originalReason);
                var fastAttemptsExhausted = false;

                while (true)
                {
                    if (_disposed || !_sessions.ContainsKey(sessionName)) return;

                    sessionData.ReconnectAttempts++;

                    if (sessionData.ReconnectAttempts > maxAttempts)
                    {
                        _logger.LogError($"Session {sessionName} exceeded maximum reconnection attempts ({maxAttempts}) with disconnect reason {originalReason}");
                        await NotifyReconnectionFailure(sessionName, sessionData);
                        fastAttemptsExhausted = true;
                        break;
                    }

                    // Enhanced backoff strategy based on attempt number
                    var delaySeconds = CalculateReconnectionDelay(sessionData.ReconnectAttempts, originalReason);
                    _logger.LogInformation($"Scheduling reconnection for {sessionName} in {delaySeconds} seconds (attempt {sessionData.ReconnectAttempts}/{maxAttempts}) - Strategy: {GetReconnectionStrategy(sessionData.ReconnectAttempts)}");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Open fired during our sleep — exit so the lock is released and
                        // the pairing-disconnect Close can start a fresh chain
                        _logger.LogInformation($"Reconnect chain cancelled for {sessionName} (Open event fired during delay)");
                        return;
                    }

                    if (_disposed || !_sessions.ContainsKey(sessionName)) return;

                    var success = false;
                    try
                    {
                        success = await ExecuteReconnectionStrategy(sessionName, sessionData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to reconnect session {sessionName} on attempt {sessionData.ReconnectAttempts}: {ex.Message}");
                    }

                    if (success)
                    {
                        _logger.LogInformation($"Successfully reconnected session {sessionName} on attempt {sessionData.ReconnectAttempts}");
                        sessionData.ReconnectAttempts = 0;
                        sessionData.AlertSent = false;
                        return;
                    }

                    _logger.LogWarning($"Reconnection attempt {sessionData.ReconnectAttempts} failed for session {sessionName}");
                }

                if (fastAttemptsExhausted)
                {
                    await SlowRetryLoopAsync(sessionName, sessionData, cts);
                }
            }
            finally
            {
                sessionData.ReconnectLock.Release();
                // If a Close event was dropped while we held the lock (PendingReconnectNeeded)
                // and we are still disconnected, start a fresh chain now that the lock is free.
                if (sessionData.PendingReconnectNeeded && !sessionData.IsConnected &&
                    !_disposed && _sessions.ContainsKey(sessionName))
                {
                    sessionData.PendingReconnectNeeded = false;
                    _ = Task.Run(() => ScheduleReconnectionAsync(sessionName, sessionData));
                }
            }
        }

        /// <summary>
        /// Runs once fast reconnection attempts are exhausted. Keeps retrying every 30 minutes,
        /// indefinitely, so the session can self-heal without a human noticing - scheduled
        /// WhatsApp/email sends depend on this service staying connected even with the CRM
        /// closed. No separate bounded cutoff is needed here: PerformHealthCheck already removes
        /// any session disconnected for more than MaxInactiveHours, which this loop's own
        /// _sessions.ContainsKey check will observe and stop on.
        /// </summary>
        private async Task SlowRetryLoopAsync(string sessionName, SessionData sessionData, CancellationTokenSource cts)
        {
            var slowRetryInterval = TimeSpan.FromMinutes(30);
            var slowRetryAttempt = 0;

            while (true)
            {
                if (_disposed || !_sessions.ContainsKey(sessionName)) return;

                try
                {
                    await Task.Delay(slowRetryInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"Slow retry cancelled for {sessionName} (Open event fired during wait)");
                    return;
                }

                if (_disposed || !_sessions.ContainsKey(sessionName)) return;

                slowRetryAttempt++;

                // Alternate strategies: a plain socket retry (SimpleSocketRetry) can never
                // recover a session whose credentials are stale, so on odd attempts do a full
                // socket recreation (which validates + rebuilds from stored creds) instead.
                var useFullRecreation = (slowRetryAttempt % 2) == 1;
                _logger.LogInformation($"Slow-retry attempt {slowRetryAttempt} for session {sessionName} using {(useFullRecreation ? "FullRecreation" : "SimpleRetry")} (down since {sessionData.LastDisconnectionTime:u})");

                var success = false;
                try
                {
                    success = useFullRecreation
                        ? await FullSocketRecreation(sessionName, sessionData)
                        : await SimpleSocketRetry(sessionData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Slow-retry reconnection failed for session {sessionName}: {ex.Message}");
                }

                if (success)
                {
                    _logger.LogInformation($"Session {sessionName} recovered via slow retry");
                    sessionData.ReconnectAttempts = 0;
                    sessionData.AlertSent = false;
                    return;
                }
            }
        }

        private int GetMaxAttemptsForDisconnectReason(DisconnectReason reason)
        {
            return reason switch
            {
                DisconnectReason.ConnectionClosed => 10, // 24-hour session expiry - more attempts
                DisconnectReason.ConnectionLost or DisconnectReason.TimedOut => 7, // Network/timeout issues - moderate attempts (attempt 7 = ForceSessionRestart, the last resort)
                DisconnectReason.BadSession => 3,        // Bad session - fewer attempts
                _ => 5                                    // Default fallback
            };
        }

        private double CalculateReconnectionDelay(int attemptNumber, DisconnectReason reason)
        {
            // Different delay strategies based on disconnect reason
            return reason switch
            {
                DisconnectReason.ConnectionClosed => // 24-hour expiry - longer delays
                    Math.Min(30 * Math.Pow(1.5, attemptNumber - 1), 1800), // 30s to 30min max
                DisconnectReason.ConnectionLost => // Network issues - moderate delays
                    Math.Min(10 * Math.Pow(2, attemptNumber - 1), 600),    // 10s to 10min max
                _ => // Default exponential backoff
                    Math.Min(Math.Pow(2, attemptNumber), 300)              // 2s to 5min max
            };
        }

        private string GetReconnectionStrategy(int attemptNumber)
        {
            return attemptNumber switch
            {
                <= 2 => "SimpleRetry",
                <= 4 => "FullRecreation",
                <= 6 => "CredentialRefresh",
                7 => "ForceSessionRestart", // last resort: wipe creds + fresh QR (previously unreachable)
                _ => "ForceSessionRestart"
            };
        }

        private async Task<bool> ExecuteReconnectionStrategy(string sessionName, SessionData sessionData)
        {
            var strategy = GetReconnectionStrategy(sessionData.ReconnectAttempts);
            
            try
            {
                return strategy switch
                {
                    "SimpleRetry" => await SimpleSocketRetry(sessionData),
                    "FullRecreation" => await FullSocketRecreation(sessionName, sessionData),
                    "CredentialRefresh" => await CredentialRefreshReconnection(sessionName, sessionData),
                    "ForceSessionRestart" => await ForceSessionRestart(sessionName, sessionData),
                    _ => await SimpleSocketRetry(sessionData)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Reconnection strategy {strategy} failed for session {sessionName}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SimpleSocketRetry(SessionData sessionData)
        {
            try
            {
                sessionData.Socket.MakeSocket();
                await Task.Delay(3000); // Wait for connection establishment
                return sessionData.IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Simple socket retry failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> FullSocketRecreation(string sessionName, SessionData sessionData)
        {
            try
            {
                _logger.LogInformation($"Performing full socket recreation for session {sessionName}");
                
                // 1. Dispose old socket properly
                if (sessionData.Socket != null)
                {
                    sessionData.Socket.CleanupSession();
                    sessionData.Socket = null;
                }

                // 2. Validate credentials before recreation
                var credentialsValid = await ValidateStoredCredentials(sessionName);
                if (!credentialsValid)
                {
                    _logger.LogWarning($"Stored credentials invalid for session {sessionName}, skipping full recreation");
                    return false;
                }

                // 3. Recreate session using existing logic
                var config = new SocketConfig() { SessionName = sessionName };
                var credsFile = FindOrMigrateCredentialsFile(sessionName, config.CacheRoot);
                
                if (File.Exists(credsFile))
                {
                    var authentication = AuthenticationCreds.Deserialize(File.ReadAllText(credsFile));
                    BaseKeyStore keys = new FileKeyStore(config.CacheRoot);
                    config.Auth = new AuthenticationState()
                    {
                        Creds = authentication,
                        Keys = keys
                    };
                    
                    var socket = new WASocket(config);
                    sessionData.Socket = socket;
                    sessionData.Config = config;
                    
                    // Setup event handlers
                    socket.EV.Connection.Update += (sender, e) => Connection_UpdateAsync(sender, e, sessionName);
                    socket.EV.Auth.Update += (sender, e) => Auth_Update(sender, e, sessionName);
                    
                    socket.MakeSocket();
                    await Task.Delay(5000); // Wait longer for full recreation
                    
                    return sessionData.IsConnected;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to recreate socket for session {sessionName}");
                return false;
            }
        }

        private async Task<bool> CredentialRefreshReconnection(string sessionName, SessionData sessionData)
        {
            try
            {
                _logger.LogInformation($"Attempting credential refresh reconnection for session {sessionName}");
                
                // Check if credentials are too old and might need refresh
                var config = new SocketConfig() { SessionName = sessionName };
                var credsFile = FindOrMigrateCredentialsFile(sessionName, config.CacheRoot);
                
                if (File.Exists(credsFile))
                {
                    var credsAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(credsFile);
                    if (credsAge > TimeSpan.FromDays(7))
                    {
                        _logger.LogWarning($"Credentials for session {sessionName} are {credsAge.Days} days old, may need manual refresh");
                    }
                }
                
                // Attempt full recreation with existing credentials
                return await FullSocketRecreation(sessionName, sessionData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Credential refresh reconnection failed for session {sessionName}");
                return false;
            }
        }

        private async Task<bool> ForceSessionRestart(string sessionName, SessionData sessionData)
        {
            try
            {
                _logger.LogWarning($"Performing force session restart for session {sessionName} - last resort attempt");

                // 1. Complete session teardown - dispose everything.
                await StopSessionAsync(sessionName, CancellationToken.None);
                await Task.Delay(2000); // Brief pause

                // 2. Back up and delete the credentials file. WhatsApp can invalidate
                //    credentials server-side after brief-connect/immediate-disconnect cycles;
                //    once that happens EVERY reconnection strategy retries with the same dead
                //    credentials and fails forever. Renaming the creds file to *.bak forces
                //    StartSessionAsync to generate a fresh QR instead of reusing dead creds.
                BackupAndDeleteCredentials(sessionName);

                // 3. Restart session - with no creds file present this will generate a fresh QR.
                await StartSessionAsync(sessionName, CancellationToken.None);

                // 4. Wait a moment and check the resulting state.
                await Task.Delay(3000);
                var connected = _sessions.ContainsKey(sessionName) && _sessions[sessionName].IsConnected;

                if (!connected)
                {
                    // We are now waiting for a human to scan a fresh QR from the dashboard.
                    // This IS a valid recovery path - notify the tenant so they know to act,
                    // and return true so the caller does NOT drop into the endless slow-retry
                    // loop (slow retry cannot help while we legitimately wait for a QR scan).
                    _logger.LogWarning($"Force restart for session {sessionName} produced a fresh QR - tenant must scan it from the dashboard");
                    _ = NotifyQRScanNeededAsync(sessionName, sessionData);
                }

                // Either connected (great) or QR-waiting (also a valid terminal state): stop retrying.
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Force session restart failed for session {sessionName}");
                return false;
            }
        }

        /// <summary>
        /// Renames the session's credentials file to {sessionName}_creds.json.bak so the next
        /// StartSessionAsync is forced to show a fresh QR rather than reusing WhatsApp-invalidated
        /// credentials. A backup (rather than a hard delete) keeps a recovery copy on disk.
        /// </summary>
        private void BackupAndDeleteCredentials(string sessionName)
        {
            try
            {
                var config = new SocketConfig() { SessionName = sessionName };
                var credsFile = FindOrMigrateCredentialsFile(sessionName, config.CacheRoot);

                if (!File.Exists(credsFile))
                {
                    _logger.LogInformation($"No credentials file to back up for session {sessionName} (fresh QR will be generated)");
                    return;
                }

                var backupFile = credsFile + ".bak";
                // Overwrite any stale previous backup so the move always succeeds.
                if (File.Exists(backupFile))
                {
                    File.Delete(backupFile);
                }

                File.Move(credsFile, backupFile);
                _logger.LogWarning($"Backed up and removed invalidated credentials for session {sessionName}: {backupFile}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to back up/delete credentials for session {sessionName}");
            }
        }

        private async Task<bool> ValidateStoredCredentials(string sessionName)
        {
            try
            {
                var config = new SocketConfig() { SessionName = sessionName };
                var credsFile = FindOrMigrateCredentialsFile(sessionName, config.CacheRoot);
                
                if (!File.Exists(credsFile))
                {
                    _logger.LogWarning($"No credentials file found for session {sessionName}");
                    return false;
                }

                var authState = AuthenticationCreds.Deserialize(File.ReadAllText(credsFile));
                
                // Basic validation checks
                if (authState?.NoiseKey == null || authState?.SignedIdentityKey == null)
                {
                    _logger.LogWarning($"Invalid or incomplete credentials for session {sessionName}");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to validate credentials for session {sessionName}");
                return false;
            }
        }

        private async Task NotifyReconnectionFailure(string sessionName, SessionData sessionData)
        {
            try
            {
                _logger.LogError($"All fast reconnection attempts failed for session {sessionName}. Switching to slow retry (every 30 min) and alerting tenant.");
                sessionData.IsConnected = false;

                if (!sessionData.AlertSent)
                {
                    sessionData.AlertSent = true;
                    _ = NotifyConnectionDownAsync(sessionName, sessionData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to notify reconnection failure for session {sessionName}");
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
            public SocketConfig Config { get; set; }
            public List<WebMessageInfo> Messages { get; set; }
            public string QRCode { get; set; }
            public string RawQrData { get; set; } // raw WhatsApp QR string for visual rendering
            public bool IsConnected { get; set; }
            public DateTime LastActivity { get; set; } = DateTime.UtcNow;
            public int ReconnectAttempts { get; set; } = 0;
            public DateTime QRSessionStartTime { get; set; } = DateTime.MinValue;
            public TimeSpan MaxQRSessionDuration { get; set; } = TimeSpan.FromMinutes(10);

            // NEW: Track disconnection details for enhanced reconnection
            public DisconnectReason LastDisconnectReason { get; set; } = DisconnectReason.None;
            public DateTime LastDisconnectionTime { get; set; } = DateTime.MinValue;

            /// <summary>
            /// Ensures at most one reconnection chain runs per session at a time. A socket
            /// recreated mid-reconnection can itself fail and raise its own Close event -
            /// without this guard that spawns a second, concurrent reconnection chain racing
            /// on ReconnectAttempts (this was the cause of the multi-per-minute disconnect storms).
            /// </summary>
            public SemaphoreSlim ReconnectLock { get; } = new SemaphoreSlim(1, 1);

            /// <summary>
            /// Cancellation source for the currently-running reconnect chain.
            /// Cancelled (and replaced) when WAConnectionState.Open fires, so a sleeping
            /// chain wakes immediately, releases the lock, and lets the pairing-disconnect
            /// Close start a fresh chain rather than being silently dropped.
            /// </summary>
            public CancellationTokenSource ReconnectCts { get; set; } = new CancellationTokenSource();

            /// <summary>
            /// Set to true when a Close event is dropped because ReconnectLock is held.
            /// Checked in ScheduleReconnectionAsync's finally block: if true and still
            /// disconnected, a new chain is kicked off as soon as the lock is released.
            /// </summary>
            public volatile bool PendingReconnectNeeded = false;

            /// <summary>
            /// Whether a "connection down" alert has already been sent to the tenant for the
            /// current down-episode. Reset to false on successful reconnect.
            /// </summary>
            public bool AlertSent { get; set; } = false;

            /// <summary>
            /// Cache for extracted caller phone numbers from messages
            /// Maps messageId -> CallerPhoneNumber (extracted from caller_pn attribute)
            /// Used to resolve @lid format JIDs to actual phone numbers for replies
            /// </summary>
            public ConcurrentDictionary<string, string> CallerPhoneNumberCache { get; set; } = new();

            /// <summary>
            /// Cache for sender phone numbers shared via SharePhoneNumber protocol
            /// Maps messageId -> SenderPhoneNumber
            /// </summary>
            public ConcurrentDictionary<string, string> SenderPhoneNumberCache { get; set; } = new();
        }
    }
}

