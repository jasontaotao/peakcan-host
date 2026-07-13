// DbcService/LoadLifecycle.partial.cs — W28 T1 (Flow A, LARGEST 85 LoC with xmldoc)
// Public LoadAsync virtual method: read DBC bytes → decode text →
// parse → mutate Current + raise DbcLoaded/LoadFailed event.
// Sister of W27 RecentSessionsService.LoadAsync which moved at W27 T1.
// LoadAsync 79 LoC LARGEST method moves here per W25 D5 + W26 + W27
// D5 deviation (4th confirmation of "largest method CAN move"
// pattern: file-IO + parsing lifecycle = sharp discrete flow).
//
// 4 [LoggerMessage] declarations (LogLoadSucceeded + LogLoadParseFailed
// + LogLoadSizeFailed + LogLoadIoFailed) stay on DbcService.cs per
// W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 sister
// precedent (CS8795 mitigation).
//
// Cross-partial helper calls: ReadDbcBytesAsync + ReadDbcText (now
// in TextDecoding.partial.cs per W28 T2) called via partial-class
// visibility.
//
// W23 STRUCT-FABRICATION LESSON: verify DbcParser.Parse(string, int,
// CancellationToken) 3-arg + Volatile.Read/Write<T>(ref T, T)
// signatures (verified during verbatim re-extraction from HEAD).
//
// W28 T1 verbatim re-extracted via `git show HEAD:src/.../DbcService.cs | sed -n '103,187p'`
// per W20 T2 R1 fabrication LESSON (30th application).

using System.IO;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.App.Services;

public partial class DbcService
{
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
                var err = new Error(ErrorCode.DbcFileTooLarge,
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
                // v3.15.0 MINOR: stamp the source path onto the document so
                // the Trace Viewer (and any other consumer) can display
                // which DBC is currently loaded. SetCurrentForTests leaves
                // SourcePath empty by design — tests that need a path can
                // stamp it manually.
                Current = r.Value with { SourcePath = path };
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
}
