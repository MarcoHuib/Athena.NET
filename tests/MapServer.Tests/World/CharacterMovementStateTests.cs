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

    // StartWalk's own contract (discard any in-flight step, begin a brand-new one from the
    // character's CURRENT cell) is still correct when a caller has ALREADY brought the state to a
    // cell boundary first (e.g. via AdvanceTo, or via ConsumePendingRetarget - see the Retarget_*
    // tests below for the actual mid-walk-client-retarget contract, which does NOT call StartWalk
    // directly while IsMoving). This test proves StartWalk itself starts from wherever CurrentX/
    // CurrentY already are, not from the ORIGINAL start or the previous walk's own destination.
    [Fact]
    public void StartWalk_AfterAdvancingToACellBoundary_StartsFromThatActualCurrentCell()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0), (3, 0), (4, 0)], orthogonalStepMs: 150, Epoch); // A -> B

        // Advance exactly 2 whole cell-durations (a real cell boundary, not a partial step).
        var now = Epoch.AddMilliseconds(300);
        var crossedBeforeRetarget = state.AdvanceTo(now);
        Assert.Equal([((ushort)1, (ushort)0), ((ushort)2, (ushort)0)], crossedBeforeRetarget);

        var actualCurrentCell = (state.CurrentX, state.CurrentY);
        Assert.Equal(((ushort)2, (ushort)0), actualCurrentCell); // C, not A=(0,0) and not B=(4,0).

        // New path from C, the cell just proven current.
        state.StartWalk([(2, 0), (2, 1), (2, 2), (2, 3), (2, 4), (2, 5)], orthogonalStepMs: 150, now);

        Assert.Equal((ushort)2, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);
        Assert.Equal(((ushort)2, (ushort)5), state.Destination);
    }

    // ===== Mid-walk retarget (RequestRetarget/ConsumePendingRetarget) =====
    //
    // Pinned unit_walktoxy (unit.cpp:884-899): a retarget received while a step is ALREADY in
    // flight (ud->walktimer != INVALID_TIMER) does NOT touch the in-flight step at all - it only
    // overwrites ud->to_x/ud->to_y and defers everything else (change_walk_target) to the next real
    // cell boundary (unit_walktoxy_timer, unit.cpp:738-744). These tests exercise
    // CharacterMovementState's own reproduction of that split in isolation, without a real
    // IMovementPathProvider/MapClientSession - see MapClientSessionMovementRetargetTests for the
    // full wire-level integration proof (0x0087 timing/contents, warp/OnTouch interaction).

    // The task's own literal scenario: start a 400ms A->B step, retarget at t=300ms, confirm the
    // in-flight step's own timing is COMPLETELY unaffected (still A at t=399ms, reaches B at
    // EXACTLY t=400ms - never 700ms, proving no elapsed progress was discarded and no extra delay
    // was added), and the replacement path only begins once ConsumePendingRetarget is actually
    // acted on by the caller from B.
    [Fact]
    public void RequestRetarget_At300ms_DoesNotAffectTheInFlight400msStep_ReachesBAtExactly400ms()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 400, Epoch); // A(0,0) -> B(1,0), 400ms.

        // Retarget arrives mid-step - must not touch _path/_pathPosition/_stepStartedAt at all.
        state.RequestRetarget(5, 5);
        Assert.Equal((ushort)0, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);
        Assert.True(state.IsMoving);
        Assert.Equal(Epoch.AddMilliseconds(400), state.NextStepDueAt); // Unchanged by the retarget.

        // t=399ms: still in A's cell - the retarget must not have shortened the step.
        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(399)));
        Assert.Equal((ushort)0, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);

        // t=400ms: reaches B exactly on time - never later (proving no time was "spent" on the
        // retarget itself, i.e. never requiring 400+300=700ms total).
        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(400));
        Assert.Equal([((ushort)1, (ushort)0)], crossed);
        Assert.Equal((ushort)1, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);
        Assert.False(state.IsMoving); // The OLD path's own single step is now fully consumed.

        // The pending retarget survived the cell crossing (AdvanceTo does not consume it - only
        // ConsumePendingRetarget does) - the caller is now expected to compute the real replacement
        // path from B=(1,0) to (5,5) and install it via StartWalk.
        var pending = state.ConsumePendingRetarget();
        Assert.Equal(((ushort)5, (ushort)5), pending);
        Assert.Null(state.PendingRetargetDestination); // One-shot consume.

        // Replacement path begins from B, starting fresh at t=400ms - not from A, and not
        // requiring any additional elapsed time before it can begin.
        state.StartWalk([(1, 0), (2, 1), (3, 2), (4, 3), (5, 4), (5, 5)], orthogonalStepMs: 400, Epoch.AddMilliseconds(400));
        Assert.Equal((ushort)1, state.CurrentX);
        Assert.Equal((ushort)0, state.CurrentY);
        Assert.Equal(((ushort)5, (ushort)5), state.Destination);
    }

    // Two retargets before the step completes - only the LATEST must survive, matching pinned
    // ud->to_x/ud->to_y plain field-assignment "latest wins" (no queue exists in pinned source).
    [Fact]
    public void RequestRetarget_CalledTwiceBeforeStepCompletes_LatestOverwritesEarlier()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 400, Epoch);

        state.RequestRetarget(5, 5);
        Assert.Equal(((ushort)5, (ushort)5), state.PendingRetargetDestination);

        state.RequestRetarget(9, 9);
        Assert.Equal(((ushort)9, (ushort)9), state.PendingRetargetDestination); // Overwritten, not queued.

        state.AdvanceTo(Epoch.AddMilliseconds(400));
        Assert.Equal(((ushort)9, (ushort)9), state.ConsumePendingRetarget());
    }

    // A diagonal step's own 560ms deadline (G_PORING-style WalkSpeed=400) must survive a mid-step
    // retarget exactly like an orthogonal one - RequestRetarget must never re-derive/reset the
    // step's duration based on which axis the CURRENT step happens to move along.
    [Fact]
    public void RequestRetarget_DuringADiagonalStep_PreservesThatSteps560msDeadline()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 1)], orthogonalStepMs: 400, Epoch); // Diagonal step, 560ms.
        Assert.Equal(Epoch.AddMilliseconds(560), state.NextStepDueAt);

        state.RequestRetarget(9, 9);
        Assert.Equal(Epoch.AddMilliseconds(560), state.NextStepDueAt); // Unaffected by the retarget.

        Assert.Empty(state.AdvanceTo(Epoch.AddMilliseconds(559)));
        Assert.Equal((ushort)0, state.CurrentX);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(560));
        Assert.Equal([((ushort)1, (ushort)1)], crossed);
        Assert.Equal(((ushort)9, (ushort)9), state.ConsumePendingRetarget());
    }

    // A retarget received exactly when a step boundary is reached must still be honored as pending
    // for that boundary - AdvanceTo's own retarget-aware early-stop (see its own doc comment) means
    // a retarget requested BEFORE calling AdvanceTo(now) for a `now` that lands exactly on a step
    // boundary is picked up at that same crossing, not deferred to a LATER one.
    [Fact]
    public void RequestRetarget_ThenAdvanceToExactlyTheStepBoundary_IsPickedUpAtThatSameCrossing()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0), (3, 0)], orthogonalStepMs: 150, Epoch);

        state.RequestRetarget(9, 9);
        // Enough elapsed time (450ms) to cross all three remaining cells of the STALE old path if
        // AdvanceTo did not stop early - proving the retarget-aware early-stop actually engages
        // here, not merely that a single-boundary call happens to look the same either way.
        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(450));

        // AdvanceTo must stop at the FIRST crossed cell once a retarget is pending - never silently
        // continue consuming the stale old path's remaining cells just because enough elapsed time
        // was also available for them.
        Assert.Equal([((ushort)1, (ushort)0)], crossed);
        Assert.Equal((ushort)1, state.CurrentX);
        Assert.True(state.IsMoving); // Still "moving" along the STALE path - the caller must now replace it.
        Assert.Equal(((ushort)9, (ushort)9), state.ConsumePendingRetarget());
    }

    // No retarget pending -> AdvanceTo behaves exactly as before (this is a regression guard for
    // the retarget-aware early-stop added to AdvanceTo: it must never trigger when nothing is
    // pending, i.e. ordinary uninterrupted multi-cell advancement must still cross every eligible
    // cell in one call, matching AdvanceTo_MultipleElapsedCells_CrossesEachOneAndReportsAllOfThem).
    [Fact]
    public void AdvanceTo_NoRetargetPending_StillCrossesMultipleCellsInOneCall()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0), (2, 0), (3, 0)], orthogonalStepMs: 150, Epoch);

        var crossed = state.AdvanceTo(Epoch.AddMilliseconds(450));

        Assert.Equal([((ushort)1, (ushort)0), ((ushort)2, (ushort)0), ((ushort)3, (ushort)0)], crossed);
        Assert.Null(state.ConsumePendingRetarget());
    }

    // StartWalk (a fresh walk, or the replacement path installed after consuming a retarget) must
    // always clear any stale pending retarget - a caller that starts a brand-new walk must never
    // have an old, already-superseded retarget silently reappear later.
    [Fact]
    public void StartWalk_ClearsAnyStalePendingRetarget()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 150, Epoch);
        state.RequestRetarget(9, 9);

        state.StartWalk([(0, 0), (2, 0)], orthogonalStepMs: 150, Epoch);

        Assert.Null(state.PendingRetargetDestination);
    }

    // Teleport must also clear a stale pending retarget - a warp/map-change mid-retarget must never
    // let a since-superseded destination reappear after the character is already somewhere else.
    [Fact]
    public void Teleport_ClearsAnyStalePendingRetarget()
    {
        var state = new CharacterMovementState("iz_int01", 0, 0);
        state.StartWalk([(0, 0), (1, 0)], orthogonalStepMs: 150, Epoch);
        state.RequestRetarget(9, 9);

        state.Teleport("int_land01", 50, 50);

        Assert.Null(state.PendingRetargetDestination);
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
