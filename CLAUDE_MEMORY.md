# BaileysCSharp WhatsApp API - Setup & Usage Memory

## Project Overview
BaileysCSharp is a C# WhatsApp Web API implementation running on Ubuntu in Windows WSL2, configured with nginx reverse proxy for production deployment.

## Quick Start Commands

### 1. Start the WhatsApp API Service
```bash
cd /root/BaileysCSharp
nohup dotnet run --project WhatsAppApi/WhatsAppApi.csproj > /tmp/whatsapp.log 2>&1 &
```

### 2. Fix Socket Permissions (Required after each start)
```bash
chmod 777 /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock
```

### 3. Monitor Service Logs
```bash
tail -f /tmp/whatsapp.log
```

### 4. Stop Service
```bash
pkill -f "dotnet.*WhatsAppApi"
```

## Network Configuration

### Nginx Configuration
- **File**: `/etc/nginx/sites-available/developmentschool`
- **Access URL**: `http://localhost/whatsapp/*`
- **Backend**: Unix socket at `/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock`

### Nginx Reload
```bash
nginx -t && systemctl reload nginx
```

## API Endpoints (WhatsAppControllerV2)

**Base URL**: `http://localhost/whatsapp/v2/WhatsAppControllerV2`

### Session Management
```bash
# Start a new session (generates QR code if not authenticated)
POST /startSession
{
  "SessionName": "your_session_name"
}

# Stop a session
POST /stopSession
{
  "SessionName": "your_session_name"
}

# Get all active sessions
GET /activeSessions
# Returns: {"sessions":["session1","session2"]}

# Check connection status
GET /connectionStatus?sessionName=your_session_name
# Returns: {"IsConnected": true/false}
```

### QR Code Management
```bash
# Get ASCII QR code for authentication
GET /getAsciiQRCode?sessionName=your_session_name

# Force regenerate QR code
POST /forceRegenerateQRCode
{
  "SessionName": "your_session_name"
}
```

### Messaging
```bash
# Send text message
POST /sendMessage
{
  "SessionName": "your_session_name",
  "RemoteJid": "27781234567@s.whatsapp.net",
  "Message": "Hello World!"
}

# Send media (image/document/etc)
POST /sendMedia
{
  "SessionName": "your_session_name",
  "RemoteJid": "27781234567@s.whatsapp.net",
  "MediaBytes": "base64_encoded_file_data",
  "MimeType": "image/jpeg",
  "Caption": "Optional caption"
}
```

## Session Storage & Persistence

### Session Location
```bash
# Sessions stored in fixed location (survives restarts)
/home/RubyManager/web/whatsapp.rubymanager.app/sessions/{sessionName}/

# Key files:
- {sessionName}_creds.json  # Authentication credentials
- store.db                  # Chat history and contacts
```

### Session States
- **Unauthenticated**: Shows QR code, needs mobile app scanning
- **Authenticated**: Can send/receive messages, auto-restores on restart
- **Connected**: Active session with established WebSocket connection

### Automatic Session Restoration
- ✅ **Already Implemented**: Sessions auto-restore on service startup
- ✅ **No User Intervention**: Authenticated sessions reconnect automatically
- ✅ **Persistent Storage**: Sessions survive server restarts/deployments

## Testing Workflow

### 1. Start a New Session
```bash
curl -X POST "http://localhost/whatsapp/v2/WhatsAppControllerV2/startSession" \
  -H "Content-Type: application/json" \
  -d '{"SessionName":"test"}'
```

### 2. Get QR Code
```bash
# ASCII QR Code (may have display/scanning issues)
curl "http://localhost/whatsapp/v2/WhatsAppControllerV2/getAsciiQRCode?sessionName=test"

# Force regenerate new QR code
curl -X POST "http://localhost/whatsapp/v2/WhatsAppControllerV2/forceRegenerateQRCode" \
  -H "Content-Type: application/json" \
  -d '{"SessionName":"test"}'
```

**⚠️ ASCII QR Code Limitations:**
- May not scan well from computer screens
- Character spacing can distort QR pattern  
- Mobile cameras struggle with text-based QR codes
- **Recommendation**: Implement proper image-based QR generation for production

### 3. Scan QR Code
- Open WhatsApp on mobile
- Go to Settings > Linked Devices > Link a Device
- Scan the QR code from the API response

### 4. Verify Connection
```bash
curl "http://localhost/whatsapp/v2/WhatsAppControllerV2/connectionStatus?sessionName=test"
```

### 5. Send Test Message
```bash
curl -X POST "http://localhost/whatsapp/v2/WhatsAppControllerV2/sendMessage" \
  -H "Content-Type: application/json" \
  -d '{
    "SessionName": "test",
    "RemoteJid": "27781234567@s.whatsapp.net",
    "Message": "Hello from API!"
  }'
```

## Important Notes

### Phone Number Format
- Use international format: `27781234567@s.whatsapp.net`
- For groups: `120363xxxxxxxxxxxxxx@g.us`

### Authentication Flow
1. **First Time**: User scans QR code → credentials saved
2. **Subsequent Restarts**: Session auto-restores without QR code
3. **Re-authentication**: Only needed if session expires or user logs out

### Service Architecture
- **Framework**: .NET 8.0, ASP.NET Core
- **Communication**: Unix Domain Sockets
- **Database**: LiteDB for session storage
- **Encryption**: Signal Protocol for E2E encryption
- **WebSocket**: WhatsApp Web protocol implementation

### Production Considerations
- Sessions auto-restore on startup ✅
- Fixed storage path survives deployments ✅
- Health checks and session cleanup ✅
- Keep-alive mechanism prevents timeouts ✅
- Rate limiting implemented ✅
- Comprehensive logging ✅

## Troubleshooting

### Common Issues
1. **502 Bad Gateway**: Socket permissions issue → run `chmod 777 app.sock`
2. **404 Not Found**: Wrong controller route → use `WhatsAppControllerV2` 
3. **No QR Code**: Session might be starting → check logs for QR generation
4. **Session Not Restoring**: Missing credentials file → complete authentication first

### Log Analysis
```bash
# Service startup logs
tail -20 /tmp/whatsapp.log

# Session restoration logs
grep "RestoreExistingSessionsAsync\|Session.*restore" /tmp/whatsapp.log

# Connection status logs  
grep "ConnectionState\|WAConnectionState" /tmp/whatsapp.log
```

### Health Checks
```bash
# Check if service is running
ps aux | grep dotnet

# Check socket file exists
ls -la /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock

# Test basic connectivity
curl "http://localhost/whatsapp/v2/WhatsAppControllerV2/activeSessions"
```

## Development Notes

- **Controller**: Use `WhatsAppControllerV2` (not `WhatsAppController`)
- **Route Pattern**: `/v2/WhatsAppControllerV2/{action}`
- **Session Management**: Automatic restoration implemented
- **Configuration**: Unix socket configuration in `appsettings.json`
- **Nginx**: Strips `/whatsapp` prefix before forwarding to backend

---

**Last Updated**: July 2025  
**Status**: Production Ready with Automatic Session Persistence ✅