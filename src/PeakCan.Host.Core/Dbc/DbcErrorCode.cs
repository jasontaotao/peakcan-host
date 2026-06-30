namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Categorical error codes emitted by <see cref="DbcParser"/>. Kept for
/// forward-compatibility when sub-errors need finer classification.
/// v1.6.7 PATCH added <c>ErrorCode.DbcFileTooLarge</c> in the canonical
/// <c>ErrorCode</c> enum for the size-cap rejection path; this enum's
/// <c>FileTooLarge</c> slot remains as a forward-compat duplicate for
/// callers that prefer the categorical DBC type.
/// </summary>
public enum DbcErrorCode
{
    Unknown,
    UnexpectedToken,
    MissingSemicolon,
    InvalidId,
    InvalidDlc,
    InvalidSignalSpec,
    DuplicateMessage,
    DuplicateSignal,
    FileTooLarge,
}
