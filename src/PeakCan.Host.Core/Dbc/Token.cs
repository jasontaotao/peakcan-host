namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Single token emitted by <see cref="DbcTokenizer"/>.
/// <para>
/// <c>Line</c> and <c>Column</c> are 1-based and point at the first character
/// of <c>Lexeme</c> in the source text. They are intended for diagnostic
/// messages and stable enough to round-trip through error reporting.
/// </para>
/// </summary>
/// <param name="Type">Token kind.</param>
/// <param name="Lexeme">Raw source text for this token. Empty for <see cref="TokenType.Eof"/>.</param>
/// <param name="Line">1-based line number of the first character.</param>
/// <param name="Column">1-based column number of the first character.</param>
public readonly record struct Token(TokenType Type, string Lexeme, int Line, int Column);