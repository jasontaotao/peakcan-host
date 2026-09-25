namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>MDF 通道规格（通道名 + 单位；单位 null/空 = 不写 unit TX）。</summary>
public sealed record MdfChannelSpec(string Name, string? Unit);

/// <summary>
/// S4-T2 记录写入器契约（spec D1 自研 MF4 写子集的 seam——测试经此注入故障/替身）。
/// 单线程消费假设：实现只被记录 sink 的后台写线程串行调用。
/// </summary>
public interface IMdfRecordWriter : IAsyncDisposable
{
    /// <summary>已成功写入的记录总数（跨通道累计；只增）。</summary>
    long RecordCount { get; }

    /// <summary>向 <paramref name="channelIndex"/>（构造时通道序）追加一条记录。</summary>
    Task WriteRecordAsync(int channelIndex, double timeSeconds, double value, CancellationToken ct = default);

    /// <summary>追加一条失效记录（空窗标记：value 载荷 NaN + invalidation 位置位，spec D3）。</summary>
    Task WriteInvalidRecordAsync(int channelIndex, double timeSeconds, CancellationToken ct = default);

    /// <summary>追加一条归因事件（事件组：时间/kind/cause/detail/receive_kind/预期时长，spec D3）。</summary>
    Task WriteGapEventAsync(double timeSeconds, string kind, string cause, string detail,
        string receiveKind, double expectedMaxSeconds, CancellationToken ct = default);

    /// <summary>向文件追加附件块（S4-T3 ContractSnapshot JSON 落盘；嵌入式未压缩）。</summary>
    Task WriteAttachmentAsync(string mimeType, string comment, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>收尾：回写块长度/条数并翻转 ID 签名为终态。</summary>
    Task FinalizeAsync(CancellationToken ct = default);
}
