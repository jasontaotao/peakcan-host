using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

/// <summary>
/// spec §6 性能预算守卫：FD 64 字节、覆盖 512 格的快照计算中位数 ≤5ms。
/// 这是回归守卫，不是基准测试；阈值不收紧，避免 CI 噪声。
/// <para>
/// 把阈值临时改成 0.0 只能证明断言接了线；真正钉住“测的是 512 格快照”的是
/// 结构断言（512 格、8 段、每段 64 位在界内）。
/// </para>
/// </summary>
public class SignalBitLayoutPerformanceTests
{
    private static Message Fd512CellMessage()
    {
        var signals = new List<Signal>(8);
        for (var i = 0; i < 8; i++)
        {
            signals.Add(new Signal(
                $"S{i}", (ushort)(i * 64), 64, ByteOrder.LittleEndian,
                DbcValueType.Unsigned, 1, 0, 0, 0, "", Array.Empty<string>(),
                false, false, null));
        }

        return new Message(0x100, "Perf", 64, "ECU", signals, false, null);
    }

    [Fact]
    public void Compute_Fd512Cells_Median_Within_Five_Milliseconds()
    {
        var message = Fd512CellMessage();

        for (var i = 0; i < 10; i++) _ = SignalBitLayout.Compute(message);  // JIT warm-up

        var samples = new double[100];
        for (var i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = SignalBitLayout.Compute(message);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        var snapshot = SignalBitLayout.Compute(message);
        snapshot.Cells.Should().HaveCount(512);
        snapshot.Segments.Should().HaveCount(8);
        snapshot.Segments.Should().OnlyContain(s => s.InRangeBitCount == 64);

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        median.Should().BeLessThanOrEqualTo(5.0,
            "spec §6 快照预算：FD 512 格 ≤5ms（中位数）");
    }
}
