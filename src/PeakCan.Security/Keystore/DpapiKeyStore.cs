using System.Security.Cryptography;

namespace PeakCan.Security.Keystore;

/// <summary>
/// 本机磁盘密钥存储：DPAPI <see cref="DataProtectionScope.CurrentUser"/> 加密
/// （spec D4）。dir 内每个 keyId 一个 .bin 文件；密文不可跨用户/机器迁移，
/// suite 分享时对端需自行导入密钥。非 Windows 平台调用会抛
/// <see cref="PlatformNotSupportedException"/>（ProtectedData 行为）。
/// </summary>
public sealed class DpapiKeyStore : IKeyStore
{
    private readonly string _directory;
    private readonly byte[]? _entropy;

    public DpapiKeyStore(string directory, string? entropy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _entropy = entropy is null ? null : System.Text.Encoding.UTF8.GetBytes(entropy);
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyCollection<string> KeyIds
        => Directory.EnumerateFiles(_directory, "*.bin")
            .Select(Path.GetFileNameWithoutExtension!)
            .ToArray();

    public bool Contains(string keyId) => File.Exists(PathFor(keyId));

    public byte[] GetKey(string keyId)
    {
        var path = PathFor(keyId);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"keyId '{keyId}' 不在 KeyStore 中");
        return ProtectedData.Unprotect(File.ReadAllBytes(path), _entropy,
            DataProtectionScope.CurrentUser);
    }

    public void SetKey(string keyId, byte[] key)
    {
        KeyStoreGuard.ValidateKeyId(keyId);
        ArgumentNullException.ThrowIfNull(key);
        var cipher = ProtectedData.Protect(key, _entropy, DataProtectionScope.CurrentUser);
        // 先写临时文件再原子替换，避免写一半留下损坏文件
        var path = PathFor(keyId);
        File.WriteAllBytes(path + ".tmp", cipher);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public bool RemoveKey(string keyId)
    {
        var path = PathFor(keyId);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string keyId)
    {
        KeyStoreGuard.ValidateKeyId(keyId);
        return Path.Combine(_directory, keyId + ".bin");
    }
}

/// <summary>keyId 合法性守卫：非空且不含路径分隔/非法文件名字符（防路径穿越）。</summary>
internal static class KeyStoreGuard
{
    public static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("keyId 不能为空", nameof(keyId));
        var invalid = Path.GetInvalidFileNameChars();
        if (keyId.Any(c => c is '/' or '\\' || invalid.Contains(c) || c == ':'))
            throw new ArgumentException($"keyId '{keyId}' 含非法字符", nameof(keyId));
    }
}