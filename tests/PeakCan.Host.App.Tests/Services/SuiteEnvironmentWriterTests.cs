using System.IO;
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.Host.App.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class SuiteEnvironmentWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"pch-suite-{Guid.NewGuid():N}.json");
    private string? _originalJson;

    [Fact]
    public void Appends_Nodes_And_Preserves_Existing_Suite_Data()
    {
        CreateSuiteFile();
        var incoming = new[] { FixedNode("Trace-0x123") };

        var result = new SuiteEnvironmentWriter().AppendNodes(_path, incoming);

        result.Success.Should().BeTrue(result.Error);
        result.Suite!.Environment.Should().HaveCount(1);
        Reload().Environment!.Single().Name.Should().Be("Trace-0x123");
    }

    [Fact]
    public void Rejects_Duplicate_Node_Name()
    {
        CreateSuiteFile(FixedNode("Duplicate"));
        var originalJson = File.ReadAllText(_path);

        var result = new SuiteEnvironmentWriter().AppendNodes(_path, [FixedNode("Duplicate")]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Duplicate");
        File.ReadAllText(_path).Should().Be(originalJson);
    }

    [Fact]
    public void Rejects_Send_Id_Conflict_In_Same_Channel()
    {
        CreateSuiteFile(FixedNode("Existing", 0x123));
        var originalJson = File.ReadAllText(_path);

        var result = new SuiteEnvironmentWriter().AppendNodes(_path, [FixedNode("Incoming", 0x123)]);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("0x123");
        File.ReadAllText(_path).Should().Be(originalJson);
    }

    [Fact]
    public void Rejects_Node_Channel_Missing_From_Multichannel_Suite()
    {
        var channels = new[] { new ChannelConfig("A", "51", null, false) };
        CreateSuiteFile(channels: channels);
        var originalJson = File.ReadAllText(_path);

        var result = new SuiteEnvironmentWriter().AppendNodes(
            _path, [FixedNode("Trace", channel: "MISSING")], channels);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("MISSING");
        File.ReadAllText(_path).Should().Be(originalJson);
    }

    private void CreateSuiteFile(
        RestbusNode? environment = null,
        IReadOnlyList<ChannelConfig>? channels = null)
    {
        var suite = new TestSuite(
            "Suite",
            [],
            [],
            [],
            new TestSuiteConfig(),
            Channels: channels,
            Environment: environment is null ? [] : [environment]);
        File.WriteAllText(_path, JsonSerializer.Serialize(suite, HILJsonOptions.Default));
        _originalJson = File.ReadAllText(_path);
    }

    private static RestbusNode FixedNode(
        string name,
        uint id = 0x456,
        string? channel = null)
        => new()
        {
            Name = name,
            Channel = channel,
            SourceChannel = channel,
            Identity = new RawCanNodeIdentity(),
            Messages =
            [
                new NodeMessage(new CanMessageRef(id, false), 20, new FixedHexSource("0102")),
            ],
        };

    private TestSuite Reload()
        => JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(_path), HILJsonOptions.Default)!;

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }
}
