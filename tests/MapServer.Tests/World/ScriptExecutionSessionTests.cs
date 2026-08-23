using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class ScriptExecutionSessionTests
{
    [Fact]
    public void NextSuspends_MatchingResponseResumes_CloseTerminates()
    {
        var session = new ScriptExecutionSession("npc:test:greeter", 42, [new MessageInstruction("Hello"), new NextInstruction(), new MessageInstruction("Welcome"), new CloseInstruction()]);

        Assert.Collection(session.Run(), item => Assert.Equal(new MessageInstruction("Hello"), item), item => Assert.IsType<NextInstruction>(item));
        Assert.Equal(ScriptExecutionState.WaitingForNext, session.State);
        Assert.Empty(session.ResumeNext(41));
        Assert.Equal(ScriptExecutionState.WaitingForNext, session.State);
        Assert.Collection(session.ResumeNext(42), item => Assert.Equal(new MessageInstruction("Welcome"), item), item => Assert.IsType<CloseInstruction>(item));
        Assert.Equal(ScriptExecutionState.Closed, session.State);
        Assert.Empty(session.ResumeNext(42));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    public void SelectSuspends_AndResumesOnlyChosenBranch(int optionIndex, string expected)
    {
        var select = new SelectInstruction([
            new("Option A", [new MessageInstruction("A"), new CloseInstruction()]),
            new("Option B", [new MessageInstruction("B"), new CloseInstruction()])]);
        var session = new ScriptExecutionSession("npc:test:menu", 42, [select]);

        Assert.Collection(session.Run(), item => Assert.Same(select, item));
        Assert.Equal(ScriptExecutionState.WaitingForSelection, session.State);
        Assert.Empty(session.ResumeSelection(41, optionIndex));
        Assert.Empty(session.ResumeSelection(42, -1));
        Assert.Empty(session.ResumeSelection(42, 2));
        Assert.Equal(ScriptExecutionState.WaitingForSelection, session.State);
        Assert.Collection(session.ResumeSelection(42, optionIndex),
            item => Assert.Equal(new MessageInstruction(expected), item),
            item => Assert.IsType<CloseInstruction>(item));
        Assert.Equal(ScriptExecutionState.Closed, session.State);
        Assert.Empty(session.ResumeSelection(42, optionIndex));
    }

    [Fact]
    public void SelectionCannotResumeNextBoundary()
    {
        var session = new ScriptExecutionSession("npc:test:next", 42, [new NextInstruction(), new CloseInstruction()]);
        session.Run();
        Assert.Empty(session.ResumeSelection(42, 0));
        Assert.Equal(ScriptExecutionState.WaitingForNext, session.State);
    }

    [Theory]
    [InlineData(CharacterQuestStatus.Active, "active")]
    [InlineData(CharacterQuestStatus.Absent, "other")]
    [InlineData(CharacterQuestStatus.Completed, "other")]
    public void QuestCheckSuspends_AndResumesMatchingBranch(CharacterQuestStatus state, string expected)
    {
        var check = new IfQuestStateInstruction(21001, CharacterQuestStatus.Active,
            [new MessageInstruction("active"), new CloseInstruction()], [new MessageInstruction("other"), new CloseInstruction()]);
        var session = new ScriptExecutionSession("npc:test:quest", 42, [check]);
        Assert.Collection(session.Run(), item => Assert.Same(check, item));
        Assert.Equal(ScriptExecutionState.WaitingForQuestState, session.State);
        Assert.Empty(session.ResumeQuestState(41, state));
        Assert.Collection(session.ResumeQuestState(42, state),
            item => Assert.Equal(new MessageInstruction(expected), item), item => Assert.IsType<CloseInstruction>(item));
    }
}
