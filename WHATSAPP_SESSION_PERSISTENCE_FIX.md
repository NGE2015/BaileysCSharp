# WhatsApp Session Persistence Fix - Implementation Guide

**Date**: July 2025  
**Issue**: WhatsApp sessions requiring QR code re-scan daily  
**Root Cause**: Session files stored in variable assembly paths instead of fixed locations  
**Status**: ✅ IMPLEMENTED

---

## 🚨 **Problem Summary**

WhatsApp sessions were dropping daily, requiring users to scan QR codes repeatedly because:

1. **Variable Storage Paths**: Session credentials stored relative to assembly location
2. **Assembly Location Changes**: Deployments/restarts changed the base path
3. **Failed Session Restoration**: Service couldn't find existing credential files
4. **New QR Generation**: Missing credentials triggered new authentication flow

---

## 🔧 **Solution Implemented**

### **1. Fixed Session Storage Path**
**File**: `/BaileysCSharp/Core/Types/SocketConfig.cs`

**Old Code** (Problem):
```csharp
public string CacheRoot
{
    get
    {
        var path = Path.Combine(Root, SessionName);  // Variable path based on assembly location
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }
}
```

**New Code** (Fixed):
```csharp
public string CacheRoot
{
    get
    {
        // Use fixed session storage path to ensure persistence across deployments
        var basePath = "/home/RubyManager/web/whatsapp.rubymanager.app/sessions";
        var path = Path.Combine(basePath, SessionName ?? "default");
        
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        catch (Exception ex)
        {
            // Fallback to original behavior if fixed path fails
            Console.WriteLine($"Warning: Could not create session directory at {path}. Error: {ex.Message}");
            Console.WriteLine("Falling back to assembly-relative path.");
            path = Path.Combine(Root, SessionName ?? "default");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        
        return path;
    }
}
```

### **2. Enhanced Session Migration Logic**
**File**: `/WhatsAppApi/Services/WhatsAppServiceV2.cs`

Added automatic migration from legacy locations:

```csharp
/// <summary>
/// Finds credentials file in current or legacy locations, migrating if necessary
/// </summary>
private string FindOrMigrateCredentialsFile(string sessionName, string newCacheRoot)
{
    var primaryCredsFile = Path.Join(newCacheRoot, $"{sessionName}_creds.json");
    
    // If file already exists in new location, use it
    if (File.Exists(primaryCredsFile))
    {
        _logger.LogDebug($"Using existing credentials from new location: {primaryCredsFile}");
        return primaryCredsFile;
    }

    // Search for credentials in potential legacy locations
    var assemblyRoot = Path.GetDirectoryName(typeof(BaileysCSharp.Core.BaseSocket).Assembly.Location);
    var legacyLocations = new[]
    {
        Path.Join(assemblyRoot, sessionName, $"{sessionName}_creds.json"),
        Path.Join("/home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp", sessionName, $"{sessionName}_creds.json"),
        Path.Join(AppContext.BaseDirectory, sessionName, $"{sessionName}_creds.json"),
        Path.Join(Environment.CurrentDirectory, sessionName, $"{sessionName}_creds.json")
    };

    foreach (var legacyPath in legacyLocations)
    {
        if (File.Exists(legacyPath))
        {
            try
            {
                _logger.LogInformation($"Found credentials in legacy location: {legacyPath}");
                _logger.LogInformation($"Migrating credentials to new location: {primaryCredsFile}");
                
                // Ensure new directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(primaryCredsFile));
                
                // Copy credential file and migrate entire session directory
                File.Copy(legacyPath, primaryCredsFile, overwrite: true);
                
                // [Additional migration logic for subdirectories and related files]
                
                _logger.LogInformation($"Successfully migrated session {sessionName} from {legacyPath} to {primaryCredsFile}");
                return primaryCredsFile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to migrate credentials from {legacyPath} to {primaryCredsFile}");
                // Continue trying other locations
            }
        }
    }

    _logger.LogInformation($"No existing credentials found for session {sessionName}, will create new ones");
    return primaryCredsFile; // Return the target path for new credentials
}
```

---

## 🛠️ **Deployment Instructions**

### **1. Server Permissions Setup**
Run these commands on your Ubuntu server:

```bash
# Create the sessions directory
sudo mkdir -p /home/RubyManager/web/whatsapp.rubymanager.app/sessions

# Set ownership to the service user (adjust user as needed)
sudo chown -R www-data:www-data /home/RubyManager/web/whatsapp.rubymanager.app/sessions

# Set appropriate permissions
sudo chmod 755 /home/RubyManager/web/whatsapp.rubymanager.app/sessions
sudo chmod g+s /home/RubyManager/web/whatsapp.rubymanager.app/sessions

# Verify permissions
ls -la /home/RubyManager/web/whatsapp.rubymanager.app/
```

### **2. Deploy Updated Code**
- Use your existing CI/CD pipeline to deploy the updated `BaileysCSharp` solution
- The changes are in:
  - `BaileysCSharp/Core/Types/SocketConfig.cs`
  - `WhatsAppApi/Services/WhatsAppServiceV2.cs`

### **3. Verification After Deployment**

**Check Logs for Migration Messages**:
```bash
tail -f /home/RubyManager/web/whatsapp.rubymanager.app/netcoreapp/logs/whatsapp-$(date +%Y%m%d).log
```

**Look for these success indicators**:
```
Found credentials in legacy location: [old_path]
Migrating credentials to new location: [new_path]
Successfully migrated session 7afd5398-97c3-11eb-a990-f23c917564d6
Successfully loaded existing credentials for session 7afd5398-97c3-11eb-a990-f23c917564d6 from [new_path]
```

**Verify Session Directory Structure**:
```bash
ls -la /home/RubyManager/web/whatsapp.rubymanager.app/sessions/
ls -la /home/RubyManager/web/whatsapp.rubymanager.app/sessions/7afd5398-97c3-11eb-a990-f23c917564d6/
```

---

## 🎯 **Expected Outcomes**

### **✅ Success Indicators**

1. **No More Daily QR Codes**: Existing sessions persist across service restarts
2. **Automatic Migration**: Legacy session files automatically moved to new location
3. **Consistent Paths**: All sessions use `/home/RubyManager/web/whatsapp.rubymanager.app/sessions/[tenantId]/`
4. **Detailed Logging**: Clear migration and session loading messages in logs

### **📊 Monitoring Points**

- **Session Directory**: Monitor `/sessions/` folder for credential files
- **Log Messages**: Look for migration and session loading success messages
- **Connection Status**: CRM should show persistent WhatsApp connections
- **QR Generation**: Should only occur for genuinely new tenants

---

## 🚨 **Troubleshooting Guide**

### **If Sessions Still Drop**

1. **Check Permissions**:
   ```bash
   ls -la /home/RubyManager/web/whatsapp.rubymanager.app/sessions/
   # Should show service user ownership and 755 permissions
   ```

2. **Verify Migration Occurred**:
   ```bash
   find /home/RubyManager/web/whatsapp.rubymanager.app/sessions -name "*_creds.json"
   # Should show credential files in new location
   ```

3. **Check Service User**:
   ```bash
   sudo systemctl status your-whatsapp-service
   # Verify which user runs the service
   ```

4. **Review Logs**:
   ```bash
   grep -i "migrate\|credentials\|session.*found" /path/to/whatsapp/logs/whatsapp-*.log
   ```

### **Manual Migration (If Needed)**

If automatic migration fails, manually move session files:

```bash
# Find existing session files
find /home/RubyManager/web/whatsapp.rubymanager.app -name "*7afd5398*" -type d

# Move to new location (example)
sudo mv /old/path/7afd5398-97c3-11eb-a990-f23c917564d6 /home/RubyManager/web/whatsapp.rubymanager.app/sessions/

# Fix permissions
sudo chown -R www-data:www-data /home/RubyManager/web/whatsapp.rubymanager.app/sessions/
```

### **Fallback Safety**

The implementation includes fallback logic - if the fixed path fails, it reverts to the original behavior, so the system won't break completely.

---

## 📁 **File Structure After Fix**

```
/home/RubyManager/web/whatsapp.rubymanager.app/
├── netcoreapp/
│   ├── app.sock                    # Unix socket
│   ├── logs/                       # Log files
│   └── [application files]
└── sessions/                       # ✅ NEW: Fixed session storage
    └── 7afd5398-97c3-11eb-a990-f23c917564d6/
        ├── 7afd5398-97c3-11eb-a990-f23c917564d6_creds.json
        ├── app-state-sync-version/
        ├── sender-keys/
        ├── session-keys/
        └── pre-keys/
```

---

## 🔄 **Code Changes Summary**

| File | Change Type | Description |
|------|-------------|-------------|
| `SocketConfig.cs` | **Modified** | Fixed session storage path to `/sessions/` directory |
| `WhatsAppServiceV2.cs` | **Enhanced** | Added migration logic and improved session restoration |

---

## 📋 **Testing Checklist**

- [ ] Deploy updated code via CI/CD
- [ ] Verify `/sessions/` directory exists with proper permissions  
- [ ] Check logs for successful migration messages
- [ ] Test WhatsApp connection persistence after service restart
- [ ] Verify no QR code required for existing tenant (`7afd5398-97c3-11eb-a990-f23c917564d6`)
- [ ] Test new tenant onboarding still works correctly

---

**This fix addresses the root cause of daily QR code generation by ensuring WhatsApp session credentials are stored in a consistent, persistent location that survives deployments and service restarts.**