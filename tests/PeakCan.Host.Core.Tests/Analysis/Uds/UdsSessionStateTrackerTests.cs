using FluentAssertions;
using PeakCan.Host.Core.Analysis.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 会话时间线：这段日志里 ECU 换过几次会话，最后确认停在哪个。
/// <para>
/// 改前 <see cref="UdsSessionTimingTracker"/> 只从 <c>0x50</c> 响应里抠 P2/P2*，
/// <c>sessionType</c> 读完就扔——用户看不出「这条 0x2E 是在编程会话下发的，
/// 还是在已经退回默认会话之后发的」。
/// </para>
/// </summary>
public sealed class UdsSessionStateTrackerTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000),
            new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction Build(params UdsOfflinePdu[] pdus)
        => new UdsTransactionBuilder().Build(pdus).Transactions.Single();

    private static IReadOnlyList<UdsSessionInterval> Track(params UdsOfflineTransaction[] txs)
        => new UdsSessionStateTracker().Track(txs);

    private static UdsOfflineTransaction SessionSwitch(byte target, double ts, bool success = true)
        => success
            ? Build(
                Pdu(UdsDirectionRole.Request, ts, 0x10, target),
                Pdu(UdsDirectionRole.Response, ts + 0.01, 0x50, target, 0x00, 0x32, 0x01, 0xF4))
            : Build(Pdu(UdsDirectionRole.Request, ts, 0x10, target));

    [Fact]
    public void Log_starting_mid_session_is_marked_unknown_not_default()
    {
        // 日志从一条 0x22 开始——ECU 当时在什么会话完全无法从日志确定。
        // 默认成「默认会话」是在编造事实。
        var tx = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        var timeline = Track(tx);

        timeline.Should().NotBeEmpty();
        timeline[0].Observation.Should().Be(UdsSessionObservation.UnknownAtLogStart);
        timeline[0].SessionType.Should().BeNull("未知就是未知，不能填 0x01");
    }

    [Fact]
    public void Confirmed_session_switch_records_the_target_session()
    {
        var timeline = Track(SessionSwitch(0x03, 0.0));

        var last = timeline[^1];
        last.SessionType.Should().Be(0x03);
        last.Observation.Should().Be(UdsSessionObservation.Confirmed);
    }

    [Fact]
    public void Unconfirmed_switch_does_not_advance_the_session()
    {
        // 发了 0x10 03 但没收到响应：ECU 会话状态未知，不能假定已切到编程。
        var timeline = Track(SessionSwitch(0x03, 0.0, success: false));

        timeline[^1].Observation.Should().Be(UdsSessionObservation.RequestedNotConfirmed);
        timeline[^1].SessionType.Should().NotBe(0x03,
            "没有 0x50 正响应就不能宣称已进入编程会话");
    }

    [Fact]
    public void Sequence_programming_then_default_ends_at_default()
    {
        var timeline = Track(
            SessionSwitch(0x03, 0.0),
            SessionSwitch(0x01, 1.0));

        timeline.Should().HaveCountGreaterThanOrEqualTo(2);
        timeline[^1].SessionType.Should().Be(0x01);
        timeline[^1].Observation.Should().Be(UdsSessionObservation.Confirmed);
    }

    /// <summary>
    /// ECU Reset 之后会话可能被硬件复位改掉，不能沿用复位前的会话。
    /// </summary>
    [Fact]
    public void Ecu_reset_marks_session_as_maybe_changed()
    {
        var reset = Build(
            Pdu(UdsDirectionRole.Request, 1.0, 0x11, 0x01),
            Pdu(UdsDirectionRole.Response, 1.01, 0x51, 0x01));

        var timeline = Track(SessionSwitch(0x03, 0.0), reset);

        timeline[^1].Observation.Should().Be(UdsSessionObservation.ResetMayHaveChanged);
        timeline[^1].SessionType.Should().BeNull("复位后会话未知");
    }

    [Fact]
    public void Interval_start_evidence_points_at_the_switch_request()
    {
        var tx = SessionSwitch(0x03, 0.0);
        var timeline = Track(tx);

        var confirmed = timeline[^1];
        confirmed.StartEvidenceIndex.Should().Be(tx.Request.FirstEvidenceIndex);
        confirmed.StartTimestamp.Should().Be(tx.Request.FirstTimestamp);
    }

    [Fact]
    public void Empty_transaction_list_produces_empty_timeline()
    {
        Track().Should().BeEmpty();
    }

    /// <summary>
    /// 日志开头到首次会话切换之间那段「未知」区间必须有实际跨度。
    /// 零长区间会让 P1 的时间轴查询在 (日志开始, 首次切换) 内找不到任何区间。
    /// </summary>
    [Fact]
    public void Unknown_interval_ends_at_the_first_confirmed_switch()
    {
        var probe = Build(
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, 0xF1, 0x90),
            Pdu(UdsDirectionRole.Response, 0.01, 0x62, 0xF1, 0x90, 0x41));

        var timeline = Track(probe, SessionSwitch(0x03, 5.0));

        var unknown = timeline[0];
        unknown.Observation.Should().Be(UdsSessionObservation.UnknownAtLogStart);
        unknown.EndTimestamp.Should().Be(5.0, "未知区间应延伸到首次确认切换的时刻");
        unknown.EndTimestamp.Should().BeGreaterThan(unknown.StartTimestamp);
    }

    /// <summary>最后一段保持开放（End = null）是预期行为。</summary>
    [Fact]
    public void Last_interval_stays_open()
    {
        var timeline = Track(SessionSwitch(0x03, 0.0));

        timeline[^1].EndTimestamp.Should().BeNull("日志在切换之后结束时，末段没有终点");
    }
}
