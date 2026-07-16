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
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

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
        // not all dev machines / CI workers will have it.
        if (!System.IO.File.Exists(RealVectorBlfPath))
        {
            return; // skip
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

            // Sanity-check first frame
            frames[0].Id.Should().NotBe(0u, "first frame should have a non-zero CAN ID");
            frames[0].Timestamp.Should().BeGreaterThan(0,
                "first frame timestamp should be > 0 seconds; 0 means the 64-bit timestamp field wasn't read from the right offset");
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
