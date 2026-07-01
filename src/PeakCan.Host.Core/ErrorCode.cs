namespace PeakCan.Host.Core;

/// <summary>
/// Canonical error codes used across <see cref="Result{T}"/> failures.
/// Stable integer values — wire-stable if the app ever serializes results.
/// </summary>
public enum ErrorCode
{
    /// <summary>Unclassified failure. Use when no specific code applies; do NOT use for "I forgot to set the code."</summary>
    Unknown = 0,
    InvalidArgument,
    InvalidState,
    IoError,
    NotFound,
    ParseFailure,
    HardwareNotAvailable,
    HardwareBusy,
    HardwareParameter,
    Cancelled,
    /// <summary>DBC file exceeds the configured MaxFileSizeBytes cap (v1.6.7 PATCH Item 1).</summary>
    DbcFileTooLarge,
}
