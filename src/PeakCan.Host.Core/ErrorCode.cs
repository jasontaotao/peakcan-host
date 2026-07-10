namespace PeakCan.Host.Core;

/// <summary>
/// Canonical error codes used across <see cref="Result{T}"/> failures.
/// Stable integer values — wire-stable if the app ever serializes results.
/// </summary>
public enum ErrorCode
{
    /// <summary>Unclassified failure. Use when no specific code applies; do NOT use for "I forgot to set the code."</summary>
    Unknown = 0,
    /// <summary>
    /// Successful operation (no error). v3.16.9.5 PATCH: added so that
    /// status-code mappers (e.g. <c>PeakErrorMapper</c>) can return a
    /// semantically-correct code for success instead of falling through to
    /// <see cref="Unknown"/>. The default value <see cref="Unknown"/> is
    /// preserved at 0 for wire stability with older serialized results.
    /// </summary>
    Ok,
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
