using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 金标用例：同一段诊断会话分别以 ASC 和 BLF 保存，解析后产出的规范化报告必须逐项一致。
/// 覆盖多帧 DID、0x78 后成功、最终 NRC、未闭合请求、其它 ID 周期帧噪声。
/// </summary>
public sealed class UdsOfflineGoldenTests
{
    private static readonly UdsOfflineAddress Addr = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    /// <summary>与 Fixtures/offline-diagnostic.asc 一致的帧序列（时间秒 → BLF 纳秒）。</summary>
    private static readonly (double Ts, uint Id, byte[] Data)[] GoldenFrames =
    {
        (0.000000, 0x7E0, new byte[] { 0x02, 0x10, 0x01 }),
        (0.010000, 0x7E8, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 }),
        (0.100000, 0x7E0, new byte[] { 0x03, 0x22, 0xF1, 0x90 }),
        (0.110000, 0x7E8, new byte[] { 0x03, 0x7F, 0x22, 0x78 }),
        (1.100000, 0x7E8, new byte[] { 0x03, 0x7F, 0x22, 0x31 }),
        (2.000000, 0x7E0, new byte[] { 0x02, 0x3E, 0x00 }),
        (2.050000, 0x7E0, new byte[] { 0x03, 0x22, 0xF1, 0x87 }),
        (2.060000, 0x7E8, new byte[] { 0x10, 0x0D, 0x62, 0xF1, 0x87, 0x41, 0x41, 0x41 }),
        (2.070000, 0x7E8, new byte[] { 0x21, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41 }),
        (2.200000, 0x123, new byte[] { 0xAA, 0xBB }),
        (2.300000, 0x123, new byte[] { 0xAA, 0xBB }),
    };

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new FileNotFoundException("找不到 PeakCan.Host.slnx —— 测试不是在仓库目录内运行？");

        return System.IO.Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static MemoryStream AscStream()
        => new(File.ReadAllBytes(RepoFile(
            "tests", "PeakCan.Host.Core.Tests", "Analysis", "Uds", "Fixtures", "offline-diagnostic.asc")));

    private static void WriteFileHeader(MemoryStream ms)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
    }

    private static void WriteObject(MemoryStream ms, uint objType, int objectDataSize, Action<BinaryWriter> writeFrameData, long timestamp)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.Write(BitConverter.GetBytes((uint)(BlfFormat.ObjectHeaderSize + objectDataSize)));
        ms.Write(BitConverter.GetBytes(objType));
        ms.Write(BitConverter.GetBytes(0u));            // object_flags
        ms.Write(BitConverter.GetBytes((ushort)0));     // client_index
        ms.Write(BitConverter.GetBytes((ushort)0));     // reserved
        ms.Write(BitConverter.GetBytes(timestamp));     // 1-ns ticks
        var start = ms.Position;
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writeFrameData(writer);
        }
        (ms.Position - start).Should().Be(objectDataSize);
    }

    /// <summary>用同一帧序列合成 BLF：CAN_MESSAGE 对象，时间戳 = 秒 × 1e9。</summary>
    private static MemoryStream BlfStream()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        foreach (var (ts, id, data) in GoldenFrames)
        {
            var payload = new byte[8];
            data.CopyTo(payload, 0);
            WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
            {
                w.Write((ushort)1);      // channel
                w.Write((byte)0);        // flags
                w.Write((byte)data.Length);
                w.Write(id);
                w.Write(payload);
            }, timestamp: (long)Math.Round(ts * 1e9));
        }
        ms.Position = 0;
        return ms;
    }

    private static UdsOfflineAnalysisRequest Request(IReadOnlyList<ReplayFrame> frames, Guid id)
        => new(
            id,
            "offline-diagnostic",
            "offline-diagnostic.asc",
            frames,
            Addr,
            new UdsOfflineDiagnostics(
                Array.Empty<DidDefinition>(),
                Array.Empty<RoutineDefinition>(),
                OdxLoaded: false,
                UserJsonLoadStatus.NotConfigured,
                UserJsonLoadStatus.NotConfigured,
                SnapshotId: "golden"),
            "golden-engine");

    private static object Normalize(UdsOfflineReport report) => new
    {
        report.Counts,
        report.Noise.ResponseValidationFailed,
        report.Noise.DuplicateFinalResponses,
        report.Noise.TopIds,
        report.Noise.Reasons,
        Transactions = report.Transactions.Select(t => new
        {
            Service = t.Interpretation?.ServiceDisplay,
            t.FinalKind,
            t.PendingCount,
            t.FinalNrc,
            FirstMs = Round(t.FirstResponseMs),
            TotalMs = Round(t.TotalMs),
            RequestEvidence = t.Request.Evidence.ToArray(),
            ResponseEvidence = t.Responses.Select(r => r.Evidence.ToArray()).ToArray(),
            t.HasDuplicateFinalResponse,
            t.IsAmbiguous,
        }).ToArray(),
        Orphans = report.OrphanResponses.Select(p => p.Evidence.ToArray()).ToArray(),
        report.IsPartial,
    };

    private static double? Round(double? value)
        => value is { } ms ? Math.Round(ms, 3) : null;

    [Fact]
    public async Task Asc_and_blf_produce_the_same_normalized_report()
    {
        var asc = await AscParser.ParseAsync(AscStream(), ReplayOptions.Default, logger: null, ct: default);
        var blf = await BlfParser.ParseAsync(BlfStream(), ReplayOptions.Default, logger: null, ct: default);

        asc.Should().HaveCount(GoldenFrames.Length, "ASC fixture 必须与合成序列一一对应");
        blf.Should().HaveCount(GoldenFrames.Length, "BLF 必须与 ASC 帧数一致");

        var id = Guid.NewGuid();
        var a = new UdsOfflineAnalyzer().Analyze(Request(asc, id));
        var b = new UdsOfflineAnalyzer().Analyze(Request(blf, id));

        Normalize(a).Should().BeEquivalentTo(Normalize(b));
    }

    [Fact]
    public async Task Golden_log_yields_expected_transaction_shapes()
    {
        var asc = await AscParser.ParseAsync(AscStream(), ReplayOptions.Default, logger: null, ct: default);
        var report = new UdsOfflineAnalyzer().Analyze(Request(asc, Guid.NewGuid()));

        report.Counts.Closes.Should().BeTrue();
        report.Transactions.Should().HaveCount(4, "0x10 成功 / 0x22 最终 NRC / 0x3E 未闭合 / 0x22 多帧成功");
        report.Transactions.Should().Contain(t => t.FinalKind == UdsFinalKind.Negative && t.FinalNrc == 0x31);
        report.Transactions.Should().Contain(t => t.FinalKind == UdsFinalKind.None && t.Request.Payload[0] == 0x3E);
        report.Transactions.Should().Contain(t =>
            t.FinalKind == UdsFinalKind.Positive && t.Request.Payload[0] == 0x22 && t.Responses.Single().IsMultiFrame);
        report.Transactions.Should().Contain(t => t.PendingCount == 1, "0x78 必须计入同一次事务");
        report.Noise.TopIds.Should().Contain(x => x.Id == 0x123u && x.Count == 2);
    }
}
