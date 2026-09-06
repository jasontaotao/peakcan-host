namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v1.2.11 PATCH: composite key used by
/// <see cref="TraceViewModel.PendingDecode"/> to disambiguate concurrent frames.
/// Two frames share a key only if their (Id, Timestamp, Channel) match exactly.
/// <para>
/// The worker in <c>DbcDecodeBackgroundService</c> looks up pending entries by
/// this key after looking the frame up in the DBC. 2026-09-05 P1-3：同 key
/// 的多帧不再互相覆盖——key 下挂 FIFO pending 队列，逐帧完成 decode
/// （此前第二帧覆盖第一帧，第一帧 Decoded 永不填充）。
/// </para>
/// </summary>
public readonly record struct TraceEntryKey(
    uint IdRaw,
    ulong TimestampMicroseconds,
    ushort ChannelHandle);