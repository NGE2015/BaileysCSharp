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

namespace WhatsAppApi.Services
{
    public interface IWhatsAppServiceV2
    {
        Task StartSessionAsync(string sessionName, CancellationToken cancellationToken);
        Task StopSessionAsync(string sessionName, CancellationToken cancellationToken);
        Task SendMessage(string sessionName, string remoteJid, string message);
        string GetQRCode(string sessionName);
        string GetAsciiQRCode(string sessionName);
        bool IsConnected(string sessionName);
        IEnumerable<string> GetActiveSessions();
        bool TryGetSessionData(string sessionName, out SessionData sessionData);
    }

    public class WhatsAppServiceV2 : IWhatsAppServiceV2
    {
        private readonly ILogger<WhatsAppServiceV2> _logger;
        private readonly ConcurrentDictionary<string, SessionData> _sessions;

        public WhatsAppServiceV2(ILogger<WhatsAppServiceV2> logger)
        {
            _logger = logger;
            _sessions = new ConcurrentDictionary<string, SessionData>();
        }

        public async Task StartSessionAsync(string sessionName, CancellationToken cancellationToken)
        {
            if (_sessions.ContainsKey(sessionName))
            {
                _logger.LogWarning($"Session {sessionName} already exists.");
                return;
            }

            var sessionData = new SessionData();
            var config = new SocketConfig()
            {
                SessionName = sessionName,
            };

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
            socket.EV.Connection.Update += (sender, state) => Connection_Update(sender, state, sessionName);
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
            if (_sessions.TryRemove(sessionName, out var sessionData))
            {
                // Implement any necessary shutdown logic here
                //sessionData.Socket?.Dispose();
                _logger.LogInformation($"Session {sessionName} stopped.");
            }
            else
            {
                _logger.LogWarning($"Session {sessionName} not found.");
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

        private void Connection_Update(object? sender, ConnectionState e, string sessionName)
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
                        try
                        {
                            Thread.Sleep(1000);
                            sessionData.Socket.MakeSocket();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error reconnecting session {sessionName}");
                        }
                    }
                    else
                    {
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
            if (_sessions.TryGetValue(sessionName, out var sessionData))
            {
                return sessionData.QRCode;
            }
            return null;
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

        public class SessionData
        {
            public WASocket Socket { get; set; }
            public SocketConfig Config { get; set; } // Added Config property
            public List<WebMessageInfo> Messages { get; set; }
            public string QRCode { get; set; }
            public bool IsConnected { get; set; }
            public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        }
    }
}


//WORKING !!!!!!!!
//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.IO;
//using System.Threading;
//using System.Threading.Tasks;
//using BaileysCSharp.Core.Events;
//using BaileysCSharp.Core.Logging;
//using BaileysCSharp.Core.Models;
//using BaileysCSharp.Core.Models.Sending.NonMedia;
//using BaileysCSharp.Core.NoSQL;
//using BaileysCSharp.Core.Sockets;
//using BaileysCSharp.Core.Types;
//using BaileysCSharp.Core.Utils;
//using BaileysCSharp.Exceptions;
//using Microsoft.Extensions.Logging;
//using Proto;
//using QRCoder;
//using System.Text.Json;
//using BaileysCSharp.Core.Helper;
//using static WhatsAppApi.Services.WhatsAppServiceV2;

//namespace WhatsAppApi.Services
//{
//    public interface IWhatsAppServiceV2
//    {
//        Task StartSessionAsync(string sessionName, CancellationToken cancellationToken);
//        Task StopSessionAsync(string sessionName, CancellationToken cancellationToken);
//        Task SendMessage(string sessionName, string remoteJid, string message);
//        string GetQRCode(string sessionName);
//        string GetAsciiQRCode(string sessionName);
//        bool IsConnected(string sessionName);
//        IEnumerable<string> GetActiveSessions();
//        bool TryGetSessionData(string sessionName, out SessionData sessionData);
//    }

//    public class WhatsAppServiceV2 : IWhatsAppServiceV2
//    {
//        private readonly ILogger<WhatsAppServiceV2> _logger;
//        private readonly ConcurrentDictionary<string, SessionData> _sessions;

//        public WhatsAppServiceV2(ILogger<WhatsAppServiceV2> logger)
//        {
//            _logger = logger;
//            _sessions = new ConcurrentDictionary<string, SessionData>();
//        }

//        public async Task StartSessionAsync(string sessionName, CancellationToken cancellationToken)
//        {
//            if (_sessions.ContainsKey(sessionName))
//            {
//                _logger.LogWarning($"Session {sessionName} already exists.");
//                return;
//            }

//            var sessionData = new SessionData();
//            var config = new SocketConfig()
//            {
//                SessionName = sessionName,
//            };

//            var credsFile = Path.Join(config.CacheRoot, $"{sessionName}_creds.json");
//            AuthenticationCreds? authentication = null;
//            if (File.Exists(credsFile))
//            {
//                authentication = AuthenticationCreds.Deserialize(File.ReadAllText(credsFile));
//            }
//            authentication = authentication ?? AuthenticationUtils.InitAuthCreds();

//            BaseKeyStore keys = new FileKeyStore(config.CacheRoot);

//            config.Logger.Level = BaileysCSharp.Core.Logging.LogLevel.Raw;
//            config.Auth = new AuthenticationState()
//            {
//                Creds = authentication,
//                Keys = keys
//            };

//            var socket = new WASocket(config);

//            // Attach event handlers
//            socket.EV.Auth.Update += (sender, creds) => Auth_Update(sender, creds, sessionName);
//            socket.EV.Connection.Update += (sender, state) => Connection_Update(sender, state, sessionName);
//            socket.EV.Message.Upsert += (sender, e) => Message_Upsert(sender, e, sessionName);
//            socket.EV.MessageHistory.Set += MessageHistory_Set;
//            socket.EV.Pressence.Update += Pressence_Update;

//            socket.MakeSocket();

//            sessionData.Socket = socket;
//            sessionData.Config = config; // Store config in sessionData
//            sessionData.Messages = new List<WebMessageInfo>();
//            sessionData.LastActivity = DateTime.UtcNow; // Initialize LastActivity

//            _sessions.TryAdd(sessionName, sessionData);
//        }

//        public async Task StopSessionAsync(string sessionName, CancellationToken cancellationToken)
//        {
//            if (_sessions.TryRemove(sessionName, out var sessionData))
//            {
//                // Implement any necessary shutdown logic here
//                //sessionData.Socket?.Dispose();
//                _logger.LogInformation($"Session {sessionName} stopped.");
//            }
//            else
//            {
//                _logger.LogWarning($"Session {sessionName} not found.");
//            }
//        }

//        private void Auth_Update(object? sender, AuthenticationCreds e, string sessionName)
//        {
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                var cacheRoot = sessionData.Config.CacheRoot;
//                var credsFile = Path.Join(cacheRoot, $"{sessionName}_creds.json");
//                var json = AuthenticationCreds.Serialize(e);
//                File.WriteAllText(credsFile, json);
//            }
//            else
//            {
//                _logger.LogWarning($"Session {sessionName} not found in Auth_Update.");
//            }
//        }

//        private void Connection_Update(object? sender, ConnectionState e, string sessionName)
//        {
//            var connection = e;
//            _logger.LogDebug(JsonSerializer.Serialize(connection));
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                if (connection.QR != null)
//                {
//                    QRCodeGenerator QrGenerator = new QRCodeGenerator();
//                    QRCodeData QrCodeInfo = QrGenerator.CreateQrCode(connection.QR, QRCodeGenerator.ECCLevel.L);
//                    AsciiQRCode qrCode = new AsciiQRCode(QrCodeInfo);
//                    var data = qrCode.GetGraphic(1);
//                    Console.WriteLine(data);
//                    sessionData.QRCode = data;
//                }
//                if (connection.Connection == WAConnectionState.Close)
//                {
//                    sessionData.IsConnected = false;
//                    if (connection.LastDisconnect.Error is Boom boom && boom.Data?.StatusCode != (int)DisconnectReason.LoggedOut)
//                    {
//                        try
//                        {
//                            Thread.Sleep(1000);
//                            sessionData.Socket.MakeSocket();
//                        }
//                        catch (Exception ex)
//                        {
//                            _logger.LogError(ex, $"Error reconnecting session {sessionName}");
//                        }
//                    }
//                    else
//                    {
//                        Console.WriteLine($"Session {sessionName} is logged out");
//                    }
//                }
//                else if (connection.Connection == WAConnectionState.Open)
//                {
//                    // Clear the QR code when connection is established
//                    sessionData.QRCode = null;
//                    sessionData.IsConnected = true;
//                }

//                // Update LastActivity
//                sessionData.LastActivity = DateTime.UtcNow;
//            }
//        }

//        private void Message_Upsert(object? sender, MessageEventModel e, string sessionName)
//        {
//            if (e.Type == MessageEventType.Notify)
//            {
//                if (_sessions.TryGetValue(sessionName, out var sessionData))
//                {
//                    foreach (var msg in e.Messages)
//                    {
//                        if (msg.Message == null)
//                            continue;

//                        // Handle incoming messages here

//                        // Update LastActivity
//                        sessionData.LastActivity = DateTime.UtcNow;
//                    }
//                    sessionData.Messages.AddRange(e.Messages);
//                }
//            }
//        }

//        private void MessageHistory_Set(object? sender, MessageHistoryModel[] e)
//        {
//            // Implement if necessary
//        }

//        private void Pressence_Update(object? sender, PresenceModel e)
//        {
//            Console.WriteLine(JsonSerializer.Serialize(e));
//        }

//        public async Task SendMessage(string sessionName, string remoteJid, string message)
//        {
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                await sessionData.Socket.SendMessage(remoteJid, new TextMessageContent()
//                {
//                    Text = message
//                });

//                // Update LastActivity
//                sessionData.LastActivity = DateTime.UtcNow;
//            }
//            else
//            {
//                throw new Exception($"Session {sessionName} not found.");
//            }
//        }

//        public string GetQRCode(string sessionName)
//        {
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                return sessionData.QRCode;
//            }
//            return null;
//        }

//        public string GetAsciiQRCode(string sessionName)
//        {
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                return sessionData.QRCode;
//            }
//            return null;
//        }

//        public bool IsConnected(string sessionName)
//        {
//            if (_sessions.TryGetValue(sessionName, out var sessionData))
//            {
//                return sessionData.IsConnected;
//            }
//            return false;
//        }

//        public IEnumerable<string> GetActiveSessions()
//        {
//            return _sessions.Keys;
//        }

//        public bool TryGetSessionData(string sessionName, out SessionData sessionData)
//        {
//            return _sessions.TryGetValue(sessionName, out sessionData);
//        }

//        public class SessionData
//        {
//            public WASocket Socket { get; set; }
//            public SocketConfig Config { get; set; } // Added Config property
//            public List<WebMessageInfo> Messages { get; set; }
//            public string QRCode { get; set; }
//            public bool IsConnected { get; set; }
//            public DateTime LastActivity { get; set; } = DateTime.UtcNow;
//        }
//    }
//}
