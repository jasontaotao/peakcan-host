using System.Text.Json;
using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: pins the <see cref="OxyColorJsonConverter"/> round-trip
/// contract. <see cref="OxyColor"/> packs four bytes (A,R,G,B) into a
/// single <c>uint</c> (<see cref="OxyColor.Value"/>). The converter writes
/// those four bytes as a JSON object so a human inspecting a
/// <c>.tmtrace</c> file can read each channel directly.
/// </summary>
public sealed class OxyColorJsonConverterTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        Converters = { new OxyColorJsonConverter() },
    };

    [Fact]
    public void RoundTrip_ArbitraryArgbColor_PreservesAllFourChannels()
    {
        var original = OxyColor.FromArgb(0xFF, 0x4E, 0x79, 0xA7);   // Tableau blue

        var json = JsonSerializer.Serialize(original, Opts);
        var roundTripped = JsonSerializer.Deserialize<OxyColor>(json, Opts);

        roundTripped.Should().Be(original,
            "OxyColor.Value is a packed ARGB uint; serialize→deserialize must be lossless");
    }

    [Fact]
    public void Serialize_Emits_FourIntProperties_ARGB()
    {
        var color = OxyColor.FromArgb(0x12, 0x34, 0x56, 0x78);

        var json = JsonSerializer.Serialize(color, Opts);

        // Expect explicit object shape — easier for humans + future schema migrations.
        json.Should().Contain("\"a\":18")
            .And.Contain("\"r\":52")
            .And.Contain("\"g\":86")
            .And.Contain("\"b\":120");
    }

    [Fact]
    public void Deserialize_OpaqueObject_ReconstructsOxyColor()
    {
        const string json = "{\"a\":255,\"r\":78,\"g\":121,\"b\":167}";

        var color = JsonSerializer.Deserialize<OxyColor>(json, Opts);

        color.A.Should().Be((byte)255);
        color.R.Should().Be((byte)78);
        color.G.Should().Be((byte)121);
        color.B.Should().Be((byte)167);
    }

    [Fact]
    public void RoundTrip_FullyTransparentColor_PreservesAlpha()
    {
        var original = OxyColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

        var json = JsonSerializer.Serialize(original, Opts);
        var roundTripped = JsonSerializer.Deserialize<OxyColor>(json, Opts);

        roundTripped.A.Should().Be((byte)0x00,
            "alpha must survive round-trip — fully-transparent color is a valid bundle state");
    }
}