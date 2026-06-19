using System.IO;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services;

/// <summary>
/// DBC load + lookup. MVP contract: parse a single DBC file off the UI
/// thread, expose the resulting <see cref="DbcDocument"/> as a property
/// + event, surface parse / IO failures via the <see cref="LoadFailed"/>
/// event. Cancellation is silent (no <c>LoadFailed</c>).
/// <para>
/// <b>Threading:</b> <see cref="LoadAsync"/> runs the file read on the
/// async I/O pool and the parse on a worker thread via
/// <see cref="Task.Run(Action, CancellationToken)"/>; the event
/// handlers fire on whatever thread the worker is on, so subscribers
/// must marshal to the UI thread if they touch WPF bindings.
/// </para>
/// <para>
/// <b>Virtual:</b> <see cref="LoadAsync"/> is <c>virtual</c> so tests can
/// swap in a no-op / canned-document stub without hitting the disk.
/// </para>
/// </summary>
public partial class DbcService
{
    private readonly ILogger<DbcService> _logger;

    /// <summary>The most recently successfully parsed DBC, or null.</summary>
    public DbcDocument? Current { get; private set; }

    /// <summary>Raised after a successful parse; carries the new document.</summary>
    public event Action<DbcDocument>? DbcLoaded;

    /// <summary>Raised on IO error or parse failure; never raised on cancellation.</summary>
    public event Action<Error>? LoadFailed;

    public DbcService(ILogger<DbcService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Load and parse the DBC at <paramref name="path"/>. Updates
    /// <see cref="Current"/> and raises <see cref="DbcLoaded"/> on
    /// success; raises <see cref="LoadFailed"/> on IO / parse errors.
    /// Cancellation is silent.
    /// </summary>
    public virtual async Task LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var r = await Task.Run(() => DbcParser.Parse(text, ct), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (r.IsSuccess)
            {
                Current = r.Value;
                LogLoadSucceeded(_logger, path, Current!.Messages.Count);
                DbcLoaded?.Invoke(Current);
            }
            else
            {
                LogLoadParseFailed(_logger, path, r.Error!.Code, r.Error.Message);
                LoadFailed?.Invoke(r.Error!);
            }
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated abort — do not surface as a failure.
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                    or UnauthorizedAccessException or IOException
                                    or PathTooLongException)
        {
            var err = new Error(ErrorCode.IoError, ex.Message);
            LogLoadIoFailed(_logger, path, ex);
            LoadFailed?.Invoke(err);
        }
        catch (Exception ex)
        {
            // Last-resort safety net: parse errors above are surfaced via
            // DbcParser's Result envelope; anything else (out of memory,
            // security, etc.) becomes an IoError.
            var err = new Error(ErrorCode.IoError, ex.Message);
            LogLoadIoFailed(_logger, path, ex);
            LoadFailed?.Invoke(err);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC loaded from {Path} ({Count} messages)")]
    private static partial void LogLoadSucceeded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC parse failed for {Path}: {Code} {Message}")]
    private static partial void LogLoadParseFailed(ILogger logger, string path, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC IO failed for {Path}")]
    private static partial void LogLoadIoFailed(ILogger logger, string path, Exception ex);
}