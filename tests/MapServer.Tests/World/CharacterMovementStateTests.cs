using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterMovementStateTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    [Fact]
    public void NewState_StartsAtGivenCell_NotMoving()
    {
        var state = new CharacterMovementState("iz_int01", 10, 10);
        Assert.Equal((ushort)10, state.CurrentX);
        Assert.Equal((ushort)10, state.CurrentY);
        Assert.False(state.IsMoving);
    }

    [Fact]
    public void StartWalk_ThreeCellPath_IsMovingAndReportsDestination()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0)], orthogonalStepMs: 150, Epoch);

        Assert.True(state.IsMoving);
        Assert.Equal((ushort)0, state.CurrentX);
        Assert.Equal(((ushort)2, (ushort)0), state.Destination);
    }

    [Fact]
    public void AdvanceTo_BeforeFirstCellDuration_DoesNotMove()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0)], orthogonalStepMs: 150, Epoch);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(100));

        Assert.Empty(crossed);
        Assert.Equal((ushort)0, state.CurrentX);
        Assert.True(state.IsMoving);
    }

    [Fact]
    public void AdvanceTo_ExactlyOneCellDuration_AdvancesExactlyOneCell()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0)], orthogonalStepMs: 150, Epoch);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(150));

        Assert.Equal([((ushort)1, (ushort)0)], crossed);
        Assert.Equal((ushort)1, state.CurrentX);
        Assert.True(state.IsMoving); // One more cell remains.
    }

    [Fact]
    public void AdvanceTo_MultipleElapsedCells_CrossesEachOneAndReportsAllOfThem()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0), (3, 0)], orthogonalStepMs: 150, Epoch);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(450)); // Exactly 3 cell-durations.

        Assert.Equal([((ushort)1, (ushort)0), ((ushort)2, (ushort)0), ((ushort)3, (ushort)0)], crossed);
        Assert.Equal((ushort)3, state.CurrentX);
        Assert.False(state.IsMoving); // Reached the destination.
    }

    [Fact]
    public void AdvanceTo_PastDestination_StopsAtDestination_DoesNotOvershoot()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0)], orthogonalStepMs: 150, Epoch);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(10_000)); // Way past total travel time.

        Assert.Equal([((ushort)1, (ushort)0), ((ushort)2, (ushort)0)], crossed);
        Assert.Equal(((ushort)2, (ushort)0), (state.CurrentX, state.CurrentY));
        Assert.False(state.IsMoving);
    }

    [Fact]
    public void AdvanceTo_AfterArrival_ReturnsEmpty_DoesNotReRaiseCells()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 150, Epoch);
        state.AdvanceTo(Epoch.AddMilliseconds(150));

        var secondCall = state.AdvanceTo(Epoch.AddMilliseconds(9999));

        Assert.Empty(secondCall);
    }

    // This is the core scenario the diagnosis identified: a second movement request arriving before
    // the first walk completes must retarget from the cell the character has ACTUALLY reached by
    // `now`, not from the original start and not by pretending the first destination was reached.
    [Fact]
    public void Retarget_MidWalk_StartsFromActualCurrentCell_NotOriginalStartOrPreviousDestination()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0), (3, 0), (4, 0)], orthogonalStepMs: 150, Epoch); // A -> B

        // Client re-clicks after 2 cells' worth of time (before arriving at B=(4,0)).
        var now = Epoch.AddMilliseconds(300);
        var crossedBeforeRetarget = state.AdvanceTo(now);
        Assert.Equal([((ushort)1, (ushort)0), ((ushort)2, (ushort)0)], crossedBeforeRetarget);

        var actualCurrentCell = (state.CurrentX, state.CurrentY);
        Assert.Equal(((ushort)2, (ushort)0), actualCurrentCell); // C, not A=(0,0) and not B=(4,0).

        // New click D=(2,5): retarget from C, the cell just proven current.
        state.StartWalk([(2, 0), (2, 1), (2, 2), (2, 3), (2, 4), (2, 5)], orthogonalStepMs: 150, now);

        Assert.Equal((ushort)2, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);
        Assert.Equal(((ushort)2, (ushort)5), state.Destination);
    }

    [Fact]
    public void Teleport_ResetsToNewMapAndCell_NotMoving()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 150, Epoch);

        state.Teleport("int_land01", 85, 107);

        Assert.Equal("int_land01", state.Map);
        Assert.Equal((ushort)85, state.CurrentX);
        Assert.Equal((ushort)107, state.CurrentY);
        Assert.False(state.IsMoving);
        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(999999)));
    }

    [Fact]
    public void StartWalk_EmptyPath_Throws()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        Assert.Throws<ArgumentException>(() => state.StartWalk([], 150, Epoch));
    }

    [Fact]
    public void StartWalk_SingleCellPath_IsNotMoving()
    {
        // A click that resolves to the character's own current cell (e.g. clicking where you stand).
        var state = new CharacterMovementState("iz_int01", 5, 5);
        state.StartWalk([(5, 5)], orthogonalStepMs: 150, Epoch);

        Assert.False(state.IsMoving);
    }

    // Pinned unit_get_walkpath_time (unit.cpp:1112-1127): orthogonal step = status_get_speed(bl);
    // diagonal step = status_get_speed(bl) * MOVE_DIAGONAL_COST / MOVE_COST (14/10). For
    // WalkSpeed=400 (G_PORING) this is 400ms orthogonal, 560ms diagonal - NOT the same duration,
    // contrary to this type's old uniform-CellDurationMs model.
    [Fact]
    public void AdvanceTo_DiagonalStep_TakesLongerThanOrthogonalStep_ForTheSameWalkSpeed()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 1)], orthogonalStepMs: 400, Epoch); // One diagonal step.

        // 400ms (the orthogonal duration) must NOT be enough to cross a diagonal step.
        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(400)));
        Assert.Equal((ushort)0, state.CurrentX);
        Assert.True(state.IsMoving);

        // 560ms (400 * 14/10) completes it.
        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(560));
        Assert.Equal([((ushort)1, (ushort)1)], crossed);
        Assert.False(state.IsMoving);
    }

    [Fact]
    public void AdvanceTo_MixedOrthogonalAndDiagonalPath_EachStepUsesItsOwnDuration()
    {
        // (0,0)->(1,0) orthogonal (400ms), (1,0)->(2,1) diagonal (560ms), (2,1)->(2,2) orthogonal
        // (400ms). Total 1360ms - NOT 3*400=1200ms under the old uniform model.
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 1), (2, 2)], orthogonalStepMs: 400, Epoch);

        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(399)));

        var afterFirstStep = state.AdvanceTo(Epoch.AddMilliseconds(400));
        Assert.Equal([((ushort)1, (ushort)0)], afterFirstStep);
        Assert.True(state.IsMoving);

        // 400ms (an orthogonal duration) is NOT enough to cross the next, diagonal step.
        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(400 + 400)));
        Assert.Equal((ushort)1, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);

        var afterDiagonalStep = state.AdvanceTo(Epoch.AddMilliseconds(400 + 560));
        Assert.Equal([((ushort)2, (ushort)1)], afterDiagonalStep);
        Assert.True(state.IsMoving);

        var afterThirdStep = state.AdvanceTo(Epoch.AddMilliseconds(400 + 560 + 400));
        Assert.Equal([((ushort)2, (ushort)2)], afterThirdStep);
        Assert.False(state.IsMoving);
    }

    [Fact]
    public void NextStepDueAt_ReflectsTheInFlightStepsOwnDuration_NotAFixedOrthogonalOne()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 1), (2, 1)], orthogonalStepMs: 400, Epoch); // diagonal, then orthogonal.

        Assert.Equal(Epoch.AddMilliseconds(560), state.NextStepDueAt); // First step is diagonal.

        state.AdvanceTo(Epoch.AddMilliseconds(560));

        Assert.Equal(Epoch.AddMilliseconds(560 + 400), state.NextStepDueAt); // Second step is orthogonal.
    }
}
