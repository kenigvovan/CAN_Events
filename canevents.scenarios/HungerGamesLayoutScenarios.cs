using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.MathTools;

namespace canevents.scenarios;

// "Generate full layout here": ring of spawns, zone and loot spots around where the admin stands.
[Trait("Category", "E2E")]
public class HungerGamesLayoutScenarios : EventsScenarioBase
{
    private (EventPlace Place, BlockPos Stand) GivenHgArena(int halfSize = 60)
    {
        int area = NewArea();
        BlockPos stand = AreaCentre(area);
        var place = AddPlace("hg-layout-" + area, stand, halfSize, PlaceTags.HungerGames);
        return (place, stand);
    }

    private static void AssertInsideRadius(string what, int x, int z, BlockPos centre, float radius)
    {
        double dx = x - centre.X, dz = z - centre.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        Assert.True(dist <= radius, $"{what} at ({x}, {z}) is {dist:0.#} blocks from the centre, radius is {radius}");
    }

    [AtlasTheory]
    [InlineData(10f)]
    [InlineData(25f)]
    [InlineData(40f)]
    public async Task Layout_Should_KeepSpawnsAndLoot_InsideRadius(float radius)
    {
        var (place, stand) = GivenHgArena();

        var result = Manager.HungerGamesSetup.GenerateLayout(place, stand, radius, snapToTerrain: false, spawnCount: 8);

        Assert.True(result.Ok, "layout refused: " + result.Message);
        Assert.True(HungerGamesSetupService.TryReadZone(place, out var centre, out float stored), "no zone was written");
        Assert.Equal(radius, stored);
        Assert.Equal(8, place.spawnPositions.Count);
        foreach (var sp in place.spawnPositions)
            AssertInsideRadius("spawn", sp.X, sp.Z, centre, radius);

        var setup = Manager.HungerGamesSetup.Describe(place);
        Assert.True(setup.SpotX.Length > 0, "no loot spots were generated");
        for (int i = 0; i < setup.SpotX.Length; i++)
            AssertInsideRadius("loot spot " + i, setup.SpotX[i], setup.SpotZ[i], centre, radius);
        await World.Ticks(1);
    }

    [AtlasScenario]
    public async Task Layout_Should_CentreZone_OnTheStandingAdmin()
    {
        var (place, stand) = GivenHgArena();

        var result = Manager.HungerGamesSetup.GenerateLayout(place, stand, 20f, snapToTerrain: false, spawnCount: 6);

        Assert.True(result.Ok, "layout refused: " + result.Message);
        Assert.True(HungerGamesSetupService.TryReadZone(place, out var centre, out _), "no zone was written");
        Assert.True(centre.X == stand.X && centre.Z == stand.Z,
            $"zone centre ({centre.X}, {centre.Z}) is not where the admin stood ({stand.X}, {stand.Z})");
        await World.Ticks(1);
    }

    // The panel refuses radii under 4; the server must hold the same floor.
    [AtlasScenario]
    public async Task Layout_Should_RaiseRadius_ToFour_When_TooSmall()
    {
        var (place, stand) = GivenHgArena();

        var result = Manager.HungerGamesSetup.GenerateLayout(place, stand, 2f, snapToTerrain: false, spawnCount: 4);

        Assert.True(result.Ok, "layout refused: " + result.Message);
        Assert.True(HungerGamesSetupService.TryReadZone(place, out var centre, out float stored), "no zone was written");
        Assert.Equal(4f, stored);
        foreach (var sp in place.spawnPositions)
            AssertInsideRadius("spawn", sp.X, sp.Z, centre, stored);
        await World.Ticks(1);
    }

    [AtlasScenario]
    public async Task Layout_Should_Warn_When_RingLeavesBounds()
    {
        var (place, stand) = GivenHgArena(halfSize: 15);

        var result = Manager.HungerGamesSetup.GenerateLayout(place, stand, 40f, snapToTerrain: false, spawnCount: 8);

        Assert.True(result.Ok, "layout refused: " + result.Message);
        Assert.True(result.Message.Contains("WARNING"), "no out-of-bounds warning in: " + result.Message);
        await World.Ticks(1);
    }
}
