using System.Collections.Generic;
using System.Windows;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewModel
{
    /// <summary>
    /// v1.2.11: test-only helper to inject a pending entry directly,
    /// bypassing <see cref="AppendBatchAsync"/>'s dispatcher hop. Used by
    /// <c>DbcDecodeBackgroundServiceTests</c> which run on the xunit MTA
    /// threadpool with no WPF Application.
    /// </summary>
    internal void RegisterForTesting(TraceEntryKey key, TraceEntry entry)
        => _pendingDecode[key] = entry;

    /// <summary>
    /// v1.2.11 PATCH review fix: atomic check-and-remove. The worker calls
    /// this after successfully filling <see cref="TraceEntry.Decoded"/> so
    /// the entry stops occupying the pending map. Returning false means
    /// another worker (or a Clear()) already removed it; the caller should
    /// not double-write Decoded in that case.
    /// </summary>
    internal bool TryCompletePending(TraceEntryKey key, out TraceEntry? entry)
        => _pendingDecode.TryRemove(key, out entry);

    /// <summary>
    /// Append a batch of frames to <see cref="Entries"/>, then trim to
    /// <see cref="MaxRows"/>. Marshals to the WPF UI thread via
    /// <c>Application.Current.Dispatcher</c>.
    /// </summary>
    public Task AppendBatchAsync(IReadOnlyList<CanFrame> batch)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            foreach (var f in batch)
            {
                // Track per-message-ID counts (before filtering).
                TotalFrameCount++;
                _messageCounts[f.Id.Raw] = _messageCounts.GetValueOrDefault(f.Id.Raw) + 1;

                // v0.9.2: pause still tracks counts but skips display.
                if (IsPaused) continue;

                // v0.6.0/v0.9.2/Task 7: composite display filter (hex prefix +
                // error-only + channel). Extracted to PassesFilters for unit
                // testing on MTA (the AppendBatchAsync dispatcher hop makes
                // the inline path STA-only).
                if (!PassesFilters(f))
                {
                    FilteredCount++;
                    continue;
                }
                Entries.Add(new TraceEntry
                {
                    Timestamp = f.Timestamp,
                    Channel = f.Channel,
                    Id = f.Id,
                    Dlc = f.Dlc,
                    // Insert a single space between every 2-char hex byte so
                    // "DEADBEEF" reads as "DE AD BE EF". The DataGrid column
                    // is wide enough for canonical 8-byte classic frames plus
                    // 1-2 trailing padding; FD frames are 16-64 bytes so the
                    // row height is fixed (RowHeight=20) and the user can
                    // horizontally scroll.
                    DataHex = FormatHexWithSpaces(f.Data.Span),
                    IsError = f.IsError,
                    IsFd = f.IsFd,
                    IsRtr = (f.Flags & FrameFlags.Rtr) != 0,
                });
                // v1.2.11: register the just-appended entry so DbcDecodeBackgroundService
                // can fill Decoded when it looks up the same CanFrame in DBC.
                var pendingKey = new TraceEntryKey(
                    f.Id.Raw,
                    f.Timestamp.TotalMicroseconds,
                    f.Channel.Handle);
                _pendingDecode[pendingKey] = Entries[^1];
            }
            while (Entries.Count > MaxRows) Entries.RemoveAt(0);
        }).Task;
    }

    /// <summary>
    /// Task 7 (phase 2 A-5): composite display filter — hex-prefix (FilterText)
    /// + error-only (ShowErrorsOnly) + channel (ChannelFilter). Returns true
    /// when the frame should be shown. Extracted from the inline AppendBatchAsync
    /// loop so it is unit-testable on MTA (the dispatcher hop makes the inline
    /// path STA-only). Zero regression: all three filters default to "show all".
    /// </summary>
    internal bool PassesFilters(CanFrame f)
    {
        // v0.6.0: hex-prefix filter.
        if (FilterText.Length > 0)
        {
            var idHex = f.Id.Raw.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            if (!idHex.StartsWith(FilterText, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        // v0.9.2: error-only filter.
        if (ShowErrorsOnly && !f.IsError)
            return false;
        // Task 7: channel filter. null = show all (zero regression).
        if (ChannelFilter is { } cf && f.Channel != cf)
            return false;
        return true;
    }
}
