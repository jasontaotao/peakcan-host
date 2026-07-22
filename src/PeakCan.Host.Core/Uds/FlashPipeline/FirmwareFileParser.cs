namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// A parsed, in-memory firmware image ready for streaming to the ECU via
/// RequestDownload (0x34) + TransferData (0x36) + RequestTransferExit (0x37).
/// Owns only the payload bytes and total length; the destination memory
/// address is supplied separately by <c>FlashProfile.MemoryAddress</c>,
/// keeping addressing and data orthogonal (per Phase 1 scope decision
/// 2026-07-22 — raw-binary format only, address in profile).
/// </summary>
public sealed record FirmwareImage
{
    /// <summary>
    /// The firmware payload, as a defensive copy independent of any caller buffer.
    /// PipelineExecutor slices this into <c>TransferDataAsync</c> chunks sized by the
    /// ECU-reported block length (RequestDownloadAsync return value, TransferFlow.cs).
    /// </summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// Total payload length in bytes. Fed to <c>RequestDownloadAsync(address, length, ct)</c>
    /// as the <c>length</c> argument. Always equals <see cref="Data"/>.Length.
    /// </summary>
    public required uint Length { get; init; }
}

/// <summary>
/// Parses a firmware file into a <see cref="FirmwareImage"/>. Phase 1 supports
/// raw binary only — the file's bytes ARE the flash data payload. Intel HEX and
/// Motorola S-record formats arrive in Phase 1.1 via a format-detecting
/// overload; this class keeps the raw entry point as the stable surface.
/// </summary>
public static class FirmwareFileParser
{
    /// <summary>
    /// Parse a raw-binary firmware payload into a <see cref="FirmwareImage"/>.
    /// The returned <see cref="FirmwareImage.Data"/> is a defensive copy — mutating
    /// the caller's array afterwards does not affect the image.
    /// </summary>
    /// <param name="bytes">The raw firmware bytes. Must not be null or empty.</param>
    /// <returns>A <see cref="FirmwareImage"/> holding a defensive copy and the total length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is empty.</exception>
    public static FirmwareImage Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            // A zero-length firmware is never legitimate — RequestDownload(addr, 0)
            // would make the ECU enter a TransferData loop with zero work, and some
            // ECU implementations NRC an empty download outright. Refuse early.
            throw new ArgumentException(
                "Firmware payload is empty — a zero-length image cannot be downloaded.", nameof(bytes));
        }

        // Defensive copy so the ECU-bound payload cannot be silently mutated by the
        // caller reusing its source buffer mid-flash (which would corrupt TransferData chunks).
        var copy = new byte[bytes.Length];
        Array.Copy(bytes, copy, bytes.Length);

        return new FirmwareImage
        {
            Data = copy,
            Length = (uint)bytes.Length,
        };
    }
}
