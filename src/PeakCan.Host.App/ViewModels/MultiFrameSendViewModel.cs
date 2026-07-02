using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v2.1.0 MINOR: backing VM for the multi-frame send window
/// (<c>MultiFrameSendWindow.xaml</c>). Lets the user build a list
/// of CAN frame definitions, choose concurrent vs sequential mode
/// (with optional inter-frame delay + iteration count), and execute
/// the sequence through the shared <see cref="SendService"/> via
/// <see cref="SequenceSendService"/>.
///
/// <para>
/// Threading: same pattern as <c>SendViewModel</c> — UI thread
/// owns the bound <see cref="ObservableCollection{T}"/>;
/// <see cref="SendCommand"/> awaits on the captured WPF
/// SynchronizationContext (no ConfigureAwait(false)) so the
/// catch + finally run on the UI dispatcher.
/// </para>
/// </summary>
public sealed partial class MultiFrameSendViewModel : ObservableObject, IDisposable
{
    private readonly SequenceSendService _service;
    private readonly DbcService? _dbcService;
    private CancellationTokenSource? _runCts;
    private readonly DispatcherTimer _progressPollTimer;

    /// <summary>One row per CAN frame definition.</summary>
    public ObservableCollection<MultiFrameSequenceRow> Rows { get; } = new();

    /// <summary>v2.1.1 PATCH: messages from the loaded DBC document,
    /// for the DBC-row message picker. Empty when no DBC loaded.</summary>
    public ObservableCollection<Message> AvailableDbcMessages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddRowCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveRowCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateRowCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRowsCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _isConcurrent = true;
    [ObservableProperty] private int  _delayMs;
    [ObservableProperty] private int  _iterations = 1;

    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private int    _progressValue;
    [ObservableProperty] private int    _progressMax;
    [ObservableProperty] private MultiFrameSequenceRow? _selectedRow;

    public MultiFrameSendViewModel(SequenceSendService service)
        : this(service, null) { }

    /// <summary>
    /// v2.1.1 PATCH: full-fidelity ctor that wires the
    /// <see cref="DbcService"/> for the DBC-row message picker and
    /// subscribes to <see cref="DbcService.DbcLoaded"/> so a DBC
    /// loaded AFTER the window opens still shows up in the picker.
    /// Tests that don't need DBC rows pass null.
    /// </summary>
    public MultiFrameSendViewModel(SequenceSendService service, DbcService? dbcService)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dbcService = dbcService;
        Rows.CollectionChanged += OnRowsChanged;
        // Seed with one empty row so the DataGrid isn't empty on first open.
        Rows.Add(new MultiFrameSequenceRow { Id = 0x100, DataHex = "DEADBEEF" });

        // v2.1.1 PATCH: populate AvailableDbcMessages from the
        // currently-loaded DBC document (if any) and subscribe to
        // DbcLoaded so a later load updates the picker.
        if (_dbcService is not null)
        {
            foreach (var msg in _dbcService.Current?.Messages ?? Enumerable.Empty<Message>())
                AvailableDbcMessages.Add(msg);
            _dbcService.DbcLoaded += OnDbcLoaded;
        }

        // Poll the run progress from the service's cancellation state.
        // The service itself reports progress via IProgress<int> which
        // marshals back to the UI thread (caller uses TaskScheduler.FromCurrentSynchronizationContext).
        _progressPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _progressPollTimer.Tick += (_, _) =>
        {
            // Intentionally empty: IProgress<int> on the caller side
            // pushes ProgressValue updates. The timer exists for future
            // use (e.g. elapsed-time display) and is cheap when idle.
        };
        _progressPollTimer.Start();
    }

    private void OnDbcLoaded(DbcDocument doc)
    {
        // DbcLoaded fires on a worker thread (DbcService.LoadAsync);
        // ObservableCollection mutation must happen on the UI
        // dispatcher. RunOnUi pattern matches DbcViewModel /
        // DbcSendViewModel.
        ((Action)(() =>
        {
            AvailableDbcMessages.Clear();
            foreach (var msg in doc.Messages)
                AvailableDbcMessages.Add(msg);
        })).RunOnUi();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshProgressMax();

    private void RefreshProgressMax()
    {
        ProgressMax = Math.Max(0, Rows.Count) * Math.Max(1, Iterations);
    }

    partial void OnIterationsChanged(int value) => RefreshProgressMax();

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void AddRow()
    {
        Rows.Add(new MultiFrameSequenceRow());
        SelectedRow = Rows[^1];
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void RemoveRow()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        Rows.Remove(SelectedRow);
        if (Rows.Count > 0)
            SelectedRow = Rows[Math.Min(idx, Rows.Count - 1)];
        else
            SelectedRow = null;
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void DuplicateRow()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        var clone = new MultiFrameSequenceRow
        {
            Id = SelectedRow.Id,
            DataHex = SelectedRow.DataHex,
            IsExtended = SelectedRow.IsExtended,
            IsFd = SelectedRow.IsFd,
            IsRtr = SelectedRow.IsRtr,
            IsBitRateSwitch = SelectedRow.IsBitRateSwitch,
            IsErrorStateIndicator = SelectedRow.IsErrorStateIndicator,
        };
        Rows.Insert(idx + 1, clone);
        SelectedRow = clone;
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void MoveUp()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        if (idx <= 0) return;
        Rows.Move(idx, idx - 1);
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void MoveDown()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        if (idx < 0 || idx >= Rows.Count - 1) return;
        Rows.Move(idx, idx + 1);
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void ClearRows()
    {
        Rows.Clear();
        SelectedRow = null;
    }

    private bool CanEditRows() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Rows.Count == 0)
        {
            StatusText = "No frames to send.";
            return;
        }
        // v2.1.1 PATCH: pass rows directly to the service; the
        // service handles row.Build() (raw) or DbcEncodeService
        // (DBC) per-row. Per-row build failures (bad hex, missing
        // DBC message) count as failures but don't abort the
        // sequence.

        IsRunning = true;
        StatusText = $"Sending {Rows.Count} frame(s) × {Iterations} iteration(s) ({ModeLabel()})…";
        ProgressValue = 0;
        RefreshProgressMax();

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var mode = IsConcurrent ? SequenceSendService.Mode.Concurrent : SequenceSendService.Mode.Sequential;

        // IProgress<int> marshals to the UI thread via the captured
        // SynchronizationContext (we're already on the UI dispatcher
        // when SendAsync runs).
        var progress = new Progress<int>(v => ProgressValue = v);

        try
        {
            var result = await _service.SendAsync(Rows, mode, DelayMs, Iterations, progress, ct).ConfigureAwait(true);
            StatusText = result.FailureCount == 0
                ? $"Done. Sent {result.SentCount} / {result.SentCount + result.FailureCount} in {result.IterationsCompleted} iteration(s)."
                : $"Done with errors. Sent {result.SentCount}, failed {result.FailureCount}, iterations {result.IterationsCompleted}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled at {ProgressValue}/{ProgressMax}.";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private bool CanSend() => !IsRunning && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runCts?.Cancel();
    }

    private bool CanStop() => IsRunning;

    private string ModeLabel() => IsConcurrent ? "concurrent" : $"sequential @ {DelayMs}ms";

    /// <summary>
    /// Parses <paramref name="text"/> as a hex (0x-prefixed) or
    /// decimal ID. Used by the Id-input converter hook when the
    /// DataGrid commits the edit. Exposed internal for unit tests.
    /// </summary>
    internal static ushort ParseId(string text)
    {
        var s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (!ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            throw new FormatException($"Invalid ID: {text}");
        return id;
    }

    public void Dispose()
    {
        _progressPollTimer.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        if (_dbcService is not null)
            _dbcService.DbcLoaded -= OnDbcLoaded;
        GC.SuppressFinalize(this);
    }
}