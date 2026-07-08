# BaileysCSharp WhatsApp API - Setup & Usage Memory

## Project Overview
BaileysCSharp is a C# WhatsApp Web API running as a systemd service on Ubuntu, behind Caddy (production) or Nginx. On Windows it is tested locally using Caddy at `C:\Caddy\WhatsAppApi\`.

## Quick Start (Production — Linux systemd)

```bash
sudo systemctl restart WhatsApp.RubyManager.app.service
sudo systemctl status WhatsApp.RubyManager.app.service
journalctl -u WhatsApp.RubyManager.app.service -f
```

## Quick Start (Local — Windows + Caddy)

```powershell
C:\Caddy\WhatsAppApi\START_ALL.ps1
# App: localhost:5284  |  Caddy proxy: localhost:5285
```

## Network Configuration

### Production (Caddy on Ubuntu)
- **Socket**: `/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock`
- **Public URL**: `https://whatsapp.rubymanager.app`

### Local Windows (Caddy)
- **Socket**: `C:\temp\whatsapp-api.sock` (or the Linux-path equivalent)
- **App port**: `localhost:5284`
- **Proxy port**: `localhost:5285`
- **Caddyfile**: `C:\Caddy\WhatsAppApi\Caddyfile`

## API Endpoints (WhatsAppControllerV2)

**Base URL (local)**: `http://localhost:5285/v2/WhatsAppControllerV2`
**Base URL (prod)**: `https://whatsapp.rubymanager.app/v2/WhatsAppControllerV2`

> **IMPORTANT**: The route is `/v2/WhatsAppControllerV2/` NOT `/v2/WhatsApp/`.
> ASP.NET Core's `[controller]` token only strips "Controller" when it is the
> last word in the class name — `WhatsAppControllerV2` ends in "V2".

### Session Management
```bash
POST /startSession       { "SessionName": "..." }
POST /stopSession        { "SessionName": "..." }
GET  /activeSessions
GET  /connectionStatus?sessionName=...
GET  /allDiagnostics     # ← primary diagnostic endpoint (see Dashboard section)
```

### QR Code Management
```bash
GET  /getAsciiQRCode?sessionName=...&timeout=10
POST /forceRegenerateQRCode   { "SessionName": "..." }
```

> For visual QR codes, open `/status.html` in the browser — it uses `qrcode.js` to render scannable QR images directly on screen.

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

## Dashboard Pages (June 2026)

Password-protected pages at `/status.html` and `/logs.html`.

- **Login**: POST `/api/dashboard/login` with `{ "password": "..." }` → sets HttpOnly `dash_tok` cookie
- **Password** (in `appsettings.json`): `"Dashboard": { "Password": "RubyManager2026!" }`
- **Production URLs**: `https://whatsapp.rubymanager.app/status.html` and `.../logs.html`

### status.html capabilities
- Traffic-light badge per session: connected / qr_pending / logged_out / reconnecting / error
- Human-readable why + action for each state
- Visual QR rendered in-browser (qrcode.js CDN) — just open the page and scan
- Live log tail, filterable by QR / Error / Warning / Session / Connection
- Adaptive auto-refresh (5 s when QR pending, 15 s otherwise)

### allDiagnostics endpoint
`GET /v2/WhatsAppControllerV2/allDiagnostics` — the single source of truth for session health.
Returns `state`, `why`, `action`, `rawQrData`, `waVersion`, timestamps per session.

---

## Troubleshooting

### Session stuck — not connecting, not showing QR
Happens when `_creds.json` exists but WhatsApp remotely logged out the session (reason 401).

```bash
rm -rf /home/RubyManager/web/whatsapp.rubymanager.app/sessions/<tenantId>
sudo systemctl restart WhatsApp.RubyManager.app.service
# Then trigger /startSession from CRM settings
```

### QR never appeared (historical bug — fixed June 2026)
`WaBuildHelper` regex included `-alpha` in the numeric capture group, causing `uint.Parse` to
always throw and fall back to an outdated WA Web version. Fixed in `feature/dashboard-v2`.

### Common issues
| Symptom | Fix |
|---|---|
| 502 Bad Gateway | `chmod 777 app.sock` |
| 404 on API calls | Route is `/v2/WhatsAppControllerV2/` not `/v2/WhatsApp/` |
| No QR on status page | Check `waVersion` in `/allDiagnostics`; stale version = silent WA rejection |
| Dashboard 401 | Re-login at `/status.html` |

### Log locations (production)
```bash
# Rolling file log
/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/logs/whatsapp-YYYYMMDD.log

# Systemd journal
journalctl -u WhatsApp.RubyManager.app.service -f
```

---

**Last Updated**: June 2026  
**Status**: Production Ready — dashboard v2 with visual QR + diagnostics deployed ✅