#!/bin/bash

# WhatsApp API Logging Setup Script
# This script sets up systemd service and logging

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
SERVICE_NAME="whatsapp-api"

echo "Setting up WhatsApp API logging and systemd service..."

# Create directories
echo "Creating log directories..."
mkdir -p "$PROJECT_DIR/logs"
mkdir -p "$PROJECT_DIR/cache"
mkdir -p "/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp"

# Set permissions
echo "Setting permissions..."
chmod 755 "$PROJECT_DIR/logs"
chmod 755 "$PROJECT_DIR/cache"
chmod 755 "/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp"

# Install systemd service
echo "Installing systemd service..."
if [[ -f "$SCRIPT_DIR/$SERVICE_NAME.service" ]]; then
    sudo cp "$SCRIPT_DIR/$SERVICE_NAME.service" "/etc/systemd/system/"
    sudo systemctl daemon-reload
    echo "Systemd service installed successfully."
else
    echo "Service file not found: $SCRIPT_DIR/$SERVICE_NAME.service"
    exit 1
fi

# Create log rotation configuration
echo "Setting up log rotation..."
sudo tee "/etc/logrotate.d/whatsapp-api" > /dev/null << EOF
$PROJECT_DIR/logs/*.log {
    daily
    missingok
    rotate 30
    compress
    delaycompress
    notifempty
    copytruncate
    create 644 root root
}
EOF

# Create alias commands
echo "Creating convenient log commands..."
sudo tee "/usr/local/bin/whatsapp-logs" > /dev/null << EOF
#!/bin/bash
exec "$SCRIPT_DIR/whatsapp-logs.sh" "\$@"
EOF
sudo chmod +x "/usr/local/bin/whatsapp-logs"

# Create systemctl aliases
echo "Creating systemctl shortcuts..."
sudo tee "/usr/local/bin/whatsapp-start" > /dev/null << 'EOF'
#!/bin/bash
sudo systemctl start whatsapp-api
EOF

sudo tee "/usr/local/bin/whatsapp-stop" > /dev/null << 'EOF'
#!/bin/bash
sudo systemctl stop whatsapp-api
EOF

sudo tee "/usr/local/bin/whatsapp-restart" > /dev/null << 'EOF'
#!/bin/bash
sudo systemctl restart whatsapp-api
EOF

sudo tee "/usr/local/bin/whatsapp-status" > /dev/null << 'EOF'
#!/bin/bash
sudo systemctl status whatsapp-api
EOF

sudo chmod +x /usr/local/bin/whatsapp-*

echo ""
echo "Setup completed successfully!"
echo ""
echo "Available commands:"
echo "  whatsapp-start      - Start the service"
echo "  whatsapp-stop       - Stop the service"
echo "  whatsapp-restart    - Restart the service"
echo "  whatsapp-status     - Check service status"
echo "  whatsapp-logs       - View and manage logs"
echo ""
echo "Log commands:"
echo "  whatsapp-logs live           - Follow live logs"
echo "  whatsapp-logs today          - Show today's logs"
echo "  whatsapp-logs errors         - Show error logs"
echo "  whatsapp-logs journal-live   - Follow systemd journal"
echo "  whatsapp-logs status         - Show logging status"
echo ""
echo "Service management:"
echo "  sudo systemctl enable whatsapp-api    - Enable auto-start"
echo "  sudo systemctl disable whatsapp-api   - Disable auto-start"
echo ""
echo "Journal commands:"
echo "  journalctl -u whatsapp-api -f         - Follow service logs"
echo "  journalctl -u whatsapp-api --since='1 hour ago'  - Recent logs"
echo ""
echo "To start the service now:"
echo "  whatsapp-start"
echo ""
echo "To enable auto-start on boot:"
echo "  sudo systemctl enable whatsapp-api"