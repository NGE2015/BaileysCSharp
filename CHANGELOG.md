# Changelog — BaileysCSharp

## [2026-07-16] — fix: ROOT CAUSE — every failed reconnect leaked an event-buffer count, stranding all inbound messages forever
**App:** BaileysCSharp
**What changed:** This is the actual cause of "when WhatsApp reconnects the bot stops receiving messages, but the connection stays healthy and outbound still works". The two earlier fixes today (`d46eb87`, `dc703c9`) were real bugs but not this one.

`EventEmitter.Buffer()` / `Flush()` are **counter-based**: `Flush()` decrements `buffersInProgress` and only actually flushes the event stores once it reaches **0**. Delivery itself is gated per store by `DataEventStore.Emit`:
```csharp
if (IsBufferable) internalData[action].AddRange(data);  // held, not delivered
else              Execute(action, data);                // delivered immediately
```
The stores split exactly along the reported symptom:
| Store | `IsBufferable` | Effect |
|---|---|---|
| `MessagingEventStore` | `true` | inbound messages buffered |
| `MessageHistoryEventStore` | `true` | buffered |
| `ConnectionEventStore` | `false` | always delivered → session keeps reporting `connected` |
| `AuthEventStore` | `false` | always delivered → creds keep saving |

`BaseSocket.BeforeConnect()` calls `EV.Buffer()` (+1) on **every** `MakeSocket()`, but the only balancing `Flush()` (−1) is driven by the server's `offline` ib notification (`events["CB:ib,,offline"]` → `HandleOfflineSynceDone`), which arrives **only on a successful connect**. So every *failed* reconnect attempt leaked a +1 that nothing ever removed. Once the counter could no longer reach 0, the otherwise-balanced `Buffer()`/`Flush()` pair in `ProcessNodeWithBuffer` could only oscillate `N → N+1 → N`, so the stores never flushed and **every inbound message accumulated in `internalData` forever** — decrypted successfully, upserted, emitted, and then silently held. Meanwhile connection and auth events bypassed buffering entirely, which is why every health signal stayed green and outbound (a direct socket call, no `EV`) kept working.

**Fix:** New `EventEmitter.ResetBuffer()` — flushes anything stranded and returns the counter to 0 — called from `BeforeConnect()` so each connection attempt starts clean. Stranded events are flushed rather than dropped: they are real customer messages, and `SessionData.ProcessedMessageIds` (added in `dc703c9`) de-duplicates on message ID, so replaying them is safe.

**Evidence (production, session `7afd5398-…`, all from today's log):**
- **08:25:11** fresh restart → one `MakeSocket` → counter 1 → `offline` ib flush → **0** → message at 08:25:59 delivered end-to-end (`Decrypted_OK` → `Message_Upsert fired` Type: `Notify` → CRM 200 → RubyManagerBot webhook 200, bot replied).
- **08:30:36** `ConnectionLost` → attempt 1 `MakeSocket` **failed** (+1, never balanced) → attempt 2 `MakeSocket` (+1) succeeded (−1) → counter stuck at **1**.
- **08:32:36** next message: `[WA_CORE] {"stage":"Decrypted_OK","hasMessage":true,"offline":false}` — and then **nothing**. No `Message_Upsert fired`, no exception, no `ERR`/`WRN`. Decryption succeeded; the emit was buffered. Session still reported `state: connected`.
- This also explains why `FullSocketRecreation` appeared to work while `SimpleSocketRetry` did not: full recreation builds a **new `WASocket`**, hence a **new `EventEmitter` with the counter reset to 0**. The 07:28:05 `Append` message that arrived right after a `FullSocketRecreation` was the same effect — not, as previously assumed, evidence about handler re-attachment.

**Files touched:** `BaileysCSharp/Core/Events/EventEmitter.cs`, `BaileysCSharp/Core/Sockets/BaseSocket.cs`, `CHANGELOG.md`
**Why:** Inbound messages were never lost to a crash or to decryption — they were sitting in an in-memory buffer that could never drain, for the entire life of the process, after the first failed reconnect attempt. Only a restart cleared it, which is exactly why the service "recovered" on every deploy and then silently died again minutes later.
**Residual risk:** If a connect succeeds but the `offline` ib never arrives, the counter stays at 1 and messages buffer until the next `MakeSocket()` — whose `ResetBuffer()` now flushes them. Delayed rather than stranded forever; previously this state was permanent.
**Known issues not addressed here:**
- `ProcessingMutex.Mutex(Action)` is called with `async () => { … await … }`, binding as **`async void`** — the semaphore releases before the work completes (no real mutual exclusion) and exceptions after the first `await` escape the try/catch. Needs `Func<Task>`.
- The ~5-minute `ConnectionLost` (408) cycle is still unexplained. It is the trigger that made this fatal, not the cause.
- `SessionData.Messages` unbounded write-only list; `FullSocketRecreation` nulls `Socket` before validating credentials; `MessageDecoder.OnCallerPhoneNumberExtracted` subscription leak.
- `WhatsSocketConsole` has 10 pre-existing compile errors on `main` (CI publishes `WhatsAppApi` only, so deploys are unaffected).
**Rollback:** Revert this commit — restores the leaking buffer counter.
**Build note:** `dotnet build WhatsAppApi/WhatsAppApi.csproj` → 0 errors.

---

## [2026-07-16] — 8142f01 — diag: make inbound-message failures visible — ProcessingMutex swallowed every exception, and core logging was silenced by an inverted level check
**App:** BaileysCSharp
**What changed:** Diagnostic commit. After `d46eb87` (handler re-attach) and `dc703c9` (accept `Append`), a test message at 07:54:00 still never reached `Message_Upsert` — decoded, then gone, with no log line anywhere. Investigating *why there was no evidence* found two independent reasons the system cannot report its own failures:

1. **`ProcessingMutex.Mutex` had a completely empty catch.** All inbound message processing (`msg.Decrypt()` → `SendReceipt` → `CleanMessage` → `UpsertMessage`) runs inside it. Any exception was caught and discarded — not logged, not rethrown. The message was silently dropped and, because `SendMessageAck(node)` sits after the mutex, never acked either. This is the black hole that made the last two fixes look ineffective. Added a static `ProcessingMutex.OnException` hook, wired to `_logger.LogError`, and moved `semaphoreSlim.Release()` into a `finally`.
2. **All core-library logging was silenced.** `LogLevel` is ordered `Raw = 100, Fatal = 60, Error = 50 … Trace = 10`, and every guard reads `if (Level <= LogLevel.Error)` — a threshold where *lower is more verbose*. `StartSessionAsync` set `config.Logger.Level = LogLevel.Raw` (100), making every such check false and suppressing **every** `Error`/`Warn`/`Info`/`Debug`/`Trace` call in the entire core library. `Raw` is not "most verbose"; it is a special high value only `Raw()` checks (`Level >= Raw`). Changed to `LogLevel.Error` (13 `Logger.Error` sites core-wide, so volume is bounded), and set the same on the `SocketConfig` built inside `FullSocketRecreation` — it defaults to `Trace`, which would now flood.
3. **Core logs never reached the log file.** `DefaultLogger.Write` only writes to `Console`, so under systemd core diagnostics went to the journal, invisible to `/logs.html`. Added a static `DefaultLogger.Sink` invoked from `Write` (guarded so a sink failure can never break the receive loop), pointed at Serilog as `[WA_CORE]` from the `WhatsAppServiceV2` constructor.

Also added `[DECRYPT_TRACE]` around `msg.Decrypt()`: a try/catch that logs the exception (type, message ID, JID, offline flag) and **rethrows**, plus a `Decrypted_OK` marker before `UpsertMessage`. Behaviour is unchanged — this only makes the failure observable.

**How to read the next test message:** `[DECRYPT_TRACE] msg.Decrypt() THREW` = decryption is failing (likely Signal session damage → the remedy is a fresh QR via `ForceSessionRestart`). `Decrypted_OK` followed by no `[DROP_TRACE] Message_Upsert fired` = the failure is in `SendReceipt`/`CleanMessage`/`UpsertMessage`, and `ProcessingMutex.OnException` will now name it. Neither appearing = the message is dying before the mutex.

**Files touched:** `BaileysCSharp/Core/Helper/ProcessingMutex.cs`, `BaileysCSharp/Core/Logging/Logger.cs`, `BaileysCSharp/Core/Sockets/MessagesRecvSocket.cs`, `WhatsAppApi/Services/WhatsAppServiceV2.cs`, `CHANGELOG.md`
**Why:** Two rounds of fixes were shipped against inferred root causes because the system could not report what was actually failing. The empty catch plus the silenced logger meant a dropped message left literally no trace. This buys evidence before the next change.
**Known issues not addressed here:**
- **`ProcessingMutex.Mutex(Action)` is called with `async () => { … await … }`, which binds as `async void`.** The lambda returns at its first `await`, so the semaphore is released before the work completes (no actual mutual exclusion), `await Mutex(…)` does not wait for the body, and any exception thrown *after* the first `await` escapes the try/catch entirely as an unhandled async-void exception. Real bug; needs `Func<Task>` and an `await action()`. Left out of a diagnostic commit — it changes hot-path concurrency semantics.
- The ~5-minute `ConnectionLost` (408) cycle remains unexplained and may be a symptom of the same underlying session damage.
- `SessionData.Messages` is still an unbounded write-only list; `FullSocketRecreation` still nulls `Socket` before validating credentials.
**Rollback:** Revert this commit — restores the silent catch and the suppressed core logging (and the blindness).
**Build note:** `dotnet build WhatsAppApi/WhatsAppApi.csproj` → 0 errors. Note `WhatsSocketConsole` has 10 **pre-existing** compile errors on `main` (verified by stashing these changes and rebuilding); it is not built or deployed by CI, which publishes `WhatsAppApi` only.

---

## [2026-07-16] — dc703c9 — fix: inbound messages delivered from WhatsApp's offline queue (MessageEventType.Append) were discarded
**App:** BaileysCSharp
**What changed:** Second bug in the same pipeline, uncovered immediately after the `AttachSocketEventHandlers` fix (`d46eb87`) let `Message_Upsert` run again. `Message_Upsert` processed only `MessageEventType.Notify` and dropped everything else. But `MessagesRecvSocket.cs:801` types an event `Append` — not `Notify` — whenever the incoming stanza carries an `offline` attribute, i.e. whenever WhatsApp delivers a message it queued while the client was disconnected:
```csharp
await UpsertMessage(msg.Msg, node.getattr("offline") != null ? MessageEventType.Append : MessageEventType.Notify);
```
Because this session drops with `ConnectionLost` (408) every few minutes, most real customer messages arrive in an offline flush right after a reconnect, get typed `Append`, and were silently discarded. This is the actual mechanism behind the original report ("when WhatsApp reconnects, the bot stops receiving messages") — the detached handler from `d46eb87` was the outer layer; this is the inner one.

**Fix:** `Message_Upsert` now accepts `Append` alongside `Notify`, with two guards, because `Append` is not inbound-only:
1. **`FromMe` filter** — `MessagesSendSocket.cs:282` upserts our *own* outgoing sends as `Append`, and history sync uses it too. Forwarding those to RubyManagerBot would make the bot reply to its own messages in a loop. Outbound is now explicitly skipped.
2. **De-duplication** — WhatsApp can redeliver an offline-queued message, and a reconnect storm can flush it more than once. A duplicate here means a duplicate bot reply to a real customer, so inbound is gated on message ID via new `SessionData.ProcessedMessageIds`, bounded by `PruneProcessedMessageIds` (cap 5000, 6h TTL — the TTL only needs to cover the redelivery window).

Also flattened the method to guard clauses (early `return`) instead of three nesting levels, and added `Type:` to the `[PHONE_NUMBER_TRACE]` line so Notify-vs-Append is visible in production.

**Evidence (production, session `7afd5398-…`):** after the `d46eb87` deploy (service restarted 07:16:28), the 07:28:05 test message produced exactly:
```
[DROP_TRACE] Message_Upsert fired - Session: 7afd5398-..., Type: Append, MessageCount: 1
[DROP_TRACE] Skipped - event type was Append, not Notify.
```
`Message_Upsert fired` had never appeared in any prior log, confirming `d46eb87` worked and the handler is attached. The message then hit the `Notify`-only filter. Connection log for that window shows why it was offline-queued: drops at 07:20:54, 07:26:48 and 07:27:48, with the message landing 17s after the last one.

**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`, `CHANGELOG.md`
**Why:** Inbound messages reached the handler but were thrown away based on a delivery-path detail that says nothing about whether a message is real. With the connection dropping every few minutes, `Notify`-only meant most customer messages never reached the bot.
**Known issues not addressed here:**
- `SessionData.Messages` is appended to on every upsert but **never read anywhere** in the codebase — an unbounded write-only `List<WebMessageInfo>` that grows for the life of the process. Left as-is to keep this change scoped; it should be removed or capped.
- The ~5-minute `ConnectionLost` (408) cycle is still unexplained. This fix makes the pipeline survive the drops rather than depend on them not happening, but the drops themselves remain the top open issue.
- `FullSocketRecreation` nulls `sessionData.Socket` before validating credentials (see `d46eb87` entry).
**Rollback:** Revert this commit — restores `Notify`-only handling (reintroduces the dropped-offline-message bug).
**Build note:** Verified locally — `dotnet build WhatsAppApi/WhatsAppApi.csproj` → 0 errors, no new warnings.

---

## [2026-07-16] — d46eb87 — fix: reconnection silently killed inbound messages — FullSocketRecreation never re-attached the Message.Upsert handler
**App:** BaileysCSharp
**What changed:** After a reconnect the session reported itself healthy (`state: connected`, `isConnected: true`) and outbound sends kept working, but **every inbound message was silently dropped** and nothing reached the RubyManagerBot webhook or the CRM. Root cause: `WASocket` builds a fresh `EventEmitter` in its constructor (`BaseSocket.cs:86` — `EV = new EventEmitter(config.Logger)`), so a new socket starts with zero subscribers. `StartSessionAsync` attached five handlers (`Auth.Update`, `Connection.Update`, `Message.Upsert`, `MessageHistory.Set`, `Pressence.Update`), but `FullSocketRecreation` re-attached only **two** (`Connection.Update`, `Auth.Update`). The recreated socket therefore had no `Message.Upsert` subscriber — `Message_Upsert` was never invoked at all. Because `Connection.Update` *did* survive, the session still reported connected, and outbound `sendMessage` talks to the socket directly and never needed the handler, so the failure was invisible from the dashboard.

This was a **one-way door**: `SimpleSocketRetry` reuses the existing socket object (`sessionData.Socket.MakeSocket()`) and so preserves whatever handlers are present, but once `FullSocketRecreation` swapped in a handler-less socket, no later reconnect could restore it — only a process restart or `ForceSessionRestart` (which routes through `StartSessionAsync`) would.

**Fix:** Extracted the handler wiring into a single `AttachSocketEventHandlers(socket, sessionName)` method, now called by **both** `StartSessionAsync` and `FullSocketRecreation`. The static `MessageDecoder.OnCallerPhoneNumberExtracted` subscription was deliberately left in `StartSessionAsync` only — it is process-global and survives socket recreation on its own, so moving it into the shared method would leak a subscription on every reconnect.

Also bumped the hardcoded WhatsApp Web version defaults `1043053164 → 1043263898` (`SocketConfig` default and `WaBuildHelper.Fallback`) to the build observed negotiated in production on 2026-07-16.

**Evidence (production logs, session `7afd5398-…`):**
- 2026-07-15 had 150 `[PHONE_NUMBER_TRACE]` lines and 25 inbound "Incoming message" lines; 2026-07-16 had **zero** of each.
- The `[DROP_TRACE]` diagnostics from `d27d8b1` (deployed — commit 13:12 UTC 07-15, app restarted 16:55:05 UTC 07-15, no restart since) produced **zero** output. `Message_Upsert`'s first line logs unconditionally before any filtering, which proves the method was never entered — ruling out every "message arrived but was filtered" theory.
- The 07:02:33 test message was received and decrypted — `[CALLER_PN_CACHE]` cached `351931652836@s.whatsapp.net` for MsgId `3A32F97624D5CD6174B8` — then vanished. That cache is fed by a **static** event on `MessageDecoder`, which is exactly why it survived recreation while the instance-level `EV` handlers did not. Decrypt pipeline alive, delivery pipeline detached.
- `FullSocketRecreation` ran 42× on 07-16 and 57× on 07-15 (driven by a ~10-minute `ConnectionLost`/408 cycle), so the bug fired within minutes of every process start.
- Why 07-15 still worked until 20:29 despite recreations starting 10:37: the 19:24–19:33 recreations all logged `Stored credentials invalid` and returned early — a path that bails *before* `new WASocket(config)`. Since `FullSocketRecreation` nulls `sessionData.Socket` at the top, those failures orphaned the original 16:55 socket in memory, still connected and still holding its handler, so messages kept flowing. 07-16 logged **zero** `Stored credentials invalid`, so every recreation ran to completion and installed a handler-less socket.

**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`, `BaileysCSharp/Core/Types/SocketConfig.cs`, `WhatsAppApi/Helper/WaBuildHelper.cs`, `CHANGELOG.md`
**Why:** Inbound WhatsApp messages — the entire bot pipeline — stopped reaching RubyManagerBot within ~10 minutes of any process start, while every health signal reported green. The `[DROP_TRACE]` logging from `d27d8b1` is retained for now: it is the cheapest way to confirm the fix in production (`Message_Upsert fired` should now appear after a recreation). Remove it once verified.
**Known issues not addressed here:**
- `FullSocketRecreation` sets `sessionData.Socket = null` *before* validating credentials, so the invalid-creds path returns `false` leaving `Socket == null`; a subsequent `SimpleSocketRetry` then throws `NullReferenceException`, caught and logged as a generic "retry failed", masking the cause. Validation should move ahead of the teardown.
- `MessageDecoder.OnCallerPhoneNumberExtracted` is subscribed on every `StartSessionAsync` and never unsubscribed (observed firing 4× per message). With multiple tenants this also writes each decoded number into every session's cache. Needs a redesign that carries the session identity through the decoder.
- The underlying ~10-minute `ConnectionLost` (408) cycle is unexplained and still present; this fix makes reconnection survivable, not unnecessary.
**Rollback:** Revert this commit — restores the two-handler `FullSocketRecreation` (reintroduces the dropped-inbound bug) and the previous version defaults.
**Build note:** Verified locally — `dotnet build WhatsAppApi/WhatsAppApi.csproj` → 0 errors (496 pre-existing warnings).

---

## [2026-07-13] — fix: refresh WhatsApp Web version on reconnect + bump hardcoded version defaults + name ClientOutdated (405)
**App:** BaileysCSharp
**What changed:** Investigation of a session dropping ~1h after each QR scan (reason `ConnectionLost`/408) ruled out the "client outdated" theory — the live diagnostic `waVersion` was `2.3000.1043053164`, which is the current wppconnect.io "stable current" build, so the scraper (`WaBuildHelper.GetLatestAlphaAsync`) was already returning the newest version on initial connect. But two real defects surfaced:
1. **Reconnection did not re-fetch the version.** `FullSocketRecreation` built `new SocketConfig()` without setting `config.Version`, so it fell back to the hardcoded `SocketConfig` default (`[2, 3000, 1024961288]`, ~18M revisions stale) instead of the current build. Attempts 3–6 (FullRecreation / CredentialRefresh, which routes through FullSocketRecreation) reconnected with a version WhatsApp may reject — sabotaging recovery. Fixed: `FullSocketRecreation` now calls `WaBuildHelper.GetLatestAlphaAsync()` before creating the socket, exactly like `StartSessionAsync`.
2. **Stale hardcoded version defaults.** `SocketConfig` default bumped `1024961288 → 1043053164` and `WaBuildHelper.Fallback` bumped `1042026337 → 1043053164`, both to the 2026-07-13 build, so any non-refetch path and the network-failure fallback still negotiate an accepted version.
3. **`ConnectionLost` was mislabeled `405` in the CHANGELOG** (it is `408`; `405` is `clientOutdated`). Corrected the two prior entries, and added a named `DisconnectReason.ClientOutdated = 405` member plus a distinct log branch in `BaseSocket.OnFailure` so a genuine server-sent `<failure reason="405">` is surfaced clearly instead of falling through as an unnamed enum value.
**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`, `WhatsAppApi/Helper/WaBuildHelper.cs`, `BaileysCSharp/Core/Types/SocketConfig.cs`, `BaileysCSharp/Core/Events/DisconnectReason.cs`, `BaileysCSharp/Core/Sockets/BaseSocket.cs`, `CHANGELOG.md`
**Why:** The current ~1h drop is a local keepalive/stream-end (408) event, not a version rejection — but when a drop does occur, the recovery path was reconnecting with a stale version, and a real 405 would have been opaque. These changes make every reconnect use a current version and make 405 diagnosable. The dashboard `WA Version` on `status.html` is already dynamic (reads `waVersion` from the API), so it needs no literal bump — it now reflects the refreshed value.
**Rollback:** Revert the `config.Version` line in `FullSocketRecreation`, the two version-default bumps, the `ClientOutdated` enum member, and the `OnFailure` 405 branch.
**Build note:** Verify with `dotnet build WhatsAppApi/WhatsAppApi.csproj` locally before relying on the deploy.

---

## [2026-07-13] — eee34d7 — fix: reconnection self-heals invalidated credentials — ForceSessionRestart reachable at attempt 7, wipes dead credentials, requests fresh QR via email alert
**App:** BaileysCSharp
**What changed:** The reconnection ladder had a dead rung: `ForceSessionRestart` was strategy #9+ but `ConnectionLost` (reason 408) capped at 8 attempts, so it never ran. When WhatsApp invalidates credentials server-side (brief-connect / immediate-disconnect cycles), all 8 strategies retried the *same* dead credentials, all failed, and the session sat in slow retry forever with no path to recovery. Five changes:
1. **Strategy schedule redesign** — `GetMaxAttemptsForDisconnectReason` for `ConnectionLost`/`TimedOut` reduced 8 → 7, and `GetReconnectionStrategy` now maps `<=2 SimpleRetry`, `<=4 FullRecreation`, `<=6 CredentialRefresh`, `7 ForceSessionRestart`. `ForceSessionRestart` is now actually reachable (attempt 7). The `ExecuteReconnectionStrategy` switch key was renamed `ForceRestart` → `ForceSessionRestart` to match.
2. **`ForceSessionRestart` self-heals** — instead of stop+start (which reused the dead creds), it now stops the session, backs up and removes the credentials file (renamed to `{sessionName}_creds.json.bak` via new `BackupAndDeleteCredentials`) so `StartSessionAsync` is forced to generate a fresh QR, restarts, and — if still not connected — fires a QR-scan-needed tenant alert. It returns `true` (QR-waiting is a valid recovery state) so the caller does NOT drop into the endless slow-retry loop.
3. **Fixed disconnect reason being overwritten mid-chain** — `ScheduleReconnectionAsync` now snapshots the disconnect reason (`originalReason`) once at chain start and uses it for the attempt cap and delay formula throughout, instead of re-reading `sessionData.LastDisconnectReason` (which a failing recreated socket's Close event overwrites, previously corrupting the delay formula from attempt 4 onward).
4. **Slow retry alternates strategies** — `SlowRetryLoopAsync` now alternates `FullSocketRecreation` (odd attempts) and `SimpleSocketRetry` (even attempts). Simple retry can never recover a session with stale credentials, so the full recreation path gives the 30-minute loop a real chance.
5. **New QR-scan alert** — `NotifyQRScanNeededAsync` POSTs to the CRM `/api/whatsappconnection/qr-scan-needed` (same fire-and-forget pattern as `NotifyConnectionDownAsync`) with `clientExternalId`, `disconnectedSince`, `dashboardUrl`, and a user-facing message. If the endpoint doesn't exist yet the failure is logged (with the dashboard URL) so the code path is in place.
**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`
**Why:** Sessions whose credentials WhatsApp invalidated server-side were unrecoverable without a human manually deleting creds and re-scanning — they retried dead credentials forever. This makes the service self-heal (fresh QR) and, when a human scan is unavoidable, automatically alert the tenant with a dashboard link instead of the dead session going unnoticed.
**Rollback:** Revert the changes in `GetMaxAttemptsForDisconnectReason`, `GetReconnectionStrategy`, `ExecuteReconnectionStrategy`, `ForceSessionRestart` (+ remove `BackupAndDeleteCredentials`), `ScheduleReconnectionAsync` (originalReason snapshot), `SlowRetryLoopAsync`, and remove `NotifyQRScanNeededAsync`.
**Build note:** Not built in the automation sandbox (no .NET SDK, package.microsoft.com blocked). Verify with `dotnet build WhatsAppApi/WhatsAppApi.csproj` locally before committing.

---

## [2026-07-13] — fix: three bugs in post-QR-scan reconnection — session killed by stale chain, wrong QR timer, inflated attempt counter
**App:** BaileysCSharp
**What changed:** Commit `196a855` (reconnect-storm fix) introduced three bugs that together prevented the WhatsApp session from staying connected after a QR scan:
1. **`ReconnectAttempts` not reset on `Open`** — when `WAConnectionState.Open` fired after a QR scan, the counter kept any pre-scan attempts, so the pairing disconnect's new chain started mid-budget and exhausted faster.
2. **`QRSessionStartTime` not reset on `Open`** — the QR timeout was never restarted after a successful scan. When reconnection called `MakeSocket()` and generated a new QR, the 10-minute timeout checked elapsed time since the *original* QR, not the new one — so a scan at t=8.5min meant any reconnect QR timed-out within ~90 seconds and the session was killed.
3. **`ReconnectLock` silently dropping the post-QR `Close`** — WhatsApp fires a brief `ConnectionLost` right after pairing (normal pairing-handshake flow). If a stale chain from a pre-scan disconnect held the lock, this critical disconnect was dropped (`Wait(0)` returned false) and never handled. The stale chain kept retrying with old context while the real reconnect was never attempted.
**Fix:** `Open` branch in `Connection_UpdateAsync` now resets `ReconnectAttempts`, `QRSessionStartTime`, and cancels/replaces `sessionData.ReconnectCts`. `ScheduleReconnectionAsync` uses the CTS for cancellable `Task.Delay` (chain wakes immediately on `Open`) and sets `PendingReconnectNeeded = true` when a `Close` is dropped; the `finally` block checks this flag and fires a fresh chain once the lock is released.
**Files touched:** `WhatsAppApi/Services/WhatsAppServiceV2.cs`
**Why:** After the storm fix, users could scan the QR and the app would acknowledge the connection briefly, then show an error — the session never stayed connected. Root cause confirmed via production diagnostics (`/allDiagnostics`): session stuck in `reconnecting` state at attempt #3 shortly after a scan, `qrElapsedMinutes: 8.5`.
**Rollback:** Revert the three changes in `Connection_UpdateAsync`'s Open branch and restore `ScheduleReconnectionAsync` and `SlowRetryLoopAsync` to not use the CTS.

---

## [2026-07-13] — 196a855 — fix: serialize WhatsApp reconnection to stop disconnect storms, add slow self-heal retry + tenant alert
**App:** BaileysCSharp
**What changed:** `Connection_UpdateAsync` called `ScheduleReconnectionAsync` unguarded on every socket-close event. A socket recreated mid-reconnection (`FullSocketRecreation`) that itself failed fast would raise its own close event, starting a second concurrent reconnection chain racing on the same `ReconnectAttempts` counter - confirmed live via production logs (`whatsapp.rubymanager.app/logs.html`) showing dozens of `disconnected with reason: ConnectionLost (408)` within ~10 minutes, far more than one backoff chain could produce alone. `SessionData` now holds a `ReconnectLock` (`SemaphoreSlim`) so at most one reconnection chain runs per session at a time; the old recursive fire-and-forget continuation is now a single guarded loop. Once fast attempts are exhausted, the session no longer dies silently forever: it retries every 30 minutes indefinitely (bounded for free by the existing 72h inactive-session cleanup in `PerformHealthCheck`) and fires a one-time alert to the CRM (new `NotifyConnectionDownAsync`, paired with `w4l_kiteschoolmanager`'s new `/api/whatsappconnection/connection-alert` endpoint) so the tenant is emailed instead of a dead session going unnoticed.
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
