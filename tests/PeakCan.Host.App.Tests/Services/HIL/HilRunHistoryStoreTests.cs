using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.HilHistory;
using Xunit;

namespace PeakCan.Host.App.Tests.ServicesSuitePreflight;

public sealed class HilRunHistoryStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"hil-history-{Guid.NewGuid():N}.json");

    private static HilRunHistoryStore CreateStore(string? path = null) =>
        new(NullLogger<HilRunHistoryStore>.Instance, path ?? TempPath());

    private static HilRunHistoryDto MakeHistory(string suitePath, bool cancelled = false, string? error = null) => new(
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), suitePath, "Hardware", true, 1, 1, 0, 0, 10, cancelled, error, null, @"C:\logs\hil");

    [Fact]
    public void Set_Then_Get_RoundTrips()
    {
        var store = CreateStore();
        store.Append(MakeHistory(@"C:\suite.json", true, "boom"));
        store.Get().Single().Should().BeEquivalentTo(MakeHistory(@"C:\suite.json", true, "boom"));
    }

    [Fact]
    public void Save_Trims_Tail_AfterFiftyRecords()
    {
        var store = CreateStore();
        foreach (var index in Enumerable.Range(0, 60))
            store.Append(MakeHistory($"suite-{index:00}"));

        var history = store.Get();
        history.Should().HaveCount(50);
        history[0].SuitePath.Should().EndWith("suite-10");
    }

    [Fact]
    public async Task Missing_Corrupt_Oversized_Are_Tolerated()
    {
        var missing = CreateStore();
        await missing.LoadAsync(default);
        missing.Get().Should().BeEmpty();

        var corruptPath = TempPath();
        File.WriteAllText(corruptPath, "{bad");
        var corrupt = CreateStore(corruptPath);
        await corrupt.LoadAsync(default);
        corrupt.Get().Should().BeEmpty();

        var oversizePath = TempPath();
        File.WriteAllText(oversizePath, new string('x', (int)HilRunHistoryStore.MaxLoadFileBytes + 1));
        var oversize = CreateStore(oversizePath);
        await oversize.LoadAsync(default);
        oversize.Get().Should().BeEmpty();
    }

    [Fact]
    public async Task Persisted_File_Reloads()
    {
        var path = TempPath();
        var original = CreateStore(path);
        original.Append(MakeHistory(@"C:\suite.json"));
        var reloaded = CreateStore(path);
        await reloaded.LoadAsync(default);
        reloaded.Get().Should().HaveCount(1);
    }
}
