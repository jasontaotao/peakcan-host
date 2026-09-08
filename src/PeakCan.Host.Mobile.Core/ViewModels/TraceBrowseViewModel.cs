using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private IReadOnlySet<uint>? _idFilter;

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
    public Task FirstAsync() =>
        LoadForwardAsync(new FrameQuery(AfterIndex: -1, CanIds: _idFilter, Limit: PageSize), hasContentBefore: false);

    [RelayCommand]
    public Task NextAsync() => _lastIndex is null
        ? Task.CompletedTask
        : LoadForwardAsync(new FrameQuery(AfterIndex: _lastIndex, CanIds: _idFilter, Limit: PageSize), hasContentBefore: true);

    [RelayCommand]
    public Task PreviousAsync() => _firstIndex is null || !HasPrevious
        ? Task.CompletedTask
        : LoadBackwardAsync(new FrameQuery(BeforeIndex: _firstIndex, CanIds: _idFilter, Limit: PageSize));

    [RelayCommand]
    public Task ApplyFilterAsync()
    {
        _idFilter = string.IsNullOrWhiteSpace(FilterText)
            ? null
            : CanIdListParser.Parse(FilterText).AllowList;
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
            var frames = page.Frames;
            var rows = frames.Select(f => f.ToFrameRow()).ToArray();

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
            var frames = page.Frames;
            var rows = frames.Select(f => f.ToFrameRow()).ToArray();

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
