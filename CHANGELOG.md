# Changelog — BaileysCSharp

## [2026-07-13] — 196a855 — fix: serialize WhatsApp reconnection to stop disconnect storms, add slow self-heal retry + tenant alert
**App:** BaileysCSharp
**What changed:** `Connection_UpdateAsync` called `ScheduleReconnectionAsync` unguarded on every socket-close event. A socket recreated mid-reconnection (`FullSocketRecreation`) that itself failed fast would raise its own close event, starting a second concurrent reconnection chain racing on the same `ReconnectAttempts` counter - confirmed live via production logs (`whatsapp.rubymanager.app/logs.html`) showing dozens of `disconnected with reason: 405` within ~10 minutes, far more than one backoff chain could produce alone. `SessionData` now holds a `ReconnectLock` (`SemaphoreSlim`) so at most one reconnection chain runs per session at a time; the old recursive fire-and-forget continuation is now a single guarded loop. Once fast attempts are exhausted, the session no longer dies silently forever: it retries every 30 minutes indefinitely (bounded for free by the existing 72h inactive-session cleanup in `PerformHealthCheck`) and fires a one-time alert to the CRM (new `NotifyConnectionDownAsync`, paired with `w4l_kiteschoolmanager`'s new `/api/whatsappconnection/connection-alert` endpoint) so the tenant is emailed instead of a dead session going unnoticed.
**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`
**Why:** This tenant's WhatsApp connection was dying after long idle periods and staying down until someone manually restarted it - the June 2026 keepalive/health-check fix (below) addressed one contributing cause but not the reconnection-storm bug, which this closes. Business requirement: scheduled WhatsApp/email sends must keep working via the always-running systemd service even with the CRM closed.
**Rollback:** `git revert 196a855` — restores the old recursive/unguarded reconnection logic (reintroduces the storm bug).

---

## [2026-07-08] — d8c606e — ci: add feature/lid-normalization-bot-webhook branch to CI/CD triggers
**App:** BaileysCSharp
**What changed:** Added `feature/lid-normalization-bot-webhook` to the `on: push: branches:` list in `.github/workflows/main.yml`.
**Files touched:** `.github/workflows/main.yml`
**Why:** Needed so pushes to this branch actually deploy for testing the LID-normalization and RubyManagerBot webhook work.
**Rollback:** `git revert d8c606e`

---

## [2026-07-08] — ad44864 — Merge remote-tracking branch 'origin/fix/lid-message-sending-null-reference' into feature/lid-normalization-bot-webhook
**App:** BaileysCSharp
**What changed:** Merged in a batch of fixes around WhatsApp's `@lid` (linked-device ID) contact identifiers: normalizes `@lid`-format sender/caller identifiers to real phone numbers for consistency (`MessageDecoder.cs`), adds null-safety checks and a device fallback so sending a message to a contact only known by LID (no cached phone number) no longer throws a null-reference exception (`MessagesSendSocket.cs`), adds phone-number extraction/caching for caller and sender numbers pulled from incoming messages (`MessagesRecvSocket.cs`, `MessageDecryptor.cs`, `SessionCipher.cs`), and includes the contact name in the payload sent to the CRM/RubyManagerBot webhook plus more detailed logging for traceability (`WhatsAppServiceV2.cs`). Conflicts in `.github/workflows/main.yml` and `README.md` were resolved keeping both branches' CI triggers and docs.
**Files touched:** `BaileysCSharp/Core/Signal/MessageDecryptor.cs`, `BaileysCSharp/Core/Sockets/MessagesRecvSocket.cs`, `BaileysCSharp/Core/Sockets/MessagesSendSocket.cs`, `BaileysCSharp/Core/Utils/MessageDecoder.cs`, `BaileysCSharp/LibSignal/SessionCipher.cs`, `WhatsAppApi/Program.cs`, `WhatsAppApi/Services/WhatsAppServiceV2.cs`, `WhatsAppApi/appsettings*.json`, `PHONE_NUMBER_INVESTIGATION_WHATSAPP.md` (new)
**Why:** Messages from contacts WhatsApp only exposes via `@lid` (rather than a phone number) were crashing the send path and/or arriving at the RubyManagerBot webhook without a usable phone number — this closes that gap so the unknown-number bot webhook (`2267b41`, on the same feature branch) can reliably identify who it's talking to.
**Rollback:** `git revert -m 1 ad44864` (merge revert) — reintroduces the LID null-reference bug.

---

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
