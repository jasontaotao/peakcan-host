using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Security.Cmac;
using PeakCan.Security.SecOc;

namespace PeakCan.Host.Infrastructure.Channel.SecOc;

/// <summary>
/// Per-PDU SecOC configuration. Key material comes from the local KeyStore
/// (spec §5-D4) — it must never travel through suite JSON.
/// </summary>
public sealed record SecOcPduConfig
{
    /// <summary>Wire-format profile (DataId, fvLen, macLen).</summary>
    public required SecOcProfile Profile { get; init; }

    /// <summary>AES-128 key (16 bytes). Copied defensively by the channel.</summary>
    public required byte[] Key { get; init; }

    /// <summary>Direction mode (spec §5-D6.4): verify | sign | both | bypass. Default both.</summary>
    public SecOcPduMode Mode { get; init; } = SecOcPduMode.Both;

    /// <summary>Initial TX freshness counter value.</summary>
    public uint InitialFv { get; init; }
}

/// <summary>Direction mode for one protected PDU (spec §5-D6.4).</summary>
public enum SecOcPduMode
{
    /// <summary>RX verification only.</summary>
    Verify,
    /// <summary>TX signing only.</summary>
    Sign,
    /// <summary>Sign TX and verify RX (default).</summary>
    Both,
    /// <summary>Explicitly unprotected: passthrough in both directions.</summary>
    Bypass,
}

public sealed record SecOcChannelOptions
{
    /// <summary>Protected PDUs keyed by raw CAN ID. Unlisted CAN IDs pass through unprotected.</summary>
    public required IReadOnlyDictionary<uint, SecOcPduConfig> ProtectedPdus { get; init; }
}

/// <summary>
/// Outermost ICanChannel decorator (spec §5-D1): TX signs before any inner
/// fault injection corrupts the frame; RX verifies after inner corruption.
/// Must be composed exactly once at the single assembly point — the ctor
/// rejects an inner channel that already carries the <see cref="ISecureChannel"/>
/// marker. Protected frames are forwarded unmodified in both directions
/// (Phase 2 has no SecurityBlock signal stripping, spec §5-D6.8); verdicts go
/// to the bypass table instead.
/// </summary>
public sealed partial class SecOcChannel : ICanChannel, ISecureChannel, IDisposable
{
    private const int AesKeyLength = 16;

    private readonly ICanChannel _inner;
    private readonly SecOcVerdictTable? _verdicts;
    private readonly SecOcStats? _stats;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, PduRuntime> _pdus;
    private long _rxSequence;

    private sealed class PduRuntime : IDisposable
    {
        public required SecOcPduConfig Config { get; init; }
        public required SecOcAuthenticator Authenticator { get; init; }

        /// <summary>Serializes TX sign+send per PDU: Sign mutates the FV counter
        /// and the signed frames must reach the wire in FV order.</summary>
        public readonly SemaphoreSlim TxGate = new(1, 1);

        /// <summary>Serializes RX verify per PDU: Verify mutates lastAcceptedFv.
        /// The delay-fault path dispatches from thread-pool threads (spec D1).>/summary>
        public readonly object VerifyGate = new();

        public void Dispose() => TxGate.Dispose();
    }

    public ChannelId Id => _inner.Id;
    public bool IsConnected => _inner.IsConnected;

    /// <summary>Inner channel for decorator-chain capability resolution (spec §5-D1).</summary>
    internal ICanChannel Inner => _inner;

    private Action<CanFrame>? _frameReceived;
    public event Action<CanFrame>? FrameReceived
    {
        add => _frameReceived += value;
        remove => _frameReceived -= value;
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add => _inner.ReadLoopError += value;
        remove => _inner.ReadLoopError -= value;
    }

    public SecOcChannel(ICanChannel inner, SecOcChannelOptions options,
        SecOcVerdictTable? verdictTable = null, SecOcStats? stats = null,
        ILogger? logger = null)
    {        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        // Idempotency guard (spec D1): composing twice would double-sign frames.
        if (inner is ISecureChannel)
            throw new InvalidOperationException(
                "Channel is already SecOc-wrapped; SecOcChannel must be composed exactly once at the assembly point.");
        _inner = inner;
        _verdicts = verdictTable;
        _stats = stats;
        _logger = logger ?? NullLogger.Instance;
        _pdus = options.ProtectedPdus.ToDictionary(
            kv => kv.Key,
            kv => BuildRuntime(kv.Value));
        _inner.FrameReceived += OnInnerFrameReceived;
    }

    private static PduRuntime BuildRuntime(SecOcPduConfig config)
    {
        ArgumentNullException.ThrowIfNull(config.Key);
        if (config.Key.Length != AesKeyLength)
            throw new ArgumentException($"SecOc key must be {AesKeyLength} bytes (AES-128), got {config.Key.Length}.");
        return new PduRuntime
        {
            Config = config with { Key = (byte[])config.Key.Clone() },
            Authenticator = new SecOcAuthenticator(config.Profile, config.Key,
                new BouncyCastleCmacProvider(), config.InitialFv),
        };
    }

    private bool ShouldSign(uint canId, out PduRuntime pdu)
    {
        if (_pdus.TryGetValue(canId, out var found) &&
            found.Config.Mode is SecOcPduMode.Sign or SecOcPduMode.Both)
        {
            pdu = found;
            return true;
        }
        pdu = null!;
        return false;
    }

    private bool ShouldVerify(uint canId, out PduRuntime pdu)
    {
        if (_pdus.TryGetValue(canId, out var found) &&
            found.Config.Mode is SecOcPduMode.Verify or SecOcPduMode.Both)
        {
            pdu = found;
            return true;
        }
        pdu = null!;
        return false;
    }

    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // TX order (spec D1): sign here (outermost) BEFORE inner fault injection.
        if (!ShouldSign(frame.Id.Raw, out var pdu))
            return await _inner.WriteAsync(frame, ct).ConfigureAwait(false);

        await pdu.TxGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = frame.Data.Span;
            var secured = new byte[pdu.Authenticator.FrameLength(data.Length)];
            var length = pdu.Authenticator.Sign(data, secured);
            return await _inner.WriteAsync(frame with { Data = secured.AsMemory(0, length) }, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            pdu.TxGate.Release();
        }
    }

    private void OnInnerFrameReceived(CanFrame frame)
    {
        // RX order (spec D1): inner (fault injectors) already corrupted the frame;
        // verify here at the outermost layer.
        if (ShouldVerify(frame.Id.Raw, out var pdu))
        {
            var seq = Interlocked.Increment(ref _rxSequence);
            VerifyResult result;
            lock (pdu.VerifyGate)
            {
                result = pdu.Authenticator.Verify(frame.Data.Span);
            }
            _stats?.Record(frame.Id.Raw, result);
            _verdicts?.Record(_inner.Id.Handle, seq, new SecOcVerdict(frame.Id.Raw, result.Accepted, result.Reason));
            if (result.Accepted)
                LogAccepted(_logger, frame.Id.Raw, result.FvUsed);
            else
                LogRejected(_logger, frame.Id.Raw, result.Reason?.ToString() ?? "unknown");
        }
        _frameReceived?.Invoke(frame);
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    private int _disposed; // 0=active, 1=disposed (CAS for idempotency)

    /// <summary>
    /// 同步释放入口（spec Rev9）：MS DI 的同步 <c>host.Dispose()</c> 走此路径，确保密钥
    /// 在宿主释放时确定性归零（此前只实现 IAsyncDisposable，同步释放可能不触发）。
    /// 释放 inner：IDisposable 直接调用，否则阻塞等待其 DisposeAsync（本仓库既有模式；
    /// inner 的 DisposeAsync 内部 ConfigureAwait(false)，不 marshal UI 上下文）。
    /// 幂等，且与 <see cref="DisposeAsync"/> 互斥（先到者生效）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inner.FrameReceived -= OnInnerFrameReceived;
        WipeKeyMaterial();
        if (_inner is IDisposable sync) sync.Dispose();
        else _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inner.FrameReceived -= OnInnerFrameReceived;
        WipeKeyMaterial();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 零化本层持有的两份密钥副本：<see cref="SecOcAuthenticator"/> 内部克隆 +
    /// <see cref="PduRuntime.Config"/><c>.Key</c>。调用方（KeyStore → <see cref="SecOcPduConfig"/>）
    /// 持有的源副本不由本层负责（spec D4 / Rev9）：block 路径由 <c>SecOcKeyMaterialZeroizer</c>
    /// 在 host 释放时归零，<c>--secoc-config</c> 路径在组装后立即归零。
    /// </summary>
    private void WipeKeyMaterial()
    {
        foreach (var pdu in _pdus.Values)
        {
            pdu.Authenticator.Wipe();
            CryptographicOperations.ZeroMemory(pdu.Config.Key);
            pdu.Dispose();
        }
    }

    /// <summary>测试探针：本层持有的 <see cref="PduRuntime.Config"/> 密钥副本是否已全部归零。</summary>
    internal bool IsKeyMaterialWiped => _pdus.Values.All(p => p.Config.Key.All(b => b == 0));

    [LoggerMessage(EventId = 6020, Level = LogLevel.Warning,
        Message = "SecOC RX rejected id=0x{CanId:X} reason={Reason}")]
    private static partial void LogRejected(ILogger logger, uint canId, string reason);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Debug,
        Message = "SecOC RX accepted id=0x{CanId:X} fv={Fv}")]
    private static partial void LogAccepted(ILogger logger, uint canId, uint fv);
}
