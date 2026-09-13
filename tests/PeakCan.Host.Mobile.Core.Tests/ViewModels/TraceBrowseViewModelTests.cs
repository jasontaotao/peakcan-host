using FluentAssertions;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Core.Replay;
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
    public async Task JumpTo_Found_ResetsPagingAtTargetAndSetsHighlight()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);
        vm.HasNext.Should().BeTrue();   // 起始在第一页

        var ok = await vm.JumpToAsync(0x200, first: true);

        ok.Should().BeTrue();
        vm.HighlightIndex.Should().Be(1);   // 0x200 最早出现在 index 1
        // 分页已重置到目标：首行即目标帧（AfterIndex = idx-1 语义）
        vm.Rows.First(r => !r.IsEmpty).Source!.Id.Should().Be(0x200);
    }

    [Fact]
    public async Task JumpTo_NotFound_ReturnsFalse()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        var ok = await vm.JumpToAsync(0x999, first: true);

        ok.Should().BeFalse();
        vm.HighlightIndex.Should().BeNull();
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

    [Fact]
    public async Task JumpTo_NextAtEndOfTrace_ReturnsFalse_NotFirstOccurrence()
    {
        // review 修复：全量可见（单页 10 帧）时按"下一处"，页末即 trace 末尾 →
        // 必须返回 false（"未找到"），绝不能退化为跳回最早一帧
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("tiny.asc", 100);
        await store.AppendFramesAsync(id, Enumerable.Range(0, 10)
            .Select(i => Frame(i, i % 2 == 0 ? 0x100u : 0x200u)).ToArray());
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);   // 10 帧 < 页大小 → 单页，页末 idx=9

        var ok = await vm.JumpToAsync(0x100, first: false);

        ok.Should().BeFalse();
        vm.HighlightIndex.Should().BeNull();
    }

    [Fact]
    public async Task ApplyFilter_IdOrPgn_OrSemantics_KeepsMatchingRows()
    {
        // P7：切换 SQL 下推后仍须保持 spec §4.5 的 OR 语义——
        // "0x200 pgn:F004"：0x200 标准帧靠 ID 命中保留（PGN 谓词只影响扩展帧）
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "0x200 pgn:F004";
        await vm.ApplyFilterAsync();

        vm.Rows.Should().Contain(r => r.Source != null && r.Source.Id == 0x200u);
        vm.Rows.Should().NotContain(r => r.Source != null && r.Source.Id == 0x100u);
    }

    // --- P7 Task 6：Browse 切换 SQL 下推 ---

    private const uint MatchId = 0x18EF01FF;   // PDU1，PGN 0xEF00
    private const uint OtherId = 0x18FF20E5;   // PDU2，PGN 0xFF20
    private const uint MissId = 0x18FF30E5;    // 不参与过滤断言的第三种 PGN

    /// <summary>旧内存后过滤谓词（spec §4.5 OR 语义），作为下推结果的参照实现。</summary>
    private static bool MemoryOracle(CachedFrame f, IReadOnlySet<uint>? idFilter, IReadOnlySet<uint>? pgnFilter)
    {
        if (idFilter is null && pgnFilter is null) return true;
        if (idFilter is not null && idFilter.Contains(f.CanId)) return true;
        if (pgnFilter is not null && f.IsExtended
            && pgnFilter.Contains(new PeakCan.Host.Core.J1939.J1939Id(f.CanId & 0x1FFFFFFFu).Pgn))
            return true;
        return false;
    }

    [Fact]
    public async Task AllInvalidFilter_ShowsZeroRows_AndHonestHasNext()
    {
        // tri-state 回归锁：all-invalid 输入必须 0 帧 + HasNext=false
        //（空集下推若被当作"无过滤"会显示全部帧——spec §2.4）
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "zzz";
        await vm.ApplyFilterAsync();

        vm.Rows.Should().OnlyContain(r => r.IsEmpty);
        vm.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task SparsePgnFilter_PagingHonest()
    {
        // 800 帧仅 3 帧命中：旧内存路径首页 80 行全被过滤掉、HasNext 撒谎；
        // SQL 下推后首页应直接给出 3 行命中帧。
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("sparse.asc", 100);
        var frames = new List<CachedFrame>();
        for (var i = 0; i < 800; i++)
            frames.Add(i is 100 or 400 or 700
                ? new CachedFrame(i, i, MatchId, true, 2, [1, 2])
                : new CachedFrame(i, i, MissId, true, 2, [1, 2]));
        await store.AppendFramesAsync(id, frames);

        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "pgn:EF00";
        await vm.ApplyFilterAsync();

        vm.Rows.Where(r => !r.IsEmpty).Select(r => r.Source!.Timestamp).Should().Equal([100.0, 400.0, 700.0]);
        vm.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task Pushdown_Parity_With_MemoryOracle()
    {
        // 空过滤 / 纯 ID / 纯 PGN / ID+PGN 四种输入与旧内存语义逐帧对拍
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("parity.asc", 100);
        var frames = new List<CachedFrame>();
        for (var i = 0; i < 100; i++)
        {
            var canId = (i % 10) switch
            {
                0 => MatchId,
                1 => OtherId,
                2 => 0x300u,
                3 => 0x100u,
                _ => MissId,
            };
            frames.Add(new CachedFrame(i, i * 0.01, canId, i % 10 is 0 or 1 or > 3, 2, [1, 2]));
        }
        await store.AppendFramesAsync(id, frames);

        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        var idsFor = (string text) => PeakCan.Host.Core.Replay.CanIdListParser.Parse(text).AllowList;
        var pgnsFor = (string text) => PeakCan.Host.Core.Replay.CanIdListParser.Parse(text).PgnAllowList;
        foreach (var filterText in new[] { "", "0x300", "pgn:EF00", "0x300 pgn:EF00" })
        {
            vm.FilterText = filterText;
            await vm.ApplyFilterAsync();

            var parsed = PeakCan.Host.Core.Replay.CanIdListParser.Parse(filterText);
            var expected = frames
                .Where(f => MemoryOracle(f, parsed.AllowList, parsed.PgnAllowList))
                .Take(80)
                .Select(f => f.Timestamp);
            vm.Rows.Where(r => !r.IsEmpty).Select(r => r.Source!.Timestamp)
                .Should().Equal(expected, $"filter '{filterText}' 必须与内存参照语义一致");
        }
    }

    [Fact]
    public async Task JumpTo_StillWorks_Under_PgnFilter()
    {
        var (store, id) = await CreateStoreAsync();
        var vm = new TraceBrowseViewModel(store);
        await vm.OpenAsync(id);

        vm.FilterText = "pgn:EF00";
        await vm.ApplyFilterAsync();

        // 搜索仍按 CAN ID（FindFrameAsync 与过滤无关），JumpTo 不得因过滤切换而失效
        var ok = await vm.JumpToAsync(0x200, first: true);

        ok.Should().BeTrue();
        vm.HighlightIndex.Should().Be(1);
    }
}
