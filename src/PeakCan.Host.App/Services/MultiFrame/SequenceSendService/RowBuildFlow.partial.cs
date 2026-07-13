// SequenceSendService/RowBuildFlow.partial.cs — W30 T2 (Flow B, 93 LoC)
// Row-encoding helpers: TryBuildRow dispatches on MultiFrameSequenceRow.Kind
// (Raw vs Dbc) to build a CanFrame + per-row error string; SendOneAsync
// delegates to _sendService.SendAsync with exception-isolation (catches
// non-cancellation exceptions and returns false; re-throws OperationCanceledException).
//
// Called from SendFlow.partial.cs (Flow A) SendAsync via partial-class
// visibility (sister of W22+W23+W24+W25+W26+W27+W28+W29 cross-partial
// helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: CanId(raw, FrameFormat format) 2-arg +
// CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)
// 5-arg signatures verified during verbatim re-extraction from main HEAD.
// CanId + CanFrame struct ctor verification applied (sister of W29
// SaveUnlocked struct-fabrication verification pattern).
//
// W30 T2 verbatim re-extracted via `git show main:src/.../SequenceSendService.cs | sed -n '174,266p'`
// per W20 T2 R1 fabrication LESSON (36th application).

using PeakCan.Host.App.Models;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Services.MultiFrame;

public sealed partial class SequenceSendService
{
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
