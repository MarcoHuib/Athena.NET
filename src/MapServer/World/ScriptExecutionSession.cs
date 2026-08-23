namespace Athena.Net.MapServer.World;

public enum ScriptExecutionState { Running, WaitingForNext, WaitingForSelection, WaitingForQuestState, Closed }

public sealed class ScriptExecutionSession
{
    private readonly List<ScriptInstructionDefinition> _instructions;
    private int _instructionIndex;
    private SelectInstruction? _pendingSelection;
    private IfQuestStateInstruction? _pendingQuestCheck;

    public ScriptExecutionSession(string entityId, uint actorId, IReadOnlyList<ScriptInstructionDefinition> instructions)
    {
        EntityId = entityId;
        ActorId = actorId;
        _instructions = [.. instructions];
    }

    public string EntityId { get; }
    public uint ActorId { get; }
    public ScriptExecutionState State { get; private set; } = ScriptExecutionState.Running;

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
