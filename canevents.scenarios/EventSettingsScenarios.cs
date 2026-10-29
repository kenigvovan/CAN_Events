using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using canevents.scenarios.Infrastructure;
using canevents.src.Network;
using Vintagestory.API.Server;

namespace canevents.scenarios;

// The Events tab's settings editor and /eventadmin setting: what a behavior reads is offered,
// only admins may change it, and a bad value is refused without touching the stored one.
[Trait("Category", "E2E")]
public class EventSettingsScenarios : LiveScenarioBase
{
    private const string Key = "winsToWin";

    private string Current() => Manager.EventSettings(Battle).First(s => s.Setting.Key == Key).Value;

    // Puts the value back after the scenario: the edit lands in the event JSON in ModConfig.
    private void RestoreAfter()
    {
        string before = Current();
        AddCleanup(() => Manager.SetEventSetting(Battle, "BattleRounds", Key, before));
    }

    private void DenyAdmin(ITestPlayer p)
    {
        var perms = World.Api.Permissions;
        perms.DenyPrivilege(p.Player.PlayerUID, Privilege.controlserver);
        AddCleanup(() => perms.RemovePrivilegeDenial(p.Player.PlayerUID, Privilege.controlserver));
        Assert.False(p.Player.HasPrivilege(Privilege.controlserver), "precondition: the player still has controlserver");
    }

    private void SendSetting(ITestPlayer p, string value)
    {
        var method = typeof(ServerNetworkHandler).GetMethod("OnSetEventSetting", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, "ServerNetworkHandler.OnSetEventSetting not found (renamed?)");
        method!.Invoke(Manager.NetworkHandler, new object[]
        {
            p.Player, new SetEventSettingMsg { EventCode = Battle, Behavior = "BattleRounds", Key = Key, Value = value }
        });
    }

    [AtlasScenario]
    public Task Settings_Should_ListWhatBehaviorsRead()
    {
        var rows = Manager.EventSettings(Battle);
        Assert.True(rows.Any(s => s.Behavior == "BattleRounds" && s.Setting.Key == Key), "winsToWin is not offered");
        Assert.True(rows.Any(s => s.Behavior == "WaitingRoom" && s.Setting.Key == "lobbySeconds"), "lobbySeconds is not offered");
        Assert.False(rows.Any(s => s.Setting.Key == "loadoutSets"), "loadout sets are offered as a plain setting");
        return Task.CompletedTask;
    }

    [AtlasScenario]
    public async Task Admin_Should_ChangeSetting_And_KeepTheList()
    {
        RestoreAfter();
        ITestPlayer admin = await JoinSurvivor("setadmin");
        string target = Current() == "7" ? "6" : "7";

        var result = await admin.ExecuteCommand("/eventadmin setting " + Battle + " " + Key + " " + target);
        Assert.True(result.Ok, "the admin's command was refused: " + result.Message);
        Assert.True(Current() == target, $"winsToWin is {Current()} after setting {target}");
        // Re-initializing the template must not lose the list.
        Assert.True(Manager.EventSettings(Battle).Count > 3, "the settings list emptied after a save");

        SendSetting(admin, "3");
        Assert.True(Current() == "3", $"the panel's message did not apply: winsToWin is {Current()}");
    }

    [AtlasScenario]
    public async Task NonAdmin_Should_NotChangeSetting_ByCommandOrPanel()
    {
        RestoreAfter();
        ITestPlayer player = await JoinSurvivor("setplayer");
        DenyAdmin(player);
        string before = Current();
        string other = before == "9" ? "8" : "9";

        var result = await player.ExecuteCommand("/eventadmin setting " + Battle + " " + Key + " " + other);
        Assert.False(result.Ok, "a non-admin ran /eventadmin setting");

        SendSetting(player, other);
        Assert.True(Current() == before, $"a non-admin's panel message changed winsToWin to {Current()}");
    }

    [AtlasScenario]
    public Task BadValue_Should_BeRefused_And_LeaveTheValue()
    {
        RestoreAfter();
        string before = Current();

        var (ok, message) = Manager.SetEventSetting(Battle, null, Key, "many");
        Assert.False(ok, "'many' was accepted as a whole number");
        Assert.False(string.IsNullOrEmpty(message), "no reason given");

        var (okUnknown, _) = Manager.SetEventSetting(Battle, null, "noSuchSetting", "1");
        Assert.False(okUnknown, "an unknown key was accepted");
        Assert.True(Current() == before, "a refused value changed the stored one");
        return Task.CompletedTask;
    }
}
