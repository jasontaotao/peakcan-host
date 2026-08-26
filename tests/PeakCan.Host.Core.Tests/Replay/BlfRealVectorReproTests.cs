// v3.51.0 T6 PATCH: repro test for the real-Vector-BLF-load failure
// the user reported. Loads C:\Users\13777\Desktop\CH0_242下坡掉READY0.blf
// (real Vector CANalyzer 743KB trace) and asserts frames are parsed.
// This test is `[Trait("Manual", "true")]` so it doesn't run in CI
// automatically — only when the user-provided fixture exists locally.
//
// IMPORTANT: this test reads the file at run-time via path. The file
// path is NOT committed to the repository (per
// peakcan-host-test-fixtures MEMORY note). Only the test scaffolding is
// in git; the file itself stays on the developer's Desktop.

using FluentAssertions;
using PeakCan.HIL.Core.Replay;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Replay;

public class BlfRealVectorReproTests
{
    private static readonly string RealVectorBlfPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "CH0_242下坡掉READY0.blf");

    [Fact]
    [Trait("Manual", "true")]
    public async Task ParseAsync_RealVectorBlf_LoadsSuccessfully()
    {
        // Skip silently if the user-provided fixture is not present —
        // not all dev machines / CI workers will have it. We log a
        // Console message so the skip reason is debuggable in CI run
        // output (vs. silent no-op that masked regressions in a prior
        // review finding).
        if (!System.IO.File.Exists(RealVectorBlfPath))
        {
            System.Console.WriteLine(
                $"[BLF Manual] skip: real-Vector fixture not present at {RealVectorBlfPath}. " +
                "Place the .blf there locally or see MEMORY peakcan-host-test-fixtures.");
            return;
        }

        await using var fs = System.IO.File.OpenRead(RealVectorBlfPath);
        IReadOnlyList<ReplayFrame> frames = Array.Empty<ReplayFrame>();
        try
        {
            frames = await BlfParser.ParseAsync(fs, new ReplayOptions());
            System.Console.WriteLine($"[Repro] frames.Count = {frames.Count}");
            if (frames.Count > 0)
            {
                System.Console.WriteLine($"[Repro] first frame ts={frames[0].Timestamp:F6}s id=0x{frames[0].Id:X}");
                System.Console.WriteLine($"[Repro] last frame ts={frames[^1].Timestamp:F6}s id=0x{frames[^1].Id:X}");
            }
            // Real Vector BLF should produce hundreds-to-thousands of frames,
            // not 0 and not throw a >50% corruption exception.
            frames.Count.Should().BeGreaterThan(0,
                "real Vector BLF must parse to >0 frames; 0 means dispatcher failed");

            // Sanity-check: first frame should have a non-zero CAN ID.
            frames[0].Id.Should().NotBe(0u, "first frame should have a non-zero CAN ID");
            // v3.17.0 PATCH (BLF playback fix): BlfParser now relativizes all
            // frame timestamps to the minimum, so frames[0].Timestamp is 0.0
            // (the first-emitted frame is the relative baseline). The
            // original "frames[0].Timestamp > 0 → 64-bit field read from the
            // right offset" assertion no longer distinguishes a correct parse
            // from a relativized baseline. The last frame's timestamp is the
            // robust signal: a real multi-frame recording has a non-zero
            // span, so frames[^1].Timestamp > 0 proves the 64-bit timestamp
            // field was read from the right offset for every frame.
            frames[^1].Timestamp.Should().BeGreaterThan(0,
                "last frame timestamp should be > 0 seconds (relative); 0 means the 64-bit timestamp field wasn't read from the right offset or the file has a single frame at t=0");

            // v3.17.0 PATCH follow-up: relativization must produce a SANE
            // relative span, not the raw absolute epoch seconds. This fixture
            // (CH0_242下坡掉READY0.blf) is a ~131s / 97246-frame recording.
            // At the correct 1ns/tick scale (BlfFormat.TimestampScale = 1e9),
            // the last relative timestamp is ~128.6s. The prior 10ns/tick
            // assumption (scale = 1e7) made every span 100× too large
            // (~12858s) — the user-visible "131s real → 13145s displayed"
            // regression. The tight <1000s bound catches both that 100× scale
            // error AND a skipped-relativization (absolute ~1.5e5s) at once;
            // the old <86400s (1 day) bound was too loose to catch either.
            frames[^1].Timestamp.Should().BeLessThan(1000.0,
                "after relativization the last frame's relative timestamp must be < 1000s for this ~131s fixture; " +
                "~1.3e4 means the 10ns/tick scale error (100× too large); ~1.5e5 means relativization was skipped");
        }
        catch (EndOfStreamException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Vector BLF parse threw EndOfStreamException — recursion consumes past inner stream tail: {ex.Message}");
        }
        catch (ReplayFormatException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Vector BLF parse threw ReplayFormatException — dispatcher/layout wrong: {ex.Message}");
        }
        catch (ReplayLoadException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Vector BLF parse threw ReplayLoadException: {ex.Message}");
        }
    }
}
