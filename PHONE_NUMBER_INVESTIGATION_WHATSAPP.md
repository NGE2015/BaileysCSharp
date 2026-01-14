# WhatsApp Phone Number Transformation Investigation

**Date:** 2026-01-14
**Status:** Investigation In Progress - Enhanced Logging Added
**Investigator:** Claude Code

---

## Problem Statement

Phone numbers from WhatsApp are being transformed or appear in different formats:
- **Normal format:** `351931652836` (country code 351 - Portugal) ✅ Works normally
- **Transformed format:** `351935348009` transforms to `171601257582835` ❓ Unknown cause

The transformation is selective - some numbers work normally, others get transformed. Need to identify where and why this happens.

---

## Investigation Findings

### Code Analysis Results

**Key Discovery:** The transformation is **NOT happening in BaileysCSharp**

- BaileysCSharp extracts phone numbers from WhatsApp JID format and sends them unchanged to external APIs
- Phone number extraction is simple: removes `@s.whatsapp.net` suffix
- No hashing, encoding, or transformation is performed internally
- Webhook responses from RubyManagerBot were **not being captured** (fixed in this investigation)

### Phone Number Processing Pipeline

```
WhatsApp Socket → RemoteJid Format (e.g., 351935348009@s.whatsapp.net)
                        ↓
                ExtractPhoneNumber() → 351935348009
                        ↓
         ┌─────────────┬─────────────┐
         ↓             ↓             ↓
    CRM API    RubyManagerBot   Message Storage
```

### Log Evidence (2026-01-14 18:00-19:00 UTC)

**Numbers that work normally (no transformation):**
- `351931652836@s.whatsapp.net` → sent as `351931652836` to APIs ✅

**Numbers that transform:**
- `351935348009` → appears as `171601257582835@s.whatsapp.net` in logs (appears to be WhatsApp's internal handling)

---

## Root Cause Hypothesis

The transformation likely occurs in **one of these locations:**

1. **WhatsApp Web API** (Most Likely)
   - WhatsApp Web may assign different JID formats based on contact type
   - Known numbers: `[phone]@s.whatsapp.net` (standard format)
   - Unknown/new numbers: May get assigned composite IDs like `171...@s.whatsapp.net`
   - This is WhatsApp's internal mechanism, not BaileysCSharp's doing

2. **RubyManagerBot** (Less Likely)
   - Could be storing/mapping incoming numbers to transformed IDs
   - Would only affect responses from RubyManagerBot
   - Now capturable with new logging

3. **CRM API** (Least Likely)
   - Could be transforming numbers for internal database
   - Would only affect what's stored, not what BaileysCSharp sends

---

## Enhanced Logging Added

### Logging Locations

All logs prefixed with `[PHONE_NUMBER_TRACE]` for easy filtering.

#### 1. Message Entry Point
**File:** `WhatsAppServiceV2.cs` (Line 376)
**Log Message:**
```
[PHONE_NUMBER_TRACE] Incoming message - Session: {sessionName}, RemoteJid: {msg.Key?.RemoteJid}, FromMe: {msg.Key?.FromMe}, MessageId: {msg.Key?.Id}
```
**Purpose:** Capture raw WhatsApp RemoteJid when message arrives

#### 2. Phone Number Extraction
**File:** `WhatsAppServiceV2.cs` (Lines 863, 870)
**Log Messages:**
```
[PHONE_NUMBER_TRACE] ExtractPhoneNumber - Input is null or empty
[PHONE_NUMBER_TRACE] Phone extraction - Input JID: {remoteJid} => Extracted: {extractedPhone}
```
**Purpose:** Show what BaileysCSharp extracts from WhatsApp's JID format

#### 3. CRM API Processing
**File:** `WhatsAppServiceV2.cs` (Lines 736, 745, 769, 777, 784, 791)
**Log Messages:**
```
[PHONE_NUMBER_TRACE] SaveMessageToCrmAsync - Raw RemoteJid from WhatsApp: {remoteJid}
[PHONE_NUMBER_TRACE] Message details - Extracted Phone: {senderPhone}, MessageType: {messageType}, MessageId: {messageId}
[PHONE_NUMBER_TRACE] CRM Payload - Full JSON: {jsonPayload}
[PHONE_NUMBER_TRACE] CRM API - URL: {crmUrl}
[PHONE_NUMBER_TRACE] CRM API SUCCESS (200) for session {sessionName}, message ID: {messageId}
[PHONE_NUMBER_TRACE] CRM API - Response body: {responseBody}
[PHONE_NUMBER_TRACE] CRM API ERROR ({response.StatusCode}) for session {sessionName}
[PHONE_NUMBER_TRACE] CRM API - Error response: {responseBody}
```
**Purpose:** Log all data sent to CRM API and responses received

#### 4. RubyManagerBot Webhook
**File:** `WhatsAppServiceV2.cs` (Lines 831, 832, 841, 842, 848, 849)
**Log Messages:**
```
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - URL: {webhookUrl}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Payload being sent: {jsonPayload}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook SUCCESS (200) for session {sessionName}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Response body: {responseBody}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook ERROR ({response.StatusCode}) for session {sessionName}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Error response: {responseBody}
```
**Purpose:** Log webhook payloads and responses (NOW CAPTURED - was previously ignored!)

---

## How to Use the Logs

### Accessing Logs
1. Navigate to: `https://whatsapp.rubymanager.app/logs.html`
2. Select log file: `whatsapp-{Date}20260114.log` (today's date)
3. Search for: `PHONE_NUMBER_TRACE`

### Reading the Trace

A complete trace for a single incoming message will show:

```
[PHONE_NUMBER_TRACE] Incoming message - RemoteJid: 351935348009@s.whatsapp.net, FromMe: false
[PHONE_NUMBER_TRACE] Phone extraction - Input JID: 351935348009@s.whatsapp.net => Extracted: 351935348009
[PHONE_NUMBER_TRACE] SaveMessageToCrmAsync - Raw RemoteJid from WhatsApp: 351935348009@s.whatsapp.net
[PHONE_NUMBER_TRACE] Message details - Extracted Phone: 351935348009, MessageType: text, MessageId: xyz123
[PHONE_NUMBER_TRACE] CRM Payload - Full JSON: {"senderPhone":"351935348009","remoteJid":"351935348009@s.whatsapp.net",...}
[PHONE_NUMBER_TRACE] CRM API - URL: https://school.rubymanager.app/api/whatsappmessagehistory/saveMessage
[PHONE_NUMBER_TRACE] CRM API SUCCESS (200)
[PHONE_NUMBER_TRACE] CRM API - Response body: {...}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - URL: https://rubymanagerbot.rubymanager.app/api/bot/webhook
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Payload being sent: {"senderPhone":"351935348009",...}
[PHONE_NUMBER_TRACE] RubyManagerBot webhook SUCCESS (200)
[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Response body: {...}
```

### What to Look For

**Key Observations:**

1. **Phone number consistency in BaileysCSharp:**
   - Does extracted phone stay the same from webhook entry to CRM/RubyManagerBot?
   - If YES: Transformation happens externally
   - If NO: Transformation happens in BaileysCSharp (unlikely based on code review)

2. **CRM API response:**
   - What does it return? Does it transform the number?
   - Log now shows: `[PHONE_NUMBER_TRACE] CRM API - Response body: {responseBody}`

3. **RubyManagerBot response:**
   - This is **NEW** - was previously being ignored!
   - Log now shows: `[PHONE_NUMBER_TRACE] RubyManagerBot webhook - Response body: {responseBody}`
   - Check if response contains transformed number

---

## Changes Made

### Files Modified

1. **`/root/BaileysCSharp/WhatsAppApi/Services/WhatsAppServiceV2.cs`**
   - Line 376: Added incoming message logging
   - Lines 736, 745: Added CRM processing logging
   - Lines 769, 777: Added CRM payload and response logging
   - Lines 783-793: Added CRM API response capture
   - Lines 831-850: **CRITICAL** - Added webhook response logging (was being discarded)
   - Lines 863-870: Enhanced phone extraction logging

### Build Status
✅ **Build successful** - 0 errors, 489 warnings (pre-existing)

---

## Testing Instructions

### 1. Deploy Updated Code
```bash
cd /root/BaileysCSharp
dotnet publish WhatsAppApi/WhatsAppApi.csproj -c Release -o publish/ -r linux-x64
# Deploy to server
```

### 2. Send Test Message
Send a WhatsApp message from a number (e.g., from 351935348009)

### 3. Check Logs
- URL: `https://whatsapp.rubymanager.app/logs.html`
- Search: `PHONE_NUMBER_TRACE`
- Filter by time (just after message sent)

### 4. Analyze Results
- Copy complete trace from log viewer
- Look for phone number transformation points
- Check CRM and RubyManagerBot responses

### 5. Document Findings
Compare with this template:
```
Message from: [raw WhatsApp JID]
Extracted by BaileysCSharp: [extracted phone]
Sent to CRM: [phone in payload]
CRM Response: [response body]
Sent to RubyManagerBot: [phone in payload]
RubyManagerBot Response: [response body]
Final stored number: [what appears in database]
```

---

## Related Files

- **Investigation Memory:** This file
- **RubyManagerBot Memory:** `PHONE_NUMBER_INVESTIGATION_RUBYMANAGERBOT.md`
- **Code:** `/root/BaileysCSharp/WhatsAppApi/Services/WhatsAppServiceV2.cs`
- **Configuration:** `/root/BaileysCSharp/WhatsAppApi/appsettings.json`

---

## Next Steps

1. ✅ Enhanced logging added
2. ⏳ Deploy to production
3. ⏳ Send test message with problem number
4. ⏳ Examine logs for transformation point
5. ⏳ Check RubyManagerBot response (now capturable)
6. ⏳ Document findings and update this file

---

## Key Takeaways

- **BaileysCSharp does NOT transform phone numbers** - it extracts and sends them unchanged
- **RubyManagerBot responses are now logged** - previously ignored, now visible
- **CRM API responses are now logged** - can see if transformation happens there
- **Complete trace is now possible** - from WhatsApp receipt to external API calls
- **Use `[PHONE_NUMBER_TRACE]` tag** - all investigation logs use this prefix for easy filtering

