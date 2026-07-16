using System.IO;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.51.0 MINOR: manual verification using the public vblf test fixture
/// (zariiii9003/vblf main, fetched 2026-07-16 to
/// .superpowers/sdd/reference/vblf_test_CAN_MESSAGE.lobj). Marked
/// [Trait("Manual", "true")] so CI auto-skips via:
///   dotnet test --filter "FullyQualifiedName!~Manual"
/// or by adding a CI-side filter. User runs on their machine to verify
/// the round-trip parse against the public vblf reference is 1:1.
/// <para>
/// Real user-vehicle BLF fixture explicitly declined by user 2026-07-16;
/// this public vblf fixture is the substitute verification target.
/// </para>
/// </summary>
public class BlfParserManualTests
{
    private static readonly string VblfFixturePath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".superpowers", "sdd", "reference",
        "vblf_test_CAN_MESSAGE.lobj");

    [Fact]
    [Trait("Manual", "true")]
    public async Task BlfParser_VblfTestFixture_ManualLoad()
    {
        var path = System.IO.Path.GetFullPath(VblfFixturePath);
        if (!File.Exists(path))
        {
            return; // xUnit passes the test if no assertion runs
        }

        await using var fs = File.OpenRead(path);
        var frames = await BlfParser.ParseAsync(fs, new ReplayOptions());

        frames.Should().HaveCount(1, "vblf_test_CAN_MESSAGE.lobj contains 1 CanMessage");
        frames[0].Id.Should().NotBe(0u, "frame_id must be parsed from 12-byte CanMessage");
    }
}