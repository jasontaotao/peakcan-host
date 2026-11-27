using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 非诊断报文（「噪声」）的画像。
/// <para>
/// <b>为什么需要它：</b>原来的报告只给「Top-20 个 CAN ID × 帧数」。一份刷写日志里
/// 这 20 个 ID 全是其它 ECU 的周期广播——它们是<b>正常背景流量</b>，不是问题。
/// 列出来等于把真正需要看的东西（地址匹配但拼不成 PDU 的帧）淹没。
/// </para>
/// <para>
/// 正确的做法是先把「背景流量」和「问题帧」分开，再给背景流量一个有意义的
/// 概括：多少帧、多快、周期还是偶发。
/// </para>
/// </summary>
public sealed class UdsBusTrafficProfilerTests
{
    private static ReplayFrame Frame(double ts, uint id, params byte[] data)
        => new(ts, id, (byte)data.Length, data, FrameFlags.None,
            IsExtended: id > 0x7FF, Channel: 1, IsTransmit: null);

    /// <summary>在 [start, start+duration) 内按固定周期产生帧。</summary>
    private static IEnumerable<ReplayFrame> Periodic(
        uint id, double start, double duration, double periodSec)
    {
        for (var t = start; t < start + duration; t += periodSec)
            yield return Frame(t, id, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22);
    }

    private static UdsBusTrafficProfile Profile(params ReplayFrame[] frames)
    {
        var ordered = frames.OrderBy(f => f.Timestamp).ToArray();
        var addr = new UdsOfflineAddress(1, 0x7E0, 0x7E8, IsExtended: false);
        var classes = new UdsFrameClassifier().Classify(ordered, addr);
        return new UdsBusTrafficProfiler().Profile(ordered, classes);
    }

    /// <summary>
    /// 稳定周期的报文必须被认成 Periodic。这正是用户截图里那 20 个 ID 的性质。
    /// </summary>
    [Fact]
    public void Regularly_spaced_frames_are_classified_as_periodic()
    {
        var profile = Profile(Periodic(0x1881D0F3, 0.0, 5.0, 0.1).ToArray());

        var id = profile.Ids.Should().ContainSingle().Subject;
        id.Pattern.Should().Be(UdsBusTrafficPattern.Periodic);
        id.AverageIntervalMs.Should().BeApproximately(100, 5);
    }

    /// <summary>
    /// 间隔忽长忽短 = 事件触发报文。它比周期报文更值得看，
    /// 因为「只在某个时刻出现」往往和故障相关。
    /// </summary>
    [Fact]
    public void Irregular_intervals_are_classified_as_sporadic()
    {
        var frames = new[]
        {
            Frame(0.00, 0x123, 0xAA),
            Frame(0.05, 0x123, 0xAA),
            Frame(0.60, 0x123, 0xAA),
            Frame(0.62, 0x123, 0xAA),
            Frame(3.00, 0x123, 0xAA),
        };

        var id = Profile(frames).Ids.Should().ContainSingle().Subject;
        id.Pattern.Should().Be(UdsBusTrafficPattern.Sporadic);
    }

    [Fact]
    public void One_or_two_frames_are_single_shot()
    {
        var profile = Profile(Frame(0.0, 0x123, 0xAA), Frame(2.0, 0x123, 0xAA));

        profile.Ids.Single().Pattern.Should().Be(UdsBusTrafficPattern.SingleShot);
    }

    /// <summary>
    /// 一个偶尔漏帧的周期报文仍应判为周期——不能因为丢了一帧就变成「偶发」，
    /// 否则真实总线上的正常报文全会被标成可疑。
    /// </summary>
    [Fact]
    public void Periodic_traffic_with_a_dropped_frame_stays_periodic()
    {
        var frames = Periodic(0x200, 0.0, 2.0, 0.1).ToList();
        frames.RemoveAt(10); // 漏一帧

        var id = Profile(frames.ToArray()).Ids.Single();
        id.Pattern.Should().Be(UdsBusTrafficPattern.Periodic);
    }

    /// <summary>
    /// 汇总要给出「多快」这个量。用户看不出 20 个 ID × 70 帧意味着什么，
    /// 但「5 秒内平均 1400 帧/秒」一眼就知道总线很忙。
    /// </summary>
    [Fact]
    public void Profile_reports_frame_rate_and_window()
    {
        var frames = Periodic(0x200, 0.0, 5.0, 0.1)
            .Concat(Periodic(0x201, 0.0, 5.0, 0.1))
            .ToArray();

        var profile = Profile(frames);

        // 两个 ID 各 50 帧（5 秒 / 100 ms），共 100 帧，窗口 4.9 秒 → 约 20 帧/秒。
        profile.WindowSeconds.Should().BeApproximately(5.0, 0.2);
        profile.FramesPerSecond.Should().BeApproximately(20.4, 1.0);
        profile.TotalFrames.Should().Be(frames.Length);
    }

    /// <summary>
    /// 按性质分组计数：周期多少、偶发多少。UI 据此决定默认展开哪一组。
    /// </summary>
    [Fact]
    public void Profile_counts_each_pattern()
    {
        var frames = Periodic(0x200, 0.0, 5.0, 0.1)
            .Concat(new[] { Frame(0.5, 0x300, 0xAA), Frame(3.0, 0x300, 0xAA) })
            .ToArray();

        var profile = Profile(frames);

        profile.PeriodicCount.Should().Be(1);
        profile.SingleShotCount.Should().Be(1);
    }

    /// <summary>
    /// 诊断地址自身的帧不能算进「背景流量」——它们是被分析的主体。
    /// </summary>
    [Fact]
    public void Diagnostic_address_frames_are_excluded_from_background_traffic()
    {
        var frames = new[]
        {
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.1, 0x200, 0xAA, 0xBB),
        };

        var profile = Profile(frames);

        profile.Ids.Should().ContainSingle();
        profile.Ids[0].Id.Should().Be(0x200);
        profile.TotalFrames.Should().Be(1);
    }

    /// <summary>
    /// 29 位 ID 要标出来。用户截图里全是扩展帧，但界面没区分。
    /// </summary>
    [Fact]
    public void Extended_ids_are_flagged()
    {
        var profile = Profile(Periodic(0x1881D0F3, 0.0, 1.0, 0.1).ToArray());

        profile.Ids.Single().IsExtended.Should().BeTrue();
    }

    /// <summary>
    /// 每个 ID 的首末时间要保留，UI 才能显示「这个 ID 在 1.2–4.8 秒之间出现」。
    /// </summary>
    [Fact]
    public void Each_id_keeps_its_time_span()
    {
        var profile = Profile(Periodic(0x200, 1.0, 2.0, 0.1).ToArray());

        var id = profile.Ids.Single();
        id.FirstTimestamp.Should().BeApproximately(1.0, 0.01);
        id.LastTimestamp.Should().BeApproximately(2.9, 0.11);
    }
}
