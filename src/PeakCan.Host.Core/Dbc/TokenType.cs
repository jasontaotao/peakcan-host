namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Kinds of tokens produced by <see cref="DbcTokenizer"/>.
/// <para>
/// Keyword_* variants are the leading DBC section markers; the actual
/// identifier text is preserved in <c>Token.Lexeme</c>.
/// </para>
/// </summary>
public enum TokenType
{
    /// <summary>Synthetic end-of-input token (always last).</summary>
    Eof,

    // Lexical categories
    Identifier,
    Integer,
    Float,
    String,

    // Punctuation
    Colon,
    Comma,
    Semicolon,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Plus,
    Minus,
    At,
    Pipe,

    // DBC section keywords (lexeme preserved for diagnostic messages)
    Keyword_BO_,
    Keyword_SG_,
    Keyword_BU_,
    Keyword_VAL_,
    Keyword_VAL_TABLE_,
    Keyword_EV_,
    Keyword_CM_,
    Keyword_BA_DEF_,
    Keyword_BA_,
    Keyword_SIG_GROUP_,
    Keyword_VERSION,
    Keyword_NS_,
    Keyword_BS_,
    Keyword_NS_DESC_,
}