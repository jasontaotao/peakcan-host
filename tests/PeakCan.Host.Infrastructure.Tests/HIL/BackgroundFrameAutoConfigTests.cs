using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Phase B2: BackgroundFrameTimer 的 counter/checksum 自动预处理测试（plan 9 项）。
/// 经 BackgroundFrameSender + VirtualChannel 收帧，验证 OnTick 写出的 payload。
/// </summary>
[Collection(BackgroundFrameCollection.Name)]
public class BackgroundFrameAutoConfigTests
{
    private static readonly CanId Id = new(0x123, FrameFormat.Standard);
    private static readonly byte[] Zero8 = new byte[8];

    /// <summary>
    /// D3-R1: 转事件驱动 TCS——收满 minFrames 帧即完成，WaitAsync 作硬上限，
    /// 取代 Task.Delay(N) + Count 断言（10ms 周期在 Windows ~15ms tick，CI 上帧数波动致 flaky）。
    /// </summary>
    private static async Task<List<CanFrame>> SendFramesAsync(BackgroundFrame frame, int minFrames = 1)
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        var received = new List<CanFrame>();
        var receivedLock = new object();
        var targetTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += f =>
        {
            lock (receivedLock)
            {
                received.Add(f);
                if (received.Count >= minFrames)
                    targetTcs.TrySetResult(true);
            }
        };
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { frame });
        try
        {
            await targetTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            sender.Stop();
            sender.Dispose();
            await channel.DisposeAsync();
        }
        lock (receivedLock) return received.ToList();
    }

    [Fact]
    public async Task CounterIncrements_0To15ThenWrap()
    {
        var counter = new CounterConfig(StartBit: 0, Length: 4);   // MaxValue=15
        var received = await SendFramesAsync(new BackgroundFrame(Id, Zero8, 10, false, counter, null), minFrames: 16);

        received.Count.Should().BeGreaterThanOrEqualTo(16);
        for (int i = 0; i < 16; i++)
            (received[i].Data.Span[0] & 0x0F).Should().Be((byte)(i % 16), $"frame {i} counter");   // 首帧 StartValue=0
    }

    [Fact]
    public async Task CounterWrapsAtMax_ToStartValue()
    {
        var counter = new CounterConfig(StartBit: 0, Length: 4, StartValue: 0, MaxValue: 3);
        var received = await SendFramesAsync(new BackgroundFrame(Id, Zero8, 10, false, counter, null), minFrames: 8);

        received.Count.Should().BeGreaterThanOrEqualTo(8);
        byte[] expected = { 0, 1, 2, 3, 0, 1, 2, 3 };
        for (int i = 0; i < 8; i++)
            (received[i].Data.Span[0] & 0x0F).Should().Be(expected[i], $"frame {i} counter");
    }

    [Fact]
    public async Task CounterBigEndian_WritesHighNibble()
    {
        var counter = new CounterConfig(StartBit: 0, Length: 4, Order: ByteOrder.BigEndian);
        var received = await SendFramesAsync(new BackgroundFrame(Id, Zero8, 10, false, counter, null), minFrames: 4);

        // Motorola: counter=0..3 → data[0] = 0x00..0x30（高半字节，首帧 StartValue=0）
        for (int i = 0; i < 4; i++)
            received[i].Data.Span[0].Should().Be((byte)(i << 4), $"frame {i} big-endian counter");
    }

    [Fact]
    public async Task CounterArbitraryMax_NonPowerOfTwo()
    {
        // MaxValue=5（非 2^n-1）→ 显式比较回绕 1,2,3,4,5,0,...
        var counter = new CounterConfig(StartBit: 0, Length: 4, StartValue: 0, MaxValue: 5);
        var received = await SendFramesAsync(new BackgroundFrame(Id, Zero8, 10, false, counter, null), minFrames: 8);

        received.Count.Should().BeGreaterThanOrEqualTo(8);
        byte[] expected = { 0, 1, 2, 3, 4, 5, 0, 1 };
        for (int i = 0; i < 8; i++)
            (received[i].Data.Span[0] & 0x0F).Should().Be(expected[i], $"frame {i} counter");
    }

    [Fact]
    public async Task ChecksumXor_WritesXorOfCoveredBytes()
    {
        var checksum = new ChecksumConfig(StartBit: 56, Length: 8, Algorithm: ChecksumAlgorithm.Xor);
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x00 };
        var received = await SendFramesAsync(new BackgroundFrame(Id, data, 10, false, null, checksum), minFrames: 2);

        received.Should().NotBeEmpty();
        // 无 counter，每帧 payload 相同，取首帧断言即可（避免 received[^1] 随调度波动）
        var payload = received[0].Data.Span;
        byte expected = (byte)(payload[0] ^ payload[1] ^ payload[2] ^ payload[3] ^ payload[4] ^ payload[5] ^ payload[6]);
        payload[7].Should().Be(expected);
    }

    [Fact]
    public async Task ChecksumCrc8_WritesCrc8()
    {
        var checksum = new ChecksumConfig(StartBit: 56, Length: 8, Algorithm: ChecksumAlgorithm.Crc8);
        var data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x00 };
        var received = await SendFramesAsync(new BackgroundFrame(Id, data, 10, false, null, checksum), minFrames: 2);

        received.Should().NotBeEmpty();
        // 无 counter，每帧 payload 相同，取首帧断言即可
        var payload = received[0].Data.Span;
        // Compute 中 checksum 位(data[7])被清零后参与 CRC8 —— 复算需 8 字节（data[7]=0）
        var input = new byte[8];
        payload[..7].CopyTo(input);   // input[7] 保持 0（Compute 中 checksum 位清零后参与 CRC8）
        input[7] = 0x00;
        payload[7].Should().Be(Crc8(input, 0x07, 0xFF));
    }

    [Fact]
    public async Task CounterAndChecksumCoexist_ChecksumExcludesCounterBits()
    {
        // counter 递增后 checksum 重算，必须排除 counter 位（否则 data[7] 含 counter 值残留 → 非 0）
        var counter = new CounterConfig(StartBit: 0, Length: 4);
        var checksum = new ChecksumConfig(StartBit: 56, Length: 8, Algorithm: ChecksumAlgorithm.Xor);
        var received = await SendFramesAsync(new BackgroundFrame(Id, Zero8, 10, false, counter, checksum), minFrames: 2);

        received.Should().NotBeEmpty();
        // 含 counter：首帧 counter=0，minFrames≥2 保证末帧为非首帧（counter>0）
        var payload = received[^1].Data.Span;
        // data 全 0，counter 位清零后 XOR(data[0..6]) = 0 → checksum = 0x00
        payload[7].Should().Be(0x00);
        // counter 位应递增（非全 0）
        (payload[0] & 0x0F).Should().NotBe(0);
    }

    [Fact]
    public async Task NoAutoConfig_DataUnchanged_BackwardCompat()
    {
        var original = new byte[] { 0xAA, 0xBB };
        var received = await SendFramesAsync(new BackgroundFrame(Id, original, 10, false), minFrames: 1);

        received.Should().NotBeEmpty();
        received[0].Data.Span.ToArray().Should().Equal(original);
    }

    [Fact]
    public void ComputeChecksum_ExcludesCounterAndChecksumBits()
    {
        // 直接单测处理器：counter 位(bit0-3) 与 checksum 位(bit56-63) 必须在覆盖外清零
        var counter = new CounterConfig(StartBit: 0, Length: 4);
        var checksum = new ChecksumConfig(StartBit: 56, Length: 8, Algorithm: ChecksumAlgorithm.Xor);
        var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        var result = FrameAutoConfigProcessor.ApplyOneShot(data, counter, checksum);

        // data[0] 的 bit0-3（counter 位）清零 → 0xF0；其余 0xFF
        // XOR: 0xF0 ^ 0xFF^5 = 0xF0 ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF
        byte expected = (byte)(0xF0 ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF ^ 0xFF);
        result[7].Should().Be(expected);
    }

    private static byte Crc8(byte[] data, byte poly, byte init)
    {
        byte crc = init;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ poly) : (byte)(crc << 1);
        }
        return crc;
    }
}
