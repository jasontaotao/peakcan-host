using A2lEditor.Core;
using PeakCan.HIL.Core;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Bench;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Cli;

/// <summary>xcp-bench 参数（spec D1：CAN ID / A2L 必显式，无默认兜底）。</summary>
public sealed record XcpBenchOptions(
    string A2LPath,
    ulong MasterCanIdRaw,
    ulong SlaveCanIdRaw,
    bool SafeStateConfirmed,
    string? WriteObjectName,
    double? WriteTestValue,
    string? MapName,
    string? OutputPath,
    string? HardwareChannel);

public sealed record XcpBenchResult(int ExitCode, string ReportJson, string OutputPath);

/// <summary>
/// S7-T6：xcp-bench 台架批次命令（spec D1/D2/D3/D4 拍板）——探针 → 静态扫描 →
/// 写场景（仅 --i-have-verified-safe-state）→ MAP 上传计时，全部结果进 BenchReport JSON。
/// 退出码：0 = 批次完成且连接正常；1 = 批次跑完但连接级失败（真机接不上，fail-loud，
/// 报告仍落盘——宁全不全）；A2L 解析失败直接抛（无报告可写）。
/// </summary>
public static class XcpBenchCommand
{
    public static XcpBenchOptions ParseArgs(string[] args)
    {
        string? a2l = null, output = null, hw = null, writeObject = null, mapName = null;
        string? masterId = null, slaveId = null, valueText = null;
        var safeState = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--a2l": a2l = NextArg(args, ref i, "--a2l"); break;
                case "--output": output = NextArg(args, ref i, "--output"); break;
                case "--hw": hw = NextArg(args, ref i, "--hw"); break;
                case "--xcp-master-id": masterId = NextArg(args, ref i, "--xcp-master-id"); break;
                case "--xcp-slave-id": slaveId = NextArg(args, ref i, "--xcp-slave-id"); break;
                case "--object": writeObject = NextArg(args, ref i, "--object"); break;
                case "--value": valueText = NextArg(args, ref i, "--value"); break;
                case "--map-name": mapName = NextArg(args, ref i, "--map-name"); break;
                case "--i-have-verified-safe-state": safeState = true; break;
                default: throw new ArgumentException($"Unknown xcp-bench argument '{args[i]}'.");
            }
        }

        if (a2l is null)
            throw new ArgumentException("xcp-bench requires --a2l <path>.");
        if (masterId is null)
            throw new ArgumentException("xcp-bench requires --xcp-master-id <id> — no default fallback (D3).");
        if (slaveId is null)
            throw new ArgumentException("xcp-bench requires --xcp-slave-id <id> — no default fallback (D3).");

        double? testValue = null;
        if (valueText is not null)
        {
            if (!double.TryParse(valueText, System.Globalization.CultureInfo.InvariantCulture, out var parsedValue))
                throw new ArgumentException($"--value '{valueText}' is not a valid number.");
            testValue = parsedValue;
        }

        return new XcpBenchOptions(
            a2l, ParseCanIdRaw(masterId!, "--xcp-master-id"),
            ParseCanIdRaw(slaveId!, "--xcp-slave-id"),
            safeState, writeObject, testValue, mapName, output, hw);
    }

    /// <summary>CAN ID 解析（0x 前缀十六进制或十进制，探针同口径；uint 上限校验）。</summary>
    private static uint ParseCanIdRaw(string text, string optionName)
    {
        var isHex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var style = isHex
            ? System.Globalization.NumberStyles.HexNumber
            : System.Globalization.NumberStyles.Integer;
        if (!ulong.TryParse(
                isHex ? text[2..] : text, style,
                System.Globalization.CultureInfo.InvariantCulture, out var raw)
            || raw > uint.MaxValue)
            throw new ArgumentException($"Invalid CAN id for {optionName}: '{text}'.");
        return (uint)raw;
    }

    /// <summary>批次主流程（transport 由调用侧注入：测试传模拟从机，Program 传 XcpCanTransport）。</summary>
    public static async Task<XcpBenchResult> RunAsync(
        XcpBenchOptions options, IXcpTransport transport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        // ---- A2L 侧（fail-loud：解析不了没有报告可写）----
        var parsed = Asap2PackageApi.ParseFile(options.A2LPath);
        var document = parsed?.Value
            ?? throw new InvalidOperationException($"A2L parse failed: {options.A2LPath}");
        var contracts = Asap2PackageApi.Contracts(document);

        var items = new List<BenchItem>();
        var restores = new List<BenchRestoreRecord>();
        var connectionOk = true;

        var masterOptions = new XcpMasterOptions(
            new CanId((uint)options.MasterCanIdRaw,
                options.MasterCanIdRaw > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard),
            TimeSpan.FromMilliseconds(500), 0);
        using var master = new XcpMaster(transport, masterOptions);

        // ---- 能力探针（A-1/2/3/4/5/10）——连接级失败不中断批次，但决定退出码 ----
        XcpCapabilityProbeResult? probe = null;
        try
        {
            probe = await XcpCapabilityProber.ProbeAsync(
                master, (uint)options.MasterCanIdRaw, (uint)options.SlaveCanIdRaw, ct);
            items.AddRange(CapabilityBenchItems.FromProbeResult(
                probe, (uint)options.MasterCanIdRaw, (uint)options.SlaveCanIdRaw));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            connectionOk = false;
            items.Add(new BenchItem("A-1", BenchItemStatus.NotCollected,
                $"能力探针失败（{ex.GetType().Name}: {ex.Message}）——真机接不上或从机不支持"));
            items.Add(new BenchItem("A-2", BenchItemStatus.NotCollected, "依赖能力探针（未采）"));
            items.Add(new BenchItem("A-4", BenchItemStatus.NotCollected, "依赖能力探针（未采）"));
            items.Add(new BenchItem("A-5", BenchItemStatus.NotCollected, "依赖能力探针（未采）"));
            items.Add(new BenchItem("A-10", BenchItemStatus.NotCollected, "依赖能力探针（未采）"));
        }

        // A-3：抖动需要 DAQ 时钟同步——本批次 host 侧口径未覆盖，指向台架人工观测。
        items.Add(new BenchItem("A-3", BenchItemStatus.NotCollected,
            "DAQ 间隔/抖动：本批次无时钟同步口径，留台架人工观测"));

        // ---- A-11 位域粗口径（离线，探针同口径）----
        var bitfieldLike = contracts.All
            .Count(c => c.TotalByteLength is not (1 or 2 or 4 or 8));
        items.Add(new BenchItem("A-11", BenchItemStatus.Measured,
            "位域/聚合体统计（包侧粗口径：字节数 ∉ {1,2,4,8} 的对象数）",
            Facts: new Dictionary<string, string> { ["nonStandardByteLengthObjects"] = bitfieldLike.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            HumanVerdict: "子字节位域分布需台架手工统计"));

        // ---- C-2 静态跨段扫描（离线）----
        var scan = CrossSegmentScanner.Scan(document, contracts);
        items.Add(new BenchItem("C-2", BenchItemStatus.Measured,
            scan.HasCrossSegmentObjects
                ? $"存在 {scan.CrossSegmentObjects} 个跨段对象（写路径将拆段执行）"
                : "无跨段对象（写路径恒单段）",
            Facts: new Dictionary<string, string>
            {
                ["objectsScanned"] = scan.ObjectsScanned.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["crossSegmentObjects"] = scan.CrossSegmentObjects.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unmappedObjects"] = scan.UnmappedObjects.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        foreach (var finding in scan.Findings)
            restores.Add(new BenchRestoreRecord($"C2_scan_{finding.ObjectName}", true, finding.Detail));

        // ---- 写场景（spec D3：显式旗标才执行）----
        if (options.SafeStateConfirmed)
        {
            await RunWriteScenariosAsync(options, master, contracts, document, items, restores, ct);
        }
        else
        {
            items.Add(new BenchItem("B-1", BenchItemStatus.NotCollected, "缺 --i-have-verified-safe-state（只读批次）"));
            items.Add(new BenchItem("B-2", BenchItemStatus.NotCollected, "缺 --i-have-verified-safe-state（只读批次）"));
            items.Add(new BenchItem("B-4", BenchItemStatus.NotCollected, "缺 --i-have-verified-safe-state（只读批次）"));
            items.Add(new BenchItem("C-1", BenchItemStatus.NotCollected, "缺 --i-have-verified-safe-state（只读批次）"));
        }

        // ---- C-3 在线 MAP 上传计时（只读，无旗标也跑）----
        var mapName = options.MapName ?? FirstMapName(contracts, document);
        if (mapName is null)
        {
            items.Add(new BenchItem("C-3", BenchItemStatus.NotCollected, "A2L 中无可在线读的 MAP 对象"));
        }
        else
        {
            try
            {
                var timing = await MapUploadTimingScenario.RunAsync(master, contracts, mapName, ct: ct);
                items.Add(new BenchItem("C-3", BenchItemStatus.Measured, $"在线 MAP 上传：{timing.XCount}×{timing.YCount}",
                    Facts: new Dictionary<string, string>
                    {
                        ["xCount"] = timing.XCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["yCount"] = timing.YCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["elapsedMs"] = timing.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                    },
                    HumanVerdict: "与 DAQ 并发的行为需台架人工观察"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new BenchItem("C-3", BenchItemStatus.NotCollected,
                    $"在线 MAP 上传失败（{ex.GetType().Name}: {ex.Message}）"));
            }
        }

        // ---- B-3 = A-10 延伸（同一实测位图，结论共用）----
        items.Add(new BenchItem("B-3", BenchItemStatus.NotCollected,
            "块模式位图结论 = A-10（共用 GET_COMM_MODE_INFO 实测，不重复采）"));

        // ---- 报告落盘（D4）----
        var report = new BenchReport(
            GeneratedAtUtc: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            A2lName: System.IO.Path.GetFileName(options.A2LPath),
            Mode: options.SafeStateConfirmed ? BenchRunMode.Full : BenchRunMode.ReadOnly,
            Items: OrderItems(items),
            Restores: restores);

        var json = BenchReportJson.Serialize(report);
        var outputPath = options.OutputPath ?? System.IO.Path.Combine(
            "docs", "bench", $"bench-run-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.json");
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, json);

        return new XcpBenchResult(connectionOk ? 0 : 1, json, outputPath);
    }

    private static async Task RunWriteScenariosAsync(
        XcpBenchOptions options,
        XcpMaster master,
        ContractSet contracts,
        A2lDocument document,
        List<BenchItem> items,
        List<BenchRestoreRecord> restores,
        CancellationToken ct)
    {
        if (options.WriteObjectName is null || options.WriteTestValue is null)
        {
            items.Add(new BenchItem("B-1", BenchItemStatus.NotCollected,
                "写场景需要 --object 与 --value（安全态已确认但未指定写对象）"));
            items.Add(new BenchItem("B-2", BenchItemStatus.NotCollected, "依赖 --object/--value（未采）"));
            items.Add(new BenchItem("B-4", BenchItemStatus.NotCollected, "依赖 --object/--value（未采）"));
            items.Add(new BenchItem("C-1", BenchItemStatus.NotCollected, "依赖 --object/--value（未采）"));
            return;
        }

        if (!contracts.TryGet(options.WriteObjectName, out var contract))
        {
            items.Add(new BenchItem("B-1", BenchItemStatus.NotCollected,
                $"对象 '{options.WriteObjectName}' 不在 A2L 合同中（未采）"));
            items.Add(new BenchItem("B-2", BenchItemStatus.NotCollected, "对象缺失（未采）"));
            items.Add(new BenchItem("B-4", BenchItemStatus.NotCollected, "对象缺失（未采）"));
            items.Add(new BenchItem("C-1", BenchItemStatus.NotCollected, "对象缺失（未采）"));
            return;
        }

        var writer = new XcpCalibrationWriter(master);

        // ---- B-1/B-2：写前后读流不中断 + 写步耗时（in-limit 测试值）----
        var writeValue = options.WriteTestValue.Value;
        var interleaved = await WriteInterleavedScenario.RunAsync(
            writer, master, contract, document, writeValue, "B1_B2_write_during_acquisition",
            beats: 2, ct: ct);
        restores.Add(interleaved.Rmr.ToRestoreRecord());
        items.Add(new BenchItem("B-1", BenchItemStatus.Measured,
            interleaved.ReadsContinuedAfterWrite
                ? "写前后轮询读流不中断（host 侧事实；真机 DAQ 表行为需人工观察）"
                : "写后读流失败（归因见报告 restore 记录）",
            Facts: new Dictionary<string, string>
            {
                ["readsBefore"] = interleaved.ReadsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["readsBeforeFailed"] = interleaved.ReadsBeforeFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["readsAfter"] = interleaved.ReadsAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["readsAfterFailed"] = interleaved.ReadsAfterFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            HumanVerdict: "真机 Cal 写期间 DAQ 表行为：批次运行时人工观察"));

        items.Add(new BenchItem("B-2", BenchItemStatus.Measured,
            $"写步（保存+写+校验+还原）耗时 {interleaved.WriteElapsed.TotalMilliseconds:F1} ms",
            Facts: new Dictionary<string, string>
            {
                ["writeElapsedMs"] = interleaved.WriteElapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                ["writeVerified"] = interleaved.Rmr.WriteVerified.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant(),
                ["restored"] = interleaved.Rmr.Restored.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant(),
            }));

        // ---- B-4：越限值拒绝面行为（host 拒绝面是唯一防线——S5 附录 C-3）----
        var upper = contract.UpperLimit;
        var outOfLimitValue = (upper ?? 100d) + 1d;
        var b4 = await ReadModifyRestore.RunAsync(
            writer, master, contract, document, outOfLimitValue, "B4_out_of_limit", ct: ct);
        restores.Add(b4.ToRestoreRecord());
        items.Add(new BenchItem("B-4", BenchItemStatus.Measured,
            b4.WriteVerified
                ? $"host writer 接受越限值（UpperLimit={upper}）——拒绝面在 UI/导出层，writer 无限值检查（事实记录）"
                : $"writer 拒绝越限值（{b4.Detail}）",
            Facts: new Dictionary<string, string>
            {
                ["upperLimit"] = upper?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
                ["writeVerified"] = b4.WriteVerified.ToString(System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant(),
            },
            HumanVerdict: "越限写拒绝面应在哪一层强制——台架判定"));

        // ---- C-1：广播写（多元素对象）----
        var elementCount = contract.DataType is { } dt
            ? contract.TotalByteLength / ByteLayout.SizeOf(dt) : 1;
        if (elementCount > 1)
        {
            var c1 = await BroadcastWriteScenario.RunAsync(
                writer, master, contract, document, writeValue, "C1_broadcast", ct);
            restores.Add(c1.Rmr.ToRestoreRecord());
            items.Add(new BenchItem("C-1", BenchItemStatus.Measured,
                $"广播 {c1.ElementCount} 元素；写校验 {c1.Rmr.WriteVerified}；还原 {c1.Rmr.Restored}",
                Facts: new Dictionary<string, string> { ["elementCount"] = c1.ElementCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                HumanVerdict: "整对象同值覆盖是否符合期望——决定是否立项参数集数组值格式"));
        }
        else
        {
            items.Add(new BenchItem("C-1", BenchItemStatus.NotCollected,
                $"对象 '{contract.ObjectName}' 为单元素（C-1 需多元素对象，建议 --object 选 MAP/VAL_BLK）"));
        }
    }

    private static string? FirstMapName(ContractSet contracts, A2lDocument document) =>
        document.Modules.SelectMany(m => m.Characteristics)
            .FirstOrDefault(c => c.Type == "MAP" && contracts.TryGet(c.Name, out var mc) && mc.DataType is not null)
            ?.Name;

    private static IReadOnlyList<BenchItem> OrderItems(List<BenchItem> items) =>
        items.OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList();

    private static string NextArg(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value after {name}.");
        return args[++i];
    }
}
