using System.Globalization;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// 2026-08-15 Task 11: case-log 全链路集成测试较重量级——每个用例构建真实 DI 宿主
/// （HeadlessHostBuilder）并驱动 VirtualEcu。串行化本集合，避免与其它 timing-sensitive
/// 测试（BackgroundFrameAutoConfigTests / EcuSimulatorHostTests 等）并行争抢 CPU 导致偶发失败。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HILIntegrationCollection
{
    public const string Name = "HILIntegration";
}

/// <summary>
/// Sprint 2 Inc 4: Integration tests for the full HIL pipeline.
/// Uses inline DBC + ASC fixtures (self-contained).
/// </summary>
[Collection(HILIntegrationCollection.Name)]
public class HILIntegrationTests
{
    /// <summary>Minimal DBC with one standard message (ID=256) and one signal.</summary>
    private const string SimpleDBC = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU

        BO_ 256 TestMsg: 8 ECU
         SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
        """;

    /// <summary>DBC with extended frame message (ID=0x98FEF100 with bit 31 set = 2566844672).</summary>
    private const string ExtendedDBC = """
        VERSION "1.0";
        NS_ :
        BS_:
        BU_: ECU

        BO_ 2566844672 ExtMsg: 8 ECU
         SG_ ExtSig : 0|8@1+ (1,0) [0|255] "V"  ECU
        """;

    private static string WriteTempAsc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string WriteTempDbc(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static TestSuite CreateSuite(params TestCase[] cases)
        => new("IntegrationSuite", cases, Array.Empty<string>(),
            Array.Empty<string>(), new TestSuiteConfig(), 0);

    private static TestCase CreateCase(string id, string name, params TestCaseStep[] steps)
        => new(id, name, "", null, steps, null, Array.Empty<string>(), 0, null);

    // ── Task 11 (2026-08-15): case-log 全链路集成测试 helper ──
    // 经真实 HilRunnerService（内部走 HeadlessHostBuilder 的 DI 宿主）跑 suite，
    // 与 CLI 端到端（Program.Main → 同一 builder）同一链路，覆盖 case-log P4 全流程。

    private static string WriteTempEcuScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "name": "TestEcu",
              "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
              "rules": [
                { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] }
              ]
            }
            """, Encoding.UTF8);
        return path;
    }

    private static string WriteTempSuite(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_int_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string NewCaseLogDir()
        => Path.Combine(Path.GetTempPath(), $"hil_case_logs_{Guid.NewGuid():N}");

    /// <summary>一次 suite 运行的结果 + runner 实例（读 LastCaseLogDirectory）。</summary>
    private sealed record RunOutcome(TestSuiteResult Result, IHilRunnerService Runner);

    /// <summary>构造 runner 并跑一次 suite；与 WPF App 注册方式一致（IHilRunnerService → HilRunnerService）。</summary>
    private static async Task<RunOutcome> RunSuite(HilRunRequest request)
    {
        var runner = new HilRunnerService(NullLogger<HilRunnerService>.Instance);
        var result = await runner.RunAsync(request);
        return new RunOutcome(result, runner);
    }

    /// <summary>Pass case：sendFrame TesterPresent(0x3E) → expect 0x7E 正响应（与 CLI e2e 同款，证明全链路可用）。</summary>
    private const string PassingCaseLogSuiteJson = """
        {
          "name": "CaseLogSuite",
          "globalCaseFixtureKeys": [],
          "suiteFixtureKeys": [],
          "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
          "cases": [
            { "id": "c1", "name": "Pass", "steps": [
              { "parameters": { "$kind": "sendFrame", "id": { "raw": 2016, "format": "Standard", "type": "Data" }, "data": [2, 62, 0], "fd": false } },
              { "parameters": { "$kind": "expectFrame", "id": { "raw": 2024, "format": "Standard", "type": "Data" }, "dataMask": [0, 126], "timeoutMs": 2000 } }
            ] }
          ]
        }
        """;

    /// <summary>负测试 case：expectedVerdict=Fail，expect 永不匹配（mask 255,255）→ 超时失败 → 引擎提升为 Passed。</summary>
    private const string NegatedCaseLogSuiteJson = """
        {
          "name": "CaseLogNegatedSuite",
          "globalCaseFixtureKeys": [],
          "suiteFixtureKeys": [],
          "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
          "cases": [
            { "id": "c2", "name": "Negated", "steps": [
              { "parameters": { "$kind": "sendFrame", "id": { "raw": 2016, "format": "Standard", "type": "Data" }, "data": [2, 62, 0], "fd": false } },
              { "expectedVerdict": "Fail", "parameters": { "$kind": "expectFrame", "id": { "raw": 2024, "format": "Standard", "type": "Data" }, "dataMask": [255, 255], "timeoutMs": 300 } }
            ] }
          ]
        }
        """;

    [Fact]
    public async Task End_to_end_standard_frame_signal_decoded_and_asserted()
    {
        // Arrange: DBC + ASC + Suite
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  64 00 00 00 00 00 00 00
 0.100000 1  100  8  64 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            doc.IsSuccess.Should().BeTrue("DBC should parse");

            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            // Build real executor chain (mirrors HeadlessHostBuilder)
            var primitives = new AssertionPrimitives(ctx);
            var executors = new PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor[]
            {
                new WaitForSignalStepExecutor(primitives),
                new AssertSignalStepExecutor(primitives),
            };

            var engine = new TestSuiteEngine(new FakeFixtureResolver(), executors);

            var suite = CreateSuite(CreateCase("case_1", "Standard Frame Test",
                TestCaseStep.Create(new WaitForSignalStep("TestMsg.TestSignal", "100.0", "5.0", "5000")),
                TestCaseStep.Create(new AssertSignalStep("TestMsg.TestSignal", "100.0", "5.0"))));

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

            result.TotalCases.Should().Be(1);
            result.CaseResults[0].Passed.Should().BeTrue("WaitForSignal(100) + AssertSignal(100) should pass");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_extended_frame_signal_decoded_correctly()
    {
        var dbcPath = WriteTempDbc(ExtendedDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  18FEF100  8  42 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            doc.IsSuccess.Should().BeTrue("DBC should parse");

            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
            await Task.Delay(500);

            // Extended frame: CanFrame.Id.Raw=0x18FEF100 → DBC key 0x98FEF100
            var value = ctx.GetSignalValue("ExtMsg.ExtSig");
            value.Should().Be(0x42, "extended frame signal should be decoded via ToDbcLookupKey");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_Dispose_cleans_up_without_hanging()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  01 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
            await Task.Delay(200);

            // Dispose should complete within 5s
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ctx.Dispose();
            await channel.DisposeAsync();
            sw.ElapsedMilliseconds.Should().BeLessThan(5000, "Dispose should complete within 5s");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public async Task End_to_end_multiple_frames_signal_cache_updates()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ascPath = WriteTempAsc(@"
date Wed Jun 28 10:00:00.000 2026
base hex  timestamps absolute

 0.000000 1  100  8  10 00 00 00 00 00 00 00
 0.100000 1  100  8  20 00 00 00 00 00 00 00
 0.200000 1  100  8  30 00 00 00 00 00 00 00
");
        try
        {
            var doc = DbcParser.Parse(File.ReadAllText(dbcPath));
            var channel = new TraceDrivenChannel(new ChannelId(1));
            channel.LoadAscii(ascPath);

            var dbc = new HeadlessDbcLookup(doc.Value!);
            using var ctx = new HILAssertionContext(channel, dbc);

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

            // Wait for all 3 frames
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double? value = null;
            while (sw.ElapsedMilliseconds < 5000)
            {
                value = ctx.GetSignalValue("TestMsg.TestSignal");
                if (value == 0x30) break; // Last frame value
                await Task.Delay(50);
            }

            value.Should().Be(0x30, "signal cache should reflect latest frame value");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ascPath);
        }
    }

    [Fact]
    public void Di_double_lambda_registers_DbcDocument_and_IDbcLookup()
    {
        // Spike 2026-08-10 验证项 3：HeadlessHostBuilder 改为 DbcDocument 工厂单例 +
        // IDbcLookup 依赖它（两个独立 lambda）。验证 MS DI 容器解析两者均成功、
        // IDbcLookup 能查到 DbcDocument 里的消息（证明依赖注入正确、无 InvalidOperationException）。
        var dbcPath = WriteTempDbc(SimpleDBC);
        try
        {
            var args = new CliArgs(dbcPath, "suite.json", TracePath: "trace.asc");
            using var host = HeadlessHostBuilder.Build(args);

            var doc = host.Services.GetRequiredService<DbcDocument>();
            doc.Should().NotBeNull();
            doc.MessagesById.Should().NotBeEmpty();

            var lookup = host.Services.GetRequiredService<IDbcLookup>();
            lookup.FindMessage(0x100).Should().NotBeNull("IDbcLookup 应从注册的 DbcDocument 中查到消息");
        }
        finally
        {
            File.Delete(dbcPath);
        }
    }

    // ── Task 11 (2026-08-15): case-log 全链路集成测试（P4）──
    // 经 HilRunnerService 真实运行，验证 CaptureCaseLogs=true 时每个 case 在解析目录产出 .asc。

    [Fact]
    public async Task CaptureCaseLogs_True_ProducesAscPerCase()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ecuPath = WriteTempEcuScript();
        var suitePath = WriteTempSuite(PassingCaseLogSuiteJson);
        var caseLogDir = NewCaseLogDir();
        try
        {
            var outcome = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: true, CaseLogDirectory: caseLogDir));

            outcome.Result.TotalCases.Should().Be(1);
            outcome.Result.PassedCases.Should().Be(1);

            var dir = outcome.Runner.LastCaseLogDirectory;
            dir.Should().NotBeNull("CaptureCaseLogs=true 应记录实际使用的目录");
            Directory.Exists(dir).Should().BeTrue();
            var ascFiles = Directory.GetFiles(dir!, "*.asc");
            ascFiles.Should().NotBeEmpty("每个 case 应产出 .asc");

            var content = File.ReadAllText(ascFiles[0]);
            content.Should().Contain("base hex", "ASC 应有标准 header");
            content.Should().Contain("Rx d", "case 期间的 CAN 帧应写入 .asc");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
            if (Directory.Exists(caseLogDir)) Directory.Delete(caseLogDir, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureCaseLogs_False_ProducesNoAsc()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ecuPath = WriteTempEcuScript();
        var suitePath = WriteTempSuite(PassingCaseLogSuiteJson);
        var caseLogDir = NewCaseLogDir();
        try
        {
            var outcome = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: false, CaseLogDirectory: caseLogDir));

            outcome.Result.TotalCases.Should().Be(1);
            outcome.Runner.LastCaseLogDirectory.Should().BeNull("CaptureCaseLogs=false 不应挂载 case-log");
            Directory.Exists(caseLogDir).Should().BeFalse("CaptureCaseLogs=false 不应创建目录");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
            if (Directory.Exists(caseLogDir)) Directory.Delete(caseLogDir, recursive: true);
        }
    }

    [Fact]
    public async Task NegatedCase_AlsoLogsAsc()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ecuPath = WriteTempEcuScript();
        var suitePath = WriteTempSuite(NegatedCaseLogSuiteJson);
        var caseLogDir = NewCaseLogDir();
        try
        {
            var outcome = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: true, CaseLogDirectory: caseLogDir));

            // expectedVerdict=Fail + 步骤确实失败 → 负测试提升为 Passed
            outcome.Result.TotalCases.Should().Be(1);
            outcome.Result.PassedCases.Should().Be(1);
            outcome.Result.CaseResults[0].StepResults
                .Should().Contain(s => s.WasNegatedTest, "负测试步骤应被判定为 WasNegatedTest");

            var dir = outcome.Runner.LastCaseLogDirectory;
            dir.Should().NotBeNull();
            var ascFiles = Directory.GetFiles(dir!, "*.asc");
            ascFiles.Should().NotBeEmpty("负测试 case 也应产出 .asc");

            var content = File.ReadAllText(ascFiles[0]);
            content.Should().Contain("base hex");
            content.Should().Contain("Rx d", "负测试 case 期间发送/接收的帧也应落盘");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
            if (Directory.Exists(caseLogDir)) Directory.Delete(caseLogDir, recursive: true);
        }
    }

    [Fact]
    public async Task MissingCaseLogDir_IsAutoCreated()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ecuPath = WriteTempEcuScript();
        var suitePath = WriteTempSuite(PassingCaseLogSuiteJson);
        var caseLogDir = NewCaseLogDir();
        try
        {
            // 首次运行：目录不存在 → 自动创建
            var first = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: true, CaseLogDirectory: caseLogDir));
            Directory.Exists(first.Runner.LastCaseLogDirectory).Should().BeTrue("目录不存在时应自动创建");

            // 删除目录后再跑 → 再次自动重建
            Directory.Delete(caseLogDir, recursive: true);
            var second = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: true, CaseLogDirectory: caseLogDir));
            second.Runner.LastCaseLogDirectory.Should().Be(caseLogDir);
            Directory.Exists(caseLogDir).Should().BeTrue();
            Directory.GetFiles(caseLogDir, "*.asc").Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
            if (Directory.Exists(caseLogDir)) Directory.Delete(caseLogDir, recursive: true);
        }
    }

    [Fact]
    public async Task UnwritableCaseLogDir_DegradesWithoutFailure()
    {
        var dbcPath = WriteTempDbc(SimpleDBC);
        var ecuPath = WriteTempEcuScript();
        var suitePath = WriteTempSuite(PassingCaseLogSuiteJson);
        const string UnwritableDir = @"Z:\no_such_drive\x";
        try
        {
            // Z:\ 不存在 → Directory.CreateDirectory 抛异常 → P4 降级：不抛、正常完成
            var outcome = await RunSuite(new HilRunRequest(
                dbcPath, suitePath, EcuScriptPath: ecuPath, Mode: HilMode.VirtualEcu,
                CaptureCaseLogs: true, CaseLogDirectory: UnwritableDir));

            outcome.Result.TotalCases.Should().BeGreaterThan(0, "降级不应影响 suite 执行");

            // 仅当 Z: 盘确实不存在时，降级信号（目录未被记录）才成立；
            // 若本机恰好映射了 Z:，目录可正常创建，此断言跳过。
            if (!Directory.Exists(@"Z:\"))
                outcome.Runner.LastCaseLogDirectory.Should().BeNull("P4 降级：不可写目录不应被记录为已启用");
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
        }
    }
}

/// <summary>Fake fixture resolver for integration tests.</summary>
internal sealed class FakeFixtureResolver : IFixtureResolver
{
    public ITestFixture Resolve(string key) => new NoOpFixture();
}

internal sealed class NoOpFixture : ITestFixture
{
    public Task SetupAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
}
