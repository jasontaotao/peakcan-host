namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS diagnostic session state management. Tracks the current
/// session type and handles session transitions.
/// </summary>
public sealed class UdsSession : IDisposable
{
    private readonly object _lock = new();
    private Timer? _s3Timer;
    private UdsClient? _udsClient;

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
            catch
            {
                // Ignore TesterPresent failures
            }
        }, null, effectiveInterval, effectiveInterval);
    }

    /// <summary>Stop automatic S3 keep-alive.</summary>
    public void StopS3KeepAlive()
    {
        _s3Timer?.Dispose();
        _s3Timer = null;
    }

    public void Dispose()
    {
        _s3Timer?.Dispose();
    }
}
