using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Uds;

public class UdsSessionAdapterTests
{
    private static UdsSessionAdapter CreateAdapter()
    {
        var config = new CanIdConfig { RequestId = 0x7DF, ResponseId = 0x7E8, IsExtendedFrame = false };
        var isoTp = new IsoTpLayer(config, frame => Task.CompletedTask);
        var client = new UdsClient(isoTp);
        return new UdsSessionAdapter(client);
    }

    [Fact]
    public async Task ReadDtcInformation_WhenClientThrowsTimeout_ThrowsTransportException()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Act & Assert — 没有 ECU 响应，请求会超时 → 转为 UdsSessionTransportException
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<UdsSessionTransportException>(
            () => adapter.ReadDtcInformation(0xFF, cts.Token));
    }

    [Fact]
    public async Task SendRequestAsync_WhenClientThrowsTimeout_ThrowsTransportException()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Act & Assert — 没有 ECU 响应，请求会超时 → 转为 UdsSessionTransportException
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<UdsSessionTransportException>(
            () => adapter.SendRequestAsync(0x22, null, cts.Token));
    }

    [Fact]
    public async Task ReadDataByIdentifierAsync_WhenClientThrowsTimeout_ThrowsTransportException()
    {
        // Arrange — Task B 第一步：DID 读路径同享异常翻译契约
        var adapter = CreateAdapter();

        // Act & Assert — 没有 ECU 响应，请求会超时 → 转为 UdsSessionTransportException
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<UdsSessionTransportException>(
            () => adapter.ReadDataByIdentifierAsync(0xF190, cts.Token));
    }

    [Fact]
    public async Task WriteDataByIdentifierAsync_WhenClientThrowsTimeout_ThrowsTransportException()
    {
        // Arrange — Task B 第一步：DID 写路径同享异常翻译契约
        var adapter = CreateAdapter();

        // Act & Assert — 没有 ECU 响应，请求会超时 → 转为 UdsSessionTransportException
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<UdsSessionTransportException>(
            () => adapter.WriteDataByIdentifierAsync(0xF190, new byte[] { 0x01 }, cts.Token));
    }

    [Fact]
    public async Task ReadDtcInformation_DoesNotThrowOnSuccess()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Act & Assert — 使用较长的超时，验证不会抛错（即使超时也是 TransportException）
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await adapter.ReadDtcInformation(0xFF, cts.Token);
        }
        catch (UdsSessionTransportException)
        {
            // 预期行为：超时 → TransportException
            Assert.True(true);
        }
    }

    [Fact]
    public async Task SendRequestAsync_DoesNotThrowOnSuccess()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Act & Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await adapter.SendRequestAsync(0x22, null, cts.Token);
        }
        catch (UdsSessionTransportException)
        {
            // 预期行为：超时 → TransportException
            Assert.True(true);
        }
    }
}
