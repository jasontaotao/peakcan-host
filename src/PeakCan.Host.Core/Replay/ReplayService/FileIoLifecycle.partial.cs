// ReplayService/FileIoLifecycle.partial.cs — W31 T1 (Flow A, 50 LoC)
// + v3.51.0 MINOR T3 extension-dispatch (`.blf` → BlfParser, default →
// AscParser). File-IO lifecycle methods: LoadAsync (file open +
// extension dispatch + AscParser/BlfParser.Parse + defensive-reset-on-entry
// + exception-wrapping) + Reset (state clear + timeline.Stop + frame
// buffer reset). Both touch _frames + _timeline + state-management.
// Sister of W22 RecordService/Lifecycle + W27
// RecentSessionsService/PersistenceOps + W28 DbcService/LoadLifecycle
// file-IO lifecycle sister-pattern.
//
// Cross-partial caller pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30 sister):
// LoadAsync reads ASC file + parses frames + delegates timeline.SetFrames;
// Reset clears state + delegates timeline.Stop + SetFrames. Both methods
// touch the same _frames + _timeline private fields.
//
// W31 T1 verbatim re-extracted via `git show main:src/.../ReplayService.cs | sed -n '152,182p;191,209p'`
// per W20 T2 R1 fabrication LESSON (37th application).

using PeakCan.Host.Core.Path;

namespace PeakCan.Host.Core.Replay;

public sealed partial class ReplayService
{
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        // v3.8.5 PATCH H1: defensive reset on entry. Clear `_frames` +
        // push to the timeline BEFORE the parse attempt so a failed
        // load (parse exception, file not found, IO error) leaves the
        // service in a clean "no file loaded" state rather than
        // silently retaining the prior file's frames. Defense-in-depth
        // alongside the v3.8.4 H2 `Reset()` call from OpenSessionAsync
        // -- this LoadAsync-level reset fires automatically without
        // caller cooperation.
        _frames = Array.Empty<ReplayFrame>();
        _timeline.SetFrames(_frames);

        try
        {
            await using var fs = File.OpenRead(PathNormalizer.Normalize(path));
            // v3.51.0 MINOR: dispatch by file extension. .blf → BlfParser,
            // default → AscParser (preserves v3.49.0 behavior for ASC).
            var options = ReplayOptions.Default;
            _frames = path.EndsWith(".blf", StringComparison.OrdinalIgnoreCase)
                ? await BlfParser.ParseAsync(fs, options, _logger, ct).ConfigureAwait(false)
                : await AscParser.ParseAsync(fs, options, _logger, ct).ConfigureAwait(false);
        }
        catch (ReplayException) { throw; }
        catch (FileNotFoundException ex)
        {
            throw new ReplayLoadException($"Replay file not found: {path}", ex);
        }
        catch (Exception ex)
        {
            throw new ReplayLoadException($"Failed to read replay file: {path}", ex);
        }
        _timeline.SetFrames(_frames);
    }

    /// <summary>
    /// v3.8.4 PATCH H2: drop the loaded frame buffer and reset the
    /// internal timeline. After <c>Reset</c>, <see cref="Frames"/> is
    /// empty and <see cref="TotalDuration"/> is 0.0; the service is in
    /// the same "no file loaded" state as a freshly-constructed instance
    /// (the timer is stopped by <c>_timeline.Stop()</c>).
    /// <para>
    /// Used by <c>ReplayViewModel.OpenSessionAsync</c> on the
    /// failure-teardown branch. Distinct from <see cref="Stop"/>, which
    /// only halts the timer (frames are preserved so a subsequent
    /// <c>Play()</c> can resume).
    /// </para>
    /// </summary>
    public void Reset()
    {
        _timeline.Stop();
        _frames = Array.Empty<ReplayFrame>();
        _timeline.SetFrames(_frames);
    }
}
