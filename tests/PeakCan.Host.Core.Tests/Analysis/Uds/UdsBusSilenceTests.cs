using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

/// <summary>
/// 背景流量的静默窗口。
/// <para>
/// <b>为什么必须有：</b>改前只报「2 帧，0.54–25.41 s」。这个写法让人以为该 ID
/// 整段都在活动，实际只有两个离散点，中间 24.9 秒完全静默。
/// 而「刷写进行中总线上的其它报文消失了」正是用户要看的现象——
/// 用首末时间概括等于把它抹掉。
/// </para>
/// </summary>
public sealed class UdsBusSilenceTests
{
    private static ReplayFrame Frame(double ts, uint id, params byte[] data)
        => new(ts, id, (byte)data.Length, data, FrameFlags.None,
            IsExtended: id > 0x7FF, Channel: 1, IsTransmit: null);

    private static readonly byte[] Payload = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22 };

    private static IEnumerable<ReplayFrame> Periodic(
        uint id, double start, double end, double periodSec)
    {
        for (var t = start; t < end; t += periodSec)
            yield return Frame(t, id, Payload);
    }

    private static UdsBusTrafficProfile Profile(params ReplayFrame[] frames)
    {
        var ordered = frames.OrderBy(f => f.Timestamp).ToArray();
        var addr = new UdsOfflineAddress(1, 0x7E0, 0x7E8, IsExtended: false);
        var classes = new UdsFrameClassifier().Classify(ordered, addr);
        return new UdsBusTrafficProfiler().Profile(ordered, classes);
    }

    // ── 单个 ID 的静默 ────────────────────────────────────────────────

    /// <summary>
    /// 一个 ID 中间断档，必须记成「最长静默」，而不是让首末区间掩盖它。
    /// </summary>
    [Fact]
    public void An_id_with_a_mid_log_gap_reports_its_max_silence()
    {
        // 0–1s 有报文，1–20s 静默，20–21s 又有报文。
        var frames = Periodic(0x200, 0.0, 1.0, 0.1)
            .Concat(Periodic(0x200, 20.0, 21.0, 0.1))
            .ToArray();

        var id = Profile(frames).Ids.Single();

        id.MaxSilenceSeconds.Should().BeApproximately(19.0, 0.3,
            "1s 到 20s 之间没有任何报文");
        id.Bursts.Should().HaveCount(2, "两段活动必须分开记，不能合成一个区间");
        id.Bursts[0].End.Should().BeLessThan(2.0);
        id.Bursts[1].Start.Should().BeGreaterThan(19.0);
    }

    /// <summary>
    /// 两帧相隔很远时，必须能看出是两个孤立点而不是一段连续活动。
    /// </summary>
    [Fact]
    public void Two_frames_far_apart_are_two_bursts_not_one_span()
    {
        var frames = new[]
        {
            Frame(0.54, 0x18F1D0F3, Payload),
            Frame(25.41, 0x18F1D0F3, Payload),
        };

        var id = Profile(frames).Ids.Single();

        id.Count.Should().Be(2);
        id.Bursts.Should().HaveCount(2);
        id.Bursts.Should().OnlyContain(b => b.Count == 1);
        id.MaxSilenceSeconds.Should().BeApproximately(24.87, 0.1);
    }

    /// <summary>连续发送的 ID 只有一个 burst，没有静默。</summary>
    [Fact]
    public void A_continuously_periodic_id_has_a_single_burst_and_no_silence()
    {
        var id = Profile(Periodic(0x200, 0.0, 5.0, 0.1).ToArray()).Ids.Single();

        id.Bursts.Should().ContainSingle();
        id.MaxSilenceSeconds.Should().Be(0, "帧之间没有断档");
    }

    // ── 总线级静默 ────────────────────────────────────────────────────

    /// <summary>
    /// <b>这是用户真正要看的：</b>刷写进行中，总线上所有其它 ECU 的报文都停了。
    /// 报告必须把这个窗口单独列出来，而不是散在各个 ID 的首末时间里。
    /// </summary>
    [Fact]
    public void Bus_wide_silence_is_reported_as_a_window()
    {
        // 0–2s 有背景流量，2–20s 全部消失（刷写占用总线），20–22s 恢复。
        var frames = Periodic(0x200, 0.0, 2.0, 0.1)
            .Concat(Periodic(0x201, 0.0, 2.0, 0.1))
            .Concat(Periodic(0x200, 20.0, 22.0, 0.1))
            .Concat(Periodic(0x201, 20.0, 22.0, 0.1))
            .ToArray();

        var profile = Profile(frames);

        profile.SilenceWindows.Should().ContainSingle();
        profile.SilenceWindows[0].Start.Should().BeApproximately(2.0, 0.2);
        profile.SilenceWindows[0].End.Should().BeApproximately(20.0, 0.2);
        profile.SilenceWindows[0].DurationSeconds.Should().BeApproximately(18.0, 0.4);
    }

    /// <summary>只有部分 ID 停发时，不算总线静默——仍有报文在发。</summary>
    [Fact]
    public void Partial_silence_is_not_bus_wide_silence()
    {
        var frames = Periodic(0x200, 0.0, 10.0, 0.1)          // 全程在发
            .Concat(Periodic(0x201, 0.0, 2.0, 0.1))           // 只在前 2 秒
            .ToArray();

        Profile(frames).SilenceWindows.Should().BeEmpty(
            "0x200 一直在发，总线没有静默");
    }

    /// <summary>
    /// 静默阈值从实际总线速率推导，不能写死。
    /// 总线本来就慢的时候，1 秒的空隙不算静默。
    /// </summary>
    [Fact]
    public void Silence_threshold_adapts_to_the_actual_bus_rate()
    {
        // 每 2 秒一帧：1.5 秒的间隔在这个速率下完全正常。
        var frames = new List<ReplayFrame>();
        for (var t = 0.0; t < 20.0; t += 2.0)
            frames.Add(Frame(t, 0x200, Payload));
        // 插一个 1.5 秒的空隙
        frames.Add(Frame(20.5, 0x200, Payload));

        var profile = Profile(frames.ToArray());

        profile.SilenceWindows.Should().BeEmpty(
            "总线本身是 2 秒一帧，1.5 秒的空隙不是静默");
    }

    /// <summary>总静默时长要能一眼看到，不能只给窗口列表。</summary>
    [Fact]
    public void Total_silence_duration_is_summed()
    {
        var frames = Periodic(0x200, 0.0, 1.0, 0.1)
            .Concat(Periodic(0x200, 5.0, 6.0, 0.1))
            .Concat(Periodic(0x200, 10.0, 11.0, 0.1))
            .ToArray();

        var profile = Profile(frames);

        profile.SilenceWindows.Should().HaveCount(2);
        profile.TotalSilenceSeconds.Should().BeApproximately(8.0, 0.5);
    }

    /// <summary>没有背景流量时不产出静默窗口（否则会报一个「全日志静默」的假窗口）。</summary>
    [Fact]
    public void No_background_traffic_means_no_silence_windows()
    {
        var profile = Profile(Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90));

        profile.SilenceWindows.Should().BeEmpty();
        profile.TotalSilenceSeconds.Should().Be(0);
    }
}
