#!/bin/bash

# Unix Socket Test Script for WhatsApp API
# This script tests and verifies Unix socket connectivity

SOCKET_PATH="/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/app.sock"
API_BASE="http://localhost"

echo "WhatsApp API Unix Socket Test"
echo "============================="
echo "Socket Path: $SOCKET_PATH"
echo ""

# Check if socket exists
echo "1. Checking if Unix socket exists..."
if [[ -S "$SOCKET_PATH" ]]; then
    echo "   ✅ Unix socket exists: $SOCKET_PATH"
    ls -la "$SOCKET_PATH"
else
    echo "   ❌ Unix socket not found: $SOCKET_PATH"
    echo "   Make sure the WhatsApp API service is running"
    exit 1
fi

# Check socket permissions
echo ""
echo "2. Checking socket permissions..."
PERMS=$(stat -c %a "$SOCKET_PATH" 2>/dev/null)
if [[ "$PERMS" == "777" ]]; then
    echo "   ✅ Socket permissions are correct: $PERMS"
else
    echo "   ⚠️  Socket permissions: $PERMS (expected: 777)"
    echo "   Run: chmod 777 $SOCKET_PATH"
fi

# Check if socket is listening
echo ""
echo "3. Checking if socket is accepting connections..."
if timeout 5 bash -c "echo > /dev/tcp/localhost/80" 2>/dev/null; then
    echo "   ❌ Port 80 is open - this should not happen for Unix socket only"
else
    echo "   ✅ No TCP ports detected - Unix socket only configuration"
fi

# Test socket connectivity with curl
echo ""
echo "4. Testing socket connectivity..."
if command -v curl >/dev/null 2>&1; then
    echo "   Testing HTTP request via Unix socket..."
    
    # Test connection status endpoint
    RESPONSE=$(curl --unix-socket "$SOCKET_PATH" \
                   --silent \
                   --write-out "HTTP_CODE:%{http_code}" \
                   --max-time 10 \
                   "$API_BASE/WhatsApp/connectionStatus" 2>/dev/null)
    
    if [[ $? -eq 0 ]]; then
        HTTP_CODE=$(echo "$RESPONSE" | sed -n 's/.*HTTP_CODE:\([0-9]*\).*/\1/p')
        BODY=$(echo "$RESPONSE" | sed 's/HTTP_CODE:[0-9]*$//')
        
        if [[ "$HTTP_CODE" == "200" ]]; then
            echo "   ✅ API responded successfully (HTTP $HTTP_CODE)"
            echo "   Response: $BODY"
        else
            echo "   ⚠️  API responded with HTTP $HTTP_CODE"
            echo "   Response: $BODY"
        fi
    else
        echo "   ❌ Failed to connect to API via Unix socket"
    fi
else
    echo "   ⚠️  curl not available for testing"
fi

# Check service status
echo ""
echo "5. Checking systemd service status..."
if systemctl is-active --quiet whatsapp-api 2>/dev/null; then
    echo "   ✅ whatsapp-api service is active"
    systemctl status whatsapp-api --no-pager -l
else
    echo "   ❌ whatsapp-api service is not active"
    echo "   Run: sudo systemctl start whatsapp-api"
fi

# Check recent logs
echo ""
echo "6. Recent log entries..."
echo "   Last 5 log entries from systemd journal:"
journalctl -u whatsapp-api --no-pager -n 5 2>/dev/null || echo "   No journal entries found"

echo ""
echo "7. Socket connection test commands:"
echo "   # Test with curl:"
echo "   curl --unix-socket '$SOCKET_PATH' http://localhost/WhatsApp/connectionStatus"
echo ""
echo "   # Test with nc (if available):"
echo "   echo -e 'GET /WhatsApp/connectionStatus HTTP/1.1\r\nHost: localhost\r\n\r\n' | nc -U '$SOCKET_PATH'"
echo ""
echo "   # Monitor socket activity:"
echo "   sudo ss -ln | grep '$SOCKET_PATH'"
echo ""
echo "Unix socket test completed."