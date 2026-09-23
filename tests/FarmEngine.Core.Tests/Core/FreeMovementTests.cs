using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of packages/engine-core/src/free-movement.test.ts.
///
/// Free (non-grid) movement: position integrates from a held intent every
/// tick; tiles are terrain/collision, not a movement grid.
///
/// Fixture geometry (makeProject): 6×6 scene, player starts centered on tile
/// (3,4) → position (3.5, 4.5); wall tile at (4,4); NPC at (1,1).
/// </summary>
public class FreeMovementTests
{
    private const string NeedsEngine = "needs Engine/EngineState/Replay + EngineTests.MakeProject — enable at integration";

    private static (EngineContext Ctx, GameState State) MakeEngine(string seed = "free-move-test")
    {
        var project = EngineTests.MakeProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, new EngineState.CreateGameStateOptions { Seed = seed });
        return (ctx, state);
    }

    private static GameState WithIntent(EngineContext ctx, GameState state, double dx, double dy) =>
        Engine.ApplyCommand(ctx, state, new SetMoveIntentCommand(dx, dy)).State;

    private static void CloseTo(double expected, double actual, int digits) =>
        Assert.True(Math.Abs(expected - actual) < Math.Pow(10, -digits) / 2, $"expected {actual} to be close to {expected} ({digits} digits)");

    // describe('free movement')

    [Fact]
    public void StartsOnTheTileCenterWithAnIdleIntent()
    {
        var (_, state) = MakeEngine();
        Assert.Equal(3.5, state.Player.X);
        Assert.Equal(4.5, state.Player.Y);
        Assert.Equal(new MoveIntent { Dx = 0, Dy = 0 }, state.Player.MoveIntent);
    }

    [Fact]
    public void IntegratesPositionFromAHeldIntentAtTheConfiguredSpeed()
    {
        var (ctx, state) = MakeEngine();
        // 4 ticks up at 4.5 tiles/s and 20 t/s = 0.9 tiles.
        var moving = WithIntent(ctx, state, 0, -1);
        var step = Engine.AdvanceTick(ctx, moving, 4);
        Assert.Equal(3.5, step.State.Player.X);
        CloseTo(4.5 - 0.9, step.State.Player.Y, 5);
        Assert.Equal("up", step.State.Player.Direction);
    }

    [Fact]
    public void DoesNotMoveWithoutAnIntentAndStopsWhenTheIntentClears()
    {
        var (ctx, state) = MakeEngine();
        var idle = Engine.AdvanceTick(ctx, state, 10);
        Assert.Equal(3.5, idle.State.Player.X);
        Assert.Equal(4.5, idle.State.Player.Y);

        var moving = WithIntent(ctx, state, 0, -1);
        moving = Engine.AdvanceTick(ctx, moving, 2).State;
        var stopped = Engine.AdvanceTick(ctx, WithIntent(ctx, moving, 0, 0), 10).State;
        Assert.Equal(moving.Player.Y, stopped.Player.Y);
    }

    [Fact]
    public void NormalizesDiagonalMovementNoSqrt2SpeedAdvantage()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, state, -1, 1), 4);
        var dx = step.State.Player.X - 3.5;
        var dy = step.State.Player.Y - 4.5;
        CloseTo(0.9, Math.Sqrt(dx * dx + dy * dy), 5);
        CloseTo(-dx, dy, 10);
    }

    [Fact]
    public void ClampsAgainstAWallTileWithACollisionSkin()
    {
        var (ctx, state) = MakeEngine();
        // Wall at (4,4); moving right from (3.5,4.5) stops at 4 − halfWidth.
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, state, 1, 0), 20);
        Assert.True(step.State.Player.X < 4 - WorldMovement.PlayerHalfWidth + 1e-3);
        Assert.True(step.State.Player.X > 4 - WorldMovement.PlayerHalfWidth - 1e-3);
        Assert.Equal(4.5, step.State.Player.Y);
    }

    [Fact]
    public void SlidesAlongBlockersOnDiagonalInput()
    {
        var (ctx, state) = MakeEngine();
        // Up is blocked by the NPC tile at (1,1) while the box overlaps column 1;
        // the leftward component keeps sliding.
        var start = state with { Player = state.Player with { X = 1.5, Y = 2.9 } };
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, start, -1, -1), 5);
        CloseTo(2 + WorldMovement.PlayerHalfWidth, step.State.Player.Y, 3);
        Assert.True(step.State.Player.X < 1.0);
    }

    [Fact]
    public void IsContainedBySceneBounds()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, state, 0, 1), 60);
        CloseTo(6 - WorldMovement.PlayerHalfWidth, step.State.Player.Y, 2);
    }

    [Fact]
    public void IsBlockedByNpcOccupiedTiles()
    {
        var (ctx, state) = MakeEngine();
        // Approach the NPC at (1,1) from below (start on tile (1,3)).
        var start = state with { Player = state.Player with { X = 1.5, Y = 3.5 } };
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, start, 0, -1), 40);
        CloseTo(2 + WorldMovement.PlayerHalfWidth, step.State.Player.Y, 2);
    }

    [Fact]
    public void FreezesWhileADialogueIsOpen()
    {
        var (ctx, state) = MakeEngine();
        var talking = WithIntent(ctx, state, 0, -1) with { Dialogue = new DialogueState { NpcId = "npc-test", DialogueId = "dlg-1" } };
        var step = Engine.AdvanceTick(ctx, talking, 10);
        Assert.Equal(4.5, step.State.Player.Y);
    }

    [Fact]
    public void PicksUpGroundItemsWhenCrossingOntoTheirTile()
    {
        var project = EngineTests.MakeProject();
        var item = project.Items.Find(i => i.Id == "seed-wheat")!;
        var scene0 = project.Scenes[0];
        var tiles = Tiles.CloneTiles(scene0.Tiles);
        tiles[3][3] = tiles[3][3] with { Item = item };
        project = project with { Scenes = [scene0 with { Tiles = tiles }, .. project.Scenes.Skip(1)] };
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, new EngineState.CreateGameStateOptions { Seed = "pickup" });

        var before = state.Player.Inventory.Find(s => s.Item.Id == "seed-wheat")?.Quantity ?? 0;
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, state, 0, -1), 20);
        var after = step.State.Player.Inventory.Find(s => s.Item.Id == "seed-wheat")?.Quantity ?? 0;
        Assert.Equal(before + 1, after);
        Assert.Contains(step.Effects, e => e.Type == "playerMoved");
    }

    [Fact]
    public void FiresTransitionsWhenTheOccupiedTileChanges()
    {
        var project = EngineTests.MakeProject();
        var cave = Tiles.CreateEmptyScene("scene-cave", "Cave", 5, 5);
        var scene0 = project.Scenes[0] with
        {
            Transitions = [new SceneTransition { FromX = 3, FromY = 2, ToSceneId = "scene-cave", ToX = 2, ToY = 2 }],
        };
        project = project with { Scenes = [scene0, .. project.Scenes.Skip(1), cave] };
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, new EngineState.CreateGameStateOptions { Seed = "transition" });

        // 7 ticks up crosses from tile (3,4) into the transition tile (3,2).
        var step = Engine.AdvanceTick(ctx, WithIntent(ctx, state, 0, -1), 7);
        Assert.Equal("scene-cave", step.State.Player.SceneId);
        // Transition lands on the target tile's center.
        Assert.Equal(2.5, step.State.Player.X);
        Assert.Equal(2.5, step.State.Player.Y);
        Assert.Contains(step.Effects, e => e is SceneChangedEffect { SceneId: "scene-cave" });
    }

    [Fact]
    public void ReplaysDeterministicallyFromTheIntentCommandLog()
    {
        var script = new List<ReplayInput>
        {
            Replay.Command(new SetMoveIntentCommand(0, -1)),
            Replay.Ticks(7),
            Replay.Command(new SetMoveIntentCommand(-1, -1)),
            Replay.Ticks(11),
            Replay.Command(new SetMoveIntentCommand(1, 0)),
            Replay.Ticks(13),
            Replay.Command(new SetMoveIntentCommand(0, 0)),
            Replay.Ticks(5),
        };
        var a = Replay.RunReplay(MakeEngine("replay-seed").Ctx, MakeEngine("replay-seed").State, script);
        var b = Replay.RunReplay(MakeEngine("replay-seed").Ctx, MakeEngine("replay-seed").State, script);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(Hash.HashState(a.State.Player), Hash.HashState(b.State.Player));
    }

    [Fact]
    public void ClampsIntentsToUnitComponents()
    {
        var (ctx, state) = MakeEngine();
        var step = Engine.ApplyCommand(ctx, state, new SetMoveIntentCommand(5, -3));
        Assert.Equal(new MoveIntent { Dx = 1, Dy = -1 }, step.State.Player.MoveIntent);
    }

    // describe('facing from intent')

    [Fact]
    public void FacesThePressedCardinalDirection()
    {
        Assert.Equal("right", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 1, Dy = 0 }, "up"));
        Assert.Equal("down", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 0, Dy = 1 }, "left"));
    }

    [Fact]
    public void KeepsTheCurrentFacingOnAMatchingDiagonal()
    {
        Assert.Equal("up", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 1, Dy = -1 }, "up"));
        Assert.Equal("right", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 1, Dy = -1 }, "right"));
    }

    [Fact]
    public void PrefersTheHorizontalAxisOnANonMatchingDiagonal()
    {
        Assert.Equal("right", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 1, Dy = -1 }, "down"));
    }

    [Fact]
    public void KeepsFacingWhenIdle()
    {
        Assert.Equal("left", WorldMovement.DirectionFromIntent(new MoveIntent { Dx = 0, Dy = 0 }, "left"));
    }
}
