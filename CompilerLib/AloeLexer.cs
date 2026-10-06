using System;
using System.Collections.Generic;
using System.Text;

namespace Aloe.CompilerLib.Lexer
{
    /// <summary>
    /// トークン種別
    /// </summary>
    public enum TokenKind
    {
        // 基本
        Identifier,
        Keyword,

        // リテラル
        IntegerLiteral,
        FloatLiteral,
        DecimalLiteral,
        StringLiteral,
        CharLiteral,
        BoolLiteral,
        NullLiteral,

        // 記号・演算子
        LParen,         // (
        RParen,         // )
        LBrace,         // {
        RBrace,         // }
        LBracket,       // [
        RBracket,       // ]
        Comma,          // ,
        Dot,            // .
        Semicolon,      // ;
        Colon,          // :
        Question,       // ?
        Plus,           // +
        Minus,          // -
        Star,           // *
        Slash,          // /
        Percent,        // %
        Assign,         // =
        EqualEqual,     // ==
        Bang,           // !
        BangEqual,      // !=
        Less,           // <
        LessEqual,      // <=
        Greater,        // >
        GreaterEqual,   // >=
        Ampersand,      // &
        AmpAmp,         // &&
        Pipe,           // |
        PipePipe,       // ||
        Caret,          // ^
        ShiftLeft,      // <<
        ShiftRight,     // >>
        Tilde,          // ~

        EndOfFile
    }

    /// <summary>
    /// 1 トークン
    /// </summary>
    public sealed class AloeToken
    {
        public TokenKind Kind { get; }
        public string Lexeme { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public AloeToken(TokenKind kind, string lexeme, int position, int line, int column)
        {
            Kind = kind;
            Lexeme = lexeme;
            Position = position;
            Line = line;
            Column = column;
        }

        public override string ToString()
            => $"{Kind} \"{Lexeme}\" (line {Line}, col {Column})";
    }

    /// <summary>
    /// Aloe 言語の字句解析器（static Parse）。
    /// </summary>
    public static class AloeLexer
    {
        private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
        {
            "abstract",
            "as",
            "async",
            "bitfield",
            "break",
            "case",
            "catch",
            "class",
            "const",
            "construct",
            "continue",
            "delete",
            "do",
            "else",
            "enum",
            "extends",
            "false",
            "field",
            "finally",
            "for",
            "if",
            "import",
            "implements",
            "in",
            "interface",
            "is",
            "let",
            "main",
            "method",
            "namespace",
            "new",
            "null",
            "private",
            "protected",
            "public",
            "readonly",
            "return",
            "sealed",
            "static",
            "struct",
            "super",
            "switch",
            "swap",
            "this",
            "throw",
            "throws",
            "trait",
            "true",
            "try",
            "var",
            "void",
            "while",
            "with",
            "yield",
        };

        /// <summary>
        /// 与えられたソースコードを字句解析し、トークン列を返す。
        /// 最後に EndOfFile トークンを含む。
        /// </summary>
        public static List<AloeToken> Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var state = new LexerState(source);
            var tokens = new List<AloeToken>();

            AloeToken token;
            do
            {
                token = state.NextToken();
                tokens.Add(token);
            } while (token.Kind != TokenKind.EndOfFile);

            return tokens;
        }

        // ---------------- LexerState 本体 ----------------

        private sealed class LexerState
        {
            private readonly string _text;
            private readonly int _length;

            private int _pos;
            private int _line;
            private int _column;

            public LexerState(string text)
            {
                _text = text;
                _length = text.Length;
                _pos = 0;
                _line = 1;
                _column = 1;
            }

            public AloeToken NextToken()
            {
                SkipWhitespaceAndComments();

                if (IsAtEnd())
                {
                    // EOF トークンはここで直接生成
                    return new AloeToken(TokenKind.EndOfFile, string.Empty, _pos, _line, _column);
                }

                int startPos = _pos;
                int startLine = _line;
                int startColumn = _column;
                char c = Peek();

                // 識別子 or キーワード
                if (IsIdentifierStart(c))
                {
                    return ReadIdentifierOrKeyword(startPos, startLine, startColumn);
                }

                // 数値（負数もここでまとめて扱う: -5, -0xFF など）
                if (char.IsDigit(c) || (c == '-' && IsStartOfNumber()))
                {
                    return ReadNumber(startPos, startLine, startColumn);
                }

                // 文字列
                if (c == '"')
                {
                    return ReadStringLiteral(startPos, startLine, startColumn);
                }

                // 文字
                if (c == '\'')
                {
                    return ReadCharLiteral(startPos, startLine, startColumn);
                }

                // 記号・演算子
                return ReadSymbol(startPos, startLine, startColumn);
            }

            // ---------- 基本ヘルパ ----------

            private bool IsAtEnd() => _pos >= _length;

            private char Peek(int offset = 0)
            {
                int index = _pos + offset;
                return index < _length ? _text[index] : '\0';
            }

            private char Advance()
            {
                char c = _pos < _length ? _text[_pos] : '\0';
                _pos++;

                if (c == '\n')
                {
                    _line++;
                    _column = 1;
                }
                else
                {
                    _column++;
                }

                return c;
            }

            private void SkipWhitespaceAndComments()
            {
                while (!IsAtEnd())
                {
                    char c = Peek();

                    // 空白類
                    if (char.IsWhiteSpace(c))
                    {
                        Advance();
                        continue;
                    }

                    // コメント
                    if (c == '/')
                    {
                        char n = Peek(1);

                        // 行コメント //
                        if (n == '/')
                        {
                            Advance(); // '/'
                            Advance(); // '/'
                            while (!IsAtEnd() && Peek() != '\n')
                            {
                                Advance();
                            }
                            continue;
                        }

                        // ブロックコメント /* ... */
                        if (n == '*')
                        {
                            Advance(); // '/'
                            Advance(); // '*'
                            while (!IsAtEnd())
                            {
                                char ch = Advance();
                                if (ch == '*' && Peek() == '/')
                                {
                                    Advance(); // '/'
                                    break;
                                }
                            }
                            continue;
                        }
                    }

                    break;
                }
            }

            private AloeToken MakeToken(TokenKind kind, string lexeme, int startPos, int line, int column)
            {
                return new AloeToken(kind, lexeme, startPos, line, column);
            }

            // ---------- 識別子 / キーワード ----------

            private static bool IsIdentifierStart(char c)
            {
                return char.IsLetter(c) || c == '_';
            }

            private static bool IsIdentifierPart(char c)
            {
                return char.IsLetterOrDigit(c) || c == '_';
            }

            private AloeToken ReadIdentifierOrKeyword(int startPos, int startLine, int startColumn)
            {
                var sb = new StringBuilder();
                sb.Append(Advance()); // 先頭

                while (!IsAtEnd() && IsIdentifierPart(Peek()))
                {
                    sb.Append(Advance());
                }

                string lexeme = sb.ToString();

                // true/false/null は専用のリテラル種別
                if (lexeme == "true" || lexeme == "false")
                {
                    return MakeToken(TokenKind.BoolLiteral, lexeme, startPos, startLine, startColumn);
                }
                if (lexeme == "null")
                {
                    return MakeToken(TokenKind.NullLiteral, lexeme, startPos, startLine, startColumn);
                }

                if (_keywords.Contains(lexeme))
                {
                    return MakeToken(TokenKind.Keyword, lexeme, startPos, startLine, startColumn);
                }

                return MakeToken(TokenKind.Identifier, lexeme, startPos, startLine, startColumn);
            }

            // ---------- 数値 ----------

            /// <summary>
            /// 現在位置が '-' で、直後が数字(or 0x/0b) の場合に true。
            /// 「-5」「-0xFF」のような負のリテラルを 1 トークンとして扱うため。
            /// </summary>
            private bool IsStartOfNumber()
            {
                if (Peek() != '-') return false;

                char next = Peek(1);
                if (char.IsDigit(next)) return true;

                // -0xFF / -0b1010 など
                if (next == '0')
                {
                    char n2 = Peek(2);
                    if (n2 == 'x' || n2 == 'X' || n2 == 'b' || n2 == 'B')
                    {
                        return true;
                    }
                }

                return false;
            }

            private AloeToken ReadNumber(int startPos, int startLine, int startColumn)
            {
                var sb = new StringBuilder();

                // 符号（-のみ想定、+ は現状リテラルとして使わない）
                if (Peek() == '-')
                {
                    sb.Append(Advance());
                }

                // 16進・2進チェック
                if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X' || Peek(1) == 'b' || Peek(1) == 'B'))
                {
                    sb.Append(Advance()); // '0'
                    char baseChar = Advance();
                    sb.Append(baseChar);

                    bool isHex = baseChar == 'x' || baseChar == 'X';
                    bool isBin = baseChar == 'b' || baseChar == 'B';

                    if (isHex)
                    {
                        while (IsHexDigit(Peek()))
                        {
                            sb.Append(Advance());
                        }
                    }
                    else if (isBin)
                    {
                        while (Peek() == '0' || Peek() == '1')
                        {
                            sb.Append(Advance());
                        }
                    }

                    string lexemeHexBin = sb.ToString();
                    return MakeToken(TokenKind.IntegerLiteral, lexemeHexBin, startPos, startLine, startColumn);
                }

                // 10進数 or 小数 or decimal
                bool hasDot = false;

                // 整数部
                while (char.IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }

                // 小数点
                if (Peek() == '.' && char.IsDigit(Peek(1)))
                {
                    hasDot = true;
                    sb.Append(Advance()); // '.'
                    while (char.IsDigit(Peek()))
                    {
                        sb.Append(Advance());
                    }
                }

                // decimal サフィックス (:d / :D)
                if (Peek() == ':' && (Peek(1) == 'd' || Peek(1) == 'D'))
                {
                    sb.Append(Advance()); // ':'
                    sb.Append(Advance()); // 'd' or 'D'
                    string lexemeDec = sb.ToString();
                    return MakeToken(TokenKind.DecimalLiteral, lexemeDec, startPos, startLine, startColumn);
                }

                string lexeme = sb.ToString();
                if (hasDot)
                {
                    return MakeToken(TokenKind.FloatLiteral, lexeme, startPos, startLine, startColumn);
                }
                else
                {
                    return MakeToken(TokenKind.IntegerLiteral, lexeme, startPos, startLine, startColumn);
                }
            }

            private static bool IsHexDigit(char c)
            {
                return char.IsDigit(c)
                       || (c >= 'a' && c <= 'f')
                       || (c >= 'A' && c <= 'F');
            }

            // ---------- 文字列 / 文字 ----------

            private AloeToken ReadStringLiteral(int startPos, int startLine, int startColumn)
            {
                var sb = new StringBuilder();
                char quote = Advance(); // '"'
                sb.Append(quote);

                bool terminated = false;

                while (!IsAtEnd())
                {
                    char c = Advance();
                    sb.Append(c);

                    if (c == '\\')
                    {
                        // エスケープ: 次の 1 文字をそのまま取り込む
                        if (!IsAtEnd())
                        {
                            char esc = Advance();
                            sb.Append(esc);
                        }
                        continue;
                    }

                    if (c == quote)
                    {
                        terminated = true;
                        break;
                    }

                    if (c == '\n')
                    {
                        break;
                    }
                }

                string text = sb.ToString();
                // terminated が false でも Lexer 的にはいったん StringLiteral として返し、
                // エラー扱いはパーサ側に任せる。
                return MakeToken(TokenKind.StringLiteral, text, startPos, startLine, startColumn);
            }

            private AloeToken ReadCharLiteral(int startPos, int startLine, int startColumn)
            {
                var sb = new StringBuilder();
                char quote = Advance(); // '\''
                sb.Append(quote);

                if (IsAtEnd())
                {
                    return MakeToken(TokenKind.CharLiteral, sb.ToString(), startPos, startLine, startColumn);
                }

                char c = Advance();
                sb.Append(c);

                if (c == '\\')
                {
                    if (!IsAtEnd())
                    {
                        char esc = Advance();
                        sb.Append(esc);
                    }
                }

                if (!IsAtEnd())
                {
                    char end = Advance();
                    sb.Append(end);
                }

                string text = sb.ToString();
                return MakeToken(TokenKind.CharLiteral, text, startPos, startLine, startColumn);
            }

            // ---------- 記号 / 演算子 ----------

            private AloeToken ReadSymbol(int startPos, int startLine, int startColumn)
            {
                char c = Advance();

                switch (c)
                {
                    case '(':
                        return MakeToken(TokenKind.LParen, "(", startPos, startLine, startColumn);
                    case ')':
                        return MakeToken(TokenKind.RParen, ")", startPos, startLine, startColumn);
                    case '{':
                        return MakeToken(TokenKind.LBrace, "{", startPos, startLine, startColumn);
                    case '}':
                        return MakeToken(TokenKind.RBrace, "}", startPos, startLine, startColumn);
                    case '[':
                        return MakeToken(TokenKind.LBracket, "[", startPos, startLine, startColumn);
                    case ']':
                        return MakeToken(TokenKind.RBracket, "]", startPos, startLine, startColumn);
                    case ',':
                        return MakeToken(TokenKind.Comma, ",", startPos, startLine, startColumn);
                    case '.':
                        return MakeToken(TokenKind.Dot, ".", startPos, startLine, startColumn);
                    case ';':
                        return MakeToken(TokenKind.Semicolon, ";", startPos, startLine, startColumn);
                    case ':':
                        return MakeToken(TokenKind.Colon, ":", startPos, startLine, startColumn);
                    case '?':
                        return MakeToken(TokenKind.Question, "?", startPos, startLine, startColumn);

                    case '+':
                        return MakeToken(TokenKind.Plus, "+", startPos, startLine, startColumn);

                    case '-':
                        // ここに来るのは「IsStartOfNumber() が false の '-'」なので、二項 Minus として扱う
                        return MakeToken(TokenKind.Minus, "-", startPos, startLine, startColumn);

                    case '*':
                        return MakeToken(TokenKind.Star, "*", startPos, startLine, startColumn);

                    case '%':
                        return MakeToken(TokenKind.Percent, "%", startPos, startLine, startColumn);

                    case '/':
                        return MakeToken(TokenKind.Slash, "/", startPos, startLine, startColumn);

                    case '=':
                        if (Peek() == '=')
                        {
                            Advance();
                            return MakeToken(TokenKind.EqualEqual, "==", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Assign, "=", startPos, startLine, startColumn);

                    case '!':
                        if (Peek() == '=')
                        {
                            Advance();
                            return MakeToken(TokenKind.BangEqual, "!=", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Bang, "!", startPos, startLine, startColumn);

                    case '<':
                        if (Peek() == '=')
                        {
                            Advance();
                            return MakeToken(TokenKind.LessEqual, "<=", startPos, startLine, startColumn);
                        }
                        if (Peek() == '<')
                        {
                            Advance();
                            return MakeToken(TokenKind.ShiftLeft, "<<", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Less, "<", startPos, startLine, startColumn);

                    case '>':
                        if (Peek() == '=')
                        {
                            Advance();
                            return MakeToken(TokenKind.GreaterEqual, ">=", startPos, startLine, startColumn);
                        }
                        if (Peek() == '>')
                        {
                            Advance();
                            return MakeToken(TokenKind.ShiftRight, ">>", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Greater, ">", startPos, startLine, startColumn);

                    case '&':
                        if (Peek() == '&')
                        {
                            Advance();
                            return MakeToken(TokenKind.AmpAmp, "&&", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Ampersand, "&", startPos, startLine, startColumn);

                    case '|':
                        if (Peek() == '|')
                        {
                            Advance();
                            return MakeToken(TokenKind.PipePipe, "||", startPos, startLine, startColumn);
                        }
                        return MakeToken(TokenKind.Pipe, "|", startPos, startLine, startColumn);

                    case '^':
                        return MakeToken(TokenKind.Caret, "^", startPos, startLine, startColumn);

                    case '~':
                        return MakeToken(TokenKind.Tilde, "~", startPos, startLine, startColumn);

                    default:
                        // 不明な文字はとりあえず単一トークンとして返す（エラーは上位で扱う）
                        string text = c.ToString();
                        return MakeToken(TokenKind.Identifier, text, startPos, startLine, startColumn);
                }
            }
        }
    }
}
