using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.Services.Sequence;
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
    private readonly SequenceLibrary? _library;
    private CancellationTokenSource? _runCts;
    private readonly DispatcherTimer _progressPollTimer;

    /// <summary>One row per CAN frame definition.</summary>
    public ObservableCollection<MultiFrameSequenceRow> Rows { get; } = new();

    /// <summary>v2.1.1 PATCH: messages from the loaded DBC document,
    /// for the DBC-row message picker. Empty when no DBC loaded.</summary>
    public ObservableCollection<Message> AvailableDbcMessages { get; } = new();

    /// <summary>v2.1.2 PATCH: saved sequences from the library,
    /// for the Load Sequence picker.</summary>
    public ObservableCollection<SequenceLibrary.SavedSequence> SavedSequences { get; } = new();

    [ObservableProperty] private string _saveNameText = "";
    [ObservableProperty] private SequenceLibrary.SavedSequence? _selectedSavedSequence;

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
        : this(service, null, null) { }

    /// <summary>
    /// v2.1.1 PATCH: full-fidelity ctor that wires the
    /// <see cref="DbcService"/> for the DBC-row message picker and
    /// subscribes to <see cref="DbcService.DbcLoaded"/> so a DBC
    /// loaded AFTER the window opens still shows up in the picker.
    /// Tests that don't need DBC rows pass null.
    /// </summary>
    public MultiFrameSendViewModel(SequenceSendService service, DbcService? dbcService)
        : this(service, dbcService, null) { }

    /// <summary>
    /// v2.1.2 PATCH: full-fidelity ctor with <see cref="SequenceLibrary"/>
    /// for Save / Load / Delete of named sequences.
    /// </summary>
    public MultiFrameSendViewModel(
        SequenceSendService service,
        DbcService? dbcService,
        SequenceLibrary? library)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dbcService = dbcService;
        _library = library;
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

        // v2.1.2 PATCH: populate SavedSequences from the library.
        if (_library is not null)
        {
            foreach (var s in _library.Load())
                SavedSequences.Add(s);
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

    // ===== v2.1.2 PATCH: Save / Load / Delete sequence =====

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void SaveCurrent()
    {
        if (_library is null) { StatusText = "Sequence library unavailable"; return; }
        var name = (SaveNameText ?? "").Trim();
        if (string.IsNullOrEmpty(name)) { StatusText = "Sequence name is required"; return; }
        var saved = BuildSavedSequence(name);
        try
        {
            var count = _library.Add(saved);
            // Refresh the picker — last-wins on duplicate name, so
            // always update the in-memory list.
            ReplaceOrAddInPicker(saved);
            StatusText = $"Saved '{name}' ({count} sequence(s) in library).";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Save '{name}': {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void LoadSaved(SequenceLibrary.SavedSequence? saved)
    {
        if (saved is null || _library is null) return;
        try
        {
            IsConcurrent = saved.Mode == SequenceLibrary.Mode.Concurrent;
            DelayMs = saved.DelayMs;
            Iterations = saved.Iterations;
            Rows.Clear();
            foreach (var sr in saved.Rows)
            {
                var row = MaterializeRow(sr);
                Rows.Add(row);
            }
            SelectedRow = Rows.Count > 0 ? Rows[0] : null;
            StatusText = $"Loaded '{saved.Name}' ({saved.Rows.Count} row(s)).";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Load '{saved?.Name}': {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void DeleteSaved(SequenceLibrary.SavedSequence? saved)
    {
        if (saved is null || _library is null) return;
        try
        {
            if (_library.Remove(saved.Name))
            {
                SavedSequences.Remove(saved);
                StatusText = $"Removed '{saved.Name}'.";
            }
            else
            {
                StatusText = $"'{saved.Name}' not found in library.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Delete '{saved.Name}': {ex.Message}";
        }
    }

    private void ReplaceOrAddInPicker(SequenceLibrary.SavedSequence saved)
    {
        for (var i = 0; i < SavedSequences.Count; i++)
        {
            if (SavedSequences[i].Name == saved.Name)
            {
                SavedSequences[i] = saved;
                return;
            }
        }
        SavedSequences.Add(saved);
    }

    /// <summary>Snapshot the current Rows + mode into a SavedSequence record.</summary>
    private SequenceLibrary.SavedSequence BuildSavedSequence(string name) =>
        new(
            Name: name,
            Mode: IsConcurrent ? SequenceLibrary.Mode.Concurrent : SequenceLibrary.Mode.Sequential,
            DelayMs: DelayMs,
            Iterations: Iterations,
            Rows: Rows.Select(SnapshotRow).ToList(),
            SavedAt: DateTimeOffset.UtcNow);

    private static SequenceLibrary.SavedRow SnapshotRow(MultiFrameSequenceRow row) =>
        new()
        {
            Kind = row.RowKind == MultiFrameSequenceRow.Kind.Dbc
                ? SequenceLibrary.RowKind.Dbc
                : SequenceLibrary.RowKind.Raw,
            Id = row.Id,
            DataHex = row.DataHex,
            IsExtended = row.IsExtended,
            IsFd = row.IsFd,
            IsRtr = row.IsRtr,
            IsBitRateSwitch = row.IsBitRateSwitch,
            IsErrorStateIndicator = row.IsErrorStateIndicator,
            DbcMessageName = row.DbcMessageName,
            DbcSignalValues = row.DbcSignalValues.Select(s => new SequenceLibrary.SavedSignalValue
            {
                Name = s.Name,
                Value = s.Value,
            }).ToList(),
        };

    private static MultiFrameSequenceRow MaterializeRow(SequenceLibrary.SavedRow sr)
    {
        var row = new MultiFrameSequenceRow
        {
            RowKind = sr.Kind == SequenceLibrary.RowKind.Dbc
                ? MultiFrameSequenceRow.Kind.Dbc
                : MultiFrameSequenceRow.Kind.Raw,
            Id = sr.Id,
            DataHex = sr.DataHex,
            IsExtended = sr.IsExtended,
            IsFd = sr.IsFd,
            IsRtr = sr.IsRtr,
            IsBitRateSwitch = sr.IsBitRateSwitch,
            IsErrorStateIndicator = sr.IsErrorStateIndicator,
            DbcMessageName = sr.DbcMessageName,
        };
        foreach (var sv in sr.DbcSignalValues)
        {
            row.DbcSignalValues.Add(new DbcSignalValue { Name = sv.Name, Value = sv.Value });
        }
        return row;
    }

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