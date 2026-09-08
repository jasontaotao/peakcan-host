using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed partial class SecOcChannel : ICanChannel, ISecureChannel
{
    private const int AesKeyLength = 16;

    private readonly ICanChannel _inner;
    private readonly SecOcVerdictTable? _verdicts;
    private readonly SecOcStats? _stats;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, PduRuntime> _pdus;
    private long _rxSequence;

    private sealed class PduRuntime
    {
        public required SecOcPduConfig Config { get; init; }
        public required SecOcAuthenticator Authenticator { get; init; }
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

        var data = frame.Data.Span;
        var secured = new byte[pdu.Authenticator.FrameLength(data.Length)];
        var length = pdu.Authenticator.Sign(data, secured);
        return await _inner.WriteAsync(frame with { Data = secured.AsMemory(0, length) }, ct)
            .ConfigureAwait(false);
    }

    private void OnInnerFrameReceived(CanFrame frame)
    {
        // RX order (spec D1): inner (fault injectors) already corrupted the frame;
        // verify here at the outermost layer.
        if (ShouldVerify(frame.Id.Raw, out var pdu))
        {
            var seq = Interlocked.Increment(ref _rxSequence);
            var result = pdu.Authenticator.Verify(frame.Data.Span);
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

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    [LoggerMessage(EventId = 6020, Level = LogLevel.Warning,
        Message = "SecOC RX rejected id=0x{CanId:X} reason={Reason}")]
    private static partial void LogRejected(ILogger logger, uint canId, string reason);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Debug,
        Message = "SecOC RX accepted id=0x{CanId:X} fv={Fv}")]
    private static partial void LogAccepted(ILogger logger, uint canId, uint fv);
}
