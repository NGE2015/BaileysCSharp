# BaileysCSharp — WhatsApp Web API

C# port of the Baileys Node.js WhatsApp Web library, wrapped as an ASP.NET Core 8 REST API. Used by **w4lCRM** and **RubyManagerBot** to send/receive WhatsApp messages.

---

## Architecture

```
Caddy / Nginx (reverse proxy)
       │
       ▼  Unix socket
 WhatsAppApi  (ASP.NET Core 8, Kestrel)
       │
       ├── WhatsAppControllerV2   — session & messaging endpoints
       ├── DashboardController    — auth cookie for dashboard pages
       ├── LogsController         — log file access
       ├── WhatsAppServiceV2      — session lifecycle, QR, reconnection
       └── WaBuildHelper          — fetches current WA version from wppconnect.io
```

Sessions are persisted to `/home/RubyManager/web/whatsapp.rubymanager.app/sessions/{sessionName}/` on Linux (or `C:\home\...\sessions\` on Windows during local dev). Each session folder contains `{name}_creds.json` and a key store — these survive restarts and deployments.

---

## Local Development (Windows + Caddy)

### Prerequisites
- .NET 8 SDK
- Caddy at `C:\Caddy\` — already configured in `C:\Caddy\WhatsAppApi\`

### Start everything
```powershell
# Option A: Start both in one shot
C:\Caddy\WhatsAppApi\START_ALL.ps1

# Option B: Start individually in separate terminals
C:\Caddy\WhatsAppApi\1_start_app.ps1   # dotnet run on localhost:5284
C:\Caddy\WhatsAppApi\2_start_caddy.ps1  # Caddy proxies localhost:5285 → 5284
```

The app runs as `ASPNETCORE_ENVIRONMENT=Development`, which loads `appsettings.Development.json`:
- `ListenLocalhost: true` + `LocalhostPort: 5284`
- Trace-level logging, larger log retention

### Test locally
```powershell
# Start a session (generates QR if no saved credentials)
curl -X POST http://localhost:5285/v2/WhatsAppControllerV2/startSession `
  -H "Content-Type: application/json" `
  -d '{"SessionName":"test"}'

# Get diagnostics for all sessions (includes QR data)
curl http://localhost:5285/v2/WhatsAppControllerV2/allDiagnostics

# Get ASCII QR code
curl "http://localhost:5285/v2/WhatsAppControllerV2/getAsciiQRCode?sessionName=test"
```

> **Windows socket note**: The Unix socket path in appsettings is a Linux path
> (`/home/RubyManager/.../app.sock`). On Windows this resolves to `C:\home\...`.
> The app creates the directory automatically — no action needed.

---

## Dashboard Pages

The API serves two password-protected HTML dashboards from `wwwroot/`:

| URL | Purpose |
|---|---|
| `/status.html` | Live session health, visual QR codes, log tail |
| `/logs.html` | Full log viewer with keyword filters |

Both pages use a cookie-based login (`dash_tok`). The password is configured in `appsettings.json`:
```json
"Dashboard": { "Password": "RubyManager2026!" }
```

### What status.html shows
- **Traffic-light badge** per session: connected / QR pending / logged out / reconnecting / error
- **Human-readable explanation** of why a session is in its current state and what action to take
- **Visual QR code** rendered in-browser using `qrcode.js` when a session needs scanning
- **Live log tail** from the most recent log file
- Attention banner at top when any session is not connected
- Auto-refresh: every 5 s when QR is pending, every 15 s otherwise

### Accessing in production
```
https://whatsapp.rubymanager.app/status.html
https://whatsapp.rubymanager.app/logs.html
```

---

## API Reference

**Base path**: `/v2/WhatsAppControllerV2`

> **Route gotcha**: ASP.NET Core's `[controller]` token strips "Controller" only when it appears at
> the very end of the class name. `WhatsAppControllerV2` ends in "V2", so the full class name is
> used verbatim in the route. Do not shorten it to `/v2/WhatsApp/`.

### Session management

```
POST /startSession          { "SessionName": "..." }
POST /stopSession           { "SessionName": "..." }
GET  /activeSessions
GET  /connectionStatus?sessionName=...
GET  /allDiagnostics        ← enriched state for all sessions (used by status.html)
```

`allDiagnostics` response shape:
```json
{
  "sessions": [
    {
      "sessionName": "tenantId",
      "state": "connected|qr_pending|logged_out|bad_session|restart_required|reconnecting|disconnected|connecting",
      "why": "Human-readable explanation",
      "action": "What to do next",
      "rawQrData": "2@xyz...",   // non-null only when state == qr_pending
      "connectedAt": "...",
      "lastActivity": "...",
      "disconnectReason": 0
    }
  ],
  "waVersion": "2.3000.1042026337"
}
```

### QR codes

```
GET  /getAsciiQRCode?sessionName=...&timeout=10
POST /forceRegenerateQRCode   { "SessionName": "..." }
```

### Messaging

```
POST /sendMessage   { "SessionName": "...", "RemoteJid": "27781234567@s.whatsapp.net", "Message": "..." }
POST /sendMedia     { "SessionName": "...", "RemoteJid": "...", "MediaBytes": "<base64>", "MimeType": "image/jpeg", "Caption": "..." }
```

### Dashboard auth

```
POST /api/dashboard/login    { "password": "..." }   → sets HttpOnly dash_tok cookie
GET  /api/dashboard/verify                           → 200 or 401
POST /api/dashboard/logout                           → clears cookie
GET  /api/dashboard/info                             → waVersion, environment, serverTime
```

---

## Configuration

### appsettings.json (production values checked in)

```json
{
  "Kestrel": {
    "UnixSocketPath": "/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock"
  },
  "publish": { "Env": "prod" },
  "Dashboard": { "Password": "RubyManager2026!" },
  "WhatsAppSettings": { "QRSessionTimeoutMinutes": 10 }
}
```

### Environment overrides

| File | When loaded | Key differences |
|---|---|---|
| `appsettings.Development.json` | `ASPNETCORE_ENVIRONMENT=Development` | `ListenLocalhost: true`, port 5284, trace logging |
| `appsettings.WindowsDev.json` | `ASPNETCORE_ENVIRONMENT=WindowsDev` | Same as Development but with Windows socket path |

### CI/CD environment selection

The GitHub Actions workflow reads `publish.Env` from `appsettings.json` to choose the SSH target:
- `"dev"` → dev server
- `"prod"` → production server

**Never commit `appsettings.json` with `"Env": "dev"` to `main`.**

---

## Production Deployment

```bash
# Restart the systemd service after a new deploy
sudo systemctl restart WhatsApp.RubyManager.app.service
sudo systemctl status WhatsApp.RubyManager.app.service

# Watch live logs
journalctl -u WhatsApp.RubyManager.app.service -f

# OR tail the rolling file log
tail -f /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/logs/whatsapp-*.log
```

### Fix a stuck session (session not showing QR, not connecting)

This happens when a session's `_creds.json` exists but WhatsApp has logged it out
(disconnect reason 401 / LoggedOut). The service keeps trying to reconnect silently.

```bash
# 1. Identify the tenant session folder
ls /home/RubyManager/web/whatsapp.rubymanager.app/sessions/

# 2. Delete it
rm -rf /home/RubyManager/web/whatsapp.rubymanager.app/sessions/<tenantId>

# 3. Restart service
sudo systemctl restart WhatsApp.RubyManager.app.service

# 4. Trigger a new session from the CRM settings page — a fresh QR will appear
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| QR never appears, logs show version errors | `WaBuildHelper` fetching failed, old fallback version | Check `wppconnect.io` reachability; `waVersion` in `/allDiagnostics` shows what's being used |
| QR appears but scanning does nothing | WhatsApp Web version mismatch | Update fallback in `WaBuildHelper.cs` to latest alpha build number |
| 404 on API calls | Wrong route — using `/v2/WhatsApp/` | Correct route is `/v2/WhatsAppControllerV2/` |
| 502 Bad Gateway | Unix socket permissions | `chmod 777 app.sock` |
| Session stuck in `reconnecting` loop | Session was remotely logged out | Delete session folder + restart (see above) |
| Dashboard shows 401 | Cookie expired or wrong password | Re-login at `/status.html` |

---

## Key Source Files

| File | Purpose |
|---|---|
| `WhatsAppApi/Helper/WaBuildHelper.cs` | Fetches current WhatsApp Web version; has `LastResolvedVersion` for dashboard |
| `WhatsAppApi/Services/WhatsAppServiceV2.cs` | Session lifecycle, QR events, `SessionData` inner class |
| `WhatsAppApi/Controllers/WhatsAppControllerV2.cs` | REST endpoints including `allDiagnostics` |
| `WhatsAppApi/Controllers/DashboardController.cs` | Cookie auth for dashboard pages |
| `WhatsAppApi/Middleware/DashboardAuthMiddleware.cs` | Protects `/api/logs` prefix |
| `WhatsAppApi/wwwroot/status.html` | Live session health dashboard |
| `WhatsAppApi/wwwroot/logs.html` | Log viewer dashboard |
| `BaileysCSharp/Core/Sockets/WASocket.cs` | Core WhatsApp Web socket |

---

## Known Fixes & History

### June 2026 — QR generation fix (`feature/dashboard-v2` → `main`)
`WaBuildHelper` regex was `>([0-9]+\.[0-9]+\.[0-9]+-alpha)<`, which captured `-alpha` inside
the group. `uint.Parse("1031772734-alpha")` always threw, so the version scraper silently fell back
to an outdated hardcoded version from January 2026. WhatsApp Web silently rejects old versions,
so no QR was ever generated.

Fix: changed regex to `>([0-9]+\.[0-9]+\.[0-9]+)-alpha<` and updated fallback to `{ 2, 3000, 1042026337 }`.

### July 2025 — Session persistence fix
Sessions were stored in variable assembly paths that changed with each deployment, causing daily
QR re-scans. Fixed by pinning the session path to
`/home/RubyManager/web/whatsapp.rubymanager.app/sessions/`.
See `WHATSAPP_SESSION_PERSISTENCE_FIX.md` for details.
