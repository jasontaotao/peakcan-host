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

    // v1.6.6 PATCH Item 1: opt-in caps applied at LoadAsync entry (size,
    // pre-read) and inside DbcParser.Parse (message-count, mid-parse).
    // Back-compat: 1-arg ctor delegates with DbcOptions.Unlimited so all
    // existing callers and tests see no behavior change.
    private readonly DbcOptions _options;

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

    /// <summary>
    /// Back-compat constructor. Equivalent to passing
    /// <see cref="DbcOptions.Unlimited"/>; delegates to the 2-arg ctor so
    /// existing callers and tests see no behavior change.
    /// </summary>
    public DbcService(ILogger<DbcService> logger)
        : this(logger, DbcOptions.Unlimited)
    {
    }

    /// <summary>
    /// v1.6.6 PATCH Item 1: full-fidelity constructor with opt-in
    /// <see cref="DbcOptions"/>. Bound at DI registration from
    /// <c>appsettings.json:Dbc</c> section.
    /// <para>
    /// <c>internal</c> because <see cref="DbcOptions"/> is internal
    /// (no public API justification for exposing the limit knobs to
    /// downstream consumers — DI configuration binding is the only
    /// entry point). Visible to test project via
    /// <c>InternalsVisibleTo PeakCan.Host.App.Tests</c>.
    /// </para>
    /// </summary>
    internal DbcService(ILogger<DbcService> logger, DbcOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
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
            // v1.6.6 PATCH Item 1: read bytes first so the cap check uses the
            // actual byte count (closes the TOCTOU window between a pre-read
            // FileInfo.Length and a subsequent File.ReadAllBytesAsync).
            var bytes = await ReadDbcBytesAsync(path, ct).ConfigureAwait(false);

            // v1.6.6 PATCH Item 1: enforce file-size cap against the just-read
            // byte count. No TOCTOU window — `bytes.Length` is the bytes we
            // actually have in hand.
            if (_options.MaxFileSizeBytes > 0 && bytes.Length > _options.MaxFileSizeBytes)
            {
                var err = new Error(ErrorCode.ParseFailure,
                    $"file size {bytes.Length} bytes exceeds MaxFileSizeBytes {_options.MaxFileSizeBytes} at {path}");
                LogLoadSizeFailed(_logger, path, _options.MaxFileSizeBytes, bytes.Length);
                LoadFailed?.Invoke(err);
                return;
            }


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
            var text = ReadDbcText(bytes);
            // v1.6.6 PATCH Item 1: thread message-count cap into the parser.
            var r = await Task.Run(() => DbcParser.Parse(text, _options.MaxMessageCount, ct), ct).ConfigureAwait(false);
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
    /// <summary>
    /// v1.6.6 PATCH Item 1 (refactor): split off the bytes-read step from the
    /// decoding step so the post-read size cap at <c>LoadAsync</c> can use the
    /// actual byte count without an extra <c>FileInfo.Length</c> probe. Returns
    /// raw bytes; decoding happens in <see cref="ReadDbcText"/>.
    /// </summary>
    private static async Task<byte[]> ReadDbcBytesAsync(string path, CancellationToken ct)
    {
        // Read raw bytes — caller checks MaxFileSizeBytes against the returned
        // array's Length before decoding.
        return await File.ReadAllBytesAsync(PathNormalizer.Normalize(path), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// v1.6.6 PATCH Item 1 (refactor): pure decode — bytes in, string out.
    /// No file I/O here; <see cref="ReadDbcBytesAsync"/> handles the read and
    /// the size cap.
    /// </summary>
    private static string ReadDbcText(byte[] bytes)
    {

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

    // v1.6.6 PATCH Item 1: emitted when the file-size cap rejects the load.
    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC size cap rejected {Path} ({Size} bytes > MaxFileSizeBytes {Cap})")]
    private static partial void LogLoadSizeFailed(ILogger logger, string path, long cap, long size);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DBC IO failed for {Path}")]
    private static partial void LogLoadIoFailed(ILogger logger, string path, Exception ex);
}