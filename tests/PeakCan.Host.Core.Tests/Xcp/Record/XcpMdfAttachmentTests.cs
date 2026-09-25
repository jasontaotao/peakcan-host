using System.Text;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Record;

/// <summary>
/// S4-T3：ContractSnapshot JSON 落 MF4 附件块（spec D2 后半）。
/// 判据 2：仅凭 MF4 内 JSON 快照（不给 A2L）能 ImportSnapshot 还原 ContractSet。
/// </summary>
public sealed class XcpMdfAttachmentTests
{
    private static readonly MdfChannelSpec Rpm = new("EngineSpeed", "rpm");

    private static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4at_{Guid.NewGuid():N}.mf4");

    private static string TempDir() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s4at_{Guid.NewGuid():N}");

    private static PlannedDaqEntry Entry(int index) =>
        new(2, 1, (ushort)index, "EngineSpeed", 0, 2, 0, 0x2000, null);

    /// <summary>最小可解码快照：单 Measurement 对象、identical 换算、单段。</summary>
    private static ContractSnapshot BuildSnapshot() => new(
        ContractSchemaVersion: 1,
        A2lSha256: "A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2A2",
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
                LowerLimit: null, UpperLimit: null, ExtLowerLimit: null, ExtUpperLimit: null,
                IsWritable: false,
                AccessSource: "Declared",
                Conversion: new SnapshottedConversion(
                    "identical", null, null, null, null, null, null,
                    null, null, null, null, null, null),
                Axis: null,
                Comment: null,
                Notes: [],
                SourceLine: 1),
        ]);

    // ---------------- writer 层：AT 块结构 ----------------

    [Fact]
    public async Task Attachment_survives_finalize_with_structure_and_payload_intact()
    {
        var path = TempPath();
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"contractSchemaVersion\":1}");
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteAttachmentAsync("application/json",
                "contractSchemaVersion=1; packageVersion=0.1.1", payload);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            var bytes = await File.ReadAllBytesAsync(path);

            // ID 终态 + HD.at 链头已回写。
            Assert.Equal("MDF     ", Encoding.ASCII.GetString(bytes, 0, 8));
            var at = BitConverter.ToUInt64(bytes, 0x70); // HD.at = hd+24+3*8
            Assert.NotEqual(0UL, at);

            var att = Assert.Single(MdfAttachmentReader.ReadAll(bytes));
            Assert.Equal("##AT", Encoding.ASCII.GetString(bytes, (int)at, 4));
            Assert.Equal(0UL, BitConverter.ToUInt64(bytes, (int)at + 24)); // 链尾 next=0
            Assert.Equal("application/json", att.MimeType);
            Assert.Contains("contractSchemaVersion=1", att.Comment);
            Assert.Contains("packageVersion=0.1.1", att.Comment);
            Assert.Equal(payload, att.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Attachment_mid_recording_keeps_records_intact()
    {
        var path = TempPath();
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"contracts\":[]}");
            var writer = Mdf4StreamWriter.Create(path, [Rpm], DateTimeOffset.UtcNow);
            await writer.WriteRecordAsync(0, 0.0, 1.0);
            await writer.WriteRecordAsync(0, 0.5, 2.0);
            await writer.WriteAttachmentAsync("application/json", "mid", payload);
            await writer.WriteRecordAsync(0, 1.0, 3.0);
            await writer.FinalizeAsync();
            await writer.DisposeAsync();

            // 附件夹在样本中间写不破坏记录面（DT 寻址走文件长度）。
            var layout = Mdf4Layout.Parse(await File.ReadAllBytesAsync(path));
            Assert.Equal(3, layout.Records.Count);
            Assert.Equal((1.0, 3.0), layout.Records[2]);

            var att = Assert.Single(MdfAttachmentReader.ReadAll(await File.ReadAllBytesAsync(path)));
            Assert.Equal(payload, att.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------- sink 层：判据 2 ----------------

    [Fact]
    public async Task Snapshot_attachment_restores_contract_set_without_a2l()
    {
        var dir = TempDir();
        try
        {
            var snapshot = BuildSnapshot();
            var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                Directory = dir,
                Channels = [Rpm],
                ContractSnapshot = snapshot,
            });
            await sink.StartAsync(DateTimeOffset.UtcNow);
            sink.OnValues(new XcpDaqSample(Entry(0), 1.5, DateTimeOffset.UtcNow));
            await sink.StopAsync();

            var bytes = await File.ReadAllBytesAsync(sink.FilePath!);
            var att = Assert.Single(MdfAttachmentReader.ReadAll(bytes));

            // 附件注释带 contractSchemaVersion + packageVersion（spec D2）。
            Assert.Contains($"contractSchemaVersion={snapshot.ContractSchemaVersion}", att.Comment);
            Assert.Contains($"packageVersion={snapshot.PackageVersion}", att.Comment);

            // 判据 2：不给 A2L，仅凭 MF4 内 JSON 快照还原 ContractSet。
            Assert.Equal("application/json", att.MimeType);
            var set = Asap2PackageApi.ImportSnapshot(Encoding.UTF8.GetString(att.Data));
            var contract = Assert.Single(set.All);
            Assert.Equal("EngineSpeed", contract.ObjectName);
            Assert.Equal("rpm", contract.Unit);
            // 快照解出的合同一律"仅回放"，不给能采集的假象（§5.3-5）。
            Assert.False(contract.Suitability.EligibleForDaq);
            Assert.Equal(AcquisitionRoute.None, contract.Suitability.Routes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Sink_without_snapshot_writes_no_attachment()
    {
        var dir = TempDir();
        try
        {
            var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
            {
                Directory = dir,
                Channels = [Rpm],
            });
            await sink.StartAsync(DateTimeOffset.UtcNow);
            await sink.StopAsync();

            var bytes = await File.ReadAllBytesAsync(sink.FilePath!);
            Assert.Equal(0UL, BitConverter.ToUInt64(bytes, 0x70)); // HD.at 空
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>测试侧最小 AT 块解析（只解本写入器产出的形态）。</summary>
internal sealed record MdfAttachment(string MimeType, string Comment, byte[] Data);

internal static class MdfAttachmentReader
{
    public static IReadOnlyList<MdfAttachment> ReadAll(byte[] b)
    {
        var list = new List<MdfAttachment>();
        var at = BitConverter.ToUInt64(b, 0x70); // HD.at
        while (at != 0)
        {
            Assert.Equal("##AT", Encoding.ASCII.GetString(b, (int)at, 4));
            var next = BitConverter.ToUInt64(b, (int)at + 24);
            var mimeAddr = BitConverter.ToUInt64(b, (int)at + 40);
            var commentAddr = BitConverter.ToUInt64(b, (int)at + 48);
            var flags = BitConverter.ToUInt16(b, (int)at + 56);          // U16：embedded=bit0, md5=bit2
            Assert.Equal(0, BitConverter.ToUInt16(b, (int)at + 58));     // creator_index
            Assert.Equal(0, BitConverter.ToUInt16(b, (int)at + 60));     // zip_type（未压缩）
            Assert.Equal((ushort)0x1, (ushort)(flags & 0x1));            // embedded 必置位

            var dataOff = (int)at + 96; // AT_COMMON_SIZE
            var dataLen = (int)BitConverter.ToUInt64(b, (int)at + 88);   // embedded_size
            var raw = b[dataOff..(dataOff + dataLen)];
#pragma warning disable CA5351 // MDF 4.1 ATBLOCK checksum (integrity, not security)
            var storedMd5 = b[((int)at + 64)..((int)at + 80)];
            Assert.Equal(storedMd5, System.Security.Cryptography.MD5.HashData(raw));
#pragma warning restore CA5351
            list.Add(new MdfAttachment(
                ReadTx(b, mimeAddr),
                commentAddr == 0 ? string.Empty : ReadTx(b, commentAddr),
                raw));
            at = next;
        }
        return list;
    }

    private static string ReadTx(byte[] b, ulong addr)
    {
        Assert.Equal("##TX", Encoding.ASCII.GetString(b, (int)addr, 4));
        var len = (long)BitConverter.ToUInt64(b, (int)addr + 8) - 24;
        return Encoding.ASCII.GetString(b, (int)addr + 24, (int)len).TrimEnd('\0');
    }
}
