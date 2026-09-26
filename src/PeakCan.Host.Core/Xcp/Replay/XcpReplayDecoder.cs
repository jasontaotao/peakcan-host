using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Record;

namespace PeakCan.Host.Core.Xcp.Replay;

/// <summary>元数据来源（spec D3 + T2 补记）。</summary>
public enum XcpReplayMetadataSource
{
    None,
    SnapshotAttachment,
    ExternalContracts,
}

/// <summary>指纹门禁态：Mismatched = 快照 SHA256 与当前 A2L 不一致。</summary>
public enum XcpReplayFingerprintState
{
    Trusted,
    Mismatched,
}

/// <summary>单通道回放元数据（来自解析期合同，只读不现算）。</summary>
public sealed record XcpReplayChannelMetadata(
    string? Unit, string Category, string? CharacteristicKind,
    double? LowerLimit, double? UpperLimit, double? ExtLowerLimit, double? ExtUpperLimit);

/// <summary>
/// 单通道回放数据。文件值 = 采集链已解码物理值（S4 口径），Invalid 行值为 NaN 不可当数据。
/// Metadata = null 表示该通道元数据降级（指纹不符 / 缺合同 / 无来源）。
/// </summary>
public sealed record XcpReplayChannel(
    string ObjectName,
    string FileUnit,
    IReadOnlyList<double> Times,
    IReadOnlyList<double> Values,
    IReadOnlyList<bool> Invalid,
    XcpReplayChannelMetadata? Metadata);

/// <summary>归因标注点（S4 事件组直通）。</summary>
public sealed record XcpReplayGapMark(
    double Time, string Kind, string Cause, string Detail, string ReceiveKind, double ExpectedMaxSeconds);

/// <summary>回放解码结果。</summary>
public sealed record XcpReplayResult(
    XcpReplayMetadataSource MetadataSource,
    XcpReplayFingerprintState FingerprintState,
    string? SnapshotA2lSha256,
    string? CurrentA2lSha256,
    IReadOnlyList<XcpReplayChannel> Channels,
    IReadOnlyList<XcpReplayGapMark> GapMarks);

/// <summary>
/// S6-T2 回放数据服务：MDF4 文件 + 合同来源 → 渲染层可消费的曲线/元数据/标注面。
/// <para>
/// S4 记录文件存的是采集链已解码的物理值（receive 链 Decode → sink 落盘）——本服务不做
/// 数值 Decode，只做：合同元数据装配（内嵌快照优先 / 外部 ContractSet 兜底）+ 指纹门禁
/// 降级（不符 → 元数据整面拒绝，曲线仍可用）+ 归因标注直通。
/// </para>
/// </summary>
public static class XcpReplayDecoder
{
    /// <summary>S4 快照附件固定 MIME（XcpMdfRecordSink 写入口径）。</summary>
    private const string SnapshotMimeType = "application/json";

    public static XcpReplayResult Decode(
        Mdf4FileData file,
        ContractSet? externalContracts = null,
        string? currentA2lSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        // 1) 合同来源：内嵌快照优先（S4 D2 自包含语义），其次外部 ContractSet，最后无元数据。
        var snapshotJson = file.Attachments
            .Where(a => a.MimeType == SnapshotMimeType)
            .Select(a => System.Text.Encoding.UTF8.GetString(a.Data))
            .FirstOrDefault();
        ContractSnapshot? snapshot = snapshotJson is { } json ? ContractSnapshotCodec.Decode(json) : null;
        var contracts = snapshot is { } snap
            ? Asap2PackageApi.ImportSnapshot(ContractSnapshotCodec.Encode(snap))
            : externalContracts;

        var source = snapshot is not null ? XcpReplayMetadataSource.SnapshotAttachment
            : externalContracts is not null ? XcpReplayMetadataSource.ExternalContracts
            : XcpReplayMetadataSource.None;

        // 2) 指纹门禁：仅快照来源可绑定 SHA256；不符 → 元数据整面降级（曲线仍可用，值即物理值）。
        var snapshotSha = snapshot?.A2lSha256;
        var mismatched = snapshotSha is not null
            && currentA2lSha256 is not null
            && !string.Equals(snapshotSha, currentA2lSha256, StringComparison.Ordinal);
        if (mismatched)
            contracts = null; // 元数据整面拒绝（含快照合同）
        var fingerprintState = mismatched
            ? XcpReplayFingerprintState.Mismatched
            : XcpReplayFingerprintState.Trusted;

        // 3) 通道装配：文件值已是物理值，只补元数据面；缺合同单通道降级。
        var channels = new List<XcpReplayChannel>(file.Channels.Count);
        foreach (var ch in file.Channels)
        {
            XcpReplayChannelMetadata? metadata = null;
            if (contracts is { } set && set.TryGet(ch.Name, out var contract))
            {
                metadata = new XcpReplayChannelMetadata(
                    contract.Unit, contract.Category.ToString(), contract.CharacteristicKind,
                    contract.LowerLimit, contract.UpperLimit, contract.ExtLowerLimit, contract.ExtUpperLimit);
            }

            channels.Add(new XcpReplayChannel(
                ch.Name, ch.Unit, ch.Samples.Select(s => s.Time).ToArray(),
                ch.Samples.Select(s => s.Value).ToArray(),
                ch.Samples.Select(s => s.Invalid).ToArray(), metadata));
        }

        // 4) 归因标注直通（S4 事件组 → 渲染层标注点）。
        var gapMarks = file.GapEvents
            .Select(e => new XcpReplayGapMark(e.Time, e.Kind, e.Cause, e.Detail, e.ReceiveKind, e.ExpectedMaxSeconds))
            .ToArray();

        return new XcpReplayResult(source, fingerprintState, snapshotSha, currentA2lSha256, channels, gapMarks);
    }
}
