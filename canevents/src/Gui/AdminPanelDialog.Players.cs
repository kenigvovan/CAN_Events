using System;
using System.Linq;
using System.Numerics;
using canevents.src.Network;
using ImGuiNET;

namespace canevents.src.Gui
{
    // Players tab: who may watch matches, and who is barred from the queue. Both were command-only
    // before, which meant leaving the panel and remembering an exact privilege spelling to change
    // either one.
    public partial class AdminPanelDialog
    {
        private string accessNameInput = "";
        private int accessOnlineIdx;

        // No fetch-on-first-draw here: BeginTab already asks for a tab's data the frame it becomes
        // active (see RequestDataFor, case 9). The list is built by walking every known player, so
        // it is fetched on arrival and on demand, never per frame.
        private void DrawPlayersTab()
        {
            DrawAccessGrantBox();
            ImGui.Separator();
            DrawAccessTable();
        }

        private void DrawAccessGrantBox()
        {
            Heading("Pick a player");

            // The dropdown covers the common case in one click. The name box stays for the case it
            // cannot serve: someone offline who has never been granted anything, and so appears
            // neither here nor in the table below.
            var online = OnlineNames();
            if (online.Length > 0)
            {
                ImGui.SetNextItemWidth(220);
                if (ImGui.Combo("Online##accessonline", ref accessOnlineIdx, online, online.Length))
                    accessNameInput = online[accessOnlineIdx];
                HelpMarker("Everyone on the server right now. Picking one fills the name box below.");
            }
            else ImGui.TextColored(ColMuted, "Nobody else is online.");

            ImGui.SetNextItemWidth(220);
            ImGui.InputText("Name##accessname", ref accessNameInput, 64);
            HelpMarker("Exact player name, as the server last saw it. For someone offline who is not "
                + "in the table below, type it here - they must have joined at least once.");

            bool hasName = !string.IsNullOrWhiteSpace(accessNameInput);
            ImGui.BeginDisabled(!hasName);

            if (GoButton("Allow spectating"))
            {
                networkHandler.SendPlayerAccessAction(PlayerAccessAction.GrantSpectate, name: accessNameInput);
                LogAction(true, "> grant spectate: " + accessNameInput);
                accessNameInput = "";
            }
            ImGui.SameLine();
            if (StopButton("Blacklist from queue"))
            {
                networkHandler.SendPlayerAccessAction(PlayerAccessAction.BlacklistAdd, name: accessNameInput);
                LogAction(true, "> blacklist: " + accessNameInput);
                accessNameInput = "";
            }

            ImGui.EndDisabled();
        }

        // Online names, from the same list the table draws - no second source to go stale. Sorted, so
        // the dropdown does not reshuffle itself as people come and go.
        private string[] OnlineNames()
        {
            var list = networkHandler.PlayerAccess.Value;
            if (list?.Players == null) return Array.Empty<string>();

            var names = list.Players
                .Where(p => p.Online && !string.IsNullOrEmpty(p.PlayerName))
                .Select(p => p.PlayerName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // A player who logged off since the last refresh would leave the index past the end.
            if (accessOnlineIdx >= names.Length) accessOnlineIdx = 0;
            return names;
        }

        private void DrawAccessTable()
        {
            Heading("Players");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##access")) networkHandler.RequestPlayerAccess();

            var list = networkHandler.PlayerAccess.Value;
            if (list?.Players == null)
            {
                ImGui.TextDisabled("Not loaded.");
                return;
            }

            if (list.Players.Length == 0)
            {
                ImGui.TextDisabled("Nobody online, and nobody has been granted or barred yet.");
                return;
            }

            ImGui.TextColored(ColMuted,
                "Everyone online, plus anyone already allowed or blacklisted. Others are left out.");
            if (list.OmittedCount > 0)
                ImGui.TextColored(ColWarn, list.OmittedCount + " more not shown - use the box above.");

            float height = Math.Min(list.Players.Length * 26f + 26f, 360f);
            if (!ImGui.BeginTable("##PlayerAccessTable", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, height))) return;

            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Role", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Spectate", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 250f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var p in list.Players) DrawAccessRow(p);

            ImGui.EndTable();
        }

        private void DrawAccessRow(PlayerAccessEntryMsg p)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            if (p.Online) ImGui.Text(p.PlayerName);
            else ImGui.TextColored(ColMuted, p.PlayerName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(p.Online ? "Online" : "Offline");

            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(ColMuted, p.RoleCode ?? "");

            ImGui.TableSetColumnIndex(2);
            if (p.MaySpectate)
            {
                // Where the right comes from decides what "Revoke" has to do, so it is on screen
                // rather than a surprise: a role-granted right needs a per-player denial.
                ImGui.TextColored(ColOk, p.SpectateFromRole ? "yes (role)" : "yes");
                if (p.SpectateFromRole && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Comes from the role '" + (p.RoleCode ?? "?")
                        + "'. Revoking here denies it for this player only; the role keeps it.");
            }
            else ImGui.TextColored(ColMuted, "no");

            ImGui.TableSetColumnIndex(3);
            if (p.QueueBlacklisted) ImGui.TextColored(ColBad, "blacklisted");
            else ImGui.TextColored(ColMuted, "allowed");

            ImGui.TableSetColumnIndex(4);
            ImGui.PushID("access_" + p.PlayerUid);

            if (p.MaySpectate)
            {
                if (StopSmallButton("Revoke spectate"))
                {
                    networkHandler.SendPlayerAccessAction(PlayerAccessAction.RevokeSpectate, p.PlayerUid);
                    LogAction(true, "> revoke spectate: " + p.PlayerName);
                }
            }
            else if (GoSmallButton("Allow spectate"))
            {
                networkHandler.SendPlayerAccessAction(PlayerAccessAction.GrantSpectate, p.PlayerUid);
                LogAction(true, "> grant spectate: " + p.PlayerName);
            }

            ImGui.SameLine();

            if (p.QueueBlacklisted)
            {
                if (GoSmallButton("Unblacklist"))
                {
                    networkHandler.SendPlayerAccessAction(PlayerAccessAction.BlacklistRemove, p.PlayerUid);
                    LogAction(true, "> unblacklist: " + p.PlayerName);
                }
            }
            else if (StopSmallButton("Blacklist"))
            {
                networkHandler.SendPlayerAccessAction(PlayerAccessAction.BlacklistAdd, p.PlayerUid);
                LogAction(true, "> blacklist: " + p.PlayerName);
            }

            ImGui.PopID();
        }
    }
}
