namespace PeakCan.Security.Keystore;

/// <summary>
/// 密钥存储抽象（spec D4）：suite/配置只携带 keyId 引用，
/// 密钥材料全部在本机 KeyStore 内，杜绝明文进 suite JSON / git。
/// </summary>
public interface IKeyStore
{
    /// <summary>当前全部 keyId（枚举按文件名序）。</summary>
    IReadOnlyCollection<string> KeyIds { get; }

    bool Contains(string keyId);

    /// <exception cref="KeyNotFoundException">keyId 不存在。</exception>
    /// <remarks>
    /// 返回明文密钥材料的独立副本。调用方是唯一责任人：用完后必须调用
    /// System.Security.Cryptography.CryptographicOperations.ZeroMemory 清零，
    /// 不得缓存到长生命周期对象或落盘。
    /// </remarks>
    byte[] GetKey(string keyId);

    /// <exception cref="ArgumentException">keyId 为空或含非法路径字符（防路径穿越）。</exception>
    void SetKey(string keyId, byte[] key);

    /// <returns>键原先是否存在（存在则删除成功返回 true）。</returns>
    bool RemoveKey(string keyId);
}