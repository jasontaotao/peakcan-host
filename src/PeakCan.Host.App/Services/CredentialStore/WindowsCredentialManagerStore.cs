using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Services.CredentialStore;

/// <summary>v3.53.1 PATCH P1a: Windows Credential Manager backend for
/// ICredentialStore. Uses advapi32.dll CredRead/CredWrite/CredDelete.
/// Credentials are DPAPI-encrypted by the OS, scoped to current user.
/// Per v3.52.0 hard-boundary: API keys MUST use this path, never
/// appsettings.json.
/// </summary>
public sealed class WindowsCredentialManagerStore : ICredentialStore
{
    private const string KeyPrefix = "peakcan-host:";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;  // 2560; well under Win32 max 32767

    private readonly ILogger<WindowsCredentialManagerStore> _logger;

    public WindowsCredentialManagerStore(ILogger<WindowsCredentialManagerStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var fullKey = KeyPrefix + key;
        if (!CredRead(fullKey, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                _logger.LogDebug("Credential '{Key}' not found in Windows Credential Manager", key);
                return Task.FromResult<string?>(null);
            }
            throw new CredentialStoreException(key,
                $"Failed to read credential '{key}' from Windows Credential Manager (HRESULT 0x{err:X8})");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            return Task.FromResult<string?>(Marshal.PtrToStringUni(cred.CredentialBlob));
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Contains('\0')) throw new ArgumentException("Credential value cannot contain null characters", nameof(value));
        if (Encoding.Unicode.GetByteCount(value) > CRED_MAX_CREDENTIAL_BLOB_SIZE)
            throw new ArgumentException($"Credential value exceeds {CRED_MAX_CREDENTIAL_BLOB_SIZE} bytes", nameof(value));

        var fullKey = KeyPrefix + key;
        var blobBytes = Encoding.Unicode.GetBytes(value);
        var blobPtr = Marshal.AllocHGlobal(blobBytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(fullKey);
        try
        {
            Marshal.Copy(blobBytes, 0, blobPtr, blobBytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blobBytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref credential, 0))
            {
                var err = Marshal.GetLastWin32Error();
                throw new CredentialStoreException(key,
                    $"Failed to write credential '{key}' to Windows Credential Manager (HRESULT 0x{err:X8})");
            }
            _logger.LogInformation("Credential '{Key}' stored in Windows Credential Manager", key);
            return Task.CompletedTask;
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var fullKey = KeyPrefix + key;
        if (!CredDelete(fullKey, CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                _logger.LogDebug("Credential '{Key}' not found for delete (no-op)", key);
                return Task.CompletedTask;
            }
            throw new CredentialStoreException(key,
                $"Failed to delete credential '{key}' from Windows Credential Manager (HRESULT 0x{err:X8})");
        }
        _logger.LogInformation("Credential '{Key}' deleted from Windows Credential Manager", key);
        return Task.CompletedTask;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}