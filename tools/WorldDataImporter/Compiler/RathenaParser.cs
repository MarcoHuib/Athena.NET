using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Rathena;

internal sealed class RathenaParser
{
    private readonly IReadOnlyList<SyntaxToken> _tokens;
    private readonly List<CompilerDiagnostic> _diagnostics;
    private int _index;
    public RathenaParser(string source, string file, int startLine = 1) { var lexer = new RathenaLexer(source, file, startLine); _tokens = lexer.Lex(); _diagnostics = [.. lexer.Diagnostics]; }

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var statements = new List<StatementSyntax>(); var start = Current.Span.Start;
        while (Current.Kind != TokenKind.EndOfFile) { var before = _index; statements.Add(ParseStatement()); if (_index == before) Next(); }
        return new(statements, _diagnostics, new(start, Current.Span.End));
    }

    private StatementSyntax ParseStatement()
    {
        if (Current.Kind == TokenKind.Semicolon) { var token = Next(); return new EmptyStatementSyntax(token.Span); }
        if (Current.Kind == TokenKind.OpenBrace) return ParseBlock();
        if (Is("if")) return ParseIf();
        if (Is("switch")) return ParseSwitch();
        if (Is("while")) return ParseWhile();
        if (Is("for")) return ParseFor();
        if (Is("break")) return SimpleKeyword<BreakStatementSyntax>(span => new(span));
        if (Is("continue")) return SimpleKeyword<ContinueStatementSyntax>(span => new(span));
        if (Is("return")) return ParseReturn();
        if (Is("goto")) return ParseGoto();
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon) { var name = Next(); var colon = Next(); return new LabelStatementSyntax(name.Text, name.Text.StartsWith("On", StringComparison.Ordinal), new(name.Span.Start, colon.Span.End)); }
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind is not TokenKind.OpenParen && !IsExpressionOperator(Peek(1).Kind)) return ParseCommand();
        var expression = ParseExpression(); var end = Match(TokenKind.Semicolon); return new ExpressionStatementSyntax(expression, new(expression.Span.Start, end.Span.End));
    }

    private BlockStatementSyntax ParseBlock()
    {
        var open = Match(TokenKind.OpenBrace); var items = new List<StatementSyntax>();
        while (Current.Kind is not TokenKind.CloseBrace and not TokenKind.EndOfFile) items.Add(ParseStatement());
        var close = Match(TokenKind.CloseBrace); return new(items, new(open.Span.Start, close.Span.End));
    }
    private IfStatementSyntax ParseIf() { var start = Next(); Match(TokenKind.OpenParen); var condition = ParseExpression(); Match(TokenKind.CloseParen); var then = ParseStatement(); StatementSyntax? other = null; if (Is("else")) { Next(); other = ParseStatement(); } return new(condition, then, other, new(start.Span.Start, (other ?? then).Span.End)); }
    private WhileStatementSyntax ParseWhile() { var start = Next(); Match(TokenKind.OpenParen); var condition = ParseExpression(); Match(TokenKind.CloseParen); var body = ParseStatement(); return new(condition, body, new(start.Span.Start, body.Span.End)); }
    private ForStatementSyntax ParseFor() { var start = Next(); Match(TokenKind.OpenParen); var init = OptionalExpression(TokenKind.Semicolon); Match(TokenKind.Semicolon); var condition = OptionalExpression(TokenKind.Semicolon); Match(TokenKind.Semicolon); var increment = OptionalExpression(TokenKind.CloseParen); Match(TokenKind.CloseParen); var body = ParseStatement(); return new(init, condition, increment, body, new(start.Span.Start, body.Span.End)); }
    private SwitchStatementSyntax ParseSwitch()
    {
        var start = Next(); Match(TokenKind.OpenParen); var value = ParseExpression(); Match(TokenKind.CloseParen); Match(TokenKind.OpenBrace); var clauses = new List<CaseClauseSyntax>();
        while (Current.Kind is not TokenKind.CloseBrace and not TokenKind.EndOfFile)
        {
            var clauseStart = Current.Span.Start; ExpressionSyntax? caseValue;
            if (Is("case")) { Next(); caseValue = ParseExpression(); } else if (Is("default")) { Next(); caseValue = null; } else { Error("RAT2004", "Expected 'case' or 'default'.", Current); Next(); continue; }
            Match(TokenKind.Colon); var statements = new List<StatementSyntax>(); while (!Is("case") && !Is("default") && Current.Kind is not TokenKind.CloseBrace and not TokenKind.EndOfFile) statements.Add(ParseStatement());
            var end = statements.Count == 0 ? Previous.Span.End : statements[^1].Span.End; clauses.Add(new(caseValue, statements, new(clauseStart, end)));
        }
        var close = Match(TokenKind.CloseBrace); return new(value, clauses, new(start.Span.Start, close.Span.End));
    }
    private ReturnStatementSyntax ParseReturn() { var start = Next(); var expression = Current.Kind == TokenKind.Semicolon ? null : ParseExpression(); var end = Match(TokenKind.Semicolon); return new(expression, new(start.Span.Start, end.Span.End)); }
    private GotoStatementSyntax ParseGoto() { var start = Next(); var label = Match(TokenKind.Identifier); var end = Match(TokenKind.Semicolon); return new(label.Text, new(start.Span.Start, end.Span.End)); }
    private T SimpleKeyword<T>(Func<SourceSpan,T> create) where T:StatementSyntax { var start=Next(); var end=Match(TokenKind.Semicolon); return create(new(start.Span.Start,end.Span.End)); }
    private CommandStatementSyntax ParseCommand() { var name = Next(); var args = new List<ExpressionSyntax>(); if (Current.Kind != TokenKind.Semicolon) { do { args.Add(ParseExpression()); } while (TryMatch(TokenKind.Comma)); } var end = Match(TokenKind.Semicolon); return new(name.Text, args, new(name.Span.Start,end.Span.End)); }

    internal ExpressionSyntax ParseExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        var unary = UnaryPrecedence(Current.Kind);
        if (unary > 0) { var op = Next(); var operand = ParseExpression(unary); left = new UnaryExpressionSyntax(op.Kind, operand, new(op.Span.Start, operand.Span.End)); }
        else left = ParsePrimary();
        left = ParsePostfix(left);
        while (true)
        {
            var precedence = BinaryPrecedence(Current.Kind); if (precedence == 0 || precedence <= parentPrecedence) break;
            var op = Next(); var right = ParseExpression(precedence - (IsAssignment(op.Kind) ? 1 : 0));
            left = IsAssignment(op.Kind) ? new AssignmentExpressionSyntax(left, op.Kind, right, new(left.Span.Start,right.Span.End)) : new BinaryExpressionSyntax(left, op.Kind, right, new(left.Span.Start,right.Span.End));
        }
        if (parentPrecedence == 0 && TryMatch(TokenKind.Question)) { var yes = ParseExpression(); Match(TokenKind.Colon); var no = ParseExpression(); left = new ConditionalExpressionSyntax(left,yes,no,new(left.Span.Start,no.Span.End)); }
        return left;
    }
    private ExpressionSyntax ParsePrimary()
    {
        if (Current.Kind == TokenKind.Integer) { var t=Next(); return new LiteralExpressionSyntax(t.IntegerValue!.Value,t.Span); }
        if (Current.Kind == TokenKind.String) { var t=Next(); return new LiteralExpressionSyntax(t.StringValue!,t.Span); }
        if (Current.Kind == TokenKind.Variable) { var t=Next(); return new VariableExpressionSyntax(t.Text, Scope(t.Text), t.Text.EndsWith('$'), t.Span); }
        if (Current.Kind == TokenKind.Identifier) { var t=Next(); return new IdentifierExpressionSyntax(t.Text,t.Span); }
        if (TryMatch(TokenKind.OpenParen, out var open)) { var value=ParseExpression(); var close=Match(TokenKind.CloseParen); return value with { Span = new(open.Span.Start,close.Span.End) }; }
        var bad=Next(); Error("RAT2001",$"Expected expression, found '{bad.Text}'.",bad); return new IdentifierExpressionSyntax("<missing>",bad.Span);
    }
    private ExpressionSyntax ParsePostfix(ExpressionSyntax value)
    {
        while (true)
        {
            if (TryMatch(TokenKind.OpenParen)) { var args=new List<ExpressionSyntax>(); if(Current.Kind!=TokenKind.CloseParen) do { args.Add(ParseExpression()); } while(TryMatch(TokenKind.Comma)); var close=Match(TokenKind.CloseParen); value=new CallExpressionSyntax(value,args,new(value.Span.Start,close.Span.End)); continue; }
            if (TryMatch(TokenKind.OpenBracket)) { var index=ParseExpression(); var close=Match(TokenKind.CloseBracket); value=new IndexExpressionSyntax(value,index,new(value.Span.Start,close.Span.End)); continue; }
            if (Current.Kind is TokenKind.PlusPlus or TokenKind.MinusMinus) { var op=Next(); value=new PostfixExpressionSyntax(value,op.Kind,new(value.Span.Start,op.Span.End)); continue; }
            return value;
        }
    }
    private ExpressionSyntax? OptionalExpression(TokenKind terminator) => Current.Kind == terminator ? null : ParseExpression();
    private bool Is(string text) => Current.Kind == TokenKind.Identifier && Current.Text.Equals(text,StringComparison.OrdinalIgnoreCase);
    private SyntaxToken Match(TokenKind kind) { if(Current.Kind==kind)return Next(); Error("RAT2002",$"Expected {kind}, found '{Current.Text}'.",Current); return new(kind,"",new(Current.Span.Start,Current.Span.Start)); }
    private bool TryMatch(TokenKind kind) => TryMatch(kind,out _);
    private bool TryMatch(TokenKind kind,out SyntaxToken token) { if(Current.Kind==kind){token=Next();return true;} token=null!;return false; }
    private SyntaxToken Next()=>_tokens[Math.Min(_index++,_tokens.Count-1)]; private SyntaxToken Current=>Peek(0); private SyntaxToken Previous=>Peek(-1); private SyntaxToken Peek(int n)=>_tokens[Math.Clamp(_index+n,0,_tokens.Count-1)];
    private void Error(string code,string message,SyntaxToken token)=>_diagnostics.Add(new(code,"Error",message,token.Span,token.Text));
    private static bool IsExpressionOperator(TokenKind kind)=>BinaryPrecedence(kind)>0;
    private static bool IsAssignment(TokenKind k)=>k is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign;
    private static int UnaryPrecedence(TokenKind k)=>k is TokenKind.Plus or TokenKind.Minus or TokenKind.Bang or TokenKind.Tilde or TokenKind.PlusPlus or TokenKind.MinusMinus ? 12:0;
    private static int BinaryPrecedence(TokenKind k)=>k switch { TokenKind.Star or TokenKind.Slash or TokenKind.Percent=>11, TokenKind.Plus or TokenKind.Minus=>10, TokenKind.ShiftLeft or TokenKind.ShiftRight=>9, TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual=>8, TokenKind.Equal or TokenKind.NotEqual=>7, TokenKind.BitAnd=>6,TokenKind.BitXor=>5,TokenKind.BitOr=>4,TokenKind.LogicalAnd=>3,TokenKind.LogicalOr=>2, TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign=>1,_=>0};
    private static RathenaVariableScope Scope(string n)=>n.StartsWith("##")?RathenaVariableScope.GlobalAccount:n.StartsWith("#")?RathenaVariableScope.Account:n.StartsWith("$@")?RathenaVariableScope.ServerTemporary:n.StartsWith("$")?RathenaVariableScope.Server:n.StartsWith(".@")?RathenaVariableScope.Local:n.StartsWith(".")?RathenaVariableScope.Npc:n.StartsWith("@")?RathenaVariableScope.CharacterTemporary:n.StartsWith("'")?RathenaVariableScope.Instance:RathenaVariableScope.Character;
}
