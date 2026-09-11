using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// Pages completed SQLite traces without materializing the whole file.
/// Forward paging uses <c>AfterIndex</c>; backward paging uses <c>BeforeIndex</c>.
/// The store returns Limit+1 descending rows for backward queries, removes the
/// extra row, then reverses the displayed page — so <see cref="HasPrevious"/>
/// is true only when that backward query reports <c>HasMore</c>.
/// </summary>
public sealed partial class TraceBrowseViewModel : ObservableObject
{
    private const int PageSize = 80;

    private readonly ITraceCacheStore _store;
    private readonly FrameRowSlot[] _slots = new FrameRowSlot[PageSize];
    private long _traceId;
    private long? _firstIndex;
    private long? _lastIndex;
    private DbcCatalog? _dbc;
    private IReadOnlySet<uint>? _idFilter;
    private IReadOnlySet<uint>? _pgnFilter;

    public TraceBrowseViewModel(ITraceCacheStore store)
    {
        _store = store;
        for (var i = 0; i < PageSize; i++)
            _slots[i] = new FrameRowSlot();
    }

    /// <summary>Stable 80-slot viewport; unused slots are cleared on each page load.</summary>
    public IReadOnlyList<FrameRowSlot> Rows => _slots;

    [ObservableProperty] private string _header = string.Empty;
    [ObservableProperty] private string _pageStatus = string.Empty;
    [ObservableProperty] private string? _filterText;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _hasNext;
    [ObservableProperty] private bool _hasPrevious;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private long? _highlightIndex;

    public async Task OpenAsync(long traceId, CancellationToken ct = default)
    {
        _traceId = traceId;
        _firstIndex = null;
        _lastIndex = null;
        _idFilter = null;
        ErrorMessage = null;
        FilterText = null;
        Header = string.Empty;

        var summary = await _store.GetTraceAsync(traceId, ct).ConfigureAwait(false);
        if (summary is null)
        {
            ErrorMessage = "缓存记录不存在";
            HasNext = false;
            HasPrevious = false;
            PageStatus = "0 帧";
            ClearAllSlots();
            return;
        }

        Header = summary.SourceName;
        await FirstAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    /// <summary>Sets the catalog used only for pages loaded after this call.</summary>
    public void SetDbc(DbcCatalog? catalog) => _dbc = catalog;

    public Task FirstAsync() =>
        LoadForwardAsync(NextQuery(-1), hasContentBefore: false);

    [RelayCommand]
    public Task NextAsync() => _lastIndex is null
        ? Task.CompletedTask
        : LoadForwardAsync(NextQuery(_lastIndex), hasContentBefore: true);

    [RelayCommand]
    public Task PreviousAsync() => _firstIndex is null || !HasPrevious
        ? Task.CompletedTask
        : LoadBackwardAsync(new FrameQuery(BeforeIndex: _firstIndex, CanIds: QueryCanIds, Limit: PageSize));

    /// <summary>SQL 只支持 ID 集合下推；存在 PGN 过滤时不下推（PGN 谓词走内存，见 ApplyMemoryFilter）。</summary>
    private IReadOnlySet<uint>? QueryCanIds => _pgnFilter is null ? _idFilter : null;

    private FrameQuery NextQuery(long? afterIndex) =>
        new(AfterIndex: afterIndex, CanIds: QueryCanIds, Limit: PageSize);

    /// <summary>
    /// PGN 过滤在内存后处理（spec §8 已知限制：内存后过滤会扭曲 keyset 分页的 HasMore）。
    /// 仅当存在 PGN 过滤时启用（ID 过滤已被 QueryCanIds 禁掉下推，一并在此处理）；
    /// 纯 ID 过滤仍走 SQL 下推，此处直接返回零开销。
    /// </summary>
    private IReadOnlyList<CachedFrame> ApplyMemoryFilter(IReadOnlyList<CachedFrame> frames)
    {
        if (_pgnFilter is null) return frames;
        return frames.Where(f => PassesBrowseFilter(f)).ToArray();
    }

    private bool PassesBrowseFilter(CachedFrame f)
    {
        // OR 语义（spec §4.5），与 TraceSessionViewModel.PassesFilter 逐字一致：
        // (ID 命中) OR (扩展帧且 PGN 命中)。空集（全部 token 无效）自然永不命中 →
        // all-invalid 输入全拒，与 Replay "emits nothing" 语义一致（review MEDIUM）。
        if (_idFilter is null && _pgnFilter is null) return true;
        if (_idFilter is not null && _idFilter.Contains(f.CanId)) return true;
        if (_pgnFilter is not null && f.IsExtended
            && _pgnFilter.Contains(new J1939Id(f.CanId & J1939Id.Raw29Mask).Pgn))
            return true;
        return false;
    }

    [RelayCommand]
    public Task ApplyFilterAsync()
    {
        var parsed = CanIdListParser.Parse(FilterText);
        _idFilter = parsed.AllowList;
        _pgnFilter = parsed.PgnAllowList;
        return FirstAsync();
    }

    private async Task LoadForwardAsync(FrameQuery query, bool hasContentBefore)
    {
        if (_traceId <= 0 || IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = await _store.GetFramesAsync(_traceId, query).ConfigureAwait(false);
            var frames = ApplyMemoryFilter(page.Frames);
            var rows = frames.Select(f => f.ToFrameRow(_dbc)).ToArray();

            FillSlots(rows);

            // Forward query: HasMore means more rows exist AFTER this page.
            HasNext = page.HasMore;
            HasPrevious = hasContentBefore;

            UpdateCursors(frames);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadBackwardAsync(FrameQuery query)
    {
        if (_traceId <= 0 || IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var page = await _store.GetFramesAsync(_traceId, query).ConfigureAwait(false);
            var frames = ApplyMemoryFilter(page.Frames);
            var rows = frames.Select(f => f.ToFrameRow(_dbc)).ToArray();

            FillSlots(rows);

            // Backward query returns Limit+1 descending, removes the extra, reverses.
            // HasMore means more rows exist BEFORE this page.
            HasPrevious = page.HasMore;
            // We came from a non-empty page, so there are rows after.
            HasNext = rows.Length > 0;

            UpdateCursors(frames);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 缓存内定位某 ID 的帧并重置分页到目标（AfterIndex=idx-1 语义，目标行成为页首）。
    /// first=true 找最早出现；first=false 从当前页末尾时刻之后找下一处。
    /// 返回 false 表示缓存范围内未找到。
    /// </summary>
    public async Task<bool> JumpToAsync(uint canId, bool first)
    {
        double? after = null;
        if (!first && _lastIndex is { } lastIndex)
        {
            // 下界取"当前页最后一帧自身"的 timestamp（Next 语义 = 页末之后第一处）。
            // AfterIndex: lastIndex-1 → idx ≥ lastIndex 的首条 = 页末帧。原来用
            // AfterIndex: lastIndex 取到的是页后一帧（cutoff 偏后）；且页末为空时
            // after 落 null 会让 FindFrameAsync 的 Next 退化为最早一帧，导致"末尾按
            // 下一处"错误跳回开头（review MEDIUM）。
            var lastPage = await _store.GetFramesAsync(_traceId,
                new FrameQuery(AfterIndex: lastIndex - 1, Limit: 1)).ConfigureAwait(false);
            if (lastPage.Frames.Count > 0) after = lastPage.Frames[0].Timestamp;
            else return false;   // 已到 trace 末尾：无"下一处"
        }

        var frame = await _store.FindFrameAsync(_traceId, canId, after,
            first ? CacheSearchDirection.First : CacheSearchDirection.Next).ConfigureAwait(false);
        if (frame is null) return false;

        HighlightIndex = frame.Index;
        await LoadForwardAsync(
            new FrameQuery(AfterIndex: frame.Index - 1, CanIds: QueryCanIds, Limit: PageSize),
            hasContentBefore: true).ConfigureAwait(false);
        return true;
    }

    private void FillSlots(FrameRow[] rows)
    {
        var blankCount = PageSize - rows.Length;
        for (var i = 0; i < blankCount; i++)
            _slots[i].Clear();
        for (var i = 0; i < rows.Length; i++)
            _slots[blankCount + i].UpdateFrom(rows[i]);
    }

    private void UpdateCursors(IReadOnlyList<CachedFrame> frames)
    {
        if (frames.Count > 0)
        {
            _firstIndex = frames[0].Index;
            _lastIndex = frames[^1].Index;
            PageStatus = $"{frames[0].Index + 1}-{frames[^1].Index + 1}";
        }
        else
        {
            // Keep cursors so the user can still navigate back.
            PageStatus = "0 帧";
        }
    }

    private void ClearAllSlots()
    {
        for (var i = 0; i < PageSize; i++)
            _slots[i].Clear();
    }
}
