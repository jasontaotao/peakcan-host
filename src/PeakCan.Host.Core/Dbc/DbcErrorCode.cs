namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Categorical error codes emitted by <see cref="DbcParser"/>. Currently
/// unused by the parser — parser failures flow through the shared
/// <c>Result&lt;T&gt;</c> with <see cref="ErrorCode.ParseFailure"/>. Kept
/// for forward-compatibility when sub-errors need finer classification.
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
