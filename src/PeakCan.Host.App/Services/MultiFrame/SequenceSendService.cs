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
}