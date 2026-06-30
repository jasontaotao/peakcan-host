namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Categorical error codes emitted by <see cref="DbcParser"/>. Currently
/// unused by any caller (no production code consumes
/// <see cref="DbcErrorCode"/>); kept for forward-compatibility when
/// sub-errors need finer classification. v1.6.7 PATCH added
/// <see cref="ErrorCode.DbcFileTooLarge"/> in the canonical
/// <see cref="ErrorCode"/> enum for the size-cap rejection path. This
/// enum's <c>FileTooLarge</c> slot remains as a duplicate of that
/// canonical code (not currently wired) for future use.
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
