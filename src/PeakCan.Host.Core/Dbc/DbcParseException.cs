namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Thrown by <see cref="DbcTokenizer"/> for unrecoverable lexical errors:
/// unexpected characters, unterminated strings, or input exceeding the
/// configured line budget. Parser-layer errors use <c>Result&lt;T&gt;</c>
/// instead — this exception is only for "garbage bytes."
/// </summary>
public sealed class DbcParseException : Exception
{
    /// <summary>1-based line number where the error was detected.</summary>
    public int Line { get; }

    /// <summary>1-based column number where the error was detected.</summary>
    public int Column { get; }

    public DbcParseException(string message, int line, int column)
        : base($"{message} at line {line}, column {column}.")
    {
        Line = line;
        Column = column;
    }
}