using System.IO;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.11.0 MINOR T2 (H7): pins the four contract guarantees of the new
/// <see cref="TraceSessionSnapshotBuilder"/> static helper class.
/// <list type="number">
/// <item>no loaded file → empty contentHash, no IO attempted;</item>
/// <item>hashing fails → fallback to empty contentHash (no throw);</item>
/// <item>all scaffold fields propagate into the DTO's scalar envelope;</item>
/// <item>cancellation token is honoured by the hasher.</item>
/// </list>
/// </summary>
public sealed class TraceSessionSnapshotBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public TraceSessionSnapshotBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"builder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string NewAscFile(string contents = "0.000 1 100x R\n")
    {
        var p = Path.Combine(_tempDir, $"rec-{Guid.NewGuid():N}.asc");
        File.WriteAllText(p, contents);
        _files.Add(p);
        return p;
    }

    private static TraceSessionSnapshotBuilder.Scaffold MakeScaffold(
        string? loadedFilePath = null,
        double currentTimestamp = 1.5,
        double speed = 2.0,
        bool loop = true,
        double startTimestamp = 0.5,
        double endTimestamp = 4.5,
        string canIdFilterText = "0x100,0x200",
        string dbcPath = "/data/can.dbc")
        => new(
            loadedFilePath,
            currentTimestamp,
            speed,
            loop,
            startTimestamp,
            endTimestamp,
            canIdFilterText,
            dbcPath);

    [Fact]
    public async Task BuildAsync_NoLoadedFile_EmptyHash()
    {
        // Arrange — scaffold has no loaded file path. Hasher must NOT
        // be called (no IO attempted). Resulting DTO has empty source
        // list (builder only fills Sources when LoadedFilePath is set)
        // and a null Playback envelope.
        var hasher = Substitute.For<IAscContentHasher>();
        var sut = new TraceSessionSnapshotBuilder(hasher);

        var scaffold = MakeScaffold(loadedFilePath: null);

        // Act
        var dto = await sut.BuildAsync(scaffold);

        // Assert
        dto.Should().NotBeNull();
        dto.Version.Should().Be(1);
        dto.Schema.Should().Be(TraceSessionLibrary.CurrentSchema);
        dto.DbcPath.Should().Be("/data/can.dbc");
        dto.GlobalCanIdFilter.Should().Be("0x100,0x200");
        dto.Sources.Should().BeEmpty("no loaded file → no source list populated");
        dto.Playback.Should().BeNull("builder only handles the scalar envelope");
        dto.Viewports.Should().BeEmpty("caller populates VM-specific viewports");
        await hasher.DidNotReceiveWithAnyArgs().ComputeAsync(default!, default);
    }

    [Fact]
    public async Task BuildAsync_HashingFails_FallsBackToEmptyHash()
    {
        // Arrange — file exists on disk but the hasher throws an
        // IOException (e.g. locked file). The builder must catch the
        // expected exception types and fall through to an empty hash
        // rather than propagating the failure to the auto-saver
        // (which would skip the entire save).
        var path = NewAscFile();
        var hasher = Substitute.For<IAscContentHasher>();
        hasher.ComputeAsync(path, Arg.Any<CancellationToken>())
            .Throws(new IOException("file locked"));
        var sut = new TraceSessionSnapshotBuilder(hasher);

        var scaffold = MakeScaffold(loadedFilePath: path);

        // Act
        var dto = await sut.BuildAsync(scaffold);

        // Assert
        dto.Should().NotBeNull();
        // Builder pre-populated Sources[0] with the failed hash → empty.
        dto.Sources.Should().HaveCount(1);
        dto.Sources[0].ContentHash.Should().BeEmpty();
        dto.Sources[0].Path.Should().Be(path);
        dto.Version.Should().Be(1);
        dto.DbcPath.Should().Be("/data/can.dbc");
        dto.GlobalCanIdFilter.Should().Be("0x100,0x200");
    }

    [Fact]
    public async Task BuildAsync_PropagatesAllScaffoldFields()
    {
        // Arrange — happy path with a real file. All scalar fields on
        // the scaffold must round-trip into the DTO so the calling VM
        // does not have to duplicate the envelope assembly.
        var path = NewAscFile();
        const string cannedHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var hasher = Substitute.For<IAscContentHasher>();
        hasher.ComputeAsync(path, Arg.Any<CancellationToken>()).Returns(cannedHash);
        var sut = new TraceSessionSnapshotBuilder(hasher);

        var scaffold = MakeScaffold(
            loadedFilePath: path,
            currentTimestamp: 3.14,
            speed: 4.0,
            loop: true,
            startTimestamp: 1.0,
            endTimestamp: 9.0,
            canIdFilterText: "0x123",
            dbcPath: "/db/cluster.dbc");

        // Act
        var dto = await sut.BuildAsync(scaffold);

        // Assert — scalar envelope fields all match the scaffold.
        dto.Version.Should().Be(1);
        dto.Schema.Should().Be(TraceSessionLibrary.CurrentSchema);
        dto.SavedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        dto.AppVersion.Should().NotBeNullOrEmpty("AppVersion stamped from assembly metadata");
        dto.DbcPath.Should().Be("/db/cluster.dbc");
        dto.GlobalCanIdFilter.Should().Be("0x123");
        // Builder populates a single Source carrying the hash.
        dto.Sources.Should().HaveCount(1);
        dto.Sources[0].Path.Should().Be(path);
        dto.Sources[0].ContentHash.Should().Be(cannedHash);
        dto.Playback.Should().BeNull();
        dto.Viewports.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAsync_RespectsCancellationToken()
    {
        // Arrange — hasher signals cancellation via the supplied CT.
        // The builder must propagate the cancel rather than swallow it
        // (cancellation is a caller-driven signal, not an IO failure
        // that we should mask into an empty-hash fallback).
        var path = NewAscFile();
        var hasher = Substitute.For<IAscContentHasher>();
        hasher.ComputeAsync(path, Arg.Any<CancellationToken>())
            .Throws(call => new OperationCanceledException(call.ArgAt<CancellationToken>(1)));
        var sut = new TraceSessionSnapshotBuilder(hasher);

        var scaffold = MakeScaffold(loadedFilePath: path);

        // Act + Assert — the await must surface the cancel.
        var act = async () => await sut.BuildAsync(scaffold, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}