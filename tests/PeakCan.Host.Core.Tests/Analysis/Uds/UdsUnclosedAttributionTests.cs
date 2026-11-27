using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 未闭合请求的归因：<b>是 ECU 没回、测试仪没等，还是日志没记到？</b>
/// <para>
/// <b>这一组测试在 2026-11-27 被整体重写过。</b>旧版本的 fixture 只有「一条请求 +
/// 一个很大的 logEnd」，之后没有任何测试仪活动——那正是「测试仪停发」的形态，
/// 却被断言成「ECU 没回」。生产日志 <c>150635_log.asc</c> 因此被误判：
/// 测试仪在 19.671s 停发，日志记到 28.569s，分析器报「ECU 未在 P2* 内响应」。
/// </para>
/// <para>
/// 现在的判据：<b>只有「测试仪在请求之后仍继续发送」时，才能说 ECU 没回。</b>
/// 测试仪自己停下来的，一律归客户端。
/// </para>
/// </summary>
public sealed class UdsUnclosedAttributionTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus).Transactions[0];

    private static UdsFinding Generate(
        IReadOnlyList<UdsOfflineTransaction> txs, double logEnd, int? p2StarMs = 5000)
    {
        var timing = p2StarMs is { } p2 ? new UdsSessionTiming(50, p2, "ECU:0x50") : null;
        return new UdsFindingGenerator()
            .Generate(txs, logEnd, timing)
            .Findings.Single(f => f.Code == "UNCLOSED_REQUEST");
    }

    private static UdsFinding Generate(
        UdsOfflineTransaction tx, double logEnd, int? p2StarMs = 5000)
        => Generate(new[] { tx }, logEnd, p2StarMs);

    // ── 测试仪仍在工作时，才轮到 ECU ─────────────────────────────────

    /// <summary>
    /// <b>关键判据：</b>请求之后测试仪还在发别的请求，日志也继续记录，
    /// ECU 始终没回 → 这才是 ECU 的问题。
    /// </summary>
    [Fact]
    public void Ecu_is_blamed_only_when_the_tester_kept_working()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90)),          // 未闭合
            Build(Pdu(UdsDirectionRole.Request, 3.0, 0x22, 0xF1, 0x91),          // 测试仪继续工作
                  Pdu(UdsDirectionRole.Response, 3.01, 0x62, 0xF1, 0x91, 0x00)),
            Build(Pdu(UdsDirectionRole.Request, 5.0, 0x22, 0xF1, 0x92),
                  Pdu(UdsDirectionRole.Response, 5.01, 0x62, 0xF1, 0x92, 0x00)),
        };

        var finding = Generate(txs, logEnd: 30.0);

        finding.Suspect.Should().Be(UdsSuspect.EcuInternalState,
            "测试仪一直发到 5s，日志记到 30s，ECU 有的是机会回");
        finding.ObservedAction.Should().NotBe(UdsObservedAction.ClientStopped);
    }

    // ── 测试仪停发 → 客户端，不是 ECU ────────────────────────────────

    /// <summary>
    /// <b>用户生产日志的形态。</b>请求之后测试仪一帧都没再发，
    /// 日志末尾全是其它 ECU 的背景报文。改前这里判 ECU 故障，是错的。
    /// </summary>
    [Fact]
    public void Tester_stopping_after_the_request_is_a_client_issue_not_an_ecu_fault()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90));

        var finding = Generate(tx, logEnd: 30.0);

        finding.Suspect.Should().Be(UdsSuspect.ClientSequencing,
            "请求之后测试仪再没发过任何报文——ECU 没机会回");
        finding.ObservedAction.Should().Be(UdsObservedAction.ClientStopped);
        finding.Message.Should().Contain("之后测试仪未再发送任何报文");
        finding.Unknowns.Should().Contain(u => u.Contains("其它 ECU 的背景报文", StringComparison.Ordinal),
            "必须说清日志末尾那段与本请求无关，否则用户仍会以为 ECU 沉默了很久");
    }

    /// <summary>
    /// 文案里的时长必须是「测试仪停发之后」的时长，不能是「日志总时长」。
    /// </summary>
    [Fact]
    public void The_reported_duration_does_not_pretend_the_ecu_was_silent_for_the_whole_log()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90));

        var finding = Generate(tx, logEnd: 30.0);

        // 请求在 1.0s，日志到 30.0s：29 秒里测试仪一帧都没发。
        // 但结论不能是「ECU 沉默了 29 秒」——ECU 只被给了不到一帧的时间。
        finding.Message.Should().NotContain("ECU 未在 P2*");
        finding.Message.Should().Contain("之后测试仪未再发送任何报文");
    }

    // ── 测试仪没等就发了下一条 ──────────────────────────────────────

    /// <summary>
    /// 请求之后 <b>20 ms</b> 就发了别的服务（短于 P2=50ms）→ 测试仪主动放弃，
    /// ECU 根本来不及回。判据用 P2 而不是 P2*：P2 才是「首个响应」的时限。
    /// </summary>
    [Fact]
    public void A_tester_that_moves_on_without_waiting_owns_the_gap()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x3E, 0x00)),   // 没等到响应
            Build(Pdu(UdsDirectionRole.Request, 1.02, 0x22, 0xF1, 0x90),
                  Pdu(UdsDirectionRole.Response, 1.03, 0x62, 0xF1, 0x90, 0x00)),
        };

        var finding = Generate(txs, logEnd: 30.0);

        finding.Suspect.Should().Be(UdsSuspect.ClientSequencing);
        finding.ObservedAction.Should().Be(UdsObservedAction.ClientStopped);
        finding.Message.Should().Contain("短于 ECU 声明的 P2");
        finding.Severity.Should().Be(UdsFindingSeverity.Info, "测试仪主动不等，不该报成警告");
    }

    /// <summary>
    /// 测试仪等了 2 秒（远超 P2）才发下一条 → 它确实等过，ECU 没回才是问题。
    /// </summary>
    [Fact]
    public void A_tester_that_waited_past_P2_transfers_the_blame_to_the_ecu()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x3E, 0x00)),
            Build(Pdu(UdsDirectionRole.Request, 3.0, 0x22, 0xF1, 0x90),
                  Pdu(UdsDirectionRole.Response, 3.01, 0x62, 0xF1, 0x90, 0x00)),
        };

        var finding = Generate(txs, logEnd: 30.0);

        finding.Suspect.Should().Be(UdsSuspect.EcuInternalState,
            "测试仪等了 2 秒，远超 P2（50ms），ECU 明明有时间回");
    }

    // ── 抑制正响应的请求根本不该产生 finding ─────────────────────────

    /// <summary>
    /// <c>3E 80</c>：ECU 按协议不回。改前每次刷写 keepalive 都报一条「ECU 未响应」。
    /// </summary>
    [Fact]
    public void A_request_that_suppresses_its_response_never_produces_a_finding()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 1.0, 0x3E, 0x80));

        var result = new UdsFindingGenerator()
            .Generate(new[] { tx }, logEnd: 30.0, new UdsSessionTiming(50, 5000, "ECU:0x50"));

        result.Findings.Should().BeEmpty("3E 80 的 ECU 不回正响应是规范要求，不是故障");
        tx.FinalKind.Should().Be(UdsFinalKind.Suppressed);
    }

    // ── 总线上有东西，只是不能用 ────────────────────────────────────

    /// <summary>
    /// 收到过响应但回显不符：不能报「ECU 没响应」，那会让用户去查一台回过话的 ECU。
    /// </summary>
    [Fact]
    public void A_response_that_failed_echo_validation_is_not_reported_as_ecu_silence()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90),
                  Pdu(UdsDirectionRole.Response, 1.01, 0x62, 0xF1, 0x91, 0x00)), // DID 回显不符
            Build(Pdu(UdsDirectionRole.Request, 3.0, 0x22, 0xF1, 0x92),
                  Pdu(UdsDirectionRole.Response, 3.01, 0x62, 0xF1, 0x92, 0x00)),
        };

        var finding = Generate(txs, logEnd: 30.0);

        finding.ObservedAction.Should().Be(UdsObservedAction.RecordingInvalid);
        finding.Message.Should().Contain("未通过回显校验");
        finding.Message.Should().NotContain("ECU 未在 P2*");
    }

    /// <summary>
    /// 只收到 0x78：ECU 明确在说「还在处理」，不能报「未响应」。
    /// </summary>
    [Fact]
    public void A_pending_only_exchange_is_not_reported_as_ecu_silence()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 1.0, 0x31, 0x01, 0xFF, 0x00),
            Pdu(UdsDirectionRole.Response, 1.02, 0x7F, 0x31, 0x78),
            Pdu(UdsDirectionRole.Response, 2.02, 0x7F, 0x31, 0x78));

        var finding = Generate(tx, logEnd: 30.0);

        finding.ObservedAction.Should().Be(UdsObservedAction.EcuRespondedLate);
        finding.Message.Should().Contain("0x78");
        // 最后一个 0x78 在 2.02s，日志到 30s —— 远超 P2*(5s)，
        // 此时「延长记录时间」是错误建议，应升为 Warn 并指出 ECU 超窗。
        finding.Severity.Should().Be(UdsFindingSeverity.Warn);
        finding.Message.Should().Contain("超过 P2*");
    }

    /// <summary>
    /// <b>只收到 0x78、但日志在 P2\* 之内就结束</b>：ECU 还在处理，没有超窗。
    /// <para>
    /// 此时「直接动作」不能写「ECU 超过自己声明的响应窗口」——那是没有证据的事实。
    /// 同一条 finding 的 Message 说的是「日志结束前未见最终响应」+ 建议「延长记录时间」，
    /// 两行会直接矛盾：上一行说还在等，下一行说已超窗。
    /// </para>
    /// </summary>
    [Fact]
    public void Pending_inside_the_window_does_not_claim_the_ecu_exceeded_it()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 1.0, 0x31, 0x01, 0xFF, 0x00),
            Pdu(UdsDirectionRole.Response, 1.02, 0x7F, 0x31, 0x78));

        // 最后一个 0x78 在 1.02s，日志到 3.0s，P2*=5000ms → 未超窗。
        var finding = Generate(tx, logEnd: 3.0);

        finding.Severity.Should().Be(UdsFindingSeverity.Info);
        finding.ObservedAction.Should().NotBe(UdsObservedAction.EcuRespondedLate,
            "ECU 没有超过 P2*，不能说它响应超窗");
    }

    // ── 边界 ────────────────────────────────────────────────────────

    /// <summary>日志在 P2* 之内结束：ECU 可能还在处理。</summary>
    [Fact]
    public void Log_ending_within_the_timeout_stays_indeterminate()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 10.0, 0x22, 0xF1, 0x90));

        var finding = Generate(tx, logEnd: 11.0, p2StarMs: 5000);

        finding.Suspect.Should().Be(UdsSuspect.LogIntegrity);
        finding.Message.Should().Contain("P2*");
    }

    /// <summary>
    /// <b>P2* = 0 不能当成 0 秒窗口。</b>部分 ECU 用 0 表示「默认/不支持」，
    /// 直接拿它当阈值会让任何未闭合都判「ECU 有时间却没回」。
    /// </summary>
    [Fact]
    public void A_zero_P2Star_falls_back_to_the_conservative_window()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 10.0, 0x22, 0xF1, 0x90));

        var finding = Generate(tx, logEnd: 11.0, p2StarMs: 0);

        finding.Suspect.Should().Be(UdsSuspect.LogIntegrity,
            "P2*=0 说明 ECU 没给出可用值，回落到保守阈值后 1 秒仍不算「有时间却没回」");
    }

    /// <summary>
    /// 两条结论的文案必须能区分「测试仪停了」与「测试仪还在发但 ECU 没回」。
    /// </summary>
    [Fact]
    public void Tester_stopped_and_ecu_silent_read_differently()
    {
        var testerStopped = Generate(
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90)), logEnd: 30.0);

        var ecuSilent = Generate(new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90)),
            Build(Pdu(UdsDirectionRole.Request, 20.0, 0x22, 0xF1, 0x92),
                  Pdu(UdsDirectionRole.Response, 20.01, 0x62, 0xF1, 0x92, 0x00)),
        }, logEnd: 30.0);

        testerStopped.Message.Should().NotBe(ecuSilent.Message);
        testerStopped.Suspect.Should().NotBe(ecuSilent.Suspect);
    }

    /// <summary>
    /// 没有 ECU 时序时用保守默认 P2*，但归因仍要看测试仪活动。
    /// </summary>
    [Fact]
    public void Without_declared_timing_the_conservative_default_still_needs_tester_activity()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x22, 0xF1, 0x90)),
            Build(Pdu(UdsDirectionRole.Request, 20.0, 0x22, 0xF1, 0x92),
                  Pdu(UdsDirectionRole.Response, 20.01, 0x62, 0xF1, 0x92, 0x00)),
        };

        var finding = Generate(txs, logEnd: 30.0, p2StarMs: null);

        finding.Suspect.Should().Be(UdsSuspect.EcuInternalState);
    }

    // ── 刷写 ────────────────────────────────────────────────────────

    private static readonly UdsOfflineTransaction[] FlashFixture =
    {
        new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x03),
            Pdu(UdsDirectionRole.Response, 0.01, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4),
        }).Transactions[0],
        new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.1, 0x34, 0x00, 0x44),
            Pdu(UdsDirectionRole.Response, 0.11, 0x74, 0x20, 0x04, 0x02),
        }).Transactions[0],
        new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 0.2, 0x36, 0x01, 0xAA),
            Pdu(UdsDirectionRole.Response, 0.21, 0x76, 0x01),
        }).Transactions[0],
    };

    /// <summary>
    /// 刷写停在中途、测试仪之后还在发别的请求 → 客户端没发 0x37。
    /// </summary>
    [Fact]
    public void Flash_missing_step_while_the_tester_kept_sending_points_at_the_tester()
    {
        var txs = FlashFixture
            .Append(new UdsTransactionBuilder().Build(new[]
            {
                Pdu(UdsDirectionRole.Request, 5.0, 0x22, 0xF1, 0x90),
                Pdu(UdsDirectionRole.Response, 5.01, 0x62, 0xF1, 0x90, 0x00),
            }).Transactions[0])
            .ToArray();

        var result = new UdsFindingGenerator()
            .Generate(txs, logEnd: 60.0, new UdsSessionTiming(50, 5000, "ECU:0x50"));

        var flash = result.Findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        flash.Suspect.Should().Be(UdsSuspect.ClientSequencing,
            "测试仪在 5s 还在发别的请求，却没发 0x37");
        flash.ObservedAction.Should().Be(UdsObservedAction.ClientSkippedStep);
    }

    /// <summary>
    /// <b>「跳过步骤」与「请求重叠」是两件事，不能共用一个枚举值。</b>
    /// <para>
    /// 改前刷写分支借用了 <see cref="UdsObservedAction.ClientOverlapped"/>，而 UI 与导出
    /// 都把它渲染成「请求重叠 / 重复最终响应」。于是一条「测试仪没发 0x37」的结论
    /// 会显示成「直接动作：请求重叠」——日志里根本没有重叠，用户会去查一个不存在的现象。
    /// </para>
    /// </summary>
    [Fact]
    public void Skipping_a_flash_step_is_not_reported_as_overlapping_requests()
    {
        var txs = FlashFixture
            .Append(new UdsTransactionBuilder().Build(new[]
            {
                Pdu(UdsDirectionRole.Request, 5.0, 0x22, 0xF1, 0x90),
                Pdu(UdsDirectionRole.Response, 5.01, 0x62, 0xF1, 0x90, 0x00),
            }).Transactions[0])
            .ToArray();

        var flash = new UdsFindingGenerator()
            .Generate(txs, logEnd: 60.0, new UdsSessionTiming(50, 5000, "ECU:0x50"))
            .Findings.Single(f => f.Code == "FLASH_INCOMPLETE");

        flash.ObservedAction.Should().NotBe(UdsObservedAction.ClientOverlapped,
            "这条结论里没有任何请求重叠");
    }

    /// <summary>
    /// <b>刷写中途测试仪彻底停发</b>——用户生产日志的形态。
    /// 报出的时长必须是「测试仪停发之后」的，不是「日志总时长」。
    /// </summary>
    [Fact]
    public void Flash_stopped_by_the_tester_reports_the_real_idle_duration()
    {
        var result = new UdsFindingGenerator()
            .Generate(FlashFixture, logEnd: 60.0, new UdsSessionTiming(50, 5000, "ECU:0x50"));

        var flash = result.Findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        flash.Suspect.Should().Be(UdsSuspect.ClientSequencing);
        flash.ObservedAction.Should().Be(UdsObservedAction.ClientStopped);
        flash.Message.Should().Contain("t=0.200s", "最后一条测试仪请求在 0.2s（0x36）");
        flash.Message.Should().NotContain("又记录了 59.8", "不能把日志总时长当成测试仪空闲时长");
    }

    /// <summary>
    /// 刷写阶段失败但没有负响应（只是没回）：不能标成「ECU 拒绝了请求」。
    /// </summary>
    [Fact]
    public void A_flash_stage_without_any_response_is_not_reported_as_ecu_rejection()
    {
        var txs = new[]
        {
            FlashFixture[0],
            new UdsTransactionBuilder().Build(new[]
            {
                Pdu(UdsDirectionRole.Request, 0.1, 0x34, 0x00, 0x44),   // 无响应
            }).Transactions[0],
        };

        var result = new UdsFindingGenerator()
            .Generate(txs, logEnd: 60.0, new UdsSessionTiming(50, 5000, "ECU:0x50"));

        var flash = result.Findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        flash.ObservedAction.Should().NotBe(UdsObservedAction.EcuRejected,
            "没有负响应就不能说 ECU 拒绝");
    }

    /// <summary>
    /// <b>刷写时长必须基于最后一个传输块，不是第一个。</b>
    /// 生产日志有 115 个 TransferData 块（6.1s 到 19.6s）；改前用
    /// <c>UdsFlashStage.StartTimestamp</c>（首次出现）算「上一活跃步骤」，
    /// 于是报出 22.4s 的空闲时长，而真实值只有 8.9s——错了 2.5 倍。
    /// </summary>
    [Fact]
    public void Flash_idle_duration_is_measured_from_the_last_transfer_block()
    {
        var txs = new List<UdsOfflineTransaction>
        {
            FlashFixture[0],   // 0x10 03
            FlashFixture[1],   // 0x34
        };
        // 5 个传输块，从 1s 到 10s。
        for (var block = 1; block <= 5; block++)
        {
            var t = 1.0 + (block - 1) * 2.25;
            txs.Add(new UdsTransactionBuilder().Build(new[]
            {
                Pdu(UdsDirectionRole.Request, t, 0x36, (byte)block, 0xAA),
                Pdu(UdsDirectionRole.Response, t + 0.01, 0x76, (byte)block),
            }).Transactions[0]);
        }

        var result = new UdsFindingGenerator()
            .Generate(txs, logEnd: 20.0, new UdsSessionTiming(50, 5000, "ECU:0x50"));

        var flash = result.Findings.Single(f => f.Code == "FLASH_INCOMPLETE");
        // 最后一块在 t=10.0（末帧 10.01），日志到 20.0 → 空闲约 9.99s。
        // 若误用第一块（1.0s）会得到 19s。
        flash.Message.Should().Contain("t=10.000s", "空闲时长要从最后一块算，不是第一块");
        flash.Message.Should().NotContain("t=1.000s");
    }

    // ── 抑制请求仍会收到负响应（H1，2026-11-27 审查发现）─────────────

    /// <summary>
    /// <b>suppressPosRspMsgIndicationBit 只抑制正响应，负响应照发。</b>
    /// <c>31 81 FF00</c>（擦除 + 抑制）→ ECU 回 <c>7F 31 22</c>（条件不满足）：
    /// NRC 必须挂在该事务上。改前抑制请求直接进 closed、不进等待队列，
    /// 这个 NRC 会变成孤立响应——刷写阶段还会被误判成成功。
    /// </summary>
    [Fact]
    public void A_negative_response_still_closes_a_suppress_bit_request()
    {
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 1.0, 0x31, 0x81, 0xFF, 0x00),
            Pdu(UdsDirectionRole.Response, 1.01, 0x7F, 0x31, 0x22));

        tx.FinalKind.Should().Be(UdsFinalKind.Negative,
            "抑制位只管正响应；ECU 明确拒绝了这次请求");
        tx.FinalNrc.Should().Be(0x22);
    }

    /// <summary>
    /// 没有负响应时，抑制请求才是 Suppressed（正常抑制）。
    /// </summary>
    [Fact]
    public void A_suppress_bit_request_without_any_response_is_suppressed()
    {
        var tx = Build(Pdu(UdsDirectionRole.Request, 1.0, 0x31, 0x81, 0xFF, 0x00));

        tx.FinalKind.Should().Be(UdsFinalKind.Suppressed);
        tx.FinalNrc.Should().BeNull();
    }

    /// <summary>
    /// 抑制的会话切换必须开一个新区间——否则后续请求会被答成切换前的旧会话。
    /// </summary>
    [Fact]
    public void A_suppressed_session_switch_opens_a_new_interval()
    {
        var txs = new[]
        {
            Build(Pdu(UdsDirectionRole.Request, 0.0, 0x10, 0x01),
                  Pdu(UdsDirectionRole.Response, 0.01, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4)),
            Build(Pdu(UdsDirectionRole.Request, 1.0, 0x10, 0x83)),   // 编程会话 + 抑制
            Build(Pdu(UdsDirectionRole.Request, 2.0, 0x22, 0xF1, 0x90),
                  Pdu(UdsDirectionRole.Response, 2.01, 0x62, 0xF1, 0x90, 0x00)),
        };

        var timeline = new UdsSessionStateTracker().Track(txs);

        // 日志从成功的 0x10 01 开始，所以没有 UnknownAtLogStart 占位：
        // [确认的默认会话, 抑制的编程会话]。
        timeline.Should().HaveCount(2);
        var last = timeline[^1];
        last.SessionType.Should().Be(0x03, "10 83 请求的是 03 会话");
        last.Observation.Should().Be(UdsSessionObservation.RequestedResponseSuppressed);
    }

    /// <summary>
    /// <b>抑制请求不能吃掉后续同 SID 请求的响应。</b>
    /// <c>3E 80</c>（不期待正响应）→ <c>3E 00</c> → <c>7E 00</c>：
    /// 那条响应属于 <c>3E 00</c>。改前按 FirstOrDefault 取最旧的事务，
    /// 响应被 <c>3E 80</c> 吃掉，真正的请求反而「未闭合」。
    /// </summary>
    [Fact]
    public void A_suppress_bit_request_does_not_swallow_the_next_requests_response()
    {
        var build = new UdsTransactionBuilder().Build(new[]
        {
            Pdu(UdsDirectionRole.Request, 1.0, 0x3E, 0x80),
            Pdu(UdsDirectionRole.Request, 3.0, 0x3E, 0x00),
            Pdu(UdsDirectionRole.Response, 3.01, 0x7E, 0x00),
        });

        var suppress = build.Transactions.Single(t => t.Request.Payload[1] == 0x80);
        var normal = build.Transactions.Single(t => t.Request.Payload[1] == 0x00);

        suppress.FinalKind.Should().Be(UdsFinalKind.Suppressed);
        suppress.Responses.Should().BeEmpty("抑制请求不该拿到 3E 00 的响应");
        normal.FinalKind.Should().Be(UdsFinalKind.Positive);
        normal.Responses.Should().ContainSingle();
    }
}
