namespace PeakCan.Host.Core;

/// <summary>
/// Canonical error codes used across <see cref="Result{T}"/> failures.
/// Stable integer values — wire-stable if the app ever serializes results.
/// </summary>
public enum ErrorCode
{
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
}