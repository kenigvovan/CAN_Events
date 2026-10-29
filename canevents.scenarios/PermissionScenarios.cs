using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;

namespace canevents.scenarios;

// Test players join on the highest role; an ordinary survival player ("suplayer") must be refused the admin commands.
// The mod's admin network packets are checked server-side too (RequireAdmin), but a test client can't send them.
[Trait("Category", "E2E")]
public class PermissionScenarios : EventsScenarioBase
{
    [AtlasScenario]
    public async Task OrdinaryPlayer_Should_BeRefusedAdminCommands_ButMayQueue()
    {
        ITestPlayer adminA = await World.JoinPlayer("permadmina");
        ITestPlayer adminB = await World.JoinPlayer("permadminb");
        ITestPlayer plain = await World.JoinPlayer("plainplayer", new JoinOptions { Role = "suplayer" });

        // A live match for /eventadmin stop to (not) stop.
        var duel = Manager.FindEventByCode("1x1");
        foreach (var p in new[] { adminA, adminB })
        {
            var (ok, msg) = Manager.AddToQueue(duel, p.Player);
            Assert.True(ok, p.Player.PlayerName + " could not queue: " + msg);
        }
        Manager.AttemptToComposeTeams();
        var match = MatchOf(adminA);
        Assert.True(match != null, "precondition: no match to protect");

        CommandResult stop = await plain.ExecuteCommand("/eventadmin stop");
        Assert.True(!stop.Ok, "an ordinary player ran /eventadmin stop: " + stop.Message);
        Assert.True(Manager.RunningEventsSnapshot().Contains(match!), "the match was stopped by an ordinary player");

        CommandResult place = await plain.ExecuteCommand("/canep new");
        Assert.True(!place.Ok, "an ordinary player ran /canep new: " + place.Message);

        CommandResult restore = await plain.ExecuteCommand("/eventadmin inv restore plainplayer");
        Assert.True(!restore.Ok, "an ordinary player ran /eventadmin inv restore: " + restore.Message);

        CommandResult reg = await plain.ExecuteCommand("/canreg 1x1");
        Assert.True(Manager.FindRegistration(plain.Player.PlayerUID, duel) != null,
            "control: an ordinary player could not queue at all: " + reg.Message);

        await adminA.ExecuteCommand("/eventadmin stop");
    }
}
