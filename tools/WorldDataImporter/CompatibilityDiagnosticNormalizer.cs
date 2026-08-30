using Athena.WorldCompiler.Rathena.Syntax;

internal sealed record NormalizedCompatibilityCapability(
    string CapabilityId, string Category, FailureStage Stage, string CompilerConstruct);

/// <summary>
/// Converts compiler diagnostics into stable rAthena capability identifiers. Syntax ownership is
/// established by source-span containment; unrelated nearby semantic occurrences are never used.
/// </summary>
internal static class CompatibilityDiagnosticNormalizer
{
    public static NormalizedCompatibilityCapability Normalize(CompilerDiagnostic diagnostic, CompilationUnitSyntax syntax)
    {
        var stage = Stage(diagnostic.Code);
        var node = FindGenerationNode(syntax, diagnostic) ?? AttributionNode(FindMostSpecificNode(syntax, diagnostic.Span));
        var compilerConstruct = node?.GetType().Name ?? diagnostic.Construct ?? diagnostic.Code;
        var capability = FromDiagnosticCode(diagnostic.Code) ?? FromNode(node) ?? FromGenerationDiagnostic(diagnostic) ?? FromConstruct(diagnostic.Construct) ?? SyntaxFallback(diagnostic.Code);
        if (!IsValid(capability.Id)) capability = SyntaxFallback(diagnostic.Code);
        Validate(capability.Id);
        return new(capability.Id, capability.Category, stage, compilerConstruct);
    }

    private static (string Id, string Category)? FromDiagnosticCode(string code) => code switch
    {
        "RAT1001" => ("syntax:unrecognized-character", "syntax"),
        "RAT1002" => ("syntax:unterminated-string", "syntax"),
        "RAT2001" => ("syntax:expected-expression", "syntax"),
        "RAT2002" => ("syntax:expected-token", "syntax"),
        "RAT2004" => ("syntax:expected-switch-clause", "syntax"),
        _ => null
    };

    private static (string Id, string Category) SyntaxFallback(string diagnosticCode) =>
        ($"syntax:{diagnosticCode.ToLowerInvariant()}", "syntax");

    private static (string Id, string Category)? FromNode(SyntaxNode? node) => node switch
    {
        WhileStatementSyntax => ("control-flow:while", "control-flow"),
        ForStatementSyntax => ("control-flow:for", "control-flow"),
        GotoStatementSyntax => ("control-flow:goto", "control-flow"),
        ContinueStatementSyntax => ("control-flow:continue", "control-flow"),
        ReturnStatementSyntax => ("control-flow:return", "control-flow"),
        ExpressionStatementSyntax statement => FromExpression(statement.Expression),
        CommandStatementSyntax command => FromCommand(command.Name),
        ExpressionSyntax expression => FromExpression(expression),
        _ => null
    };

    private static (string Id, string Category)? FromExpression(ExpressionSyntax expression) => expression switch
    {
        AssignmentExpressionSyntax { Target: IndexExpressionSyntax } => ("array:set", "array"),
        AssignmentExpressionSyntax { Target: VariableExpressionSyntax variable } when variable.Scope != RathenaVariableScope.Local => FromVariable(variable.Scope),
        AssignmentExpressionSyntax assignment => FromExpression(assignment.Value),
        CallExpressionSyntax { Target: IdentifierExpressionSyntax identifier } => ($"function:{NormalizeName(identifier.Name)}", "function"),
        VariableExpressionSyntax variable when variable.Scope != RathenaVariableScope.Local => FromVariable(variable.Scope),
        IndexExpressionSyntax => ("array:index", "array"),
        UnaryExpressionSyntax unary => FromOperator(unary.Operator),
        BinaryExpressionSyntax binary => FromOperator(binary.Operator),
        PostfixExpressionSyntax postfix => FromOperator(postfix.Operator),
        ConditionalExpressionSyntax => ("expression:conditional", "expression"),
        _ => null
    };

    private static (string Id, string Category) FromCommand(string name)
    {
        var normalized = NormalizeName(name);
        return normalized switch
        {
            "callfunc" or "callsub" => ($"function:{normalized}", "function"),
            "sleep" or "sleep2" or "addtimer" or "deltimer" or "initnpctimer" or "startnpctimer" or "stopnpctimer" => ($"timer:{normalized}", "timer"),
            "disablenpc" => ("npc:disable", "npc"),
            "enablenpc" => ("npc:enable", "npc"),
            "donpcevent" or "doevent" => ("npc:event-dispatch", "npc"),
            "mapannounce" or "announce" => ($"announcement:{normalized}", "announcement"),
            "questinfo" => ("quest:info", "quest"),
            _ => ($"command:{normalized}", "command")
        };
    }

    private static (string Id, string Category) FromVariable(RathenaVariableScope scope) => scope switch
    {
        RathenaVariableScope.Character => ("variable:character", "variable"),
        RathenaVariableScope.CharacterTemporary => ("variable:character-temporary", "variable"),
        RathenaVariableScope.Account => ("variable:account", "variable"),
        RathenaVariableScope.GlobalAccount => ("variable:account-global", "variable"),
        RathenaVariableScope.Npc => ("variable:npc", "variable"),
        RathenaVariableScope.Server => ("variable:server-global", "variable"),
        RathenaVariableScope.ServerTemporary => ("variable:server-temporary", "variable"),
        RathenaVariableScope.Instance => ("variable:instance", "variable"),
        RathenaVariableScope.Local => ("variable:local", "variable"),
        _ => ("variable:unknown", "variable")
    };

    private static (string Id, string Category)? FromOperator(TokenKind kind) => kind switch
    {
        TokenKind.LogicalAnd => ("operator:logical-and", "expression"),
        TokenKind.LogicalOr => ("operator:logical-or", "expression"),
        TokenKind.BitAnd => ("operator:bit-and", "expression"),
        TokenKind.BitOr => ("operator:bit-or", "expression"),
        TokenKind.BitXor => ("operator:bit-xor", "expression"),
        TokenKind.Bang => ("operator:logical-not", "expression"),
        TokenKind.Tilde => ("operator:bit-not", "expression"),
        TokenKind.PlusPlus => ("operator:increment", "expression"),
        TokenKind.MinusMinus => ("operator:decrement", "expression"),
        TokenKind.ShiftLeft => ("operator:shift-left", "expression"),
        TokenKind.ShiftRight => ("operator:shift-right", "expression"),
        _ => null
    };

    private static (string Id, string Category)? FromGenerationDiagnostic(CompilerDiagnostic diagnostic)
    {
        foreach (var kind in Enum.GetValues<TokenKind>())
            if (diagnostic.Message.Equals(kind.ToString(), StringComparison.Ordinal) && FromOperator(kind) is { } capability)
                return capability;
        return diagnostic.Message.StartsWith("Quest state ", StringComparison.Ordinal) ? ("quest:state-value", "quest") : null;
    }

    private static SyntaxNode? AttributionNode(SyntaxNode? node) => node switch
    {
        ExpressionStatementSyntax statement => AttributionExpression(statement.Expression),
        _ => node
    };

    private static ExpressionSyntax AttributionExpression(ExpressionSyntax expression) => expression switch
    {
        AssignmentExpressionSyntax { Target: IndexExpressionSyntax index } => index,
        AssignmentExpressionSyntax { Target: VariableExpressionSyntax variable } when variable.Scope != RathenaVariableScope.Local => variable,
        AssignmentExpressionSyntax assignment => AttributionExpression(assignment.Value),
        _ => expression
    };

    private static SyntaxNode? FindGenerationNode(CompilationUnitSyntax syntax, CompilerDiagnostic diagnostic)
    {
        if (diagnostic.Code != "RAT5001") return null;
        foreach (var kind in Enum.GetValues<TokenKind>())
        {
            if (!diagnostic.Message.Equals(kind.ToString(), StringComparison.Ordinal)) continue;
            return Nodes(syntax).FirstOrDefault(node => node is BinaryExpressionSyntax binary && binary.Operator == kind || node is UnaryExpressionSyntax unary && unary.Operator == kind || node is PostfixExpressionSyntax postfix && postfix.Operator == kind);
        }
        return null;
    }

    private static (string Id, string Category)? FromConstruct(string? construct) => construct switch
    {
        "WhileStatementSyntax" => ("control-flow:while", "control-flow"),
        "ForStatementSyntax" => ("control-flow:for", "control-flow"),
        "GotoStatementSyntax" => ("control-flow:goto", "control-flow"),
        "IndexExpressionSyntax" => ("array:index", "array"),
        "strcharinfo" => ("function:strcharinfo", "function"),
        _ => null
    };

    private static SyntaxNode? FindMostSpecificNode(CompilationUnitSyntax syntax, SourceSpan diagnosticSpan)
    {
        return Nodes(syntax).Where(node => Contains(node.Span, diagnosticSpan))
            .OrderBy(node => node.Span.End.Offset - node.Span.Start.Offset).FirstOrDefault();
    }

    private static bool Contains(SourceSpan owner, SourceSpan target) =>
        owner.Start.Offset <= target.Start.Offset && owner.End.Offset >= target.End.Offset;

    private static IEnumerable<SyntaxNode> Nodes(CompilationUnitSyntax syntax)
    {
        yield return syntax;
        foreach (var statement in syntax.Statements)
            foreach (var node in Nodes(statement)) yield return node;
    }

    private static IEnumerable<SyntaxNode> Nodes(StatementSyntax statement)
    {
        yield return statement;
        IEnumerable<SyntaxNode> children = statement switch
        {
            BlockStatementSyntax block => block.Statements.SelectMany(Nodes),
            ExpressionStatementSyntax expression => Nodes(expression.Expression),
            CommandStatementSyntax command => command.Arguments.SelectMany(Nodes),
            IfStatementSyntax conditional => Nodes(conditional.Condition).Concat(Nodes(conditional.Then)).Concat(conditional.Else is null ? [] : Nodes(conditional.Else)),
            WhileStatementSyntax loop => Nodes(loop.Condition).Concat(Nodes(loop.Body)),
            ForStatementSyntax loop => new[] { loop.Initializer, loop.Condition, loop.Increment }.OfType<ExpressionSyntax>().SelectMany(Nodes).Concat(Nodes(loop.Body)),
            SwitchStatementSyntax selection => Nodes(selection.Expression).Concat(selection.Clauses.SelectMany(clause => clause.Statements.SelectMany(Nodes))),
            ReturnStatementSyntax { Expression: { } expression } => Nodes(expression),
            _ => []
        };
        foreach (var child in children) yield return child;
    }

    private static IEnumerable<SyntaxNode> Nodes(ExpressionSyntax expression)
    {
        yield return expression;
        IEnumerable<ExpressionSyntax> children = expression switch
        {
            UnaryExpressionSyntax unary => [unary.Operand],
            BinaryExpressionSyntax binary => [binary.Left, binary.Right],
            AssignmentExpressionSyntax assignment => [assignment.Target, assignment.Value],
            CallExpressionSyntax call => new[] { call.Target }.Concat(call.Arguments),
            IndexExpressionSyntax index => [index.Target, index.Index],
            PostfixExpressionSyntax postfix => [postfix.Operand],
            ConditionalExpressionSyntax conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            _ => []
        };
        foreach (var child in children)
            foreach (var node in Nodes(child)) yield return node;
    }

    private static FailureStage Stage(string diagnosticCode) =>
        diagnosticCode.StartsWith("RAT1", StringComparison.Ordinal) || diagnosticCode.StartsWith("RAT2", StringComparison.Ordinal) ? FailureStage.Parsing :
        diagnosticCode.StartsWith("RAT3", StringComparison.Ordinal) ? FailureStage.SemanticAnalysis :
        diagnosticCode.StartsWith("RAT4", StringComparison.Ordinal) ? FailureStage.Lowering :
        diagnosticCode.StartsWith("RAT5", StringComparison.Ordinal) ? FailureStage.Generation : FailureStage.Discovery;

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    internal static void Validate(string capabilityId)
    {
        if (!IsValid(capabilityId))
            throw new InvalidOperationException($"Suspicious compatibility capability ID '{capabilityId}'.");
    }

    private static bool IsValid(string capabilityId) =>
        !string.IsNullOrWhiteSpace(capabilityId) && char.IsAsciiLetterOrDigit(capabilityId[0]) &&
        capabilityId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
