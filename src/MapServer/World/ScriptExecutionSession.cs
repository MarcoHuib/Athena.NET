namespace Athena.Net.MapServer.World;

public enum ScriptExecutionState { Running, WaitingForNext, WaitingForSelection, WaitingForQuestState, Closed }

public sealed class ScriptExecutionSession
{
    private readonly List<ScriptInstructionDefinition> _instructions;
    private int _instructionIndex;
    private SelectInstruction? _pendingSelection;
    private IfQuestStateInstruction? _pendingQuestCheck;
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);

    public ScriptExecutionSession(string entityId, uint actorId, IReadOnlyList<ScriptInstructionDefinition> instructions)
        : this(entityId, actorId, entityId, null, string.Empty, instructions) { }

    public ScriptExecutionSession(string entityId, uint actorId, string executingNpcName, string? baseNpcName, string mapName, IReadOnlyList<ScriptInstructionDefinition> instructions)
    {
        EntityId = entityId;
        ActorId = actorId;
        ExecutingNpcName = executingNpcName;
        BaseNpcName = baseNpcName;
        MapName = mapName;
        _instructions = [.. instructions];
    }

    public string EntityId { get; }
    public uint ActorId { get; }
    public string ExecutingNpcName { get; }
    public string? BaseNpcName { get; }
    public string MapName { get; }
    public ScriptExecutionState State { get; private set; } = ScriptExecutionState.Running;

    public void Assign(string variable, ScriptExpressionDefinition value) => _variables[variable] = Evaluate(value);

    public string Evaluate(ScriptExpressionDefinition expression) => expression switch
    {
        StringLiteralExpression literal => literal.Value,
        VariableExpression variable when _variables.TryGetValue(variable.Name, out var value) => value,
        VariableExpression variable => throw new InvalidOperationException($"Script variable '{variable.Name}' is not assigned."),
        ConcatExpression concat => Evaluate(concat.Left) + Evaluate(concat.Right),
        StrNpcInfoExpression { InfoType: 2 } => ExecutingNpcName.TrimStart('#'),
        StrNpcInfoExpression info => throw new NotSupportedException($"strnpcinfo({info.InfoType}) is not executable."),
        ReplaceStringExpression replace => Evaluate(replace.Value).Replace(Evaluate(replace.Search), Evaluate(replace.Replacement), StringComparison.Ordinal),
        _ => throw new NotSupportedException($"Script expression '{expression.GetType().Name}' is not executable."),
    };

    public IReadOnlyList<ScriptInstructionDefinition> Run()
    {
        if (State != ScriptExecutionState.Running) return [];
        var emitted = new List<ScriptInstructionDefinition>();
        while (_instructionIndex < _instructions.Count)
        {
            var instruction = _instructions[_instructionIndex++];
            emitted.Add(instruction);
            if (instruction is NextInstruction) { State = ScriptExecutionState.WaitingForNext; break; }
            if (instruction is SelectInstruction select) { _pendingSelection = select; State = ScriptExecutionState.WaitingForSelection; break; }
            if (instruction is IfQuestStateInstruction check) { _pendingQuestCheck = check; State = ScriptExecutionState.WaitingForQuestState; break; }
            if (instruction is CloseInstruction) { State = ScriptExecutionState.Closed; break; }
        }
        if (_instructionIndex == _instructions.Count && State == ScriptExecutionState.Running) State = ScriptExecutionState.Closed;
        return emitted;
    }

    public IReadOnlyList<ScriptInstructionDefinition> ResumeNext(uint actorId)
    {
        if (actorId != ActorId || State != ScriptExecutionState.WaitingForNext) return [];
        State = ScriptExecutionState.Running;
        return Run();
    }

    public IReadOnlyList<ScriptInstructionDefinition> ResumeSelection(uint actorId, int optionIndex)
    {
        if (actorId != ActorId || State != ScriptExecutionState.WaitingForSelection || _pendingSelection is null || optionIndex < 0 || optionIndex >= _pendingSelection.Options.Count) return [];
        var branch = _pendingSelection.Options[optionIndex].Instructions;
        _instructions.InsertRange(_instructionIndex, branch);
        _pendingSelection = null;
        State = ScriptExecutionState.Running;
        return Run();
    }

    public IReadOnlyList<ScriptInstructionDefinition> ResumeQuestState(uint actorId, CharacterQuestStatus state)
    {
        if (actorId != ActorId || State != ScriptExecutionState.WaitingForQuestState || _pendingQuestCheck is null) return [];
        var branch = state == _pendingQuestCheck.Expected ? _pendingQuestCheck.Then : _pendingQuestCheck.Else;
        _instructions.InsertRange(_instructionIndex, branch);
        _pendingQuestCheck = null;
        State = ScriptExecutionState.Running;
        return Run();
    }
}
