using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Implementation of IHilRunnerService. Builds a scoped IHost per run and executes the test suite.
/// </summary>
public sealed class HilRunnerService : IHilRunnerService
{
    private readonly ILogger<HilRunnerService> _logger;

    public HilRunnerService(ILogger<HilRunnerService> logger) => _logger = logger;

    /// <inheritdoc/>
    public DbcDocument? LastDbcDocument { get; private set; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<ChannelId, DbcDocument>? LastPerChannelDbcs { get; private set; }

    /// <inheritdoc/>
    public string? LastCaseLogDirectory { get; private set; }

    /// <summary>解析 case-log 目录：request 覆盖值 或 默认 %LocalAppData%\PeakCanHost\hil-reports\case-logs\。internal 便于测试。</summary>
    internal static string ResolveCaseLogDirectory(HilRunRequest request)
        => request.CaseLogDirectory
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports", "case-logs");

    public async Task<TestSuiteResult> RunAsync(
        HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default)
    {
        // 每次运行前重置，避免上一次运行的 DBC 残留：若本次 Build()/DBC 解析失败，
        // LastDbcDocument 保持 null，报告回落 hex，而不是沿用上次的陈旧文档。
        LastDbcDocument = null;
        // 同样每次 run 重置：只有本次 CaptureCaseLogs 成功才重新赋值。
        LastCaseLogDirectory = null;

        using var host = HeadlessHostBuilder.Build(HilRunRequestExtensions.ToCliArgs(request));

        // 报告用 DBC 必须与运行实际解析的文档一致（避免 DbcService.Current 指向 trace 面板的其它文件）。
        LastDbcDocument = host.Services.GetService<DbcDocument>();

        // 多通道 per-channel DBC 字典（HeadlessHostBuilder 注册，null = 单通道）。
        LastPerChannelDbcs = host.Services.GetService<IReadOnlyDictionary<ChannelId, DbcDocument>>();

        var engine = host.Services.GetRequiredService<TestSuiteEngine>();
        var channel = host.Services.GetRequiredService<ICanChannel>();
        var ctx = host.Services.GetRequiredService<IAssertionContext>();
        var environmentRuntime = new EnvironmentRuntime(channel, _logger as ILogger<EnvironmentRuntime> ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentRuntime>.Instance);

        var suiteJson = await File.ReadAllTextAsync(request.SuitePath, ct);
        var suite = System.Text.Json.JsonSerializer.Deserialize<TestSuite>(suiteJson, PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize test suite JSON.");

        // 用例选择: 非空列表时只运行匹配的用例
        if (request.SelectedCaseNames is { Count: > 0 } selected)
            suite = suite with { Cases = suite.Cases.Where(c => selected.Contains(c.Name)).ToList() };

        // 连接通道：多通道路径逐通道 connect（每通道独立 BaudRate/Fd，从 request.HardwareChannels 查）。
        // 第一个 SingleChannelContext 复用 DI 默认 ICanChannel singleton（同 handle，见 HeadlessHostBuilder），
        // 故 ConnectAllAsync 连第一个 = 连 DI singleton，单通道默认依赖（BackgroundFrameSender /
        // IFrameStatistics / IsoTpLayer / UdsClient，§3.4 延迟多通道化）即观察到默认 bus——无需再单独 connect。
        // 单通道路径维持原样（默认 ICanChannel，FD 1Mbps）。
        if (ctx is MultiChannelAssertionContext multi && request.HardwareChannels is { Count: > 0 } hwCfgs)
        {
            // Review HIGH-1: 连接失败明细显式上报——首通故障不再静默降级。
            multi.Failures.Clear();
            var cfgByName = hwCfgs.ToDictionary(c => c.Name, c => (c.BaudRate, c.Fd), StringComparer.Ordinal);
            await multi.ConnectAllAsync(name =>
            {
                // 已声明的通道用其 ChannelConfig；未找到（不应发生——ctx 与 cfgs 同源）回落默认。
                if (cfgByName.TryGetValue(name, out var v) && v.BaudRate is not null)
                    return (v.BaudRate, v.Fd);
                return (BaudRate.CanFd1Mbps, true);
            }, ct);
            if (multi.Failures.Count > 0)
            {
                var failed = string.Join(", ", multi.Failures.Select(f => $"{f.ChannelName}({f.ErrorCode})"));
                throw new InvalidOperationException($"CAN 通道连接失败: {failed}");
            }
        }
        else
        {
            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true, ct);
        }

        // 启动后台帧
        if (suite.Environment is { Count: > 0 })
        {
            var envErrors = RestbusNodeValidator.Validate(suite.Environment, suite.Channels, null);
            if (envErrors.Count > 0)
                throw new InvalidOperationException(
                    "Environment validation failed:\n" + string.Join("\n", envErrors));
            environmentRuntime.Start(suite.Environment, suite.Channels);
        }

        try
        {
            // 每 case 全量报文 log: 建目录 + 构造 factory（P4 降级）
            IHilFrameSinkFactory? sinkFactory = null;
            if (request.CaptureCaseLogs)
            {
                var dir = ResolveCaseLogDirectory(request);
                try
                {
                    Directory.CreateDirectory(dir);
                    var runTimestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                    sinkFactory = new AscFrameSinkFactory(dir, runTimestamp);
                    LastCaseLogDirectory = dir;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Case log directory unavailable, capture disabled: {Dir}", dir);
                    sinkFactory = null;
                }
            }

            // B2-R1 + Bug-2：帧统计从 DI 取（多通道=MultiChannelFrameStatistics 按通道路由，
            // 单通道=FrameStatisticsCollector）。替代手动 new 单通道 collector——后者在多通道下
            // 无法路由到非默认通道，导致 frameCount/frameSeen 表达式多通道失效。
            // DI 注册的 IFrameStatistics 由 host Dispose 负责释放（退订 FrameReceived），无需手动 Dispose。
            var frameStats = host.Services.GetService<IFrameStatistics>();
            return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct, sinkFactory, frameStats);
        }
        finally
        {
            environmentRuntime.Stop();
            // Bug-1：多通道路径必须断开所有通道（非首通道 PCAN handle 否则泄漏 + 读循环空转）。
            // 单通道路径维持原样（只断 DI 默认 singleton）。
            if (ctx is MultiChannelAssertionContext multiCtx)
                await multiCtx.DisconnectAllAsync(ct);
            else
                await channel.DisconnectAsync(ct);
        }
    }
}
