using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.Network;
using Vintagestory.API.Common;

namespace canevents.src.Gui
{
    // The Content tab: templates, schematics and loadout sets - reusable content that exists
    // independently of any particular place or match.
    public partial class AdminPanelDialog
    {
        // Templates

        private void DrawTemplatesTab()
        {
            Heading("Arena Templates");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##tpl")) networkHandler.RequestTemplateList();
            ImGui.TextDisabled("A template is a prebuilt arena: schematic plus spawn points and types.");
            ImGui.TextDisabled("'Place Here' stamps it into the world and registers it as a ready place.");
            ImGui.Spacing();

            var templateList = networkHandler.TemplateList.Value;
            if (templateList?.Templates != null && templateList.Templates.Length > 0)
            {
                if (ImGui.BeginTable("##TemplatesTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Types", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 90f);
                    ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 160f);
                    ImGui.TableHeadersRow();

                    foreach (var t in templateList.Templates)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text(t.Code ?? "");

                        ImGui.TableSetColumnIndex(1);
                        ImGui.Text(t.PlaceTypes ?? "");

                        ImGui.TableSetColumnIndex(2);
                        if (t.OnDemand)
                            ImGui.TextColored(ColWarn, "on-demand");
                        else
                            ImGui.TextColored(ColMuted, "persistent");

                        ImGui.TableSetColumnIndex(3);
                        ImGui.Text(t.Description ?? "");

                        ImGui.TableSetColumnIndex(4);
                        ImGui.PushID(t.Code ?? "");
                        if (GoSmallButton("Place Here"))
                        {
                            networkHandler.SendPlaceTemplate(t.Code);
                            LogAction(true, "> place template '" + t.Code + "'");
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Builds this arena at your position and registers it as a place.");
                        ImGui.SameLine();
                        if (ImGui.SmallButton("Edit"))
                        {
                            networkHandler.SendTplAction(TplAction.Start, t.Code);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Places it temporarily so you can adjust its spawn points, then save back to the template.");
                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.TextColored(ColMuted, "No templates found.");
                ImGui.Spacing();
                ImGui.TextWrapped("Add arena template JSON files to assets/canevents/config/arenas/ " +
                    "and schematic files to assets/canevents/schematics/");
            }

            DrawTplEditorPanel();
        }

        private void DrawTplEditorPanel()
        {
            var s = networkHandler.TplSession.Value;
            if (s == null || !s.InSession) return;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColAccent, "Editing: " + s.TemplateCode);
            ImGui.TextColored(ColMuted,
                string.Format("Origin: {0}, {1}, {2}   |   Place ID: {3}", s.OriginX, s.OriginY, s.OriginZ, s.PlaceId));
            ImGui.Spacing();

            if (ImGui.Button("Add Spawn Here"))
            {
                networkHandler.SendTplAction(TplAction.AddSpawn);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                networkHandler.SendTplAction(TplAction.Clear);
            }
            ImGui.SameLine();
            if (ImGui.Button("Save"))
            {
                networkHandler.SendTplAction(TplAction.Save);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel & Restore"))
            {
                networkHandler.SendTplAction(TplAction.Cancel);
            }
            ImGui.SameLine();
            if (ImGui.Button("Finish (keep)"))
            {
                networkHandler.SendTplAction(TplAction.Finish);
            }

            ImGui.Spacing();
            int count = s.OffsetsX?.Length ?? 0;
            ImGui.TextColored(ColMuted,
                "Spawn offsets (" + count + "):");

            if (count > 0 && ImGui.BeginTable("##TplOffsets", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30f);
                ImGui.TableSetupColumn("Offset (x, y, z)", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                for (int i = 0; i < count; i++)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(i.ToString());
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(string.Format("{0}, {1}, {2}", s.OffsetsX[i], s.OffsetsY[i], s.OffsetsZ[i]));
                }
                ImGui.EndTable();
            }
        }

        // Schematics

        private string schematicNewCode = "";
        private bool[] schematicSelectedTypes = new bool[typeNames.Length];
        private string schematicFormTarget = null; // which schematic's form is open
        private bool schematicOnDemand = false;

        private void DrawSchematicsTab()
        {
            Heading("Schematics");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##sch")) networkHandler.RequestSchematicList();
            ImGui.TextDisabled("Raw building files from ModConfig/canevents/schematics/.");
            ImGui.TextDisabled("A schematic is just geometry; turn it into a template to give it event types and spawns.");
            ImGui.Spacing();

            var list = networkHandler.SchematicList.Value;
            if (list?.Schematics == null || list.Schematics.Length == 0)
            {
                ImGui.TextColored(ColMuted, "No schematics found.");
                ImGui.TextDisabled("Export a build with the game's worldedit tools, then drop the .json");
                ImGui.TextDisabled("into ModConfig/canevents/schematics/ and hit Refresh.");
                return;
            }

            if (ImGui.BeginTable("##SchematicsTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Template", ImGuiTableColumnFlags.WidthFixed, 75f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 130f);
                ImGui.TableHeadersRow();

                foreach (var s in list.Schematics)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(s.Name ?? "");

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(string.Format("{0}x{1}x{2}", s.SizeX, s.SizeY, s.SizeZ));

                    ImGui.TableSetColumnIndex(2);
                    if (s.HasTemplate)
                        ImGui.TextColored(ColOk, "yes");
                    else
                        ImGui.TextColored(ColMuted, "no");

                    ImGui.TableSetColumnIndex(3);
                    ImGui.PushID(s.Name ?? "");
                    if (ImGui.SmallButton("Create Template"))
                    {
                        schematicFormTarget = s.Name;
                        schematicNewCode = s.Name ?? "";
                        for (int i = 0; i < schematicSelectedTypes.Length; i++) schematicSelectedTypes[i] = false;
                    }
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            if (schematicFormTarget != null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(ColAccent, "New template from '" + schematicFormTarget + "'");
                ImGui.Spacing();
                ImGui.InputText("Template code", ref schematicNewCode, 64);
                ImGui.Text("Event types:");
                DrawTypeCheckboxes(schematicSelectedTypes, "##schematic");
                ImGui.Checkbox("On Demand (blocks appear only during the event)", ref schematicOnDemand);
                ImGui.Spacing();
                if (ImGui.Button("Create"))
                {
                    if (!string.IsNullOrWhiteSpace(schematicNewCode))
                    {
                        var selected = new List<string>();
                        for (int i = 0; i < typeNames.Length; i++)
                        {
                            if (schematicSelectedTypes[i]) selected.Add(typeNames[i]);
                        }
                        if (selected.Count > 0)
                        {
                            networkHandler.SendCreateTemplateFromSchematic(schematicFormTarget, schematicNewCode, selected.ToArray(), schematicOnDemand);
                            schematicFormTarget = null;
                            schematicNewCode = "";
                            schematicOnDemand = false;
                        }
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    schematicFormTarget = null;
                }
            }
        }

        // Loadouts

        private void DrawLoadoutsTab()
        {
            var list = networkHandler.LoadoutList.Value;
            int setCount = list?.Events?.Sum(e => e.Sets?.Length ?? 0) ?? 0;

            ImGui.TextColored(ColAccent, "Loadout Sets");
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh##lo")) networkHandler.RequestLoadoutList();
            ImGui.TextDisabled("Gear handed to players at match start. Sets edited here override the ones in event JSON.");
            ImGui.TextDisabled("Several sets on one event become a choice players make before the match.");
            ImGui.Spacing();

            if (setCount == 0)
            {
                ImGui.TextColored(ColMuted, "No managed loadout sets yet.");
                ImGui.TextDisabled("Build one below, or use 'Import from JSON' to copy an event's inline sets");
                ImGui.TextDisabled("into editable ones.");
            }
            else if (ImGui.BeginTable("##LoadoutsTable", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                new Vector2(ImGui.GetContentRegionAvail().X, System.Math.Min(setCount * 22f + 26f, 160f))))
            {
                ImGui.TableSetupColumn("Event",   ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Set",     ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Items",   ImGuiTableColumnFlags.WidthFixed, 50f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var ev in list.Events)
                {
                    if (ev.Sets == null) continue;
                    foreach (var set in ev.Sets)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);

                        bool isSelected = selectedLoadoutEvent == ev.EventCode && selectedLoadoutSet == set.Name;
                        ImGui.PushID(ev.EventCode + "/" + set.Name);
                        if (ImGui.Selectable(ev.EventCode, isSelected, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            selectedLoadoutEvent = ev.EventCode;
                            selectedLoadoutSet = set.Name;
                        }
                        ImGui.PopID();

                        ImGui.TableSetColumnIndex(1);
                        ImGui.Text(set.Name ?? "");

                        ImGui.TableSetColumnIndex(2);
                        ImGui.Text((set.ItemCodes?.Length ?? 0).ToString());
                    }
                }

                ImGui.EndTable();
            }

            // Detail panel for selected set
            LoadoutSetMsg selSet = null;
            if (!string.IsNullOrEmpty(selectedLoadoutEvent) && list?.Events != null)
            {
                var selEv = System.Array.Find(list.Events, e => e.EventCode == selectedLoadoutEvent);
                selSet = selEv?.Sets != null ? System.Array.Find(selEv.Sets, s => s.Name == selectedLoadoutSet) : null;
            }

            if (selSet != null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(ColAccent,
                    selectedLoadoutEvent + " / " + selSet.Name);
                ImGui.Spacing();

                if (ImGui.Button("Load into editor"))
                {
                    loadoutEventCode = selectedLoadoutEvent;
                    loadoutSetName = selSet.Name;
                    pendingLoadoutItems.Clear();
                    if (selSet.ItemCodes != null)
                    {
                        for (int i = 0; i < selSet.ItemCodes.Length; i++)
                        {
                            int qty = selSet.ItemQuantities != null && i < selSet.ItemQuantities.Length
                                ? selSet.ItemQuantities[i] : 1;
                            pendingLoadoutItems.Add((selSet.ItemCodes[i], qty));
                        }
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copies this set into the editor below.\nSaving under the same name overwrites it.");
                ImGui.SameLine();
                // Re-arm the ref flag before opening: Escape flips it to false, and
                // BeginPopupModal with a false ref never opens again.
                if (StopButton("Delete this set"))
                {
                    deleteSetModalOpen = true;
                    ImGui.OpenPopup("##ConfirmDeleteSet");
                }

                if (ImGui.BeginPopupModal("##ConfirmDeleteSet", ref deleteSetModalOpen,
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
                {
                    ImGui.TextColored(ColBad, "Delete loadout set?");
                    ImGui.Text(selSet.Name + "  (" + selectedLoadoutEvent + ")");
                    ImGui.Spacing();
                    if (ImGui.Button("Yes, delete", new Vector2(100, 0)))
                    {
                        networkHandler.SendDeleteLoadoutSet(selectedLoadoutEvent, selSet.Name);
                        selectedLoadoutSet = "";
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(70, 0))) ImGui.CloseCurrentPopup();
                    ImGui.EndPopup();
                }

                ImGui.Spacing();
                if (selSet.ItemCodes != null && selSet.ItemCodes.Length > 0
                    && ImGui.BeginTable("##SetItems", 3,
                        ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("",         ImGuiTableColumnFlags.WidthFixed, 32f);
                    ImGui.TableSetupColumn("Item",     ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Qty",      ImGuiTableColumnFlags.WidthFixed, 35f);
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < selSet.ItemCodes.Length; i++)
                    {
                        string code = selSet.ItemCodes[i] ?? "";
                        int qty = selSet.ItemQuantities != null && i < selSet.ItemQuantities.Length
                            ? selSet.ItemQuantities[i] : 1;
                        ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
                        ImGui.TableSetColumnIndex(0); DrawItemIcon(code, 28f);
                        ImGui.TableSetColumnIndex(1); ImGui.Text(code);
                        ImGui.TableSetColumnIndex(2); ImGui.Text(qty.ToString());
                    }
                    ImGui.EndTable();
                }
            }

            // Create new set
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColAccent,
                pendingLoadoutItems.Count > 0 ? "Set editor" : "Create set");
            ImGui.TextDisabled("Equip the gear you want, then pull it in from your own inventory.");
            ImGui.Spacing();

            if (ImGui.Button("+ Hotbar"))
                AddInventoryToPending(Vintagestory.API.Config.GlobalConstants.hotBarInvClassName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds everything in your hotbar to the list below.");
            ImGui.SameLine();
            if (ImGui.Button("+ Armor"))
                AddInventoryToPending(Vintagestory.API.Config.GlobalConstants.characterInvClassName);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Adds what you are currently wearing.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear list")) pendingLoadoutItems.Clear();

            if (pendingLoadoutItems.Count > 0)
            {
                float pendingHeight = System.Math.Min(pendingLoadoutItems.Count * 34f + 26f, 180f);
                if (ImGui.BeginTable("##PendingItems", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
                    new Vector2(ImGui.GetContentRegionAvail().X, pendingHeight)))
                {
                    ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 32f);
                    ImGui.TableSetupColumn("Item",  ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Qty",   ImGuiTableColumnFlags.WidthFixed, 70f);
                    ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 22f);
                    ImGui.TableHeadersRow();

                    int removeAt = -1;
                    for (int i = 0; i < pendingLoadoutItems.Count; i++)
                    {
                        var (code, qty) = pendingLoadoutItems[i];
                        ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
                        ImGui.TableSetColumnIndex(0); DrawItemIcon(code, 28f);
                        ImGui.TableSetColumnIndex(1); ImGui.Text(code);

                        // Editable: hotbar stack sizes rarely match what you want to hand out.
                        ImGui.TableSetColumnIndex(2);
                        ImGui.PushID("qty_" + i);
                        int edited = qty;
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.InputInt("##q", ref edited, 0))
                            pendingLoadoutItems[i] = (code, System.Math.Max(1, edited));
                        ImGui.PopID();

                        ImGui.TableSetColumnIndex(3);
                        ImGui.PushID("rm_" + i);
                        if (ImGui.SmallButton("x")) removeAt = i;
                        ImGui.PopID();
                    }
                    if (removeAt >= 0) pendingLoadoutItems.RemoveAt(removeAt);

                    ImGui.EndTable();
                }
            }
            else
            {
                ImGui.TextColored(ColMuted, "List is empty. Use + Hotbar / + Armor.");
            }

            ImGui.Spacing();
            var eventCodes = list?.Events != null
                ? list.Events.Select(e => e.EventCode).ToArray()
                : new string[0];
            int ecIdx = System.Array.IndexOf(eventCodes, loadoutEventCode);
            if (ecIdx < 0) ecIdx = 0;
            if (eventCodes.Length > 0 && string.IsNullOrEmpty(loadoutEventCode))
                loadoutEventCode = eventCodes[0];
            ImGui.SetNextItemWidth(160);
            if (eventCodes.Length > 0 && ImGui.Combo("Event##lec", ref ecIdx, eventCodes, eventCodes.Length))
                loadoutEventCode = eventCodes[ecIdx];
            else if (eventCodes.Length == 0)
                ImGui.InputText("Event##lec", ref loadoutEventCode, 32);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(190); ImGui.InputText("Set name##lsn", ref loadoutSetName, 64);
            ImGui.Spacing();

            bool canSave = pendingLoadoutItems.Count > 0
                && !string.IsNullOrWhiteSpace(loadoutEventCode)
                && !string.IsNullOrWhiteSpace(loadoutSetName);
            if (!canSave) ImGui.BeginDisabled();
            if (GoButton("Save set"))
            {
                networkHandler.SendSaveLoadout(
                    loadoutEventCode.Trim(),
                    loadoutSetName.Trim(),
                    pendingLoadoutItems.Select(x => x.code).ToArray(),
                    pendingLoadoutItems.Select(x => x.qty).ToArray());
                loadoutSetName = "";
                pendingLoadoutItems.Clear();
            }
            if (!canSave) ImGui.EndDisabled();
            ImGui.SameLine();
            bool canImport = !string.IsNullOrWhiteSpace(loadoutEventCode);
            if (!canImport) ImGui.BeginDisabled();
            if (ImGui.Button("Import from JSON"))
            {
                networkHandler.SendImportInlineSets(loadoutEventCode.Trim());
            }
            if (!canImport) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copies the loadout sets defined in this event's JSON into editable ones,\nso you can tweak them here instead of editing files.");

            if (!canSave && pendingLoadoutItems.Count > 0)
                ImGui.TextDisabled("Pick an event and type a set name to save.");
        }

        /// <summary>
        /// Pulls one of the player's own inventories into the pending set, merging stacks of
        /// the same item.
        /// </summary>
        private void AddInventoryToPending(string inventoryClassName)
        {
            foreach (var (code, qty) in GuiItemUtil.ReadInventory(capi, inventoryClassName))
            {
                int idx = pendingLoadoutItems.FindIndex(x => x.code == code);
                if (idx >= 0) pendingLoadoutItems[idx] = (code, pendingLoadoutItems[idx].qty + qty);
                else pendingLoadoutItems.Add((code, qty));
            }
        }
    }
}
