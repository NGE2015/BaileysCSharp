# WhatsApp API - Configurable Rate Limiting Deployment

## 📋 **What Was Implemented**

✅ **Configurable Rate Limiting** - Rate limits now read from appsettings.json
✅ **Enhanced Logging Controls** - Turn detailed logging on/off per category
✅ **Unix Socket Only** - No HTTP ports, maximum security
✅ **Production Ready** - Optimized for your Ubuntu server

## 🚀 **Deployment Steps**

### 1. **Build and Copy Files**

From your development machine, copy these updated files to your server:

```bash
# Copy the built application
scp -r /root/BaileysCSharp/WhatsAppApi/bin/Debug/net8.0/* user@your-server:/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/

# Or if using Release build:
# scp -r /root/BaileysCSharp/WhatsAppApi/bin/Release/net8.0/* user@your-server:/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/
```

### 2. **Update appsettings.json on Server**

Create or update `/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/appsettings.json`:

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
    "LogRateLimit": false,
    "LogSessionEvents": true,
    "LogErrorDetails": true
  },
  "RateLimiting": {
    "MaxRequestsPerMinute": 200,
    "MaxRequestsPerHour": 2000,
    "MaxQRRequestsPerMinute": 50,
    "MaxSessionOperationsPerMinute": 1000
  },
  "Kestrel": {
    "UnixSocketPath": "/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock"
  },
  "publish": {
    "Env": "prod"
  },
  "AllowedHosts": "*"
}
```

### 3. **Restart Service**

```bash
sudo systemctl restart WhatsApp.RubyManager.app.service
sudo systemctl status WhatsApp.RubyManager.app.service
```

## ⚙️ **Rate Limiting Configuration**

### Current Testing Values
```json
"RateLimiting": {
  "MaxRequestsPerMinute": 200,     // Was 30 - now 200
  "MaxRequestsPerHour": 2000,      // Was 300 - now 2000  
  "MaxQRRequestsPerMinute": 50,    // Was 5 - now 50
  "MaxSessionOperationsPerMinute": 1000  // Sessions per minute
}
```

### Production Recommendations
```json
"RateLimiting": {
  "MaxRequestsPerMinute": 100,
  "MaxRequestsPerHour": 1000,
  "MaxQRRequestsPerMinute": 20,
  "MaxSessionOperationsPerMinute": 50
}
```

### For Heavy Testing (No Limits)
```json
"RateLimiting": {
  "MaxRequestsPerMinute": 10000,
  "MaxRequestsPerHour": 100000,
  "MaxQRRequestsPerMinute": 1000,
  "MaxSessionOperationsPerMinute": 5000
}
```

## 🔧 **Logging Configuration**

### Disable Rate Limit Logging (Cleaner Logs)
```json
"DetailedLogging": {
  "LogRateLimit": false  // No more rate limit noise in logs
}
```

### Full Debug Mode
```json
"DetailedLogging": {
  "Enabled": true,
  "LogConnections": true,
  "LogMessages": true,
  "LogQRGeneration": true,
  "LogRateLimit": true,    // Show all rate limiting
  "LogSessionEvents": true,
  "LogErrorDetails": true
}
```

### Minimal Logging (Production)
```json
"DetailedLogging": {
  "Enabled": true,
  "LogConnections": false,
  "LogMessages": false,
  "LogQRGeneration": false,
  "LogRateLimit": false,
  "LogSessionEvents": true,   // Keep session events
  "LogErrorDetails": true     // Always keep errors
}
```

## 🔍 **Testing Your Changes**

### 1. Verify Rate Limits Work
```bash
# Test rapid requests (should not hit rate limit now)
for i in {1..60}; do
  curl --unix-socket /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock \
       http://localhost/v2/WhatsAppControllerV2/getAsciiQRCode?sessionName=test
  echo "Request $i completed"
done
```

### 2. Check Logs Are Clean
```bash
# Should see much less rate limiting noise
journalctl -u WhatsApp.RubyManager.app.service -f
```

### 3. Test Configuration Changes
```bash
# Edit config
sudo nano /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/appsettings.json

# Restart service  
sudo systemctl restart WhatsApp.RubyManager.app.service

# Verify new limits are loaded
journalctl -u WhatsApp.RubyManager.app.service --since "1 minute ago"
```

## 🎯 **Key Benefits**

- ✅ **No more rate limit blocking** during testing
- ✅ **Configurable without code changes** - edit JSON and restart
- ✅ **Environment-specific settings** - different limits for dev/prod
- ✅ **Cleaner logs** - rate limiting noise turned off
- ✅ **Hot-swappable** - change limits without rebuilding

## 🔄 **Quick Configuration Changes**

### Increase Limits for Heavy Testing
```bash
# Edit the file
sudo nano /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/appsettings.json

# Change MaxRequestsPerMinute to 500
# Change MaxQRRequestsPerMinute to 100

# Restart
sudo systemctl restart WhatsApp.RubyManager.app.service
```

### Turn Off All Rate Limiting Temporarily
```json
"RateLimiting": {
  "MaxRequestsPerMinute": 999999,
  "MaxRequestsPerHour": 999999,
  "MaxQRRequestsPerMinute": 999999,
  "MaxSessionOperationsPerMinute": 999999
}
```

## 📊 **Monitoring**

### Check Rate Limiting Status
```bash
# See initialization message with current limits
journalctl -u WhatsApp.RubyManager.app.service --since "1 minute ago" | grep "Rate limiting middleware initialized"
```

### Monitor Rate Limit Events (if enabled)
```bash
# Turn on rate limit logging first, then:
journalctl -u WhatsApp.RubyManager.app.service -f | grep "Rate limit"
```

Now you have full control over rate limiting without touching code! 🎉