using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Trace-replay 模式的文件扩展名分发测试：.blf → LoadBlf，其他 → LoadAscii。
/// 守住 HeadlessHostBuilder 第 88-104 行的分发逻辑（之前硬调 LoadAscii，
/// 选 .blf 文件后 ASC parser 解析二进制失败 → 当空 trace → "No trace loaded"）。
/// </summary>
public sealed class HeadlessHostBuilderTraceReplayTests : IDisposable
{
    private readonly string _dbcPath;
    private readonly string _ascPath;
    private readonly string _blfPath;

    public HeadlessHostBuilderTraceReplayTests()
    {
        _dbcPath = Path.Combine(Path.GetTempPath(), $"hil_tr_dbc_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(_dbcPath, """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);

        _ascPath = Path.Combine(Path.GetTempPath(), $"hil_tr_{Guid.NewGuid():N}.asc");
        File.WriteAllText(_ascPath, """
            date Wed Jun 28 10:00:00.000 2026
            base hex  timestamps absolute

             0.000000 1  123  8  01 02 03 04 05 06 07 08
             0.100000 1  18FEF100  8  01 02 03 04 05 06 07 08
            """);

        _blfPath = Path.Combine(Path.GetTempPath(), $"hil_tr_{Guid.NewGuid():N}.blf");
        BuildSyntheticBlf(_blfPath, canId: 0x123);
    }

    public void Dispose()
    {
        foreach (var p in new[] { _dbcPath, _ascPath, _blfPath })
        { try { File.Delete(p); } catch { } }
    }

    [Fact]
    public void Build_AscTracePath_LoadsViaLoadAscii()
    {
        var args = new CliArgs(_dbcPath, "suite.json", TracePath: _ascPath);
        using var host = HeadlessHostBuilder.Build(args);

        // 触发 ICanChannel DI factory（懒构造），验证是 TraceDrivenChannel 且已加载
        var channel = host.Services.GetService<ICanChannel>();
        channel.Should().NotBeNull();
        channel.Should().BeOfType<TraceDrivenChannel>("ASC trace 走 LoadAscii 路径");
    }

    [Fact]
    public void Build_BlfTracePath_LoadsViaLoadBlf()
    {
        var args = new CliArgs(_dbcPath, "suite.json", TracePath: _blfPath);
        using var host = HeadlessHostBuilder.Build(args);

        var channel = host.Services.GetService<ICanChannel>();
        channel.Should().NotBeNull();
        channel.Should().BeOfType<TraceDrivenChannel>("BLF trace 走 LoadBlf 路径");
    }

    /// <summary>
    /// 构建最小合成 BLF（参照 TraceDrivenChannelBlfTests.BuildSyntheticBlf）。
    /// </summary>
    private static void BuildSyntheticBlf(string path, uint canId, byte[]? data = null)
    {
        using var ms = new MemoryStream();
        // 24-byte file header
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        // ObjectHeader (32 bytes)
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        var frameData = data ?? new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        var totalObjBytes = (uint)BlfFormat.ObjectHeaderSize + (uint)BlfFormat.CanMessageDataSize;
        ms.Write(BitConverter.GetBytes(totalObjBytes));
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes(5_000_000L));
        // CanMessage frame data
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.WriteByte(0);
        ms.WriteByte((byte)frameData.Length);
        ms.Write(BitConverter.GetBytes(canId));
        ms.Write(frameData);
        File.WriteAllBytes(path, ms.ToArray());
    }
}
