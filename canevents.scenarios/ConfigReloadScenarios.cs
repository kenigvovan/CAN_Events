using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers;
using Newtonsoft.Json.Linq;

namespace canevents.scenarios;

// /eventadmin reload re-reads ModConfig. A changed event with players queued blocks it unless 'force',
// and 'force' drops those queues - which must hand the entry fees back.
[Trait("Category", "E2E")]
public class ConfigReloadScenarios : EventsScenarioBase
{
    private const string PaidDuel = "1x1_ranked";
    private const string FeeItem = "game:gear-rusty";

    // Edits the event's ModConfig file; the original text and definitions come back after the scenario.
    private void EditEventFile(string eventCode, Action<JObject> change)
    {
        static string? CodeIn(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path))["code"]?.Value<string>(); }
            catch (Exception) { return null; }
        }
        string file = Directory.GetFiles(ModPaths.Events, "*.json")
            .First(f => string.Equals(CodeIn(f), eventCode, StringComparison.OrdinalIgnoreCase));
        string original = File.ReadAllText(file);
        var json = JObject.Parse(original);
        change(json);
        File.WriteAllText(file, json.ToString());
        AddCleanup(() =>
        {
            File.WriteAllText(file, original);
            Mod.ReloadDefinitions(World.Api, dropQueues: true);
        });
    }

    [AtlasScenario]
    public async Task Reload_Should_WaitForQueuedPlayers_And_RefundThemWhenForced()
    {
        ITestPlayer p = await World.JoinPlayer("queuedreload");
        await p.GiveItem(FeeItem, 1);
        CommandResult reg = await p.ExecuteCommand("/canreg " + PaidDuel);
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, Manager.FindEventByCode(PaidDuel)) != null,
            "could not queue: " + reg.Message);
        Assert.True(CountOf(p.Player, FeeItem) == 0, "precondition: the fee was not taken");

        EditEventFile(PaidDuel, json => json["reward"]!["winnerItems"]![0]!["quantity"] = 2);

        CommandResult plain = await p.ExecuteCommand("/eventadmin reload");
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, Manager.FindEventByCode(PaidDuel)) != null,
            "a plain reload dropped the queue of a changed event: " + plain.Message);
        Assert.True(EntryFeeEscrow.Has(p.Player), "the escrow is gone after a refused reload");
        Assert.True(Manager.FindEventByCode(PaidDuel).Reward.WinnerItems[0].Quantity == 1,
            "a refused reload still applied the change");

        CommandResult forced = await p.ExecuteCommand("/eventadmin reload force");
        Assert.True(Manager.FindEventByCode(PaidDuel).Reward.WinnerItems[0].Quantity == 2,
            "a forced reload did not apply the change: " + forced.Message);
        Assert.True(Manager.FindRegistration(p.Player.PlayerUID, Manager.FindEventByCode(PaidDuel)) == null,
            "the queue of the changed event survived a forced reload");
        Assert.True(CountOf(p.Player, FeeItem) == 1 && !EntryFeeEscrow.Has(p.Player),
            $"after the forced reload: {FeeItem} x{CountOf(p.Player, FeeItem)}, escrow held={EntryFeeEscrow.Has(p.Player)}; the fee was not refunded");
    }
}
