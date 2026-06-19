using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Backing view model for the Trace tab. Owns an
/// <see cref="ObservableCollection{TraceEntry}"/> that the WPF
/// <c>DataGrid</c> in <c>Views/TraceView.xaml</c> binds to.
/// <para>
/// <b>Dispatcher contract:</b> the WPF UI thread is the only thread that
/// may mutate an <see cref="ObservableCollection{T}"/> that's already
/// bound to a <c>ItemsControl</c>. <see cref="AppendBatchAsync"/> is
/// called from the <see cref="Services.TraceService"/> background loop,
/// so it must marshal back to the UI thread via
/// <c>Application.Current.Dispatcher</c>. The contract is:
/// </para>
/// <list type="bullet">
///   <item>In production, <c>Application.Current</c> is always non-null
///     (the WPF app owns the singleton), so the dispatcher is always
///     available and the batch is appended on the UI thread.</item>
///   <item>In test contexts, <c>Application.Current</c> is null (xunit
///     has no <c>Application</c> instance). The method then returns
///     <see cref="Task.CompletedTask"/> without throwing or modifying
///     <see cref="Entries"/>. This is documented and pinned by
///     <c>TraceViewModelTests.AppendBatch_With_Null_Dispatcher_*</c>.</item>
/// </list>
/// <para>
/// <b>Why a parameterless constructor?</b> <c>AppHostBuilder</c> registers
/// this VM as a singleton via <c>AddSingleton&lt;TraceViewModel&gt;()</c>;
/// a parameterless ctor avoids a DI circular-reference (the
/// <see cref="Services.TraceService"/> depends on the VM and the VM is
/// resolved before the service starts).
/// </para>
/// </summary>
public sealed partial class TraceViewModel : ObservableObject
{
    /// <summary>
    /// Backing store of trace rows. Mutated only on the WPF UI thread via
    /// <see cref="AppendBatchAsync"/>; reads from any thread are safe
    /// because the DataGrid marshals binding reads to the UI thread.
    /// </summary>
    public ObservableCollection<TraceEntry> Entries { get; } = new();

    /// <summary>
    /// FIFO trim threshold. When <see cref="Entries"/>.Count exceeds this
    /// value after a batch is appended, the oldest rows are removed
    /// (from index 0) until the count is back at the cap. Default 10_000
    /// matches the bounded channel depth so memory pressure is bounded
    /// under sustained bus load.
    /// </summary>
    [ObservableProperty]
    private int _maxRows = 10_000;

    /// <summary>
    /// Append a batch of frames to <see cref="Entries"/>, then trim to
    /// <see cref="MaxRows"/>. Marshals to the WPF UI thread via
    /// <c>Application.Current.Dispatcher</c>.
    /// <para>
    /// <b>Test-context behaviour:</b> when <c>Application.Current</c> is
    /// null (no WPF <c>Application</c> has been created — e.g. in xunit
    /// test runs), the method returns <see cref="Task.CompletedTask"/>
    /// silently without modifying <see cref="Entries"/>. This is a
    /// pragmatic MVP shortcut: spinning up a <c>Application</c> for
    /// tests is heavy, and the trace rows are not testable from a
    /// non-UI process anyway. The WPF path is exercised by the live
    /// AppHostBuilder smoke run (Task 13 Step 6).
    /// </para>
    /// <para>
    /// <b>Why <c>IReadOnlyList</c> and not <c>IEnumerable</c>?</c> the
    /// caller (<see cref="Services.TraceService"/>) already holds a
    /// <c>List&lt;CanFrame&gt;</c>; taking <c>IReadOnlyList</c> avoids a
    /// re-enumeration and documents the contract.
    /// </para>
    /// </summary>
    public Task AppendBatchAsync(IReadOnlyList<CanFrame> batch)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            foreach (var f in batch)
            {
                Entries.Add(new TraceEntry
                {
                    Timestamp = f.Timestamp,
                    Channel = f.Channel,
                    Id = f.Id,
                    Dlc = f.Dlc,
                    DataHex = Convert.ToHexString(f.Data.Span),
                    IsError = f.IsError,
                    IsFd = f.IsFd,
                });
            }
            while (Entries.Count > MaxRows) Entries.RemoveAt(0);
        }).Task;
    }
}
