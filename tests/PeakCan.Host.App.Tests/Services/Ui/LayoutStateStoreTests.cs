using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

public sealed class LayoutStateStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"layout-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Set_Then_Get_RoundTrips()
    {
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, TempPath());
        var dto = new LayoutStateDto(420.0, 2, 1);
        store.Set(dto);
        store.Get().Should().Be(dto);
    }

    [Fact]
    public async Task Persisted_File_Reloads_After_New_Instance()
    {
        var path = TempPath();
        new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path)
            .Set(new LayoutStateDto(350.0, 0, 2));

        var reloaded = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        await reloaded.LoadAsync(default);
        reloaded.Get().Should().Be(new LayoutStateDto(350.0, 0, 2));
    }

    [Fact]
    public void Missing_File_Loads_Empty()
    {
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, TempPath());
        store.Get().Should().BeNull();
    }

    [Fact]
    public async Task Corrupt_File_Loads_Empty_Without_Throwing()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json !!!");
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        await store.LoadAsync(default);
        store.Get().Should().BeNull();
    }

    [Fact]
    public async Task Oversized_File_Is_Treated_As_Empty()
    {
        var path = TempPath();
        File.WriteAllText(path, new string('x', (int)LayoutStateStore.MaxLoadFileBytes + 1));
        var store = new LayoutStateStore(NullLogger<LayoutStateStore>.Instance, path);
        await store.LoadAsync(default);
        store.Get().Should().BeNull();
    }
}
