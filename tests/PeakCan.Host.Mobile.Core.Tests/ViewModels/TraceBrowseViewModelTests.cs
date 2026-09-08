using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class TraceBrowseViewModelTests
{
    private static CachedFrame Frame(long index, uint id = 0x100) =>
        new(index, index * 0.01, id, false, 2, [1, 2]);

    private static async Task<(TraceCacheStore Store, long TraceId)> CreateStoreAsync()
    {
        var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("browse.asc", 100);
        await store.AppendFramesAsync(id, Enumerable.Range(0, 181)
            .Select(i => Frame(i, i % 2 == 0 ? 0x100u : 0x200u))
            .ToArray());
        return (store, id);
    }

    [Fact]
    public async Task Open_Shows_First_Page_And_HasNext()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);

        await vm.OpenAsync(id);

        vm.Header.Should().Be("browse.asc");
        vm.HasNext.Should().BeTrue();
        vm.HasPrevious.Should().BeFalse();
        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.PageStatus.Should().Contain("1-80");
    }

    [Fact]
    public async Task Next_Then_Previous_Returns_To_Previous_Page()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        await vm.NextAsync();
        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.HasPrevious.Should().BeTrue();

        await vm.PreviousAsync();
        vm.PageStatus.Should().Contain("1-80");
    }

    [Fact]
    public async Task ApplyFilter_Shows_Only_Matching_CanIds()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "0x200";
        await vm.ApplyFilterAsync();

        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.Rows.Where(r => !r.IsEmpty).Select(r => r.IdText).Should().OnlyContain(text => text == "200");
    }

    [Fact]
    public async Task Previous_To_First_Page_Sets_HasPrevious_False()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        await vm.NextAsync();
        vm.HasPrevious.Should().BeTrue();
        await vm.NextAsync();
        vm.HasNext.Should().BeFalse();

        await vm.PreviousAsync();
        vm.HasPrevious.Should().BeTrue();

        await vm.PreviousAsync();
        vm.PageStatus.Should().Contain("1-80");
        vm.HasPrevious.Should().BeFalse();
        vm.HasNext.Should().BeTrue();
        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
    }

    [Fact]
    public async Task Open_NonExistent_Trace_Shows_Error_And_Empty()
    {
        var (store, _) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);

        await vm.OpenAsync(999);

        vm.ErrorMessage.Should().NotBeEmpty();
        vm.Rows.Should().OnlyContain(r => r.IsEmpty);
        vm.HasNext.Should().BeFalse();
        vm.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public async Task Slots_Remain_At_80_After_Partial_Page()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        await vm.NextAsync();
        await vm.NextAsync();

        vm.Rows.Count(r => !r.IsEmpty).Should().Be(21);
        vm.Rows.Count(r => r.IsEmpty).Should().Be(59);
        vm.Rows.Count.Should().Be(80);
        vm.PageStatus.Should().Contain("161-181");
        vm.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task SetDbc_Affects_Subsequent_Loaded_Pages_Only()
    {
        var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("browse.asc", 100);
        await store.AppendFramesAsync(id, Enumerable.Range(0, 81)
            .Select(i => Frame(i))
            .ToArray());
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);
        vm.Rows.Where(r => !r.IsEmpty).Should().OnlyContain(r => r.SignalSummaryText == string.Empty);

        var catalog = DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """, "engine.dbc").Catalog!;
        vm.SetDbc(catalog);

        await vm.NextAsync();

        vm.Rows.Should().Contain(r => r.SignalSummaryText == "EngineSpeed=128.25rpm");
        vm.Rows.Should().OnlyContain(r => r.IsEmpty || r.SignalSummaryText == "EngineSpeed=128.25rpm");
    }

    [Fact]
    public async Task Clear_Filter_Restores_Unpaged_View()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "0x200";
        await vm.ApplyFilterAsync();
        vm.Rows.Where(r => !r.IsEmpty).Select(r => r.IdText).Should().OnlyContain(text => text == "200");

        vm.FilterText = string.Empty;
        await vm.ApplyFilterAsync();

        vm.Rows.Count(r => !r.IsEmpty).Should().Be(80);
        vm.Rows.Select(r => r.IdText).Where(t => !string.IsNullOrEmpty(t))
            .Should().Contain("100").And.Contain("200");
    }
}
