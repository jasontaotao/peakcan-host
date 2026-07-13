using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.MultiFrame;

/// <summary>
/// v2.1.0 MINOR: send a sequence of CAN frames in either
/// concurrent (all at once via <see cref="Task.WhenAll"/>) or
/// sequential (one after another with optional inter-frame delay)
/// mode. Iteration count repeats the whole sequence N times.
///
/// <para>
/// v2.1.1 PATCH: rows can be either <see cref="MultiFrameSequenceRow.Kind.Raw"/>
/// (manually entered ID/Data/flags) or
/// <see cref="MultiFrameSequenceRow.Kind.Dbc"/> (DBC message +
/// signal values, encoded via <see cref="DbcEncodeService"/>).
/// The service accepts <see cref="MultiFrameSequenceRow"/>s
/// directly so it can branch on kind before building the
/// <see cref="CanFrame"/>.
/// </para>
///
/// <para>
/// Cancellation: <see cref="SendAsync"/> honors the supplied
/// <see cref="CancellationToken"/> between iterations and between
/// sequential frames. Concurrent mode cancels all in-flight
/// sub-tasks on first cancel.
/// </para>
/// </summary>
public sealed partial class SequenceSendService
{
    private readonly SendService _sendService;
    private readonly DbcEncodeService? _dbcEncodeService;
    private readonly DbcService? _dbcService;

    public SequenceSendService(SendService sendService)
        : this(sendService, null, null) { }

    /// <summary>
    /// v2.1.1 PATCH: full-fidelity ctor that injects the DBC
    /// dependencies needed for DBC-kind rows. Production DI binds
    /// these; tests that only exercise raw rows pass null.
    /// </summary>
    public SequenceSendService(
        SendService sendService,
        DbcEncodeService? dbcEncodeService,
        DbcService? dbcService)
    {
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _dbcEncodeService = dbcEncodeService;
        _dbcService = dbcService;
    }

    /// <summary>Send mode: concurrent vs sequential.</summary>
    public enum Mode
    {
        /// <summary>Fire all frames in the iteration concurrently via Task.WhenAll.</summary>
        Concurrent,
        /// <summary>Send frames one after another in order, with optional DelayMs between each.</summary>
        Sequential,
    }

    /// <summary>Outcome of one sequence send operation.</summary>
    public sealed record Result(int SentCount, int FailureCount, int IterationsCompleted)
    {
        public bool AllSucceeded => FailureCount == 0;
    }

    /// <summary>
    /// Send the rows in <paramref name="rows"/> repeated
    /// <paramref name="iterations"/> times using the chosen
    /// <paramref name="mode"/>.
    /// </summary>

    /// <summary>
    /// v2.1.1 PATCH: build a <see cref="CanFrame"/> from a row,
    /// dispatching on <see cref="MultiFrameSequenceRow.Kind"/>.
    /// Returns false on any build error (bad hex, unknown DBC
    /// message, encoder exception) — caller treats as a row-level
    /// failure and continues with the rest of the sequence.
    /// </summary>
    private bool TryBuildRow(MultiFrameSequenceRow row, out CanFrame frame, out string? error)
    {
        try
        {
            if (row.RowKind == MultiFrameSequenceRow.Kind.Raw)
            {
                frame = row.Build();
                error = null;
                return true;
            }

            // DBC kind: look up the message in the currently-loaded
            // DBC document and encode the per-signal values via
            // DbcEncodeService. Any of these three steps can fail
            // (no DBC loaded, unknown message name, encode error);
            // we surface a single error string and skip the row.
            if (_dbcEncodeService is null || _dbcService is null)
            {
                frame = default;
                error = "DBC row requires DbcEncodeService + DbcService (not registered in DI).";
                return false;
            }
            var doc = _dbcService.Current;
            if (doc is null)
            {
                frame = default;
                error = "No DBC document loaded — load a .dbc file first.";
                return false;
            }
            // DbcDocument doesn't expose a name-based lookup; linear scan is
            // fine for typical DBC sizes (≤ few hundred messages) and
            // avoids a Core-layer API addition for a v2.1.1 PATCH.
            Message? msg = null;
            foreach (var m in doc.Messages)
            {
                if (string.Equals(m.Name, row.DbcMessageName, StringComparison.Ordinal))
                {
                    msg = m;
                    break;
                }
            }
            if (msg is null)
            {
                frame = default;
                error = $"DBC message '{row.DbcMessageName}' not found in loaded document.";
                return false;
            }
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var sv in row.DbcSignalValues)
            {
                if (sv.Value.HasValue && !string.IsNullOrEmpty(sv.Name))
                    values[sv.Name] = sv.Value.Value;
            }
            var payload = _dbcEncodeService.Encode(msg, values);

            // DBC messages use the PEAK convention: bit 31 set ⇒
            // Extended (29-bit ID), clear ⇒ Standard (11-bit ID).
            // Mirrors DbcSendViewModel.SendAsync logic.
            var id = msg.Id;
            var isExtended = (id & 0x80000000u) != 0u;
            var raw = isExtended ? (id & 0x1FFFFFFFu) : (id & 0x7FFu);
            var canId = new CanId(raw, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
            // DBC encoding always returns exactly Dlc bytes — no
            // flags beyond what the DBC specifies (no FD bit, no
            // BRS in this code path; future work).
            frame = new CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            frame = default;
            error = ex.Message;
            return false;
        }
    }

    private async Task<bool> SendOneAsync(CanFrame frame, CancellationToken ct)
    {
        try
        {
            var r = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            return r.IsSuccess;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}