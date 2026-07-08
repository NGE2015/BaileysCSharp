# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

BaileysCSharp is a C# implementation of the Baileys WhatsApp Web API library. The project consists of four main components:

1. **BaileysCSharp** - Core library containing WhatsApp protocol implementation
2. **WhatsAppApi** - ASP.NET Core Web API service providing REST endpoints
3. **WhatsSocketConsole** - Console application for testing and demos
4. **BaileysCSharp.Tests** - NUnit test project

## Common Development Commands

### Building the Solution
```bash
# Build entire solution
dotnet build BaileysCSharp.sln

# Build specific projects
dotnet build BaileysCSharp/BaileysCSharp.csproj
dotnet build WhatsAppApi/WhatsAppApi.csproj
dotnet build WhatsSocketConsole/WhatsSocketConsole.csproj
```

### Running Tests
```bash
# Run all tests
dotnet test BaileysCSharp.Tests/BaileysCSharp.Tests.csproj

# Run tests with verbose output
dotnet test BaileysCSharp.Tests/BaileysCSharp.Tests.csproj --verbosity normal
```

### Running Applications
```bash
# Run WhatsApp API service
dotnet run --project WhatsAppApi/WhatsAppApi.csproj

# Run console application
dotnet run --project WhatsSocketConsole/WhatsSocketConsole.csproj
```

### Publishing for Production
```bash
# Publish WhatsApp API for Linux deployment
dotnet publish WhatsAppApi/WhatsAppApi.csproj -c Release -o publish/ -r linux-x64
```

## Architecture Overview

### Core Library Structure

- **Core/Sockets/** - WebSocket connection management and protocol handling
  - `WASocket` - Main WhatsApp socket implementation
  - `BaseSocket`, `ChatSocket`, `GroupSocket` - Specialized socket functionality
  - `Client/WebSocketClient` - Low-level WebSocket communication

- **Core/Signal/** - End-to-end encryption using Signal protocol
  - `SignalRepository` - Manages cryptographic sessions
  - `MessageDecryptor` - Handles message decryption

- **Core/NoSQL/** - Data persistence layer
  - `BaseKeyStore`, `FileKeyStore`, `MemoryStore` - Storage implementations
  - Supports both file-based and in-memory storage

- **Core/Types/** - Data models and type definitions
  - `SocketConfig` - Socket configuration and settings
  - `AuthenticationState`, `ConnectionState` - Connection management
  - `MessageModel`, `Chat`, `Contact` - WhatsApp entities

- **Proto/** - Protocol buffer definitions for WhatsApp messages

### API Service Architecture

- **Controllers/** - REST API endpoints
  - `WhatsAppController` - Message sending, QR code generation, connection status
  - `WhatsAppControllerV2` - Enhanced version with additional features

- **Services/** - Business logic and WhatsApp integration
  - `WhatsAppService` - Core WhatsApp functionality
  - `WhatsAppHostedService` - Background service for connection management

- **Middleware/** - Cross-cutting concerns
  - `RateLimitingMiddleware` - API rate limiting

### Key Features

- QR code authentication for WhatsApp Web
- **Fixed Session Persistence** (July 2025) - Sessions now persist across deployments
- Message sending/receiving with encryption
- Multi-tenant session management
- Automatic session migration from legacy locations
- Group chat management
- Newsletter support
- Rate limiting and health check cleanup

## Important Configuration

- The API service uses .NET 8.0 target framework
- Tests use NUnit framework
- Protocol buffers are compiled automatically during build
- Session data is stored in configurable directories (CreateSession/, TEST/, etc.)
- Rate limiting is implemented for API endpoints

## Critical Session Persistence Fix (July 2025)

**IMPORTANT**: This project includes a critical fix for session persistence issues where WhatsApp sessions were dropping daily and requiring QR code re-scan.

### Fix Summary
- **Issue**: Sessions stored in variable assembly paths that changed with deployments
- **Solution**: Fixed storage path to `/home/RubyManager/web/whatsapp.rubymanager.app/sessions/`
- **Migration**: Automatic migration from legacy locations with fallback safety
- **Status**: ✅ IMPLEMENTED and PRODUCTION-READY

### Key Changes
1. **SocketConfig.cs**: Fixed `CacheRoot` property to use consistent session storage path
2. **WhatsAppServiceV2.cs**: Added `FindOrMigrateCredentialsFile()` method for automatic migration
3. **Health Checks**: Extended session retention from 24 hours to 7 days

### Documentation
For complete implementation details, deployment instructions, and troubleshooting:
- See `WHATSAPP_SESSION_PERSISTENCE_FIX.md` for technical implementation
- See `DEPLOYMENT-INSTRUCTIONS.md` for deployment steps
- See `UNIX-SOCKET-DEPLOYMENT.md` for production configuration

## Critical QR Generation Fix (June 2026)

**Merged in `feature/dashboard-v2`** — QR codes were silently failing to generate in production.

### Root cause
`WaBuildHelper.cs` regex was `>([0-9]+\.[0-9]+\.[0-9]+-alpha)<`, which included `-alpha` inside
the capture group. `uint.Parse("1031772734-alpha")` always threw, so the scraper always fell back
to a hardcoded version from January 2026. WhatsApp Web silently rejects old versions — no QR.

### Fix
- Regex changed to `>([0-9]+\.[0-9]+\.[0-9]+)-alpha<` (numeric part only)
- `uint.Parse` replaced with `uint.TryParse` for safety
- Fallback updated: `{ 2, 3000, 1042026337 }` (June 2026 alpha build)
- `WaBuildHelper.LastResolvedVersion` added — updated on each successful fetch, exposed via dashboard

### Files changed
- `WhatsAppApi/Helper/WaBuildHelper.cs`
- `WhatsAppApi/Services/WhatsAppServiceV2.cs` — `SessionData.RawQrData` property added
- `WhatsAppApi/Controllers/WhatsAppControllerV2.cs` — `allDiagnostics` endpoint added

---

## Dashboard v2 (June 2026)

Password-protected HTML dashboards at `/status.html` and `/logs.html`.

### Auth
- `DashboardController.cs` — POST `/api/dashboard/login` validates password, sets `dash_tok` HttpOnly cookie (30-day MaxAge)
- `DashboardAuthMiddleware.cs` — protects path prefix `/api/logs`
- Salt: `_ruby_dash_2026`, hashed with SHA256
- Password configured in `appsettings.json` → `"Dashboard": { "Password": "..." }`
- Middleware registered in `Program.cs` after `UseStaticFiles()` and before `RateLimitingMiddleware`

### allDiagnostics endpoint
`GET /v2/WhatsAppControllerV2/allDiagnostics` — returns every session enriched with:
- `state` — one of: `connected`, `qr_pending`, `logged_out`, `bad_session`, `restart_required`, `reconnecting`, `disconnected`, `connecting`
- `why` — human-readable explanation of the current state
- `action` — what the operator should do
- `rawQrData` — raw WhatsApp QR string (non-null only when `qr_pending`), used by `qrcode.js` in browser
- `waVersion` — what version was actually negotiated with WhatsApp (from `WaBuildHelper.LastResolvedVersion`)

### Route naming gotcha
ASP.NET Core's `[controller]` token strips "Controller" **only** when it is the last word in the
class name. `WhatsAppControllerV2` ends in "V2" so the full name is used:
- Correct: `/v2/WhatsAppControllerV2/startSession`
- Wrong:   `/v2/WhatsApp/startSession` (404)

---

## Local Windows Development with Caddy

### Setup (one-time)
Scripts live at `C:\Caddy\WhatsAppApi\` (not in git — Windows-only). The app runs as
`ASPNETCORE_ENVIRONMENT=Development` which loads `appsettings.Development.json`:
- `ListenLocalhost: true`, `LocalhostPort: 5284`

If you need `WindowsDev` overrides (separate socket path), use `appsettings.WindowsDev.json`.

### Start
```powershell
C:\Caddy\WhatsAppApi\START_ALL.ps1          # opens two terminals: app + Caddy
# App: localhost:5284   Caddy proxy: localhost:5285
```

### Test
```powershell
# Start a session
curl -X POST http://localhost:5285/v2/WhatsAppControllerV2/startSession -H "Content-Type: application/json" -d '{"SessionName":"test"}'

# Check diagnostics (includes QR data)
curl http://localhost:5285/v2/WhatsAppControllerV2/allDiagnostics
```

### Windows Unix socket behavior
The socket path `/home/RubyManager/.../app.sock` resolves to `C:\home\...` on Windows.
`Directory.CreateDirectory` succeeds silently — no action needed. AF_UNIX is available on
Windows 10 build 17063+ (April 2018 Update and later).

---

## CI/CD Branch Triggers

`main.yml` triggers CI/CD on pushes to:
- `main`
- `feature/dashboard-v2` (added June 2026 — remove once fully merged)

The workflow reads `publish.Env` from `appsettings.json` (`"dev"` or `"prod"`) to select the
SSH deployment target. **Never leave `"Env": "dev"` in `appsettings.json` before merging to main.**

---

## Deployment

The project includes GitHub Actions CI/CD pipeline that:
- Builds and tests the solution
- Publishes for Linux x64 runtime
- Deploys to configured environments based on appsettings.json configuration
- Supports dev, demo, and production environments
- **Includes session persistence setup** - automatically creates `/sessions/` directory with proper permissions