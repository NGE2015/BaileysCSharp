#!/bin/bash

# WhatsApp API Logging Helper Script
# This script provides easy commands to view logs

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_DIR/logs"

show_help() {
    echo "WhatsApp API Log Viewer"
    echo "Usage: $0 [COMMAND] [OPTIONS]"
    echo ""
    echo "Commands:"
    echo "  live          Show live logs (tail -f)"
    echo "  today         Show today's logs"
    echo "  errors        Show only error logs from today"
    echo "  journal       Show systemd journal logs"
    echo "  journal-live  Follow systemd journal logs"
    echo "  app-log       Show app.log file"
    echo "  clear         Clear old log files (keep last 7 days)"
    echo "  status        Show logging status and configuration"
    echo ""
    echo "Options:"
    echo "  -h, --help    Show this help message"
    echo "  -n NUM        Number of lines to show (default: 100)"
    echo ""
    echo "Examples:"
    echo "  $0 live                    # Follow live logs"
    echo "  $0 today                   # Show today's logs"
    echo "  $0 errors                  # Show today's error logs"
    echo "  $0 journal-live            # Follow systemd journal"
    echo "  $0 today -n 500            # Show last 500 lines from today"
}

get_latest_log_file() {
    local pattern="$1"
    ls -t "$LOG_DIR"/$pattern 2>/dev/null | head -n1
}

show_live_logs() {
    local lines=${1:-100}
    echo "Showing live logs (last $lines lines, then following)..."
    
    local latest_file=$(get_latest_log_file "whatsapp-*.log")
    if [[ -n "$latest_file" ]]; then
        echo "Following: $latest_file"
        tail -n "$lines" -f "$latest_file"
    else
        echo "No log files found in $LOG_DIR"
        exit 1
    fi
}

show_today_logs() {
    local lines=${1:-100}
    local today=$(date +%Y%m%d)
    local log_file=$(get_latest_log_file "whatsapp-*$today*.log")
    
    if [[ -n "$log_file" ]]; then
        echo "Showing today's logs (last $lines lines): $log_file"
        tail -n "$lines" "$log_file"
    else
        echo "No log file found for today ($today)"
        exit 1
    fi
}

show_error_logs() {
    local lines=${1:-100}
    local today=$(date +%Y%m%d)
    local log_file=$(get_latest_log_file "whatsapp-*$today*.log")
    
    if [[ -n "$log_file" ]]; then
        echo "Showing today's error logs: $log_file"
        grep -i "error\|exception\|fatal" "$log_file" | tail -n "$lines"
    else
        echo "No log file found for today ($today)"
        exit 1
    fi
}

show_journal_logs() {
    local lines=${1:-100}
    echo "Showing systemd journal logs for whatsapp-api..."
    journalctl -u whatsapp-api -n "$lines" --no-pager
}

follow_journal_logs() {
    echo "Following systemd journal logs for whatsapp-api..."
    journalctl -u whatsapp-api -f
}

show_app_log() {
    local lines=${1:-100}
    if [[ -f "$PROJECT_DIR/app.log" ]]; then
        echo "Showing app.log (last $lines lines):"
        tail -n "$lines" "$PROJECT_DIR/app.log"
    else
        echo "app.log not found in $PROJECT_DIR"
        exit 1
    fi
}

clear_old_logs() {
    echo "Clearing old log files (keeping last 7 days)..."
    find "$LOG_DIR" -name "whatsapp-*.log" -mtime +7 -delete 2>/dev/null
    echo "Old log files cleared."
}

show_status() {
    echo "WhatsApp API Logging Status"
    echo "=========================="
    echo "Project Directory: $PROJECT_DIR"
    echo "Log Directory: $LOG_DIR"
    echo ""
    
    echo "Log Files:"
    if [[ -d "$LOG_DIR" ]]; then
        ls -lah "$LOG_DIR"/whatsapp-*.log 2>/dev/null || echo "  No log files found"
    else
        echo "  Log directory does not exist"
    fi
    
    echo ""
    echo "App Log:"
    if [[ -f "$PROJECT_DIR/app.log" ]]; then
        ls -lah "$PROJECT_DIR/app.log"
    else
        echo "  app.log not found"
    fi
    
    echo ""
    echo "Service Status:"
    systemctl is-active whatsapp-api 2>/dev/null || echo "  whatsapp-api service not found/active"
    
    echo ""
    echo "Recent Journal Entries:"
    journalctl -u whatsapp-api --since "1 hour ago" --no-pager -n 5 2>/dev/null || echo "  No recent journal entries"
}

# Parse command line arguments
LINES=100
COMMAND=""

while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help)
            show_help
            exit 0
            ;;
        -n)
            LINES="$2"
            shift 2
            ;;
        live|today|errors|journal|journal-live|app-log|clear|status)
            COMMAND="$1"
            shift
            ;;
        *)
            echo "Unknown option: $1"
            show_help
            exit 1
            ;;
    esac
done

# Create log directory if it doesn't exist
mkdir -p "$LOG_DIR"

# Execute command
case $COMMAND in
    live)
        show_live_logs "$LINES"
        ;;
    today)
        show_today_logs "$LINES"
        ;;
    errors)
        show_error_logs "$LINES"
        ;;
    journal)
        show_journal_logs "$LINES"
        ;;
    journal-live)
        follow_journal_logs
        ;;
    app-log)
        show_app_log "$LINES"
        ;;
    clear)
        clear_old_logs
        ;;
    status)
        show_status
        ;;
    "")
        echo "No command specified."
        show_help
        exit 1
        ;;
    *)
        echo "Unknown command: $COMMAND"
        show_help
        exit 1
        ;;
esac