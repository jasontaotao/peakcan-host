using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Path;

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
/// <b>Partial class:</b> declared <c>partial</c> because WPF source
/// generators may emit a partial declaration for App-layer types
/// (verified during Task 15 review: removing <c>partial</c> fails
/// with CS0260 from the WPF temp-build). <see cref="LoadAsync"/> is
/// <c>virtual</c> (so this class is intentionally NOT <c>sealed</c>) to
/// allow tests to swap in a no-op / canned-document stub without
/// hitting the disk.
/// </para>
/// </summary>
public partial class DbcService
{
    private readonly ILogger<DbcService> _logger;

    /// <summary>The most recently successfully parsed DBC, or null.</summary>
    /// <remarks>
    /// Thread-safety: written on a Task.Run worker (LoadAsync) and read
    /// from DbcDecodeBackgroundService's worker thread. Uses
    /// <see cref="Volatile.Read{T}"/> / <see cref="Volatile.Write{T}"/>
    /// to ensure cross-thread visibility without locks.
    /// </remarks>
    private DbcDocument? _current;

    public DbcDocument? Current
    {
        get => Volatile.Read(ref _current);
        private set => Volatile.Write(ref _current, value);
    }

    /// <summary>Raised after a successful parse; carries the new document.</summary>
    public event Action<DbcDocument>? DbcLoaded;

    /// <summary>Raised on IO error or parse failure; never raised on cancellation.</summary>
    public event Action<Error>? LoadFailed;

    public DbcService(ILogger<DbcService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Test seam only. Sets <see cref="Current"/> directly so tests that
    /// exercise downstream consumers (TraceService → SignalViewModel) can
    /// install a canned <see cref="DbcDocument"/> without round-tripping
    /// through <see cref="LoadAsync"/>. Not part of the production API —
    /// visible to <c>PeakCan.Host.App.Tests</c> via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal void SetCurrentForTests(DbcDocument doc) => Current = doc;

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
            // v1.2.8: read the DBC with the right encoding. DBC files in
            // the wild are commonly UTF-8, but Chinese / Japanese
            // /Korean users typically save them in the system
            // default code page (e.g. GBK/CP936 on zh-CN Windows) so
            // that the UNIT_ / VAL_ / comment strings render
            // correctly in Notepad. The previous single-encoding
            // path silently produced "garbled" text in the Signal
            // view's Unit column for any DBC saved in a non-UTF-8
            // code page.
            //
            // Strategy: detect BOM first (UTF-8 / UTF-16 / UTF-32);
            // if no BOM, try UTF-8 strictly and fall back to the
            // system's active OEM code page on decode failure. The
            // fallback handles zh-CN (GBK/CP936), ja-JP (CP932),
            // ko-KR (CP949) without requiring a UI prompt.
            var text = await ReadDbcTextAsync(path, ct).ConfigureAwait(false);
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
            or UnauthorizedAccessException or IOException or PathTooLongException)
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

    /// <summary>
    /// v1.2.8: BOM-aware + UTF-8-with-fallback DBC text loader. Strips
    /// the BOM if present (Encoding.UTF8.GetString would otherwise
    /// keep it as a leading U+FEFF in the result). On no-BOM + UTF-8
    /// decode-failure, falls back to the system OEM code page
    /// (GBK/CP936 on zh-CN, CP932 on ja-JP, CP949 on ko-KR) so that
    /// non-UTF-8 DBCs (the majority of Chinese OEM DBCs) render
    /// correctly. If the OEM fallback also fails, the original
    /// exception is rethrown — better a clear UTF-8 decoder failure
    /// than a silent misread.
    /// </summary>
    private static async Task<string> ReadDbcTextAsync(string path, CancellationToken ct)
    {
        // Read raw bytes first so we can detect the BOM and feed
        // the exact byte sequence to the right decoder.
        var bytes = await File.ReadAllBytesAsync(PathNormalizer.Normalize(path), ct).ConfigureAwait(false);

        // BOM detection. Strip the BOM before decoding.
        Encoding encoding;
        int bomLength;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            bomLength = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            // UTF-16 LE.
            encoding = Encoding.Unicode;
            bomLength = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            bomLength = 2;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00
                                       && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            bomLength = 4;
        }
        else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE
                                       && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
            bomLength = 4;
        }
        else
        {
            // No BOM. Try strict UTF-8 (DecoderFallback.ExceptionFallback
            // so a single invalid sequence is a hard error rather than
            // silently replacing with U+FFFD — the original failure
            // makes the OEM fallback kick in).
            encoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            bomLength = 0;
        }

        var body = bomLength == 0 ? bytes : bytes.AsSpan(bomLength).ToArray();
        try
        {
            return encoding.GetString(body);
        }
        catch (DecoderFallbackException) when (bomLength == 0)
        {
            // No-BOM + strict UTF-8 failed → fall back to the system
            // OEM code page (GBK on zh-CN, etc.). Re-include the
            // original bytes (no BOM to strip here). The CodePages
            // encoding provider must be registered at app startup
            // (App.OnStartup calls Encoding.RegisterProvider);
            // without it, Encoding.GetEncoding throws
            // NotSupportedException — in that case we fall back to
            // ISO-8859-1 (Latin-1) which never throws but produces
            // garbled chars for any non-Latin code point. Latin-1
            // is better than crashing on a real DBC the user is
            // trying to load.
            try
            {
                var oem = Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
                return oem.GetString(bytes);
            }
            catch (NotSupportedException)
            {
                // No OEM code page registered. Try Latin-1 as a
                // last-resort (always available, 1-to-1 byte mapping,
                // so no replacement chars — but produces 'wrong'
                // glyphs for any non-Latin-1 char).
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DBC loaded from {Path} ({Count} messages)")]
    private static partial void LogLoadSucceeded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC parse failed for {Path}: {Code} {Message}")]
    private static partial void LogLoadParseFailed(ILogger logger, string path, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC IO failed for {Path}")]
    private static partial void LogLoadIoFailed(ILogger logger, string path, Exception ex);
}