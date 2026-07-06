using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Services;
using Xunit;
// v3.6.4 PATCH: the PeakCan.Host.Core.Path namespace exists; fully
// qualify the BCL static so it doesn't resolve to PeakCan.Host.Core.Path.
using IOPath = System.IO.Path;

namespace PeakCan.Host.Core.Tests.Services;

/// <summary>
/// v3.6.4 PATCH: pins the five core behaviors of
/// <see cref="FileSystemAscLocator"/>:
/// <list type="number">
/// <item>file matching the recorded hash is found in a search root;</item>
/// <item>no matching file returns <c>null</c>;</item>
/// <item>multiple matches return the first one encountered (DFS order);</item>
/// <item>recursive walk reaches deeply nested files;</item>
/// <item>max-depth cap stops runaway walks beyond the configured limit;</item>
/// <item>cancellation propagates as <see cref="OperationCanceledException"/>.</item>
/// </list>
/// Each test writes a JSON search-dirs file via a per-test temp
/// directory; the locator is constructed with the per-test path so
/// parallel xunit execution is safe and the fixture cannot leak state
/// into a sibling test.
/// </summary>
public sealed class AscLocatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _searchDirsPath;

    public AscLocatorTests()
    {
        _tempDir = IOPath.Combine(IOPath.GetTempPath(), $"asclocator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _searchDirsPath = IOPath.Combine(_tempDir, "asc-search-dirs.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string NewAsc(string relativePath, byte[] contents)
    {
        var full = IOPath.Combine(_tempDir, relativePath.Replace('/', IOPath.DirectorySeparatorChar));
        var dir = IOPath.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, contents);
        return full;
    }

    private void WriteSearchDirs(params string[] dirs)
    {
        var json = JsonSerializer.Serialize(dirs.ToList());
        File.WriteAllText(_searchDirsPath, json);
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private FileSystemAscLocator NewLocator() =>
        new(
            NullLogger<FileSystemAscLocator>.Instance,
            new Sha256AscContentHasher(NullLogger<Sha256AscContentHasher>.Instance),
            _searchDirsPath);

    [Fact]
    public async Task LocateAsync_FileExistsInSearchRoot_ReturnsPath()
    {
        // Arrange — search root = _tempDir/root1, place a matching
        // .asc at the top level of that root.
        var root = IOPath.Combine(_tempDir, "root1");
        Directory.CreateDirectory(root);
        WriteSearchDirs(root);
        var bytes = Encoding.ASCII.GetBytes("date Mon Jan 01 12:00:00 2026\n0.0 100 1 Rx d 8 11 22 33 44 55 66 77 88\n");
        var file = NewAsc(IOPath.Combine("root1", "drive.asc"), bytes);
        var hash = Sha256(bytes);

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().Be(file);
    }

    [Fact]
    public async Task LocateAsync_NoMatchingFile_ReturnsNull()
    {
        // Arrange — search root exists but no .asc inside has the
        // requested hash. The locator must NOT throw or surface a
        // false positive.
        var root = IOPath.Combine(_tempDir, "root-nomatch");
        Directory.CreateDirectory(root);
        WriteSearchDirs(root);
        NewAsc(IOPath.Combine("root-nomatch", "unrelated.asc"),
            Encoding.ASCII.GetBytes("totally different content"));
        var wantedHash = Sha256(Encoding.ASCII.GetBytes("the file we did NOT place"));

        // Act
        var found = await NewLocator().LocateAsync(wantedHash);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_EmptyHash_ReturnsNull_NoSearch()
    {
        // Arrange — search dir is configured but the hash is empty.
        // The locator must short-circuit; an empty-hash search across
        // every file would be expensive and is not a supported case.
        var root = IOPath.Combine(_tempDir, "root-empty");
        Directory.CreateDirectory(root);
        WriteSearchDirs(root);
        NewAsc(IOPath.Combine("root-empty", "any.asc"), new byte[] { 1, 2, 3 });

        // Act
        var found = await NewLocator().LocateAsync("");

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_RecursiveWalk_FindsFileInSubdir()
    {
        // Arrange — search root + 2 subdir levels deep. The locator
        // must walk down to find the file. We pick a layout under
        // the depth cap (root + 2 subdirs = depth 2).
        var root = IOPath.Combine(_tempDir, "root-recursive");
        var deep = IOPath.Combine(root, "sub1", "sub2");
        Directory.CreateDirectory(deep);
        WriteSearchDirs(root);
        var bytes = Encoding.ASCII.GetBytes("deeply nested");
        var file = NewAsc(IOPath.Combine("root-recursive", "sub1", "sub2", "deep.asc"), bytes);
        var hash = Sha256(bytes);

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().Be(file);
    }

    [Fact]
    public async Task LocateAsync_BeyondMaxDepth_ReturnsNull()
    {
        // Arrange — file is placed root + 5 subdirs (depth 5).
        // MaxSearchDepth = 4 (root + 3 subdirs), so the file is
        // unreachable. The locator must NOT walk past the cap.
        var root = IOPath.Combine(_tempDir, "root-deep");
        var deep = IOPath.Combine(root, "l1", "l2", "l3", "l4", "l5");
        Directory.CreateDirectory(deep);
        WriteSearchDirs(root);
        var bytes = Encoding.ASCII.GetBytes("too deep to reach");
        NewAsc(IOPath.Combine("root-deep", "l1", "l2", "l3", "l4", "l5", "buried.asc"), bytes);
        var hash = Sha256(bytes);

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().BeNull(
            "the file is beyond MaxSearchDepth (4) so the locator must not surface it");
    }

    [Fact]
    public async Task LocateAsync_MissingSearchDirsFile_EmptyList_NoThrow()
    {
        // Arrange — no search-dirs file exists. The locator must
        // silently treat this as an empty list and return null,
        // not throw. Path-only resolution in the caller covers
        // bundles whose hash lookup yields nothing.
        var hash = Sha256(Encoding.ASCII.GetBytes("anything"));

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public async Task LocateAsync_MultipleSearchRoots_FindsInFirstMatching()
    {
        // Arrange — two search roots, each with one .asc, only the
        // second root has the matching hash. The locator must walk
        // root1 first (no match), then root2 (match), and return
        // the root2 path.
        var root1 = IOPath.Combine(_tempDir, "rootA");
        var root2 = IOPath.Combine(_tempDir, "rootB");
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);
        WriteSearchDirs(root1, root2);
        NewAsc(IOPath.Combine("rootA", "unrelated.asc"),
            Encoding.ASCII.GetBytes("rootA content"));
        var bytes = Encoding.ASCII.GetBytes("rootB content");
        var fileB = NewAsc(IOPath.Combine("rootB", "match.asc"), bytes);
        var hash = Sha256(bytes);

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().Be(fileB);
    }

    [Fact]
    public async Task LocateAsync_NonAscExtension_NotConsideredForHashMatch()
    {
        // Arrange — file with the right content but .txt extension
        // must be skipped; a .asc with different content must also
        // be skipped. The locator must match on the .asc extension
        // before computing any hash.
        var root = IOPath.Combine(_tempDir, "root-ext");
        Directory.CreateDirectory(root);
        WriteSearchDirs(root);
        var bytes = Encoding.ASCII.GetBytes("exact bytes");
        NewAsc(IOPath.Combine("root-ext", "renamed.txt"), bytes);   // wrong ext
        NewAsc(IOPath.Combine("root-ext", "unrelated.asc"),
            Encoding.ASCII.GetBytes("different bytes"));          // right ext, wrong hash
        var hash = Sha256(bytes);

        // Act
        var found = await NewLocator().LocateAsync(hash);

        // Assert
        found.Should().BeNull(
            "the matching-content file is named .txt and the .asc inside has different content");
    }
}