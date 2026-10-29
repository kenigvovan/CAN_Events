using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// HG loot chests: placed on the loot spots at the start, filled from their tier, journaled and removed at the end.
[Trait("Category", "E2E")]
public class HungerGamesLootScenarios : EventsScenarioBase
{
    private const string SoloHg = "hungergames_solotest";

    private sealed class LootArena
    {
        public required List<(BlockPos Pos, string Tier, int Original)> Spots;
        public required Dictionary<string, HashSet<string>> Tables;
        public required int MinSlots;
        public required int MaxSlots;
        public required Block Chest;
    }

    // Reads the tables from the event as it will run, so set attribute overrides first.
    private async Task<LootArena> GivenLootArena()
    {
        int area = NewArea();
        BlockPos centre = AreaCentre(area);
        var place = AddPlace("loot-" + area, centre, 30, PlaceTags.HungerGames);
        var layout = Manager.HungerGamesSetup.GenerateLayout(place, centre, 12f, snapToTerrain: false, spawnCount: 4);
        Assert.True(layout.Ok, "layout refused: " + layout.Message);

        var attrs = (JObject)Manager.FindEventByCode(SoloHg).GetBehavior<HungerGamesEB>().Attributes.Token;
        var tables = ((JObject)attrs["lootTables"]!).Properties().ToDictionary(
            p => p.Name, p => p.Value.Select(e => e.Value<string>("code")!).ToHashSet(), StringComparer.OrdinalIgnoreCase);

        var setup = Manager.HungerGamesSetup.Describe(place);
        Assert.True(setup.SpotX.Length > 0, "precondition: the layout made no loot spots");
        var ba = World.Api.World.BlockAccessor;
        var spots = new List<(BlockPos, string, int)>();
        for (int i = 0; i < setup.SpotX.Length; i++)
        {
            await SurfaceAt(setup.SpotX[i], setup.SpotZ[i]);
            var pos = new BlockPos(setup.SpotX[i], setup.SpotY[i], setup.SpotZ[i], 0);
            spots.Add((pos, setup.SpotTier[i], ba.GetBlock(pos).BlockId));
        }

        return new LootArena
        {
            Spots = spots,
            Tables = tables,
            MinSlots = attrs["minSlotsPerChest"]?.Value<int>() ?? 3,
            MaxSlots = attrs["maxSlotsPerChest"]?.Value<int>() ?? 6,
            Chest = World.Api.World.GetBlock(new AssetLocation(attrs["lootContainerBlock"]?.Value<string>() ?? "game:chest-east"))
        };
    }

    private async Task<(ITestPlayer, RunningEvent)> StartSoloMatch(string name)
    {
        ITestPlayer p = await World.JoinPlayer(name);
        var (ok, msg) = Manager.AddToQueue(Manager.FindEventByCode(SoloHg), p.Player);
        Assert.True(ok, "could not queue: " + msg);
        return (p, await StartQueuedMatch(p));
    }

    private IBlockEntityContainer ChestAt(BlockPos pos)
    {
        var chest = World.Api.World.BlockAccessor.GetBlockEntity(pos) as IBlockEntityContainer;
        Assert.True(chest != null, $"no loot chest inventory at {pos}");
        return chest!;
    }

    private void AssertFilledFromTier(LootArena arena, BlockPos pos, string tier, string when)
    {
        var chest = ChestAt(pos);
        var filled = chest.Inventory.Where(s => !s.Empty).ToList();
        Assert.True(filled.Count >= Math.Min(arena.MinSlots, chest.Inventory.Count) && filled.Count <= arena.MaxSlots,
            $"{when}: the {tier} chest at {pos} has {filled.Count} filled slot(s), expected {arena.MinSlots}..{arena.MaxSlots}");
        foreach (var slot in filled)
        {
            string code = slot.Itemstack.Collectible.Code.ToString();
            Assert.True(arena.Tables.TryGetValue(tier, out var allowed) && allowed.Contains(code),
                $"{when}: the {tier} chest at {pos} holds {code}, which is not in the '{tier}' table");
        }
    }

    [AtlasScenario]
    public async Task LootChests_Should_BeFilledFromTheirTier_And_Removed_After_TheMatch()
    {
        UseCountdown(1);
        var arena = await GivenLootArena();
        var (p, _) = await StartSoloMatch("looter");
        var ba = World.Api.World.BlockAccessor;

        foreach (var (pos, tier, _) in arena.Spots)
        {
            Assert.True(ba.GetBlock(pos).BlockId == arena.Chest.BlockId, $"no loot chest on the spot at {pos} ({tier})");
            AssertFilledFromTier(arena, pos, tier, "at the start");
        }

        await p.ExecuteCommand("/eventadmin stop");
        await World.Ticks(10);

        foreach (var (pos, tier, original) in arena.Spots)
        {
            var now = ba.GetBlock(pos);
            Assert.True(now.BlockId == original,
                $"the {tier} loot spot at {pos} is {now.Code} after the match, was {World.Api.World.GetBlock(original)?.Code}");
        }
    }

    // The feast re-rolls the feast tier's chests (free slots only) and leaves the others alone.
    [AtlasScenario]
    public async Task Feast_Should_RefillItsTierOnly()
    {
        const string FeastTier = "cornucopia";
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["feastAtSeconds"] = 8;
            attrs["feastAnnounceSeconds"] = 3;
            attrs["feastTier"] = FeastTier;
            attrs["sponsorEverySeconds"] = 0;
        });
        var arena = await GivenLootArena();
        var feastSpot = arena.Spots.FirstOrDefault(s => string.Equals(s.Tier, FeastTier, StringComparison.OrdinalIgnoreCase));
        var otherSpot = arena.Spots.FirstOrDefault(s => !string.Equals(s.Tier, FeastTier, StringComparison.OrdinalIgnoreCase));
        Assert.True(feastSpot.Pos != null && otherSpot.Pos != null, "precondition: the layout needs a feast-tier spot and another one");

        var (p, match) = await StartSoloMatch("feaster");

        // Looted before the feast: empty the feast chest, remember the other one.
        var feastChest = ChestAt(feastSpot.Pos);
        foreach (var slot in feastChest.Inventory) { slot.Itemstack = null; slot.MarkDirty(); }
        string Contents(IBlockEntityContainer c) => string.Join(";", c.Inventory
            .Select(s => s.Empty ? "-" : s.Itemstack.Collectible.Code + "x" + s.Itemstack.StackSize));
        string otherBefore = Contents(ChestAt(otherSpot.Pos));
        Assert.True(match.ElapsedSeconds < 8, "precondition: the feast already happened before the chest was emptied");

        await World.Until(() => match.ElapsedSeconds >= 10, "the feast at 8 s", 1200);

        AssertFilledFromTier(arena, feastSpot.Pos, FeastTier, "after the feast");
        Assert.True(Contents(ChestAt(otherSpot.Pos)) == otherBefore,
            $"the feast changed the {otherSpot.Tier} chest at {otherSpot.Pos}");

        await p.ExecuteCommand("/eventadmin stop");
    }

    // A sponsor gift is a sponsor-tier item dropped onto a living tribute (6 blocks above them).
    // What happens to an uncollected gift after the match is R17, not checked again here.
    [AtlasScenario]
    public async Task SponsorGift_Should_DropASponsorTierItem_OntoALivingTribute()
    {
        const string SponsorTier = "rare";
        UseCountdown(1);
        UseEventAttributes<HungerGamesEB>(SoloHg, attrs =>
        {
            attrs["sponsorEverySeconds"] = 5;
            attrs["sponsorTier"] = SponsorTier;
            attrs["feastAtSeconds"] = 0;
        });
        var arena = await GivenLootArena();
        var allowed = arena.Tables[SponsorTier];
        var (p, match) = await StartSoloMatch("sponsored");

        bool IsGift(Vintagestory.API.Common.Entities.Entity e, BlockPos at)
            => e is EntityItem item && item.Alive
               && allowed.Contains(item.Itemstack.Collectible.Code.ToString())
               && Math.Abs(item.Pos.X - (at.X + 0.5)) <= 3 && Math.Abs(item.Pos.Z - (at.Z + 0.5)) <= 3;
        int carriedBefore = allowed.Sum(code => CountOf(p.Player, code));

        // Either still falling or lying next to them, or already picked up.
        await World.Until(() =>
            World.Api.World.LoadedEntities.Values.Any(e => IsGift(e, p.Position))
            || allowed.Sum(code => CountOf(p.Player, code)) > carriedBefore,
            "a sponsor gift from the '" + SponsorTier + "' table near the tribute", 1200);
        Assert.True(match.ElapsedSeconds >= 5, $"the gift came at {match.ElapsedSeconds:0.#} s, before the first sponsor time (5 s)");

        await p.ExecuteCommand("/eventadmin stop");
    }
}
