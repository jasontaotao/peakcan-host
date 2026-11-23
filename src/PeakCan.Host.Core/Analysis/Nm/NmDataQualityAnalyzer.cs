using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 数据体检（spec §9）。跑规则前先做一致性检查，结果放报告顶部。
/// <para>
/// <b>不做成规则原语</b>：体检需要原始帧与 DBC（<see cref="NmRuleContext"/> 没有），
/// 且必须在规则求值之前运行——做成规则需要引入规则间调度，v1 不做。
/// </para>
/// <para>
/// <b>三态而非「问题列表」</b>：「通过」与「没查」必须可区分。问题列表装不下这个语义，
/// 会把「无 DBC 所以没查」误读成「查了没问题」。
/// </para>
/// </summary>
public static class NmDataQualityAnalyzer
{
    /// <summary>
    /// <paramref name="frames"/> 必须是选择器过滤后的帧集（通道/窗口/RTR/错误帧），
    /// 不能传 <see cref="NmAnalysisRequest.Frames"/>：否则会把其它通道或窗口外的
    /// 帧 ID 报成未知，且时间区间会越出报告自身的窗口。
    /// <para>
    /// <paramref name="diagnostics"/> 是选择器对原始帧序的诊断：过滤后的帧集已按时间
    /// 排序，<c>non_monotonic</c> 只能从这里拿到「原始顺序是否回退」及其位置（R48）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmDataQualityCheck> Analyze(
        NmAnalysisRequest request,
        IReadOnlyList<ReplayFrame> frames,
        NmSelectionDiagnostics diagnostics)
    {
        var checks = new List<NmDataQualityCheck>(4)
        {
            CheckUnknownId(request, frames),
            CheckDlcMismatch(request, frames),
            CheckNonMonotonic(request, frames, diagnostics),
            CheckNidMapping(request, frames),
        };
        return checks;
    }

    /// <summary>
    /// spec §9.1：报文 ID 是否在 DBC / profile 声明中。
    /// <para>
    /// 已知 ID 有两个来源：DBC 的报文集合（应用报文也算已知），
    /// 与 profile 显式声明的 NM 报文。只查后者会把 DBC 里的全部应用报文
    /// 误判成「未知 ID」——正常 trace 每次都会报。两者都没有才报 NotChecked。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckUnknownId(
        NmAnalysisRequest request, IReadOnlyList<ReplayFrame> frames)
    {
        var declaredNm = request.Profile.Messages
            .Where(m => m.IsNm)
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();
        var dbcIds = request.Dbc?.MessagesById.Keys.ToHashSet();

        // spec §9.2：没有帧就没有可检查的对象——不能因为「循环没跑」而报通过。
        if (frames.Count == 0)
        {
            return NotChecked("unknown_id", "窗口内没有可检查的帧，无法判定帧 ID 是否合法");
        }

        if (declaredNm.Count == 0 && dbcIds is null)
        {
            return NotChecked("unknown_id", "既无 DBC 也无 profile 报文声明，无法判定帧 ID 是否合法");
        }

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var times = new List<double>();
        foreach (var frame in frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            // DBC 侧按数值比对 Id：MessagesById 的键是解析器剥掉 IDE 位（bit31）后的 29 位原始 ID
            // （ParseDocumentFlow.cs 的 `m.Id & 0x7FFFFFFF`），与 ReplayFrame.Id 同域。
            // 注意耦合：Message.Id 本身是带 bit31 的；若解析器改为保留该位，
            // 这里会静默漏判全部扩展帧。标准帧与扩展帧同数值时也会互相掩护。
            var knownInDbc = dbcIds is not null && dbcIds.Contains(frame.Id);
            var knownInProfile = declaredNm.Contains((hex, frame.IsExtended));
            if (!knownInDbc && !knownInProfile)
            {
                unknown.Add(hex);
                times.Add(frame.Timestamp);
            }
        }

        if (unknown.Count == 0)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.Passed,
                $"全部 {frames.Count} 帧的 ID 均在已知范围内",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        return new NmDataQualityCheck(
            "unknown_id", NmDataQualityStatus.Failed,
            $"{times.Count} 帧的 CAN ID 不在 DBC 或 profile 声明中：{string.Join(", ", unknown)}",
            [new NmTimeRange(times.Min(), times.Max())],
            unknown.ToArray());
    }

    /// <summary>
    /// spec §9.1：实测 DLC 与声明是否一致。检测口径复用 M1 的 <c>NM_DLC_MISMATCH</c>，
    /// 但声明来源升级为 DBC 优先（R45）。
    /// <para>
    /// 不能只看 profile 的 NM 声明：<see cref="NmProfile.Messages"/> 只含 NM 报文，
    /// 应用报文会被整体跳过，检查会在几乎没验证任何帧的情况下报 Passed。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckDlcMismatch(
        NmAnalysisRequest request, IReadOnlyList<ReplayFrame> frames)
    {
        // spec §9.2：没有帧就没有可检查的对象——不能因为「循环没跑」而报通过。
        if (frames.Count == 0)
        {
            return NotChecked("dlc_mismatch", "窗口内没有可检查的帧，无法比对 DLC");
        }

        var dbcMessages = request.Dbc?.MessagesById;
        var declaredNm = new Dictionary<(string CanIdHex, bool IsExtended), byte>();
        foreach (var message in request.Profile.Messages)
        {
            // 重复定义时按 NmAnalyzer 的口径保留第一条，不静默让后者覆盖前者。
            if (message.IsNm)
            {
                declaredNm.TryAdd((message.CanIdHex, message.IsExtended), message.Dlc);
            }
        }

        if ((dbcMessages is null || dbcMessages.Count == 0) && declaredNm.Count == 0)
        {
            return NotChecked("dlc_mismatch", "既无 DBC 也无 profile 报文声明，无法比对 DLC");
        }

        var wrongTimes = new List<double>();
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var compared = 0;
        foreach (var frame in frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);

            // 声明优先级：DBC（任意报文）> profile 的 NM 声明 > 无声明。
            byte? declared = null;
            if (dbcMessages is not null && dbcMessages.TryGetValue(frame.Id, out var dbcMessage))
            {
                declared = dbcMessage.Dlc;
            }
            else if (declaredNm.TryGetValue((hex, frame.IsExtended), out var profileDlc))
            {
                declared = profileDlc;
            }

            // 没有声明 DLC 的 ID 属于 unknown_id 的判定范围，这里不臆造期望值。
            if (declared is not { } dlc) continue;

            compared++;
            if (frame.Dlc != dlc)
            {
                wrongTimes.Add(frame.Timestamp);
                ids.Add(hex);
            }
        }

        // spec §9.2：一帧都没比对就报 Passed，与空帧集报通过是同一类假通过。
        if (compared == 0)
        {
            return NotChecked(
                "dlc_mismatch", "帧 ID 均未在 DBC 或 profile 中声明 DLC，没有可比对的帧");
        }

        return wrongTimes.Count == 0
            ? new NmDataQualityCheck(
                "dlc_mismatch", NmDataQualityStatus.Passed,
                "实测 DLC 与声明一致",
                Array.Empty<NmTimeRange>(), Array.Empty<string>())
            : new NmDataQualityCheck(
                "dlc_mismatch", NmDataQualityStatus.Failed,
                $"{wrongTimes.Count} 帧实测 DLC 与声明不一致",
                [new NmTimeRange(wrongTimes.Min(), wrongTimes.Max())], ids.ToArray());
    }

    /// <summary>
    /// spec §9.1：时间戳必须单调。非单调要报受影响区间，供上层剔除。
    /// <para>
    /// 回退只能在 <see cref="NmFrameSelector"/> 过滤原始帧序时识别：传给本分析的
    /// 帧集已按时间排序，再比较就永远单调。这里消费选择器的诊断（R48）——裸
    /// <c>bool</c> 不带时间，故诊断补了第一处回退的位置，区间取回退点到窗口末端。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckNonMonotonic(
        NmAnalysisRequest request,
        IReadOnlyList<ReplayFrame> frames,
        NmSelectionDiagnostics diagnostics)
    {
        // spec §9.2：没有帧就没有可检查的对象——不能因为「循环没跑」而报通过。
        if (frames.Count == 0)
        {
            return NotChecked("non_monotonic", "窗口内没有可检查的帧，无法判定时间戳单调性");
        }

        // 两个信号必须一致：selector 同时设置它们。若只设了 bool 而没有时间戳，
        // 说明有人改了 selector 却漏了这里——宁可说「没查成」，也不能谎报通过。
        if (diagnostics.TimestampRegression && diagnostics.TimestampRegressionAt is null)
        {
            return NotChecked("non_monotonic",
                "检测到时间戳回退，但无法定位回退位置，单调性结论不可用");
        }

        if (diagnostics.TimestampRegressionAt is not { } regressionAt)
        {
            return new NmDataQualityCheck(
                "non_monotonic", NmDataQualityStatus.Passed,
                "原始帧序时间戳单调",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        return new NmDataQualityCheck(
            "non_monotonic", NmDataQualityStatus.Failed,
            $"原始帧序在 {regressionAt.ToString("F3", CultureInfo.InvariantCulture)}s 处时间戳回退，此后区间结论不可靠",
            [new NmTimeRange(regressionAt, request.End)], Array.Empty<string>());
    }

    /// <summary>
    /// spec §9.1：NID 是否与 CAN ID 映射一致。
    /// 只有 profile 能解析出生效 <c>nid_byte</c> 时才可判——否则 NotChecked，不能记成通过。
    /// <para>
    /// 判据是 <c>nid_byte</c> 本身而非 <see cref="NmProfile.StandardRules"/>（R46）：
    /// 声明了 layout 但没声明规则的 profile 仍然可判 NID。
    /// </para>
    /// <para>
    /// <b>只校验 profile 声明为 NM 的报文</b>：应用报文的 byte 0 没有 NID 语义，
    /// 把它们也按 NM 布局解释会在任何带应用流量的真实 trace 上恒报 Failed。
    /// 口径与规则引擎一致——<c>NmAnalyzer</c> 交给 <c>NmRuleContext.NmFrames</c> 的
    /// 也只包含 profile 声明为 NM 的报文，两条路径不能对 NID 给出相反结论。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckNidMapping(
        NmAnalysisRequest request, IReadOnlyList<ReplayFrame> frames)
    {
        // spec §9.2：没有帧就没有可检查的对象——不能因为「循环没跑」而报通过。
        if (frames.Count == 0)
        {
            return NotChecked("nid_mapping", "窗口内没有可检查的帧，无法校验 NID 映射");
        }

        if (NmProfilePrecedence.EffectiveNidByte(request.Profile) is not { } nidByte)
        {
            return NotChecked(
                "nid_mapping", "profile 未声明 layout.nid_byte，无法校验 NID 映射");
        }

        // NID 语义只属于 profile 声明的 NM 报文。没有声明就没有可校验的对象。
        var nmKeys = request.Profile.Messages
            .Where(m => m.IsNm)
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();
        if (nmKeys.Count == 0)
        {
            return NotChecked("nid_mapping",
                "profile 未声明 NM 报文，无法校验 NID 映射");
        }

        // spec §14.4：扩展帧 NID == CAN ID 低字节；标准帧 NID == ID - 0x400。
        var wrongTimes = new List<double>();
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var compared = 0;
        foreach (var frame in frames)
        {
            // 应用报文（未在 profile 中声明为 NM）不参与 NID 映射判定。
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            if (!nmKeys.Contains((hex, frame.IsExtended))) continue;

            var data = frame.Data;
            if (data.Length <= nidByte) continue;

            // 标准帧 ID < 0x400 时 (Id - 0x400) 下溢，会算出一个貌似合理的期望 NID；
            // 这类帧不得臆造期望值。
            if (!frame.IsExtended && frame.Id < 0x400) continue;

            compared++;
            var expected = frame.IsExtended
                ? (byte)(frame.Id & 0xFF)
                : (byte)((frame.Id - 0x400) & 0xFF);
            if (data[nidByte] != expected)
            {
                wrongTimes.Add(frame.Timestamp);
                ids.Add(hex);
            }
        }

        // spec §9.2：一帧都没比对就报 Passed，与空帧集报通过是同一类假通过。
        if (compared == 0)
        {
            return NotChecked(
                "nid_mapping",
                "窗口内没有可判定的 NM 帧（profile 声明的 NM 报文未出现，或 DLC 不足/标准帧 CAN ID < 0x400）");
        }

        return wrongTimes.Count == 0
            ? new NmDataQualityCheck(
                "nid_mapping", NmDataQualityStatus.Passed,
                "NID 与 CAN ID 映射一致（仅校验 profile 声明的 NM 报文，按 layout.nid_byte 解释）",
                Array.Empty<NmTimeRange>(), Array.Empty<string>())
            : new NmDataQualityCheck(
                "nid_mapping", NmDataQualityStatus.Failed,
                $"{wrongTimes.Count} 帧 NID 与 CAN ID 映射不一致（仅校验 profile 声明的 NM 报文，按 layout.nid_byte 解释）",
                [new NmTimeRange(wrongTimes.Min(), wrongTimes.Max())], ids.ToArray());
    }

    private static NmDataQualityCheck NotChecked(string code, string detail)
        => new(code, NmDataQualityStatus.NotChecked, detail,
            Array.Empty<NmTimeRange>(), Array.Empty<string>());
}
