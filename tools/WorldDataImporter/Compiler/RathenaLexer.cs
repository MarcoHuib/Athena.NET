using System.Globalization;
using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Rathena;

internal sealed class RathenaLexer(string source, string sourceFile, int startLine = 1)
{
    private readonly List<CompilerDiagnostic> _diagnostics = [];
    private int _offset;
    private int _line = startLine;
    private int _column = 1;
    public IReadOnlyList<CompilerDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyList<SyntaxToken> Lex()
    {
        var tokens = new List<SyntaxToken>();
        while (true) { var token = Next(); tokens.Add(token); if (token.Kind == TokenKind.EndOfFile) return tokens; }
    }

    private SyntaxToken Next()
    {
        SkipTrivia(); var start = Position();
        if (Current == '\0') return Token(TokenKind.EndOfFile, start);
        if (char.IsDigit(Current)) { while (char.IsDigit(Current)) Advance(); var text = source[start.Offset.._offset]; return new(TokenKind.Integer, text, Span(start), long.Parse(text, CultureInfo.InvariantCulture)); }
        if (Current == '"') return String(start);
        if (IsVariableStart()) return Word(start, true);
        if (char.IsLetter(Current) || Current == '_') return Word(start, false);
        foreach (var (text, kind) in Operators)
            if (source.AsSpan(_offset).StartsWith(text, StringComparison.Ordinal)) { for (var i = 0; i < text.Length; i++) Advance(); return new(kind, text, Span(start)); }
        var bad = Current.ToString(); Advance(); var token = new SyntaxToken(TokenKind.Bad, bad, Span(start));
        _diagnostics.Add(new("RAT1001", "Error", $"Unrecognized character '{bad}'.", token.Span, bad)); return token;
    }

    private SyntaxToken Word(SourcePosition start, bool variable)
    {
        if (variable)
        {
            if (Current is '.' or '$' or '#') { var prefix = Current; Advance(); if (Current == prefix || (prefix is '.' or '$' && Current == '@')) Advance(); }
            else if (Current is '@' or '\'') Advance();
        }
        while (char.IsLetterOrDigit(Current) || Current == '_') Advance();
        if (variable && Current == '$') Advance();
        var text = source[start.Offset.._offset]; return new(variable ? TokenKind.Variable : TokenKind.Identifier, text, Span(start));
    }

    private SyntaxToken String(SourcePosition start)
    {
        Advance(); var value = new System.Text.StringBuilder(); var malformed = true;
        while (Current != '\0')
        {
            if (Current == '"') { Advance(); malformed = false; break; }
            if (Current == '\\') { Advance(); var escaped = Current; if (escaped == '\0') break; value.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => escaped }); Advance(); }
            else { value.Append(Current); Advance(); }
        }
        var token = new SyntaxToken(TokenKind.String, source[start.Offset.._offset], Span(start), StringValue: value.ToString());
        if (malformed) _diagnostics.Add(new("RAT1002", "Error", "Unterminated string literal.", token.Span, token.Text));
        return token;
    }

    private void SkipTrivia()
    {
        while (true)
        {
            while (char.IsWhiteSpace(Current)) Advance();
            if (Current == '/' && Peek(1) == '/') { while (Current is not '\0' and not '\n') Advance(); continue; }
            if (Current == '/' && Peek(1) == '*') { Advance(); Advance(); while (Current != '\0' && !(Current == '*' && Peek(1) == '/')) Advance(); if (Current != '\0') { Advance(); Advance(); } continue; }
            return;
        }
    }
    private bool IsVariableStart() => Current is '.' or '@' or '$' or '#' or '\'';
    private char Current => Peek(0);
    private char Peek(int delta) => _offset + delta < source.Length ? source[_offset + delta] : '\0';
    private void Advance() { if (Current == '\0') return; if (Current == '\n') { _line++; _column = 1; } else _column++; _offset++; }
    private SourcePosition Position() => new(sourceFile, _offset, _line, _column);
    private SourceSpan Span(SourcePosition start) => new(start, Position());
    private SyntaxToken Token(TokenKind kind, SourcePosition start) => new(kind, source[start.Offset.._offset], Span(start));
    private static readonly (string Text, TokenKind Kind)[] Operators =
    [
        ("<<",TokenKind.ShiftLeft),(">>",TokenKind.ShiftRight),("+=",TokenKind.PlusAssign),("-=",TokenKind.MinusAssign),("*=",TokenKind.StarAssign),("/=",TokenKind.SlashAssign),("%=",TokenKind.PercentAssign),("==",TokenKind.Equal),("!=",TokenKind.NotEqual),("<=",TokenKind.LessEqual),(">=",TokenKind.GreaterEqual),("&&",TokenKind.LogicalAnd),("||",TokenKind.LogicalOr),("++",TokenKind.PlusPlus),("--",TokenKind.MinusMinus),
        ("=",TokenKind.Assign),("+",TokenKind.Plus),("-",TokenKind.Minus),("*",TokenKind.Star),("/",TokenKind.Slash),("%",TokenKind.Percent),("<",TokenKind.Less),(">",TokenKind.Greater),("!",TokenKind.Bang),("?",TokenKind.Question),("&",TokenKind.BitAnd),("|",TokenKind.BitOr),("^",TokenKind.BitXor),("~",TokenKind.Tilde),("(",TokenKind.OpenParen),(")",TokenKind.CloseParen),("{",TokenKind.OpenBrace),("}",TokenKind.CloseBrace),("[",TokenKind.OpenBracket),("]",TokenKind.CloseBracket),(",",TokenKind.Comma),(";",TokenKind.Semicolon), (":",TokenKind.Colon)
    ];
}
