using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace PeakCan.Security.Keystore;

/// <summary>
/// 进程内密钥存储：CI / 单测 / 临时场景使用，不落盘（spec D4）。
/// 线程安全（ConcurrentDictionary）；读写均返回副本，保持不可变语义。
/// </summary>
public sealed class InMemoryKeyStore : IKeyStore
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> KeyIds => _keys.Keys.ToArray();

    public bool Contains(string keyId) => _keys.ContainsKey(keyId);

    public byte[] GetKey(string keyId)
        => _keys.TryGetValue(keyId, out var key)
            ? (byte[])key.Clone()
            : throw new KeyNotFoundException($"keyId '{keyId}' 不在 KeyStore 中");

    public void SetKey(string keyId, byte[] key)
    {
        KeyStoreGuard.ValidateKeyId(keyId);
        ArgumentNullException.ThrowIfNull(key);
        _keys[keyId] = (byte[])key.Clone();
    }

    public bool RemoveKey(string keyId)
    {
        if (!_keys.TryRemove(keyId, out var key)) return false;
        // 删除即销毁：清零移出的密钥材料，缩短明文在托管堆的暴露窗口
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        return true;
    }
}