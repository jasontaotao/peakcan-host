namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v1.2.11 PATCH: composite key used by
/// <see cref="TraceViewModel.PendingDecode"/> to disambiguate concurrent frames.
/// Two frames share a key only if their (Id, Timestamp, Channel) match exactly.
/// <para>
/// The worker in <c>DbcDecodeBackgroundService</c> looks up pending entries by
/// this key after looking the frame up in the DBC; collisions are vanishingly
/// unlikely at &lt; 10 k fps but possible if the bus delivers two frames with
/// identical (id, channel) in the same microsecond — in that case the second
/// frame's Decoded would overwrite the first. This is acceptable for the PATCH;
/// a finer-grained key (sequence number) can be added later if observed.
/// </para>
/// </summary>
public readonly record struct TraceEntryKey(
    uint IdRaw,
    ulong TimestampMicroseconds,
    ushort ChannelHandle);