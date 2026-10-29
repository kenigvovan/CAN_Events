using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// Nothing holds a participant on the arena during a match: no bounds check, no command or teleport guard.
// 1x1 has no storm either, so walking off works as well as any teleport.
[Trait("Category", "E2E")]
public class EscapeScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";

    private async Task<(ITestPlayer, ITestPlayer, RunningEvent, EventPlace)> GivenRunningDuel(string nameA, string nameB)
    {
        UseCountdown(1);
        int area = NewArea();
        var place = AddArena("escape-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);
        ITestPlayer a = await World.JoinPlayer(nameA);
        ITestPlayer b = await World.JoinPlayer(nameB);
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        return (a, b, await StartQueuedMatch(a, b), place);
    }

    private static bool Inside(EventPlace place, BlockPos pos)
        => pos.X >= place.BoundsMin.X && pos.X <= place.BoundsMax.X && pos.Z >= place.BoundsMin.Z && pos.Z <= place.BoundsMax.Z;

    // An active participant is on the arena; one who leaves is either brought back or out of the match.
    [AtlasScenario]
    public async Task Participant_Should_NotStayActiveOutsideTheArena()
    {
        var (runner, other, match, place) = await GivenRunningDuel("runaway", "leftbehind");
        string uid = runner.Player.PlayerUID;
        BlockPos away = await SurfaceAt(place.BoundsMax.X + 100, place.BoundsMax.Z + 100);

        await runner.TeleportTo(away);
        float leftAt = match.ElapsedSeconds;
        await World.Until(() => match.ElapsedSeconds >= leftAt + 3f, "3 s off the arena", 600);

        Assert.True(Inside(place, runner.Position) || !match.IsActive(uid),
            $"3 s after leaving, the participant is still active at {runner.Position}, outside the arena bounds");

        await other.ExecuteCommand("/eventadmin stop");
    }

    // R20 path: off the arena mid-match, the issued kit can be put into the player's own chest.
    [AtlasScenario]
    public async Task IssuedKit_Should_NotStayInAChestOutsideTheArena_When_MatchEnds()
    {
        var (runner, other, _, place) = await GivenRunningDuel("stasher", "stashfoe");
        BlockPos away = await SurfaceAt(place.BoundsMax.X + 100, place.BoundsMax.Z + 100);
        await runner.TeleportTo(away);

        var world = World.Api.World;
        var ba = world.BlockAccessor;
        BlockPos chestAt = away.AddCopy(2, 0, 0);
        var chestBlock = world.GetBlock(new AssetLocation("game:chest-east"));
        ba.SetBlock(chestBlock.BlockId, chestAt);
        if (ba.GetBlockEntity(chestAt) == null) ba.SpawnBlockEntity(chestBlock.EntityClass, chestAt);
        AddCleanup(() => ba.SetBlock(0, chestAt));
        var chest = (IBlockEntityContainer)ba.GetBlockEntity(chestAt);

        ItemSlot? kitSlot = runner.Player.InventoryManager.Inventories.Values
            .Where(inv => inv.ClassName != "creative")
            .SelectMany(inv => inv)
            .FirstOrDefault(s => !s.Empty && IssuedGear.IsIssued(s.Itemstack));
        Assert.True(kitSlot != null, "precondition: the participant carries no issued kit");
        string code = kitSlot!.Itemstack.Collectible.Code.ToString();
        chest.Inventory[0].Itemstack = kitSlot.TakeOutWhole();
        chest.Inventory[0].MarkDirty();
        kitSlot.MarkDirty();

        await other.ExecuteCommand("/eventadmin stop");
        await World.Ticks(10);

        int left = chest.Inventory.Count(s => !s.Empty && IssuedGear.IsIssued(s.Itemstack));
        Assert.True(left == 0, $"issued {code} stashed off the arena is still in the chest after the match");
    }
}
