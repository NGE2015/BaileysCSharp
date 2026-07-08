# Changelog — BaileysCSharp

## [2026-07-08] — fix: WhatsApp session stall after ~24h (keepalive zombie + silent reconnect)
**App:** BaileysCSharp  
**What changed:** Fixed a bug where WhatsApp sessions silently died after ~24 hours and required a manual disconnect + QR re-scan to recover. Sessions now auto-reconnect without any user intervention.  
**Root cause:** Two stacked bugs:
1. `BaseSocket.KeepAliveHandler` called `Query()` (ping) with no timeout. When WhatsApp silently drops the TCP connection (no WS close frame), `Query()` hangs forever — the keepalive thread freezes, `IsConnected` stays `true`, and the session becomes a zombie with no error or event fired.
2. `End(string, DisconnectReason)` — the original overload — did not emit `ConnectionState.Close`, so even if the stale-diff check fired, `WhatsAppServiceV2.Connection_UpdateAsync` was never called and `ScheduleReconnectionAsync` was never triggered.
3. The health check timer was disabled (`_healthCheckTimer = null`) with a "TEMPORARILY DISABLED" comment, removing the only secondary safety net.

**Fix:**
- `BaseSocket.KeepAliveHandler`: ping now uses `.WaitAsync(20s)`. On timeout → calls `End(Boom)` and returns, which fires the Close event and triggers auto-reconnect (no QR needed if credentials are valid).
- `End(string, DisconnectReason)`: now routes through `End(Boom)` so the Close event is always emitted consistently regardless of which overload is called.
- `WhatsAppServiceV2`: health check timer re-enabled (runs every 5 minutes as a secondary safety net).

**Files touched:** `BaileysCSharp/Core/Sockets/BaseSocket.cs`, `WhatsAppApi/Services/WhatsAppServiceV2.cs`  
**Why:** Sessions were dropping daily in dev environment requiring manual QR re-scan. The ~24h window matches WhatsApp's server-side TCP connection rotation cycle.  
**Rollback:** Revert the two files above; re-disable the health check timer line.

---

## [2026-06-24] — cc4610c — Merge feature/dashboard-v2 into main
**App:** BaileysCSharp  
**What changed:** Merged the `feature/dashboard-v2` branch. Delivered a fully rebuilt dashboard (`status.html`) with QR display, session diagnostics, and real-time health indicators. Added `DashboardController.cs` with password-protected auth middleware, a new `DashboardAuthMiddleware.cs`, improved `WhatsAppControllerV2.cs`, and updated `logs.html` with richer output.  
**Files touched:** `WhatsAppApi/Controllers/DashboardController.cs`, `WhatsAppApi/Controllers/WhatsAppControllerV2.cs`, `WhatsAppApi/Middleware/DashboardAuthMiddleware.cs`, `WhatsAppApi/Program.cs`, `WhatsAppApi/Helper/WaBuildHelper.cs`, `WhatsAppApi/wwwroot/status.html`, `WhatsAppApi/wwwroot/logs.html`, `WhatsAppApi/appsettings.json`, `WhatsAppApi/appsettings.Development.json`, `WhatsAppApi/appsettings.WindowsDev.json`, `.github/workflows/main.yml`  
**Why:** The original status dashboard was difficult to read and lacked auth. Staff needed a reliable visual indicator of session health and an easier way to re-scan the QR when sessions drop.  
**Rollback:** `git revert cc4610c` (merge revert) or `git checkout cc4610c~1`

---

## [2026-06-24] — 9e3e875 — Feature: fix QR generation and add dashboard v2 with diagnostics and auth
**App:** BaileysCSharp  
**What changed:** Fixed QR code generation reliability and built the v2 dashboard with diagnostics panel and password auth (pre-merge commit).  
**Files touched:** See merge entry above — this is the squashed feature commit included in `cc4610c`.  
**Why:** QR generation had a race condition causing blank codes; diagnostics panel surfaces session errors without needing log access.  
**Rollback:** `git revert 9e3e875`

---

## [Earlier] — 293f17e — Refactor code structure for improved readability and maintainability
**App:** BaileysCSharp  
**What changed:** General code structure refactor across the WhatsApp API layer for clarity and long-term maintainability.  
**Files touched:** Multiple files across `WhatsAppApi/`.  
**Why:** Codebase had grown organically from the Baileys port; cleanup pass to improve onboarding and reduce cognitive load.  
**Rollback:** `git revert 293f17e`
