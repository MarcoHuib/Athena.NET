using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Lowering;

internal sealed record LoweredNpcScript(string EventName, IReadOnlyList<LoweredScriptStatement> Statements, SourceSpan Span);
internal abstract record LoweredScriptStatement(SourceSpan Span);
internal sealed record LoweredBlock(IReadOnlyList<LoweredScriptStatement> Statements, SourceSpan Span) : LoweredScriptStatement(Span);
internal sealed record LoweredIf(LoweredScriptExpression Condition, LoweredScriptStatement Then, LoweredScriptStatement? Else, SourceSpan Span) : LoweredScriptStatement(Span);
internal sealed record LoweredSwitch(LoweredScriptExpression Expression, IReadOnlyList<LoweredSwitchCase> Cases, SourceSpan Span) : LoweredScriptStatement(Span);
internal sealed record LoweredSwitchCase(LoweredScriptExpression? Value, IReadOnlyList<LoweredScriptStatement> Statements, SourceSpan Span);
internal sealed record LoweredAssignment(string Variable, LoweredScriptExpression Value, SourceSpan Span) : LoweredScriptStatement(Span);
internal sealed record LoweredCommand(string Name, IReadOnlyList<LoweredScriptExpression> Arguments, bool Terminates, SourceSpan Span) : LoweredScriptStatement(Span);
internal abstract record LoweredScriptExpression(SourceSpan Span);
internal sealed record LoweredLiteral(object Value, SourceSpan Span) : LoweredScriptExpression(Span);
internal sealed record LoweredVariable(string Name, RathenaVariableScope Scope, bool IsString, SourceSpan Span) : LoweredScriptExpression(Span);
internal sealed record LoweredBinary(LoweredScriptExpression Left, TokenKind Operator, LoweredScriptExpression Right, SourceSpan Span) : LoweredScriptExpression(Span);
internal sealed record LoweredCall(string Name, IReadOnlyList<LoweredScriptExpression> Arguments, SourceSpan Span) : LoweredScriptExpression(Span);
internal sealed record LoweredIdentifier(string Name, SourceSpan Span) : LoweredScriptExpression(Span);

internal sealed record ScriptLoweringResult(LoweredNpcScript? Script, IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    public bool Success => Script is not null && Diagnostics.All(diagnostic => diagnostic.Severity != "Error");
}

internal static class RathenaScriptLowerer
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "mes", "next", "select", "close", "close2", "setquest", "completequest", "warp", "savepoint", "cutin", "npctalk", "cloakonnpcself", "cloakoffnpcself", "navigateto", "specialeffect2", "heal", "skilleffect", "sc_start", "getexp", "end"
    };
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "isbegin_quest", "strnpcinfo", "replacestr"
    };

    public static ScriptLoweringResult LowerEvent(CompilationUnitSyntax syntax, string eventName)
    {
        var diagnostics = new List<CompilerDiagnostic>(syntax.Diagnostics);
        var all = syntax.Statements.ToList();
        var labelIndex = all.FindIndex(statement => statement is LabelStatementSyntax label && label.Name.Equals(eventName, StringComparison.OrdinalIgnoreCase));
        var startIndex = labelIndex + 1;
        if (labelIndex < 0 && eventName.Equals("OnClick", StringComparison.OrdinalIgnoreCase)) startIndex = 0;
        else if (labelIndex < 0)
        {
            diagnostics.Add(new("RAT4001", "Error", $"Event label '{eventName}' was not found.", syntax.Span, eventName));
            return new(null, diagnostics);
        }

        var statements = new List<LoweredScriptStatement>();
        foreach (var statement in all.Skip(startIndex).TakeWhile(statement => statement is not LabelStatementSyntax { IsEvent: true }))
            if (LowerStatement(statement, diagnostics) is { } lowered) statements.Add(lowered);
        return new(new(eventName, statements, syntax.Span), diagnostics);
    }

    private static LoweredScriptStatement? LowerStatement(StatementSyntax syntax, List<CompilerDiagnostic> diagnostics) => syntax switch
    {
        EmptyStatementSyntax => null,
        BlockStatementSyntax block => new LoweredBlock(block.Statements.Select(statement => LowerStatement(statement, diagnostics)).OfType<LoweredScriptStatement>().ToArray(), block.Span),
        IfStatementSyntax conditional => new LoweredIf(LowerExpression(conditional.Condition, diagnostics), LowerStatement(conditional.Then, diagnostics)!, conditional.Else is null ? null : LowerStatement(conditional.Else, diagnostics), conditional.Span),
        SwitchStatementSyntax selection => new LoweredSwitch(LowerExpression(selection.Expression, diagnostics), selection.Clauses.Select(clause =>
            new LoweredSwitchCase(clause.Value is null ? null : LowerExpression(clause.Value, diagnostics), clause.Statements.Select(statement => LowerStatement(statement, diagnostics)).OfType<LoweredScriptStatement>().Where(statement => statement is not LoweredBreak).ToArray(), clause.Span)).ToArray(), selection.Span),
        BreakStatementSyntax statement => new LoweredBreak(statement.Span),
        ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Target: VariableExpressionSyntax variable, Operator: TokenKind.Assign } assignment }
            when variable.Scope == RathenaVariableScope.Local => new LoweredAssignment(variable.Name, LowerExpression(assignment.Value, diagnostics), assignment.Span),
        ExpressionStatementSyntax { Expression: CallExpressionSyntax { Target: IdentifierExpressionSyntax identifier } call }
            when Commands.Contains(identifier.Name) => new LoweredCommand(identifier.Name.ToLowerInvariant(), call.Arguments.Select(argument => LowerExpression(argument, diagnostics)).ToArray(), false, call.Span),
        ExpressionStatementSyntax expression => UnsupportedStatement(expression, diagnostics),
        CommandStatementSyntax command when Commands.Contains(command.Name) => new LoweredCommand(command.Name.ToLowerInvariant(), command.Arguments.Select(argument => LowerExpression(argument, diagnostics)).ToArray(), command.Name.Equals("close", StringComparison.OrdinalIgnoreCase), command.Span),
        _ => UnsupportedStatement(syntax, diagnostics),
    };

    private static LoweredScriptStatement? UnsupportedStatement(StatementSyntax statement, List<CompilerDiagnostic> diagnostics)
    {
        diagnostics.Add(new("RAT4002", "Error", $"Statement '{statement.GetType().Name}' is parsed but not lowerable for generated execution.", statement.Span, statement.GetType().Name));
        return null;
    }

    private static LoweredScriptExpression LowerExpression(ExpressionSyntax syntax, List<CompilerDiagnostic> diagnostics) => syntax switch
    {
        LiteralExpressionSyntax literal => new LoweredLiteral(literal.Value, literal.Span),
        VariableExpressionSyntax variable when variable.Scope == RathenaVariableScope.Local => new LoweredVariable(variable.Name, variable.Scope, variable.IsString, variable.Span),
        BinaryExpressionSyntax binary => new LoweredBinary(LowerExpression(binary.Left, diagnostics), binary.Operator, LowerExpression(binary.Right, diagnostics), binary.Span),
        CallExpressionSyntax { Target: IdentifierExpressionSyntax identifier } call when Functions.Contains(identifier.Name) => new LoweredCall(identifier.Name.ToLowerInvariant(), call.Arguments.Select(argument => LowerExpression(argument, diagnostics)).ToArray(), call.Span),
        IdentifierExpressionSyntax identifier when identifier.Name.Equals("bc_self", StringComparison.OrdinalIgnoreCase) => new LoweredLiteral(3L, identifier.Span),
        IdentifierExpressionSyntax identifier when identifier.Name.Equals("nav_none", StringComparison.OrdinalIgnoreCase) => new LoweredLiteral(0L, identifier.Span),
        IdentifierExpressionSyntax identifier => new LoweredIdentifier(identifier.Name, identifier.Span),
        _ => UnsupportedExpression(syntax, diagnostics),
    };

    private static LoweredScriptExpression UnsupportedExpression(ExpressionSyntax expression, List<CompilerDiagnostic> diagnostics)
    {
        diagnostics.Add(new("RAT4003", "Error", $"Expression '{expression.GetType().Name}' is parsed but not lowerable for generated execution.", expression.Span, expression.GetType().Name));
        return new LoweredLiteral(0L, expression.Span);
    }
}
internal sealed record LoweredBreak(SourceSpan Span) : LoweredScriptStatement(Span);
