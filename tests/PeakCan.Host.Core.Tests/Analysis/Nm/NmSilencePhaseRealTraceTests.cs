using System.Security.Cryptography;
using System.Text;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 静默相位在真实 trace 上的验收（spec §9）。
/// <para>
/// 数据与 <see cref="NmReasonRealTraceTests"/> 同源：`TestData/Nm/bms.dbc` + `nm16.blf`。
/// 该目录当前未被 git 跟踪；缺失时打印 skip 行并返回（xunit 2.9 无动态 Skip）。
/// CI 不会因此变红，但「绿」不等于验过——只有输出里有 skip 行才说明是跳过的。
/// </para>
/// <para>
/// 真实事实：9.714–11.714s BMS 仍在发应用报文（RSS），11.714–12.677s 应用报文停止、
/// 总线 0.92s 无帧（预睡眠窗口），12.677s 被唤醒。
/// </para>
/// </summary>
public class NmSilencePhaseRealTraceTests
{
    private const string DbcSha =
        "23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f";

    static NmSilencePhaseRealTraceTests()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string DataDir()
        => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "Nm");

    /// <summary>
    /// 与 <see cref="NmRealTraceEndToEndTests"/> 同一约定：外部 trace 不入仓（涉密 + 体积）。
    /// 缺 fixture 时打印 skip 而不是失败；xunit 2.9 没有动态 Skip。
    /// </summary>
    private static bool FixtureExists()
    {
        var dbc = System.IO.Path.Combine(DataDir(), "bms.dbc");
        var blf = System.IO.Path.Combine(DataDir(), "nm16.blf");
        var exists = System.IO.File.Exists(dbc) && System.IO.File.Exists(blf);
        if (!exists)
        {
            Console.WriteLine(
                $"[NM silence phase] skip: fixture 缺失。dbc={dbc} exists={System.IO.File.Exists(dbc)}；" +
                $"blf={blf} exists={System.IO.File.Exists(blf)}");
        }
        return exists;
    }

    private static NmReport Analyze()
    {
        var dbcBytes = File.ReadAllBytes(System.IO.Path.Combine(DataDir(), "bms.dbc"));
        Assert.Equal(DbcSha, Convert.ToHexString(SHA256.HashData(dbcBytes)).ToLowerInvariant());

        var dbc = DbcParser.Parse(Encoding.GetEncoding("GBK").GetString(dbcBytes)).Value!;
        using var fs = File.OpenRead(System.IO.Path.Combine(DataDir(), "nm16.blf"));
        var frames = BlfParser.ParseAsync(fs, ReplayOptions.Default).GetAwaiter().GetResult();
        var profile = NmProfileBuilder.Build(dbc, null);
        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "real", null, frames, dbc, profile, 0, frames[^1].Timestamp + 1e-9, "full"));
    }

    [Fact]
    [Trait("Manual", "true")]
    public void RealTrace_Nm16_EmitsReleaseWaitAndPrepareSleep()
    {
        if (!FixtureExists()) return;

        var report = Analyze();

        // 该 trace 在 30.536s 还有第二个尾部空档；按时间锚定第一段。
        var release = Assert.Single(report.SilencePhases,
            p => p.Kind == NmSilencePhaseKind.ReleaseWait && Math.Abs(p.Start - 9.713674) < 0.001);
        Assert.Equal(9.713674, release.Start, 3);
        // 该 DBC 的 NmAsrTimeoutTime=2000 → 11.713674。
        Assert.Equal(11.713674, release.End, 3);
        Assert.Equal(NmSilenceBasis.NmSenderStillActive, release.Basis);

        var prepare = Assert.Single(report.SilencePhases,
            p => p.Kind == NmSilencePhaseKind.PrepareSleep && Math.Abs(p.Start - 11.713674) < 0.001);
        Assert.Equal(11.713674, prepare.Start, 3);
        Assert.Equal(12.677179, prepare.End, 3);
        Assert.Equal(NmSilenceBasis.NmSenderStoppedAfterDrain, prepare.Basis);
        // 超时后只剩清缓存尾巴；不得把整段应用报文算进准备休眠窗口。
        Assert.InRange(prepare.NmSenderAppFrameCount, 0, 10);

        Assert.All(report.SilencePhases, p => Assert.Equal("inferred", p.EvidenceKind));
        Assert.DoesNotContain(report.Findings,
            f => f.Code.Contains("SILENCE_PHASE", StringComparison.Ordinal));

        // 第二尾部空档（30.536–32.570）：w2 仅 34ms，必须 Unknown/WindowTooShort，
        // 不得升级成 PrepareSleep。真实数据是这条判据的最终防线。
        var tail = Assert.Single(report.SilencePhases,
            p => p.Kind == NmSilencePhaseKind.Unknown && Math.Abs(p.Start - 32.536181) < 0.001);
        Assert.Equal(NmSilenceBasis.WindowTooShort, tail.Basis);
        Assert.True(tail.IsWindowTruncated);
    }

    [Fact]
    [Trait("Manual", "true")]
    public void RealTrace_Nm16_KeepsLegacyAllNmSilentObservation()
    {
        if (!FixtureExists()) return;

        var report = Analyze();

        // 该 trace 有 9.714–12.677 与尾部 30.536–32.570 两个空档，各一条 observation。
        var allSilent = Assert.Single(report.Observations!,
            o => o.Kind == "all_nm_silent" && Math.Abs(o.Start - 9.713674) < 0.001);
        Assert.True(allSilent.IsInference);
        Assert.Contains(report.Unknowns,
            u => u.Contains("all_nm_silent_reason_unknown", StringComparison.Ordinal));
    }
}
