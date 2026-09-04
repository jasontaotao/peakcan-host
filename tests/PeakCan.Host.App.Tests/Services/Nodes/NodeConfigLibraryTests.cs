using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nodes;

public class NodeConfigLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"peakcan-nodes-{Guid.NewGuid():N}");

    private NodeConfigLibrary CreateLib() => new(_dir, NullLogger<NodeConfigLibrary>.Instance);

    private static NodeConfig Sample(string name = "n1") => new()
    {
        Name = name,
        Identity = new J1939NodeIdentity(0x56),
        Messages = Array.Empty<NodeMessage>(),
        Rules = Array.Empty<ResponseRule>(),
    };

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var lib = CreateLib();
        lib.Save(Sample());

        lib.Load().Should().ContainSingle().Which.Name.Should().Be("n1");
        Directory.GetFiles(_dir, "*.node.json").Should().ContainSingle();
    }

    [Fact]
    public void Corrupt_File_Is_Skipped_Not_Thrown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.node.json"), "{ not json");
        var lib = CreateLib();

        lib.Load().Should().BeEmpty();   // 容错：跳过 + LogWarning
    }

    [Fact]
    public void Unknown_Kind_File_Is_Skipped_Not_Thrown()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "future.node.json"),
            """{"version":1,"config":{"name":"future","identity":{"kind":"canfd-everything"},"messages":[],"rules":[]}}""");
        var lib = CreateLib();

        lib.Load().Should().BeEmpty();   // 未知 discriminator → JsonException → 跳过
    }

    [Fact]
    public void Delete_Removes_File()
    {
        var lib = CreateLib();
        lib.Save(Sample("gone"));

        lib.Delete("gone").Should().BeTrue();
        lib.Load().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
