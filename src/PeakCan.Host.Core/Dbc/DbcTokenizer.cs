namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Single-pass lexical scanner for DBC files. Produces a flat
/// <see cref="IReadOnlyList{Token}"/> terminated by <see cref="TokenType.Eof"/>.
/// <para>
/// Skips whitespace and <c>//</c> line comments. Recognizes all DBC section
/// keywords listed in <see cref="TokenType.Keyword_BO_"/> through
/// <see cref="TokenType.Keyword_NS_DESC_"/>. Unrecognized characters and
/// unterminated string literals throw <see cref="DbcParseException"/>.
/// </para>
/// <para>
/// Threading: instances are stateless and safe to reuse concurrently.
/// </para>
/// </summary>
public sealed class DbcTokenizer
{
    /// <summary>Default safety cap on input line count to prevent DoS via huge files.</summary>
    public const int DefaultMaxLine = 1_000_000;

    private static readonly Dictionary<string, TokenType> Keywords = new()
    {
        ["BO_"] = TokenType.Keyword_BO_,
        ["SG_"] = TokenType.Keyword_SG_,
        ["BU_"] = TokenType.Keyword_BU_,
        ["VAL_"] = TokenType.Keyword_VAL_,
        ["VAL_TABLE_"] = TokenType.Keyword_VAL_TABLE_,
        ["EV_"] = TokenType.Keyword_EV_,
        ["CM_"] = TokenType.Keyword_CM_,
        ["BA_DEF_"] = TokenType.Keyword_BA_DEF_,
        ["BA_"] = TokenType.Keyword_BA_,
        ["SIG_GROUP_"] = TokenType.Keyword_SIG_GROUP_,
        ["VERSION"] = TokenType.Keyword_VERSION,
        ["NS_"] = TokenType.Keyword_NS_,
        ["BS_"] = TokenType.Keyword_BS_,
        ["NS_DESC_"] = TokenType.Keyword_NS_DESC_,
    };

    /// <summary>
    /// Tokenize <paramref name="text"/> into a flat list. The list always ends
    /// with a synthetic <see cref="TokenType.Eof"/> token.
    /// </summary>
    /// <param name="text">Raw DBC source.</param>
    /// <param name="maxLine">Safety cap; throws if the input has more lines than this.</param>
    public IReadOnlyList<Token> Tokenize(string text, int maxLine = DefaultMaxLine)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<Token>(text.Length / 8);
        int line = 1;
        int col = 1;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // Line endings: LF advances line+col, CR is swallowed (handles CRLF).
            if (c == '\n')
            {
                line++;
                col = 1;
                i++;
                continue;
            }
            if (c == '\r')
            {
                i++;
                continue;
            }

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                col++;
                continue;
            }

            // // line comment: skip to next LF (or EOF), do not emit a token.
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            // String literal: capture between matching quotes, no escapes (per DBC grammar).
            if (c == '"')
            {
                int startCol = col + 1;
                i++;
                col++;
                int start = i;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\n')
                    {
                        throw new DbcParseException("Unterminated string literal", line, startCol);
                    }
                    i++;
                    col++;
                }
                if (i >= text.Length)
                {
                    throw new DbcParseException("Unterminated string literal", line, startCol);
                }
                tokens.Add(new Token(TokenType.String, text[start..i], line, startCol));
                i++; // past closing quote
                col++;
                continue;
            }

            // Number: optional leading '-', digits, optional fractional part.
            // Scientific notation (1.0e3) is recognized as a Float by spotting 'e' / 'E'.
            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i;
                int startCol = col;
                if (text[i] == '-')
                {
                    i++;
                    col++;
                }
                bool isFloat = false;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == 'e' || text[i] == 'E' || text[i] == '+' || text[i] == '-'))
                {
                    char nc = text[i];
                    if (nc == '.')
                    {
                        isFloat = true;
                    }
                    else if (nc == 'e' || nc == 'E')
                    {
                        isFloat = true;
                        i++;
                        col++;
                        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
                        {
                            i++;
                            col++;
                        }
                        while (i < text.Length && char.IsDigit(text[i]))
                        {
                            i++;
                            col++;
                        }
                        break;
                    }
                    i++;
                    col++;
                }
                tokens.Add(new Token(
                    isFloat ? TokenType.Float : TokenType.Integer,
                    text[start..i], line, startCol));
                continue;
            }

            // Identifier / keyword: starts with letter or underscore, continues with letters/digits/underscores.
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                int startCol = col;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                    col++;
                }
                var lex = text[start..i];
                var type = Keywords.TryGetValue(lex, out var kt) ? kt : TokenType.Identifier;
                tokens.Add(new Token(type, lex, line, startCol));
                continue;
            }

            // Punctuation (single character).
            TokenType punc = c switch
            {
                ':' => TokenType.Colon,
                ',' => TokenType.Comma,
                ';' => TokenType.Semicolon,
                '(' => TokenType.LParen,
                ')' => TokenType.RParen,
                '[' => TokenType.LBracket,
                ']' => TokenType.RBracket,
                '+' => TokenType.Plus,
                '-' => TokenType.Minus,
                '@' => TokenType.At,
                '|' => TokenType.Pipe,
                _ => throw new DbcParseException($"Unexpected character '{c}'", line, col),
            };
            tokens.Add(new Token(punc, c.ToString(), line, col));
            i++;
            col++;
        }

        if (line > maxLine)
        {
            throw new DbcParseException($"Input exceeds {maxLine} lines", line, col);
        }

        tokens.Add(new Token(TokenType.Eof, string.Empty, line, col));
        return tokens;
    }
}