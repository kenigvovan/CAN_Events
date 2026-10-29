using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// What a finished match leaves behind in its arena.
[Trait("Category", "E2E")]
public class MatchCleanupScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";
    private const string Item = "game:gear-rusty";

    private EntityItem Drop(BlockPos at, bool issued)
    {
        var world = World.Api.World;
        var stack = new ItemStack(world.GetItem(new AssetLocation(Item)), 1);
        if (issued) IssuedGear.Mark(stack, "scenario");
        return (EntityItem)world.SpawnItemEntity(stack, at.ToVec3d().Add(0.5, 1, 0.5));
    }

    private int ItemsOnArena(EventPlace place, bool issued)
        => World.Api.World.GetEntitiesInsideCuboid(place.BoundsMin, place.BoundsMax,
                e => e is EntityItem item && item.Alive && IssuedGear.IsIssued(item.Itemstack) == issued)
            .Length;

    // R17: only issued kit is swept at the end; anything else stays for the next match or for whoever walks by.
    [AtlasScenario]
    public async Task Arena_Should_HaveNoItemsOnTheGround_When_MatchEnds()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddArena("cleanup-" + area, centre, 12, PlaceTags.Arena1x1, 2);

        ITestPlayer a = await World.JoinPlayer("cleanupa");
        ITestPlayer b = await World.JoinPlayer("cleanupb");
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        await StartQueuedMatch(a, b);

        // Away from the spawn line, so nobody picks them up.
        BlockPos corner = centre.AddCopy(-8, 0, 8);
        Drop(corner, issued: true);
        Drop(corner, issued: false);
        await World.Ticks(5);
        Assert.True(ItemsOnArena(place, issued: true) == 1 && ItemsOnArena(place, issued: false) == 1,
            "precondition: both dropped items must be lying on the arena");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Ticks(5);

        Assert.True(ItemsOnArena(place, issued: true) == 0, "issued kit was not swept (control)");
        Assert.True(ItemsOnArena(place, issued: false) == 0,
            "an unmarked item is still lying on the arena after the match");
    }

    // R20: a chest on the arena is a way out for issued kit - the sweep only looks at the ground.
    [AtlasScenario]
    public async Task IssuedKit_Should_NotStayInAChestOnTheArena_When_MatchEnds()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        AddArena("chest-" + area, centre, 12, PlaceTags.Arena1x1, 2);

        ITestPlayer a = await World.JoinPlayer("chesta");
        ITestPlayer b = await World.JoinPlayer("chestb");
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        await StartQueuedMatch(a, b);

        var world = World.Api.World;
        var ba = world.BlockAccessor;
        BlockPos at = centre.AddCopy(-8, 0, 8);
        var chestBlock = world.GetBlock(new AssetLocation("game:chest-east"));
        Assert.True(chestBlock != null, "chest block not found");
        ba.SetBlock(chestBlock.BlockId, at);
        if (ba.GetBlockEntity(at) == null) ba.SpawnBlockEntity(chestBlock.EntityClass, at);
        var chest = ba.GetBlockEntity(at) as IBlockEntityContainer;
        Assert.True(chest != null, "no container block entity at " + at);

        var stack = new ItemStack(world.GetItem(new AssetLocation(Item)), 1);
        IssuedGear.Mark(stack, a.Player.PlayerName);
        chest.Inventory[0].Itemstack = stack;
        chest.Inventory[0].MarkDirty();

        try
        {
            await a.ExecuteCommand("/eventadmin stop");
            await World.Ticks(5);

            // The chest itself may be gone (that would be the arena restore, R21); then nothing is left in it.
            var after = ba.GetBlockEntity(at) as IBlockEntityContainer;
            int left = after == null ? 0
                : after.Inventory.Count(slot => slot.Itemstack != null && IssuedGear.IsIssued(slot.Itemstack));
            Assert.True(left == 0, $"{left} issued item(s) still sit in a chest on the arena after the match");
        }
        finally
        {
            ba.SetBlock(0, at);
        }
    }
}
