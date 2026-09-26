using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.Core.Tests.Xcp.Replay;

/// <summary>
/// S6-T2：回放数据服务（spec D3 + T2 补记口径）。
/// S4 记录文件存的是采集链已解码的物理值——回放层不做 Decode，
/// 只做：元数据装配（快照/外部合同）+ 指纹门禁降级 + 空窗标注面。
/// </summary>
public sealed class XcpReplayDecoderTests
{
    private static readonly MdfChannelSpec Rpm = new("EngineSpeed", "rpm");
    private static readonly MdfChannelSpec Speed = new("VehicleSpeed", "km/h");

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s6rp_{Guid.NewGuid():N}.mf4");

    private const string ShaA = "AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111";
    private const string ShaB = "BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222";

    /// <summary>最小快照：两个对象、identical 换算、带单位/限值。</summary>
    private static ContractSnapshot BuildSnapshot(string sha) => new(
        ContractSchemaVersion: 1,
        A2lSha256: sha,
        A2l: new A2lFingerprint(1, 0, 0, 0, 0),
        PackageVersion: "0.1.1",
        Contracts:
        [
            new SnapshottedContract(
                ObjectName: "EngineSpeed",
                Category: "Measurement",
                CharacteristicKind: null,
                Segments: [new ValueSegment(0x2000, 2, (ushort)0, 0)],
                TotalByteLength: 2,
                DataType: "UWORD",
                BigEndian: false,
                Unit: "rpm",
                Format: null,
                LowerLimit: 0.0, UpperLimit: 8000.0, ExtLowerLimit: null, ExtUpperLimit: null,
                IsWritable: false,
                AccessSource: "Declared",
                Conversion: new SnapshottedConversion(
                    "identical", null, null, null, null, null, null,
                    null, null, null, null, null, null),
                Axis: null,
                Comment: null,
                Notes: [],
                SourceLine: 1),
            new SnapshottedContract(
                ObjectName: "VehicleSpeed",
                Category: "Measurement",
                CharacteristicKind: null,
                Segments: [new ValueSegment(0x2100, 2, (ushort)0, 0)],
                TotalByteLength: 2,
                DataType: "UWORD",
                BigEndian: false,
                Unit: "km/h",
                Format: null,
                LowerLimit: null, UpperLimit: null, ExtLowerLimit: null, ExtUpperLimit: null,
                IsWritable: false,
                AccessSource: "Declared",
                Conversion: new SnapshottedConversion(
                    "identical", null, null, null, null, null, null,
                    null, null, null, null, null, null),
                Axis: null,
                Comment: null,
                Notes: [],
                SourceLine: 2),
        ]);

    private static async Task<(Mdf4FileData File, string Path)> BuildFileAsync(
        ContractSnapshot? snapshot, bool withGap = false, bool withInvalid = false)
    {
        var path = TempPath();
        var writer = Mdf4StreamWriter.Create(path, [Rpm, Speed], DateTimeOffset.UtcNow);
        await writer.WriteRecordAsync(0, 0.0, 1000.0);
        await writer.WriteRecordAsync(0, 0.1, 1200.0);
        await writer.WriteRecordAsync(1, 0.0, 60.0);
        if (withInvalid)
            await writer.WriteInvalidRecordAsync(0, 0.2);
        if (withInvalid)
            await writer.WriteRecordAsync(0, 0.3, 1500.0);
        if (withGap)
            await writer.WriteGapEventAsync(0.2, "MissingCauseAttributed",
                "ConversionUnsupported", "cvt unsupported", "MalformedFrame", 0.0);
        if (snapshot is { } snap)
            await writer.WriteAttachmentAsync("application/json",
                "contractSchemaVersion=1; packageVersion=0.1.1",
                System.Text.Encoding.UTF8.GetBytes(ContractSnapshotCodec.Encode(snap)));
        await writer.FinalizeAsync();
        await writer.DisposeAsync();
        return (Mdf4StreamReader.Read(path), path);
    }

    [Fact]
    public async Task Snapshot_with_matching_sha_trusts_metadata()
    {
        var (file, path) = await BuildFileAsync(BuildSnapshot(ShaA));
        try
        {
            var result = XcpReplayDecoder.Decode(file, currentA2lSha256: ShaA);

            Assert.Equal(XcpReplayMetadataSource.SnapshotAttachment, result.MetadataSource);
            Assert.Equal(XcpReplayFingerprintState.Trusted, result.FingerprintState);
            Assert.Equal(ShaA, result.SnapshotA2lSha256);
            Assert.Equal(2, result.Channels.Count);

            var rpm = result.Channels[0];
            Assert.Equal("EngineSpeed", rpm.ObjectName);
            Assert.NotNull(rpm.Metadata);
            var md = rpm.Metadata!;
            Assert.Equal("rpm", md.Unit);
            Assert.Equal(0.0, md.LowerLimit);
            Assert.Equal(8000.0, md.UpperLimit);
            Assert.Equal(2, rpm.Times.Count);
            Assert.Equal(1200.0, rpm.Values[1]);
            Assert.All(rpm.Invalid, i => Assert.False(i));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Snapshot_with_mismatched_sha_degrades_metadata_but_keeps_curves()
    {
        var (file, path) = await BuildFileAsync(BuildSnapshot(ShaA));
        try
        {
            var result = XcpReplayDecoder.Decode(file, currentA2lSha256: ShaB);

            Assert.Equal(XcpReplayFingerprintState.Mismatched, result.FingerprintState);
            Assert.All(result.Channels, ch => Assert.Null(ch.Metadata)); // 元数据拒绝应用
            Assert.Equal(1200.0, result.Channels[0].Values[1]);          // 曲线仍可用（文件值即物理值）
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task External_contracts_supply_metadata_when_no_snapshot()
    {
        var (file, path) = await BuildFileAsync(snapshot: null);
        try
        {
            var contracts = Asap2PackageApi.ImportSnapshot(ContractSnapshotCodec.Encode(BuildSnapshot(ShaB)));
            var result = XcpReplayDecoder.Decode(file, externalContracts: contracts);

            Assert.Equal(XcpReplayMetadataSource.ExternalContracts, result.MetadataSource);
            Assert.NotNull(result.Channels[0].Metadata);
            Assert.Equal("rpm", result.Channels[0].Metadata!.Unit);
            Assert.NotNull(result.Channels[1].Metadata);
            Assert.Equal("km/h", result.Channels[1].Metadata!.Unit);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task No_metadata_source_degrades_but_curves_survive()
    {
        var (file, path) = await BuildFileAsync(snapshot: null);
        try
        {
            var result = XcpReplayDecoder.Decode(file);

            Assert.Equal(XcpReplayMetadataSource.None, result.MetadataSource);
            Assert.All(result.Channels, ch => Assert.Null(ch.Metadata));
            Assert.Equal(2, result.Channels.Count);
            Assert.Equal(60.0, result.Channels[1].Values[0]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_rows_and_gap_events_surface_as_replay_marks()
    {
        var (file, path) = await BuildFileAsync(BuildSnapshot(ShaA), withGap: true, withInvalid: true);
        try
        {
            var result = XcpReplayDecoder.Decode(file, currentA2lSha256: ShaA);

            var rpm = result.Channels[0];
            Assert.Equal(4, rpm.Times.Count);
            Assert.True(rpm.Invalid[2]);   // 0.2 s 空窗行
            Assert.False(rpm.Invalid[3]);

            var mark = Assert.Single(result.GapMarks);
            Assert.Equal(0.2, mark.Time);
            Assert.Equal("MissingCauseAttributed", mark.Kind);
            Assert.Equal("ConversionUnsupported", mark.Cause);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Channel_without_contract_degrades_per_channel()
    {
        // 快照只含 EngineSpeed 合同；VehicleSpeed 缺合同 → 单通道降级。
        var full = BuildSnapshot(ShaA);
        var partial = full with { Contracts = [full.Contracts[0]] };
        var (file, path) = await BuildFileAsync(partial);
        try
        {
            var result = XcpReplayDecoder.Decode(file);

            Assert.Equal(XcpReplayMetadataSource.SnapshotAttachment, result.MetadataSource);
            Assert.NotNull(result.Channels[0].Metadata);
            Assert.Null(result.Channels[1].Metadata);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
