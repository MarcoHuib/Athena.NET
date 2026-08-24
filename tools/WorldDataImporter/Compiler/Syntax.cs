namespace Athena.WorldCompiler.Rathena.Syntax;

internal readonly record struct SourcePosition(string File, int Offset, int Line, int Column);
internal readonly record struct SourceSpan(SourcePosition Start, SourcePosition End);

internal enum TokenKind
{
    Bad, EndOfFile, Identifier, Variable, Integer, String,
    Comma, Semicolon, Colon, Question, OpenParen, CloseParen, OpenBrace, CloseBrace, OpenBracket, CloseBracket,
    Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign,
    Plus, Minus, Star, Slash, Percent, Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
    LogicalAnd, LogicalOr, Bang, PlusPlus, MinusMinus, ShiftLeft, ShiftRight, BitAnd, BitOr, BitXor, Tilde
}

internal sealed record SyntaxToken(TokenKind Kind, string Text, SourceSpan Span, long? IntegerValue = null, string? StringValue = null);
internal sealed record CompilerDiagnostic(string Code, string Severity, string Message, SourceSpan Span, string? Construct = null);

internal abstract record SyntaxNode(SourceSpan Span);
internal sealed record CompilationUnitSyntax(IReadOnlyList<StatementSyntax> Statements, IReadOnlyList<CompilerDiagnostic> Diagnostics, SourceSpan Span) : SyntaxNode(Span);
internal abstract record StatementSyntax(SourceSpan Span) : SyntaxNode(Span);
internal sealed record BlockStatementSyntax(IReadOnlyList<StatementSyntax> Statements, SourceSpan Span) : StatementSyntax(Span);
internal sealed record ExpressionStatementSyntax(ExpressionSyntax Expression, SourceSpan Span) : StatementSyntax(Span);
internal sealed record CommandStatementSyntax(string Name, IReadOnlyList<ExpressionSyntax> Arguments, SourceSpan Span) : StatementSyntax(Span);
internal sealed record IfStatementSyntax(ExpressionSyntax Condition, StatementSyntax Then, StatementSyntax? Else, SourceSpan Span) : StatementSyntax(Span);
internal sealed record SwitchStatementSyntax(ExpressionSyntax Expression, IReadOnlyList<CaseClauseSyntax> Clauses, SourceSpan Span) : StatementSyntax(Span);
internal sealed record CaseClauseSyntax(ExpressionSyntax? Value, IReadOnlyList<StatementSyntax> Statements, SourceSpan Span) : SyntaxNode(Span);
internal sealed record WhileStatementSyntax(ExpressionSyntax Condition, StatementSyntax Body, SourceSpan Span) : StatementSyntax(Span);
internal sealed record ForStatementSyntax(ExpressionSyntax? Initializer, ExpressionSyntax? Condition, ExpressionSyntax? Increment, StatementSyntax Body, SourceSpan Span) : StatementSyntax(Span);
internal sealed record BreakStatementSyntax(SourceSpan Span) : StatementSyntax(Span);
internal sealed record ContinueStatementSyntax(SourceSpan Span) : StatementSyntax(Span);
internal sealed record ReturnStatementSyntax(ExpressionSyntax? Expression, SourceSpan Span) : StatementSyntax(Span);
internal sealed record GotoStatementSyntax(string Label, SourceSpan Span) : StatementSyntax(Span);
internal sealed record LabelStatementSyntax(string Name, bool IsEvent, SourceSpan Span) : StatementSyntax(Span);
internal sealed record EmptyStatementSyntax(SourceSpan Span) : StatementSyntax(Span);

internal abstract record ExpressionSyntax(SourceSpan Span) : SyntaxNode(Span);
internal sealed record LiteralExpressionSyntax(object Value, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record IdentifierExpressionSyntax(string Name, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record VariableExpressionSyntax(string Name, RathenaVariableScope Scope, bool IsString, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record UnaryExpressionSyntax(TokenKind Operator, ExpressionSyntax Operand, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record BinaryExpressionSyntax(ExpressionSyntax Left, TokenKind Operator, ExpressionSyntax Right, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record AssignmentExpressionSyntax(ExpressionSyntax Target, TokenKind Operator, ExpressionSyntax Value, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record CallExpressionSyntax(ExpressionSyntax Target, IReadOnlyList<ExpressionSyntax> Arguments, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record IndexExpressionSyntax(ExpressionSyntax Target, ExpressionSyntax Index, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record PostfixExpressionSyntax(ExpressionSyntax Operand, TokenKind Operator, SourceSpan Span) : ExpressionSyntax(Span);
internal sealed record ConditionalExpressionSyntax(ExpressionSyntax Condition, ExpressionSyntax WhenTrue, ExpressionSyntax WhenFalse, SourceSpan Span) : ExpressionSyntax(Span);

internal enum RathenaVariableScope { Character, CharacterTemporary, Server, ServerTemporary, Npc, Local, Instance, Account, GlobalAccount }
