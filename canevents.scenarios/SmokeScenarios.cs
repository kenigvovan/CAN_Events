using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using Vintagestory.API.Common;

namespace canevents.scenarios;

[Trait("Category", "Smoke")]
public class SmokeScenarios : EventsScenarioBase
{
    [AtlasScenario]
    public async Task Server_Should_BootWithoutErrors()
    {
        var errors = World.BootDiagnostics
            .Where(e => e.Tick is null && e.Level >= EnumLogType.Error)
            .Select(e => $"[{e.DescribeSource()}] {e.Message}")
            .ToList();
        Assert.True(errors.Count == 0, "errors while booting:\n" + string.Join("\n", errors));
        await World.Ticks(1);
    }

    [AtlasScenario]
    public async Task Mod_Should_LoadEventsAndServices()
    {
        Assert.True(Mod != null, "canevents mod system is not loaded");
        Assert.True(Manager != null, "CANEventManager was not created");
        Assert.True(Manager.Events.Count > 0, "no event definitions were loaded");
        Assert.True(Manager.FindEventByCode("hungergames") != null, "the shipped 'hungergames' event is missing");
        Assert.True(Manager.HungerGamesSetup != null, "HungerGamesSetupService was not created");
        await World.Ticks(1);
    }

    [AtlasScenario]
    public async Task EventAdminStatus_Should_Answer()
    {
        ITestPlayer admin = await World.JoinPlayer("smokeadmin");
        CommandResult result = await admin.ExecuteCommand("/eventadmin status");
        Assert.True(result.Ok, "/eventadmin status failed: " + result.Message);
    }
}
