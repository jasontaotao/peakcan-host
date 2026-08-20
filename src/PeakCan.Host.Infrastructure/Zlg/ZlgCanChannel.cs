using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG USBCAN FD 适配器，实现 <see cref="ICanChannel"/>。
/// 封装 zlgcan.dll 的 VCI_* API，每个实例对应一个 (deviceType, deviceIndex, canIndex) 通道。
/// <para>
/// 设备生命周期通过 <see cref="ZlgDeviceManager"/> 管理（引用计数），
/// 读循环通过 <see cref="IZlgReader"/> 抽象以支持测试。
/// </para>
/// </summary>
public sealed partial class ZlgCanChannel : ICanChannel
{
    // 读循环 backoff 策略（与 PEAK 通道一致）。
    private static readonly int[] ReadLoopBackoffMs = { 1, 10, 50 };
    internal const int MaxConsecutiveReadFailures = 100;

    private readonly uint _devType;
    private readonly uint _devIdx;
    private readonly uint _canIdx;
    private readonly ZlgDeviceManager _deviceManager;
    private readonly ILogger<ZlgCanChannel> _logger;
    private readonly IZlgReader _reader;

    // 连接状态门
    private readonly object _connectLock = new();
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private bool _connected;
    private bool _disconnecting;

    public ChannelId Id { get; }
    public bool IsConnected => _connected;

    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    public ZlgCanChannel(
        ChannelId id,
        ZlgDeviceManager deviceManager,
        ILogger<ZlgCanChannel>? logger = null,
        IZlgReader? reader = null)
    {
        Id = id;
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _logger = logger ?? NullLogger<ZlgCanChannel>.Instance;
        _reader = reader ?? new ZlgReader();

        // 从 ChannelId.Handle 解码 (devType, devIdx, canIdx)
        // 编码格式：高 1 位固定 1, 7 位 devType, 4 位 devIdx, 4 位 canIdx
        _devType = (uint)((id.Handle >> 8) & 0x7F);
        _devIdx = (uint)((id.Handle >> 4) & 0x0F);
        _canIdx = (uint)(id.Handle & 0x0F);
    }

    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_connectLock)
        {
            if (_connected)
                return Result<Unit>.Fail(ErrorCode.InvalidState, "Channel is already connected");
            if (_disconnecting)
                return Result<Unit>.Fail(ErrorCode.InvalidState, "Channel is disconnecting");
        }

        // 1. 打开设备（引用计数管理）
        var openRet = _deviceManager.AcquireDevice(_devType, _devIdx);
        if (openRet != ZlgError.Success)
        {
            var (code, msg) = ZlgErrorMapper.ToErrorCode(openRet);
            return Result<Unit>.Fail(code, $"OpenDevice failed: {msg}");
        }

        try
        {
            // 2. 初始化 CAN 通道（波特率配置）
            var initRet = InitCanChannel(baud, fd);
            if (initRet != ZlgError.Success)
            {
                _deviceManager.ReleaseDevice(_devType, _devIdx);
                var (code, msg) = ZlgErrorMapper.ToErrorCode(initRet);
                return Result<Unit>.Fail(code, $"InitCAN failed: {msg}");
            }

            // 3. 启动 CAN 通道
            var startRet = ZlgNative.ZCAN_StartCAN(_devType, _devIdx, _canIdx);
            if (startRet != ZlgError.Success)
            {
                ZlgNative.ZCAN_ResetCAN(_devType, _devIdx, _canIdx);
                _deviceManager.ReleaseDevice(_devType, _devIdx);
                var (code, msg) = ZlgErrorMapper.ToErrorCode(startRet);
                return Result<Unit>.Fail(code, $"StartCAN failed: {msg}");
            }

            // 4. 启动读循环
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var loop = Task.Run(() => ReadLoopAsync(token), ct);

            lock (_connectLock)
            {
                _cts = cts;
                _readLoop = loop;
                _connected = true;
            }

            return Result<Unit>.Ok(default);
        }
        catch (Exception ex)
        {
            _deviceManager.ReleaseDevice(_devType, _devIdx);
            return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, ex.Message);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_connectLock)
        {
            if (!_connected) return;
            _disconnecting = true;
            cts = _cts;
            loop = _readLoop;
            _connected = false;
            _cts = null;
            _readLoop = null;
        }

        try
        {
            // 取消读循环
            try { cts?.Cancel(); }
            catch { /* best-effort */ }

            if (loop is not null)
            {
                try { await loop.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }

            // 复位 CAN 通道
            try { ZlgNative.ZCAN_ResetCAN(_devType, _devIdx, _canIdx); }
            catch { /* best-effort */ }

            // 释放设备
            _deviceManager.ReleaseDevice(_devType, _devIdx);
        }
        finally
        {
            lock (_connectLock) { _disconnecting = false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    // 构建 ZLG 的 INIT_CONFIG 并调用 VCI_InitCAN。
    // FD 模式下额外调用 VCI_SetReference 配置数据段波特率。
    private uint InitCanChannel(BaudRate baud, bool fd)
    {
        // 从 BaudRate.Name 匹配经典波特率时序
        var timing = ResolveClassicTiming(baud.Name);
        if (timing is null)
            return ZlgError.Failed;

        var config = new ZlgInitConfig
        {
            AccCode = 0,
            AccMask = 0xFFFFFFFF,
            Filter = ZlgFilterMode.ReceiveAll,
            Timing0 = timing.Value.Timing0,
            Timing1 = timing.Value.Timing1,
            Mode = ZlgWorkMode.Normal,
        };

        var initRet = ZlgNative.ZCAN_InitCAN(_devType, _devIdx, _canIdx, ref config);
        if (initRet != ZlgError.Success)
            return initRet;

        // FD 模式：额外配置数据段波特率
        if (fd)
        {
            var dataRate = ResolveFdDataRate(baud.Name);
            if (dataRate.HasValue)
            {
                var rate = dataRate.Value;
                var ratePtr = MarshalEx.StructureToPtr(rate);
                try
                {
                    var setRet = ZlgNative.ZCAN_SetReference(_devType, _devIdx, _canIdx,
                        ZlgReferenceCode.CanFdDataRate, ratePtr);
                    if (setRet != ZlgError.Success)
                        _logger.LogWarning("VCI_SetReference (FD data rate) returned {Ret}", setRet);
                }
                finally
                {
                    MarshalEx.FreeHGlobal(ratePtr);
                }
            }
        }

        return ZlgError.Success;
    }

    // 经典波特率时序表。Name 与 PEAK 的 BaudRate 预设名对齐。
    private static (byte Timing0, byte Timing1)? ResolveClassicTiming(string name) => name switch
    {
        "1 Mbps" => (0x00, 0x14),
        "800 kbps" => (0x00, 0x16),
        "500 kbps" => (0x00, 0x1C),
        "250 kbps" => (0x01, 0x1C),
        "125 kbps" => (0x03, 0x1C),
        "100 kbps" => (0x04, 0x1C),
        "50 kbps" => (0x09, 0x1C),
        "20 kbps" => (0x18, 0x1C),
        "10 kbps" => (0x31, 0x1C),
        // FD 预设名（仲裁段沿用 1 Mbps）
        "1 Mbps (FD)" => (0x00, 0x14),
        "2 Mbps (FD)" => (0x00, 0x14),
        "5 Mbps (FD)" => (0x00, 0x14),
        _ => null,
    };

    // FD 数据段波特率，匹配 BaudRate.Name。
    private static uint? ResolveFdDataRate(string name) => name switch
    {
        "1 Mbps (FD)" => ZlgFdDataRate.Rate1Mbps,
        "2 Mbps (FD)" => ZlgFdDataRate.Rate2Mbps,
        "5 Mbps (FD)" => ZlgFdDataRate.Rate5Mbps,
        _ => null,
    };
}

/// <summary>Marshal 辅助（.NET 10 LibraryImport 不直接支持结构体 → IntPtr 的简单方式）。</summary>
internal static class MarshalEx
{
    public static IntPtr StructureToPtr<T>(T value) where T : unmanaged
    {
        var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<T>());
        System.Runtime.InteropServices.Marshal.StructureToPtr(value, ptr, false);
        return ptr;
    }

    public static void FreeHGlobal(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
    }
}