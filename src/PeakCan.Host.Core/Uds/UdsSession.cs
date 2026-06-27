using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS diagnostic session state management. Tracks the current
/// session type and handles session transitions.
/// </summary>
public sealed partial class UdsSession : IDisposable
{
    private readonly object _lock = new();
    private readonly ILogger<UdsSession>? _logger;
    private Timer? _s3Timer;
    private UdsClient? _udsClient;

    /// <summary>
    /// v1.2.12 PATCH Item 9: number of S3 keepalive <c>TesterPresent</c>
    /// failures observed since the last <see cref="StopS3KeepAlive"/>.
    /// Updated via <see cref="Interlocked.Increment(ref long)"/> from the
    /// timer callback and read via <see cref="Interlocked.Read(ref long)"/>
    /// so all updates are race-free.
    /// </summary>
    private long _s3Failures;

    /// <summary>Current session type.</summary>
    public byte SessionType { get; private set; } = 0x01; // Default

    /// <summary>P2 timeout (ms) — time between request and response.</summary>
    public int P2Timeout { get; private set; } = 50;

    /// <summary>P2* timeout (ms) — time after NRC 0x78.</summary>
    public int P2StarTimeout { get; private set; } = 5000;

    /// <summary>True if in Default session.</summary>
    public bool IsDefault => SessionType == 0x01;

    /// <summary>True if in Extended session.</summary>
    public bool IsExtended => SessionType == 0x02;

    /// <summary>True if in Programming session.</summary>
    public bool IsProgramming => SessionType == 0x03;

    /// <summary>
    /// Number of S3 keepalive <c>TesterPresent</c> failures observed
    /// since the last <see cref="StopS3KeepAlive"/>. Returns 0 if
    /// the keepalive was never started.
    /// </summary>
    public long S3FailureCount => Interlocked.Read(ref _s3Failures);

    /// <summary>
    /// Create a new UdsSession without a logger (legacy callers).
    /// </summary>
    public UdsSession()
    {
    }

    /// <summary>
    /// Create a new UdsSession with an optional logger for S3 keepalive
    /// failure diagnostics (v1.2.12 PATCH Item 9).
    /// </summary>
    /// <param name="logger">
    /// Logger used to emit <c>Warning</c> events when an S3
    /// keepalive <c>TesterPresent</c> fails. May be <c>null</c>.
    /// </param>
    public UdsSession(ILogger<UdsSession>? logger)
    {
        _logger = logger;
    }

    /// <summary>Set session state after successful DiagnosticSessionControl.</summary>
    public void SetSession(byte sessionType, int p2, int p2Star)
    {
        lock (_lock)
        {
            SessionType = sessionType;
            P2Timeout = p2;
            P2StarTimeout = p2Star;
        }
    }

    /// <summary>Reset S3 timer after TesterPresent.</summary>
    public void ResetS3Timer()
    {
        _s3Timer?.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Start automatic S3 keep-alive (TesterPresent).</summary>
    public void StartS3KeepAlive(UdsClient client, TimeSpan? interval = null)
    {
        _udsClient = client;
        var effectiveInterval = interval ?? TimeSpan.FromSeconds(4); // S3 is 5s, send at 4s

        _s3Timer?.Dispose();
        _s3Timer = new Timer(async _ =>
        {
            try
            {
                await client.TesterPresentAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown path: do not log or count — the timer is being
                // disposed and the exception is expected when the host tears
                // down the session.
            }
            catch (Exception ex)
            {
                // v1.2.12 PATCH Item 9: previously swallowed silently. Now
                // increment the failure counter and emit a Warning so bus
                // drops and ECU-disconnect surface in the diagnostic logs.
                Interlocked.Increment(ref _s3Failures);
                if (_logger is not null)
                {
                    LogS3KeepAliveFailed(_logger, ex);
                }
            }
        }, null, effectiveInterval, effectiveInterval);
    }

    /// <summary>Stop automatic S3 keep-alive and reset the failure counter.</summary>
    public void StopS3KeepAlive()
    {
        _s3Timer?.Dispose();
        _s3Timer = null;
        // v1.2.12 PATCH Item 9: reset the failure counter on stop so a
        // subsequent StartS3KeepAlive observes a fresh window.
        Interlocked.Exchange(ref _s3Failures, 0);
    }

    public void Dispose()
    {
        _s3Timer?.Dispose();
    }

    // v1.2.12 PATCH Item 9: source-generated log helper. Source-gen
    // requires a non-null ILogger argument (it dereferences without a
    // null check), so the call site guards with `if (_logger is not null)`.
    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "UdsSession S3 keepalive TesterPresent failed")]
    private static partial void LogS3KeepAliveFailed(ILogger logger, Exception ex);
}
