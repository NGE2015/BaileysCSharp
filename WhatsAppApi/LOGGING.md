# WhatsApp API Comprehensive Logging Setup

This document explains the enhanced logging system with configurable toggles and monitoring capabilities.

## Overview

The WhatsApp API now has comprehensive logging with:
- **Structured logging** using Serilog
- **Configurable logging levels** per component
- **Multiple output destinations** (console, files, systemd journal)
- **Detailed logging toggles** you can turn on/off
- **Log rotation** and management
- **Easy viewing commands**

## Configuration

### Logging Settings (appsettings.json)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "WhatsAppApi": "Debug",
      "BaileysCSharp": "Debug"
    }
  },
  "DetailedLogging": {
    "Enabled": true,
    "LogConnections": true,
    "LogMessages": true,
    "LogQRGeneration": true,
    "LogRateLimit": true,
    "LogSessionEvents": true,
    "LogErrorDetails": true
  }
}
```

### Toggle Options

You can control what gets logged by editing these settings:

- **`Enabled`**: Master switch for detailed logging
- **`LogConnections`**: WebSocket connections/disconnections
- **`LogMessages`**: WhatsApp message sending/receiving
- **`LogQRGeneration`**: QR code generation and scanning
- **`LogRateLimit`**: Rate limiting events
- **`LogSessionEvents`**: Session start/stop/restore events
- **`LogErrorDetails`**: Detailed error information

### To Turn Off Detailed Logging

Set `"Enabled": false` in the `DetailedLogging` section, or turn off specific categories:

```json
"DetailedLogging": {
  "Enabled": true,
  "LogConnections": false,    // Turn off connection logs
  "LogMessages": false,       // Turn off message logs
  "LogQRGeneration": true,    // Keep QR logs
  "LogRateLimit": false,      // Turn off rate limit logs
  "LogSessionEvents": true,   // Keep session logs
  "LogErrorDetails": true     // Always keep error details
}
```

## Log Files

### File Locations
- **Main logs**: `WhatsAppApi/logs/whatsapp-YYYYMMDD.log`
- **Development logs**: `WhatsAppApi/logs/whatsapp-dev-YYYYMMDD.log`
- **Legacy app.log**: `WhatsAppApi/app.log` (if running manually)

### Log Rotation
- **Daily rotation**: New file each day
- **Size limit**: 10MB (production), 50MB (development)
- **Retention**: 7 days (production), 14 days (development)
- **Automatic cleanup**: Old files deleted automatically

## Viewing Logs

### Quick Commands (After Setup)

```bash
# Follow live logs
whatsapp-logs live

# Show today's logs (last 100 lines)
whatsapp-logs today

# Show only error logs
whatsapp-logs errors

# Follow systemd journal
whatsapp-logs journal-live

# Show logging status
whatsapp-logs status

# Show more lines
whatsapp-logs today -n 500
```

### Manual Commands

```bash
# View latest log file
tail -f /root/BaileysCSharp/WhatsAppApi/logs/whatsapp-*.log

# Show errors only
grep -i "error\|exception\|fatal" /root/BaileysCSharp/WhatsAppApi/logs/whatsapp-*.log

# Follow systemd journal
journalctl -u whatsapp-api -f

# Show recent journal entries
journalctl -u whatsapp-api --since "1 hour ago"
```

## Service Management

### Setup Commands

```bash
# Install logging setup and systemd service
cd /root/BaileysCSharp/WhatsAppApi/scripts
./setup-logging.sh

# Enable auto-start on boot
sudo systemctl enable whatsapp-api
```

### Service Control (After Setup)

```bash
# Start service
whatsapp-start
# or: sudo systemctl start whatsapp-api

# Stop service
whatsapp-stop
# or: sudo systemctl stop whatsapp-api

# Restart service
whatsapp-restart
# or: sudo systemctl restart whatsapp-api

# Check status
whatsapp-status
# or: sudo systemctl status whatsapp-api
```

## Log Format

### Structured Format
```
[2024-07-20 11:30:45.123 +00:00 INF] WhatsAppApi.Services.LoggingService: [CONNECTION] Unix socket created successfully at /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock
[2024-07-20 11:30:46.456 +00:00 DBG] WhatsAppApi.Middleware.RateLimitingMiddleware: [DEBUG] Rate limiting middleware initialized with limits: 30/min, 300/hour
[2024-07-20 11:30:47.789 +00:00 WRN] WhatsAppApi.Middleware.RateLimitingMiddleware: Rate limit exceeded for client 192.168.1.100 on QRCode: 6 requests in last minute (limit: 5)
[2024-07-20 11:30:48.012 +00:00 ERR] WhatsAppApi.Services.WhatsAppServiceV2: [ERROR] Failed to send message to 1234567890@s.whatsapp.net
```

### Log Categories
- **[CONNECTION]**: WebSocket and network events
- **[MESSAGE]**: WhatsApp message operations
- **[QR_CODE]**: QR code generation and scanning
- **[SESSION]**: Session management events
- **[ERROR]**: Error conditions
- **[DEBUG]**: Debug information
- **[INFO]**: General information
- **[WARNING]**: Warning conditions

## Troubleshooting

### Common Issues

1. **Service won't start**
   ```bash
   # Check logs
   journalctl -u whatsapp-api --no-pager
   
   # Check socket permissions
   ls -la /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/
   ```

2. **No log files appearing**
   ```bash
   # Check directory permissions
   ls -la /root/BaileysCSharp/WhatsAppApi/logs/
   
   # Create directory if missing
   mkdir -p /root/BaileysCSharp/WhatsAppApi/logs
   ```

3. **Too much logging**
   ```bash
   # Edit configuration
   nano /root/BaileysCSharp/WhatsAppApi/appsettings.json
   
   # Set DetailedLogging.Enabled to false
   # Or disable specific categories
   
   # Restart service
   whatsapp-restart
   ```

4. **Check current running application logs**
   ```bash
   # If running manually with nohup
   tail -f /root/BaileysCSharp/WhatsAppApi/app.log
   
   # If running as systemd service
   journalctl -u whatsapp-api -f
   ```

## Performance Considerations

- **File I/O**: Logs are written asynchronously to minimize performance impact
- **Log levels**: Set appropriate levels for production (avoid Trace/Debug)
- **Disk space**: Monitor disk usage, logs rotate automatically
- **Network**: Detailed logging may increase network traffic for debugging

## Security Notes

- Log files contain operational data but no credentials
- Unix socket permissions set to 777 for accessibility
- Systemd service runs with appropriate security constraints
- Rate limiting events are logged for security monitoring

## Integration with External Systems

The structured logging format makes it easy to integrate with:
- **Log aggregation**: ELK Stack, Splunk, etc.
- **Monitoring**: Grafana, Prometheus
- **Alerting**: Based on error patterns
- **Analytics**: Message volume, connection patterns