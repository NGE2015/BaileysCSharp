# WhatsApp API GetAsciiQRCode Waiting Mechanism - Test Guide

## Overview
Updated the WhatsApp API controller to implement a waiting mechanism for the `GetAsciiQRCode` endpoint instead of immediately returning 404 when QR code is not ready.

## Changes Made

### 1. Updated Controller (`WhatsAppControllerV2.cs`)
- Modified `GetAsciiQRCode` endpoint to be async and accept a timeout parameter
- Added comprehensive error handling and validation
- Implemented proper HTTP status codes (200, 400, 404, 408, 500)

### 2. Enhanced Service (`WhatsAppServiceV2.cs`)
- Added `GetAsciiQRCodeWithWaitAsync` method with configurable timeout
- Added `IsSessionReady` method for session readiness detection
- Enhanced logging throughout session lifecycle
- Maintained backward compatibility with existing `GetAsciiQRCode` method

## API Endpoints

### GET /v2/WhatsApp/getAsciiQRCode
**New Parameters:**
- `sessionName` (required): Name of the WhatsApp session
- `timeout` (optional): Timeout in seconds (default: 10, max: 60)

**HTTP Status Codes:**
- `200 OK`: QR code retrieved successfully or session already connected
- `400 Bad Request`: Invalid parameters (missing sessionName or invalid timeout)
- `404 Not Found`: Session not found
- `408 Request Timeout`: QR code not ready within timeout period
- `500 Internal Server Error`: Unexpected error occurred

## Testing the New Functionality

### 1. Test Sequence
```bash
# 1. Start a new session
curl -X POST "http://localhost:5000/v2/WhatsApp/startSession" \
  -H "Content-Type: application/json" \
  -d '{"sessionName": "test-session"}'

# 2. Immediately request QR code with waiting (should wait up to 10 seconds)
curl -X GET "http://localhost:5000/v2/WhatsApp/getAsciiQRCode?sessionName=test-session&timeout=10"

# 3. Test with custom timeout
curl -X GET "http://localhost:5000/v2/WhatsApp/getAsciiQRCode?sessionName=test-session&timeout=15"

# 4. Test with invalid timeout (should return 400)
curl -X GET "http://localhost:5000/v2/WhatsApp/getAsciiQRCode?sessionName=test-session&timeout=100"

# 5. Test with non-existent session (should return 404)
curl -X GET "http://localhost:5000/v2/WhatsApp/getAsciiQRCode?sessionName=non-existent"
```

### 2. Expected Behaviors

#### Successful QR Code Retrieval (200 OK)
```json
{
  "asciiQrCode": "██████████████  ██  ██████████████\n██          ██      ██          ██\n...",
  "isConnected": false
}
```

#### Session Already Connected (200 OK)
```json
{
  "message": "Session is already connected, no QR code needed",
  "isConnected": true
}
```

#### Request Timeout (408 Request Timeout)
```json
{
  "message": "Request timeout: QR code not ready within 10 seconds"
}
```

#### Session Not Found (404 Not Found)
```json
{
  "message": "Session 'non-existent' not found"
}
```

#### Invalid Parameters (400 Bad Request)
```json
{
  "message": "Timeout must be between 1 and 60 seconds"
}
```

### 3. Timing Behavior
- **Before**: GetAsciiQRCode would return 404 immediately if QR not ready
- **After**: GetAsciiQRCode waits up to the specified timeout (default 10s) for QR to become available
- Session initialization typically takes 1-3 seconds, so most requests will succeed within the default timeout

## Key Features

### Thread Safety
- Uses semaphores to prevent concurrent QR requests for the same session
- Proper async/await patterns throughout

### Configurable Timeout
- Default timeout: 10 seconds
- Maximum timeout: 60 seconds
- Validates timeout parameter range

### Comprehensive Logging
- Session initialization tracking
- QR code generation logging
- Connection state changes
- Timeout and error conditions

### Backward Compatibility
- Original `GetAsciiQRCode` method preserved
- Existing API clients continue to work
- New functionality opt-in via timeout parameter

## Architecture Benefits

1. **Reduced Client Complexity**: Clients no longer need to implement polling logic
2. **Better User Experience**: QR codes appear as soon as available instead of requiring retries
3. **Resource Efficiency**: Server-side waiting is more efficient than client-side polling
4. **Robust Error Handling**: Clear error messages and appropriate HTTP status codes
5. **Production Ready**: Proper logging, timeouts, and thread safety

## Monitoring and Logs
The service now logs:
- Session initialization progress
- QR code generation events
- Connection state changes
- Timeout events and errors

Look for log entries like:
- `"Starting session: {sessionName}"`
- `"QR code generated and stored for session {sessionName}"`
- `"QR code ready for session {sessionName} after {time}ms"`
- `"Timeout waiting for QR code for session {sessionName}"`