# WhatsApp API - Unix Socket Only Deployment

This document explains how to deploy the WhatsApp API service exclusively using Unix sockets (no HTTP/HTTPS ports).

## Overview

The WhatsApp API is now configured for **Unix socket only** deployment:
- ✅ **No HTTP/HTTPS ports** - Only Unix socket communication
- ✅ **Enhanced security** - Network isolation through socket files
- ✅ **Comprehensive logging** - Detailed monitoring and debugging
- ✅ **Production ready** - Systemd service with auto-restart

## Configuration

### Socket Configuration
```json
{
  "Kestrel": {
    "UnixSocketPath": "/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock"
  }
}
```

### Security Features
- **No port binding** - Application will not listen on any TCP/UDP ports
- **Unix socket permissions** - Automatically set to 777 for accessibility
- **Directory auto-creation** - Socket directory created automatically
- **Cleanup on restart** - Old socket files removed on startup

## Deployment Steps

### 1. Setup Logging and Service
```bash
cd /root/BaileysCSharp/WhatsAppApi/scripts
./setup-logging.sh
```

### 2. Install and Enable Service
```bash
# Enable auto-start on boot
sudo systemctl enable whatsapp-api

# Start service
sudo systemctl start whatsapp-api
```

### 3. Verify Unix Socket
```bash
# Check socket exists
ls -la /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock

# Test connectivity
./scripts/unix-socket-test.sh
```

## Service Management

### Quick Commands (After Setup)
```bash
whatsapp-start          # Start service
whatsapp-stop           # Stop service  
whatsapp-restart        # Restart service
whatsapp-status         # Check status
whatsapp-logs live      # Follow live logs
```

### Manual Commands
```bash
# Service control
sudo systemctl start whatsapp-api
sudo systemctl stop whatsapp-api
sudo systemctl restart whatsapp-api
sudo systemctl status whatsapp-api

# Log monitoring
journalctl -u whatsapp-api -f
tail -f logs/whatsapp-*.log
```

## Testing Unix Socket

### Using curl
```bash
# Test connection status
curl --unix-socket /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock \
     http://localhost/WhatsApp/connectionStatus

# Get QR code
curl --unix-socket /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock \
     http://localhost/WhatsApp/getQRCode

# Send message
curl --unix-socket /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock \
     -X POST \
     -H "Content-Type: application/json" \
     -d '{"RemoteJid":"1234567890@s.whatsapp.net","Message":"Hello World"}' \
     http://localhost/WhatsApp/sendMessage
```

### Using nc (netcat)
```bash
echo -e 'GET /WhatsApp/connectionStatus HTTP/1.1\r\nHost: localhost\r\n\r\n' | \
nc -U /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock
```

## Troubleshooting

### Common Issues

1. **Socket not created**
   ```bash
   # Check service logs
   journalctl -u whatsapp-api --no-pager
   
   # Check directory permissions
   ls -la /home/RubyManager/web/whatsapp.rubymanager.app/
   ```

2. **Permission denied**
   ```bash
   # Fix socket permissions
   chmod 777 /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock
   
   # Check directory ownership
   ls -la /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/
   ```

3. **Service won't start**
   ```bash
   # Check configuration
   dotnet run --project /root/BaileysCSharp/WhatsAppApi/WhatsAppApi.csproj --dry-run
   
   # Check dependencies
   dotnet restore /root/BaileysCSharp/WhatsAppApi/WhatsAppApi.csproj
   ```

4. **Connection refused**
   ```bash
   # Verify socket exists and is listening
   ./scripts/unix-socket-test.sh
   
   # Check no ports are bound
   sudo ss -tlnp | grep -E ":80|:443|:5000|:5001"  # Should return nothing
   ```

## Nginx Integration

If using nginx as a reverse proxy:

```nginx
upstream whatsapp_api {
    server unix:/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock;
}

server {
    listen 80;
    server_name whatsapp.rubymanager.app;
    
    location / {
        proxy_pass http://whatsapp_api;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

## Security Benefits

### Network Isolation
- **No network ports** - Eliminates network-based attacks
- **File system permissions** - Access controlled by Unix permissions
- **Process isolation** - Systemd security constraints applied

### Systemd Security
- `PrivateNetwork=false` - Network access for WhatsApp connections
- `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6` - Limited address families
- `IPAddressDeny=any` / `IPAddressAllow=localhost` - Network restrictions
- `NoNewPrivileges=true` - Privilege escalation prevention

## Logging and Monitoring

### Log Locations
- **Application logs**: `logs/whatsapp-unix-{Date}.log`
- **System logs**: `journalctl -u whatsapp-api`
- **Socket activity**: Logged in application logs

### Monitoring Commands
```bash
# Monitor socket connections
sudo lsof /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock

# Check socket activity
sudo ss -x | grep app.sock

# Monitor log files
whatsapp-logs live
```

## Performance Considerations

### Unix Socket Benefits
- **Lower latency** - No network stack overhead
- **Higher throughput** - Direct kernel communication
- **Better security** - File system based access control

### Resource Usage
- **Memory**: Lower memory usage (no network buffers)
- **CPU**: Reduced CPU overhead (no network processing)
- **I/O**: Direct file descriptor communication

## Migration from Port-based

If migrating from HTTP/HTTPS ports:

1. **Update client applications** to use Unix socket
2. **Configure reverse proxy** (nginx/apache) if needed
3. **Test connectivity** with provided scripts
4. **Monitor logs** for any connection issues
5. **Update monitoring tools** to check socket instead of ports

## Production Checklist

- [ ] Unix socket path configured correctly
- [ ] Socket directory exists with proper permissions
- [ ] Systemd service installed and enabled
- [ ] Logging configured and working
- [ ] Socket connectivity tested
- [ ] No unintended port bindings
- [ ] Monitoring setup (optional)
- [ ] Backup and recovery procedures documented