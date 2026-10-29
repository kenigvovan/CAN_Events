using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// R21 in docs/testing/03-risks.md: a hand-built (persistent) arena has no rollback of its own.
[Trait("Category", "E2E")]
public class ArenaIntegrityScenarios : EventsScenarioBase
{
    private const string Duel = "1x1";

    // The block is broken through the block accessor, not the player's break action (Atlas has none),
    // so a future break guard on the player event would not be exercised here; the invariant is the
    // state after the match, which a restore satisfies as well as a guard.
    [AtlasScenario]
    public async Task ArenaWall_Should_StandAgain_When_BrokenDuringTheMatch()
    {
        UseCountdown(1);
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        AddArena("wall-" + area, centre, 12, PlaceTags.Arena1x1, 2);

        var world = World.Api.World;
        var ba = world.BlockAccessor;
        BlockPos wall = await SurfaceAt(centre.X + 5, centre.Z + 5);
        var granite = world.GetBlock(new AssetLocation("game:rock-granite"));
        Assert.True(granite != null, "granite block not found");
        ba.SetBlock(granite.BlockId, wall);
        AddCleanup(() => ba.SetBlock(0, wall));

        ITestPlayer a = await World.JoinPlayer("wallbreaker");
        ITestPlayer b = await World.JoinPlayer("wallwatcher");
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        await StartQueuedMatch(a, b);

        ba.BreakBlock(wall, a.Player);
        Assert.True(ba.GetBlock(wall).BlockId == 0, "precondition: the wall block did not break");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Ticks(10);

        var now = ba.GetBlock(wall);
        Assert.True(now.BlockId == granite.BlockId,
            $"the arena wall at {wall} is {now.Code} after the match, was {granite.Code}");
    }

    // R10: an on-demand arena is stamped at the start and rolled back at the end, but the rollback
    // keeps block ids only, so a chest that stood in the footprint comes back empty.
    [AtlasScenario]
    public async Task ChestUnderOnDemandArena_Should_KeepItsItems_When_ArenaIsRolledBack()
    {
        const string Template = "arena_witchhut";
        const string Own = "game:flint";
        UseCountdown(1);
        int area = NewArea();
        var world = World.Api.World;
        var ba = world.BlockAccessor;

        var template = Manager.ArenaTemplates.Find(t => t.Code == Template);
        Assert.True(template != null, $"shipped template '{Template}' is required");
        Assert.True(Manager.SchematicPlacer.TryLoadSchematic(template, out BlockSchematic schematic, logErrors: true),
            "the template's schematic did not load");

        BlockPos origin = await SurfaceAt(AreaCentre(area).X, AreaCentre(area).Z);

        // Schematics store only non-air blocks, and this one does not overwrite with air:
        // the first stored block is a spot the arena is sure to cover.
        uint index = schematic.Indices[0];
        BlockPos chestAt = origin.AddCopy((int)(index & 0x3ff), (int)((index >> 20) & 0x3ff), (int)((index >> 10) & 0x3ff));
        // The offset can land in a column nobody has loaded; SetBlock there is silently dropped.
        await SurfaceAt(chestAt.X, chestAt.Z);

        var chestBlock = world.GetBlock(new AssetLocation("game:chest-east"));
        Assert.True(chestBlock != null && chestBlock.BlockId != 0, "game:chest-east not found");
        ba.SetBlock(chestBlock.BlockId, chestAt);
        if (ba.GetBlockEntity(chestAt) == null) ba.SpawnBlockEntity(chestBlock.EntityClass, chestAt);
        var chest = ba.GetBlockEntity(chestAt) as IBlockEntityContainer;
        Assert.True(chest != null, $"precondition: no chest block entity at {chestAt} (block {ba.GetBlock(chestAt)?.Code})");
        var ownItem = world.GetItem(new AssetLocation(Own));
        Assert.True(ownItem != null, Own + " not found");
        chest!.Inventory[0].Itemstack = new ItemStack(ownItem, 3);
        chest.Inventory[0].MarkDirty();
        AddCleanup(() => ba.SetBlock(0, chestAt));

        var place = TemplateStamper.Stamp(template, origin, placeBlocks: false,
            Manager.eventPlacesManager, Manager.SchematicPlacer, out string error, p =>
            {
                p.SchematicOnDemand = true;
                p.SchematicTemplateCode = template.Code;
                p.SchematicOrigin = origin.Copy();
                p.spawnPositions.Add(origin.AddCopy(2, 1, schematic.SizeZ / 2));
                p.spawnPositions.Add(origin.AddCopy(schematic.SizeX - 3, 1, schematic.SizeZ / 2));
            });
        Assert.True(place != null, "could not create the on-demand place: " + error);
        DisableAfterScenario(place);

        ITestPlayer a = await World.JoinPlayer("ondemanda");
        ITestPlayer b = await World.JoinPlayer("ondemandb");
        var duel = Manager.FindEventByCode(Duel);
        foreach (var p in new[] { a, b })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        await StartQueuedMatch(a, b);
        Assert.True(ba.GetBlock(chestAt).BlockId != chestBlock.BlockId,
            "precondition: the arena schematic did not cover the chest");

        await a.ExecuteCommand("/eventadmin stop");
        await World.Until(() => ba.GetBlock(chestAt).BlockId == chestBlock.BlockId,
            "the chest to come back with the rollback", 600);

        var after = ba.GetBlockEntity(chestAt) as IBlockEntityContainer;
        int left = after == null ? 0 : after.Inventory.Where(s => s.Itemstack != null)
            .Where(s => s.Itemstack.Collectible.Code.ToString() == Own).Sum(s => s.Itemstack.StackSize);
        Assert.True(left == 3, $"the chest under the arena came back with {Own} x{left}, had 3");
    }
}
