using System.Security.Cryptography;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// 归零 KeyStore 派生的**源**密钥副本（spec D4 / Rev9）。<see cref="SecOcChannel"/> 会克隆密钥，
/// 但 <c>SecOcConfigLoader</c> 返回给调用方的源副本（<see cref="SecOcPduConfig"/>.Key，来自
/// <c>IKeyStore.GetKey</c>，按约定由调用方负责归零）会在 host 释放前一直驻留。
/// <para>
/// block 路径下该字典在 <c>HeadlessHostBuilder</c> 中一次构建、被各模式闭包共享，故由本类
/// 注册为 DI singleton 并在 <c>Build</c> 末尾急切实例化，在 host 释放时统一归零
/// （只清一次，避免重复调用 compose 时密钥已被清空）。
/// </para>
/// </summary>
internal sealed class SecOcKeyMaterialZeroizer : IDisposable
{
    /// <summary>测试探针：本类实例化次数（用于断言 <c>Build</c> 已急切实例化，而非靠首次解析）。</summary>
    internal static int InstancesCreated;

    private readonly IReadOnlyList<byte[]> _keys;
    private int _disposed;

    internal SecOcKeyMaterialZeroizer(IEnumerable<byte[]> keys)
    {
        _keys = keys.ToArray();
        Interlocked.Increment(ref InstancesCreated);
    }

    /// <summary>测试探针：全部源密钥是否已归零。</summary>
    internal bool AllZeroed => _keys.Count > 0 && _keys.All(k => k.All(b => b == 0));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var key in _keys)
            CryptographicOperations.ZeroMemory(key);
    }
}
