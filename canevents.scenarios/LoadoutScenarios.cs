using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.EventPlacesManagers.Places;

namespace canevents.scenarios;

// R22 in docs/testing/03-risks.md: the kit is mutator → explicit pick → saved default → first set.
[Trait("Category", "E2E")]
public class LoadoutScenarios : EventsScenarioBase
{
    // Sets in order: Spearman (first), Archer, Plate.
    private const string Duel = "1x1";

    // R22a: a solo /canreg without a set makes the player the registrant, and registrants are
    // skipped both at queue time and before the start, so the saved default is never looked up.
    [AtlasScenario]
    public async Task SavedDefault_Should_BeGiven_When_RegisteredWithoutPick()
    {
        UseCountdown(1);
        int area = NewArea();
        AddArena("loadout-" + area, AreaCentre(area), 12, PlaceTags.Arena1x1, 2);

        ITestPlayer archer = await World.JoinPlayer("savedarcher");
        ITestPlayer foe = await World.JoinPlayer("plainfoe");
        var duel = Manager.FindEventByCode(Duel);
        Assert.True(duel != null, $"shipped event '{Duel}' is required");

        Manager.LoadoutPreferences.Set(archer.Player.PlayerUID, Duel, "Archer");
        foreach (var p in new[] { archer, foe })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }

        await StartQueuedMatch(archer, foe);

        int bows = CountOf(archer.Player, "game:bow-long");
        int spears = CountOf(archer.Player, "game:spear-generic-steel");
        Assert.True(bows == 1 && spears == 0,
            $"saved default 'Archer' was not given: bow-long x{bows}, spear-generic-steel x{spears}");

        await archer.ExecuteCommand("/eventadmin stop");
    }
}
