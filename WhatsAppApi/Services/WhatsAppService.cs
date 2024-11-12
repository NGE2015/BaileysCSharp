using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BaileysCSharp.Core.Events;
using BaileysCSharp.Core.Logging;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Models.Sending;
using BaileysCSharp.Core.Models.Sending.NonMedia;
using BaileysCSharp.Core.NoSQL;
using BaileysCSharp.Core.Sockets;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.Utils;
using BaileysCSharp.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proto;
using QRCoder;
using System.Text.Json;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.Core.Extensions;
using AuthenticationState = BaileysCSharp.Core.Types.AuthenticationState;

namespace WhatsAppApi.Services
{
    public interface IWhatsAppService
    {
        Task SendMessage(string remoteJid, string message);
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
        string GetCurrentQRCode();
        string GetCurrentAsciiQRCode();
        bool IsConnected { get; }

    }

    public class WhatsAppService : IWhatsAppService
    {
        private readonly ILogger<WhatsAppService> _logger;
        private WASocket _socket;
        private List<WebMessageInfo> _messages;
        private readonly object locker = new object();
        private string _qrCodeBase64;
        private string _asciiQRCode;
        private bool _isConnected = false;
        public bool IsConnected => _isConnected;

        public WhatsAppService(ILogger<WhatsAppService> logger)
        {
            _logger = logger;
            _messages = new List<WebMessageInfo>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var config = new SocketConfig()
            {
                SessionName = "27665458845745067",
            };

            var credsFile = Path.Join(config.CacheRoot, $"creds.json");
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

            _socket = new WASocket(config);

            _socket.EV.Auth.Update += Auth_Update;
            _socket.EV.Connection.Update += Connection_Update;
            _socket.EV.Message.Upsert += Message_Upsert;
            _socket.EV.MessageHistory.Set += MessageHistory_Set;
            _socket.EV.Pressence.Update += Pressence_Update;

            _socket.MakeSocket();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Implement any necessary shutdown logic here
            return Task.CompletedTask;
        }

        private void Auth_Update(object? sender, AuthenticationCreds e)
        {
            lock (locker)
            {
                var credsFile = Path.Join(_socket.SocketConfig.CacheRoot, $"creds.json");
                var json = AuthenticationCreds.Serialize(e);
                File.WriteAllText(credsFile, json);
            }
        }
        private async void Connection_Update(object? sender, BaileysCSharp.Core.Types.ConnectionState e)
        {
            var connection = e;
            _logger.LogDebug(JsonSerializer.Serialize(connection));
            if (connection.QR != null)
            {
                QRCodeGenerator QrGenerator = new QRCodeGenerator();
                QRCodeData QrCodeInfo = QrGenerator.CreateQrCode(connection.QR, QRCodeGenerator.ECCLevel.L);
                AsciiQRCode qrCode = new AsciiQRCode(QrCodeInfo);
                var data = qrCode.GetGraphic(1);
                //data = qrCode.GetGraphic(1, drawQuietZones: false, whiteSpaceString: " ", darkColorString: "█");

                Console.WriteLine(data);
                _qrCodeBase64 = data;
            }
            if (connection.Connection == WAConnectionState.Close)
            {
                if (connection.LastDisconnect.Error is Boom boom && boom.Data?.StatusCode != (int)DisconnectReason.LoggedOut)
                {
                    try
                    {
                        Thread.Sleep(1000);
                        _socket.MakeSocket();
                    }
                    catch (Exception)
                    {

                    }
                }
                else
                {
                    Console.WriteLine("You are logged out");
                }
            }

            else if (connection.Connection == WAConnectionState.Open)
            {
                // Clear the QR code when connection is established
                _qrCodeBase64 = null;
                _isConnected = true;
            }
            else if (connection.Connection == WAConnectionState.Close)
            {
                _isConnected = false;
            }
        }
       

        private async void Message_Upsert(object? sender, MessageEventModel e)
        {
            if (e.Type == MessageEventType.Notify)
            {
                foreach (var msg in e.Messages)
                {
                    if (msg.Message == null)
                        continue;

                    // Handle incoming messages here
                }
                _messages.AddRange(e.Messages);
            }
        }

        private void MessageHistory_Set(object? sender, MessageHistoryModel[] e)
        {
            _messages.AddRange(e[0].Messages);
            var jsons = _messages.Select(x => x.ToJson()).ToArray();
            var array = $"[\n{string.Join(",", jsons)}\n]";
            Debug.WriteLine(array);
        }

        private void Pressence_Update(object? sender, PresenceModel e)
        {
            Console.WriteLine(JsonSerializer.Serialize(e));
        }

        public async Task SendMessage(string remoteJid, string message)
        {
            await _socket.SendMessage(remoteJid, new TextMessageContent()
            {
                Text = message
            });
        }

        public string GetCurrentQRCode()
        {
            return _qrCodeBase64;
        }
        public string GetCurrentAsciiQRCode()
        {
            return _qrCodeBase64;
        }

    }
}


//version of qr base64
//private async void Connection_Update(object? sender, BaileysCSharp.Core.Types.ConnectionState e)
//{
//    var connection = e;
//    _logger.LogDebug(JsonSerializer.Serialize(connection));
//    if (connection.QR != null)
//    {
//        QRCodeGenerator QrGenerator = new QRCodeGenerator();
//        QRCodeData QrCodeInfo = QrGenerator.CreateQrCode(connection.QR, QRCodeGenerator.ECCLevel.L);

//        // Generate QR code image in PNG format
//        using (var qrCode = new PngByteQRCode(QrCodeInfo))
//        {
//            var qrCodeBytes = qrCode.GetGraphic(20); // Adjust pixel size as needed
//            _qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);
//        }
//    }
//    else if (connection.Connection == WAConnectionState.Open)
//    {
//        // Clear the QR code when connection is established
//        _qrCodeBase64 = null;
//        _isConnected = true;
//    }
//    else if (connection.Connection == WAConnectionState.Close)
//    {
//        _isConnected = false;
//    }
//}