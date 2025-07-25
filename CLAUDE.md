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

## Deployment

The project includes GitHub Actions CI/CD pipeline that:
- Builds and tests the solution
- Publishes for Linux x64 runtime
- Deploys to configured environments based on appsettings.json configuration
- Supports dev, demo, and production environments
- **Includes session persistence setup** - automatically creates `/sessions/` directory with proper permissions