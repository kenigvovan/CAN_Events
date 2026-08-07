using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
// Needed for the parameterless Value<T>() extension on JToken (else it binds to JToken.Value<T>(object key)).
using Newtonsoft.Json.Linq;
using canevents.src.Network;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    // Hunger Games arena setup: layout generator, zone, loot spots, in-world preview/marker,
    // and the advanced editor for the event's loot tables and zone phases.
    public partial class AdminPanelDialog
    {
        private float hgRadius = 40f;
        private string hgLootTier = "";
        private int hgLootTierIdx = 0;

        // InputFloat commits on Enter/defocus, so a same-frame button click would send the PRE-edit
        // value. These flags defer the send by one frame, past the input's own draw.
        private bool hgSetZonePending;
        private bool hgGenSendPending;

        // Client-side marker for "Add loot spot here": highlights the block under the admin's feet
        // in the selected tier's colour. Nothing goes over the network.
        private bool hgPlacementMarkerWanted;       // re-set by DrawHungerGamesSection every frame
        private int hgPlacementTierIdx = -1;        // -1 = neutral (free-text tier fallback)
        private bool hgPlacementMarkerShown;
        private BlockPos hgPlacementMarkerPos;
        private int hgPlacementMarkerColor;
        private const int HgPlacementHighlightSlot = 7802; // must not collide with other mods' slots

        // Last place asked for HG setup data, so we fetch once per selection instead of every frame.
        private long hgSetupReqPlace = -1;
        // In-world preview toggle (zone ring + loot/spawn markers), server keeps it per admin.
        private bool hgPreviewOn = false;
        private bool hgSnapTerrain = true;  // generator: follow ground surface (off for schematic floors)
        private bool hgGenArmed = false;    // second-click confirm before overwriting existing spawns/loot

        // Advanced editor buffers: parsed from the server's config JSON, re-serialized on Save.
        // Classes (not structs) so `ref field` edits in ImGui inputs hit the stored row, not a copy.
        private sealed class HgPhaseRow { public float End; public float Radius; public float Dps; }
        private sealed class HgLootRow { public string Code = ""; public int Min = 1; public int Max = 1; public float Weight = 1f; }
        private sealed class HgTierEdit { public string Name = ""; public List<HgLootRow> Rows = new List<HgLootRow>(); }
        private readonly List<HgPhaseRow> hgEditPhases = new List<HgPhaseRow>();
        private readonly List<HgTierEdit> hgEditTiers = new List<HgTierEdit>();
        private string hgEditLoadedFor = null; // event code the buffers were parsed for
        private string hgNewTierName = "";
        // HG variant picked in the editor's combo. Each variant (ffa/duo/ranked/solotest) has its OWN
        // loot tables, so without this an edit lands on whichever one the server resolved.
        private string hgCfgEventCode = null;
        // Off by default: a save must touch only the variant shown in the combo.
        private bool hgCfgApplyToAll = false;
        // Setup message a re-request was already sent for: one packet per server push, not per frame.
        private object hgCfgReqAfterMsg = null;

        /// <summary>
        /// Highlights the block a loot spot would be added on, in the selected tier's colour.
        /// Re-sent only when block or tier changes; cleared when the HG section is off screen.
        /// </summary>
        private void UpdateHgPlacementMarker()
        {
            var plr = capi.World?.Player;

            if (!hgPlacementMarkerWanted || plr?.Entity == null)
            {
                if (hgPlacementMarkerShown && plr != null)
                {
                    capi.World.HighlightBlocks(plr, HgPlacementHighlightSlot,
                        new List<BlockPos>(), new List<int>());
                    hgPlacementMarkerShown = false;
                    hgPlacementMarkerPos = null;
                }
                return;
            }

            var pos = plr.Entity.Pos.AsBlockPos.DownCopy();
            // Preview-marker palette, made translucent (highlight ints are ABGR; alpha in top byte).
            int color = (EventManagers.eventBehavior.Shared.ZoneVisualizer.OwnerColor(hgPlacementTierIdx)
                & 0x00FFFFFF) | (110 << 24);

            if (hgPlacementMarkerShown && pos.Equals(hgPlacementMarkerPos) && color == hgPlacementMarkerColor)
                return;

            capi.World.HighlightBlocks(plr, HgPlacementHighlightSlot,
                new List<BlockPos> { pos }, new List<int> { color });
            hgPlacementMarkerShown = true;
            hgPlacementMarkerPos = pos;
            hgPlacementMarkerColor = color;
        }

        /// <summary>Tier colour as an ImGui vector, for the swatch next to the tier combo.</summary>
        private static Vector4 TierColorVec(int tierIdx)
        {
            int c = EventManagers.eventBehavior.Shared.ZoneVisualizer.OwnerColor(tierIdx);
            return new Vector4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1f);
        }

        private void DrawHungerGamesSection(PlaceListEntryMsg place)
        {
            // Fetch the setup picture once per selection; the server pushes a fresh one after edits.
            if (hgSetupReqPlace != place.Id)
            {
                hgSetupReqPlace = place.Id;
                hgLootTierIdx = 0;
                hgGenArmed = false;
                hgCfgEventCode = null;   // another place may run a different set of HG variants
                hgCfgReqAfterMsg = null;
                networkHandler.SendRequestHgSetup(place.Id);
                if (hgPreviewOn) networkHandler.SendHgPreview(place.Id, true);
            }
            var setup = networkHandler.HgSetup.Value;
            bool haveSetup = setup != null && setup.PlaceId == place.Id;
            string[] tiers = haveSetup && setup.Tiers != null && setup.Tiers.Length > 0 ? setup.Tiers : null;

            ImGui.Spacing();
            ImGui.TextColored(ColAccent, "Hunger Games");

            if (haveSetup && !string.IsNullOrEmpty(setup.Overview))
                ImGui.TextWrapped(setup.Overview);

            if (haveSetup) DrawHgReadiness(setup);

            if (ImGui.CollapsingHeader("How Hunger Games setup works##hghelp"))
            {
                ImGui.BulletText("Fastest path: stand at the arena centre and hit 'Generate full layout'.");
                ImGui.BulletText("The zone is a circle that shrinks over the match; players outside take damage.");
                ImGui.BulletText("Loot spots are chest positions rolled at match start; pick a tier per spot.");
            }

            DrawStandingCheck(place, "the zone or loot spot would be written to another place or rejected.");

            // One-click layout
            ImGui.Spacing();
            DrawHgGenerateButton(place);

            if (ImGui.Checkbox("Preview zone & markers in world##hgprev", ref hgPreviewOn))
                networkHandler.SendHgPreview(place.Id, hgPreviewOn);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Draws the storm ring (grey), spawns (blue) and loot chests (by tier)\nas particles at this arena. Stand in the arena to see them.");

            // Zone
            ImGui.Spacing();
            ImGui.SetNextItemWidth(120);
            ImGui.InputFloat("Zone radius##hg", ref hgRadius, 1f, 10f, "%.1f");
            if (hgRadius <= 0f) hgRadius = 1f;
            // Deferred one frame from the click so a value still being typed has committed.
            if (hgSetZonePending)
            {
                hgSetZonePending = false;
                networkHandler.SendSetHgZone(place.Id, hgRadius);
                LogAction(true, "> set HG zone r=" + hgRadius.ToString("0.#", CultureInfo.InvariantCulture)
                    + " on '" + place.Name + "'");
            }
            ImGui.SameLine();
            if (ImGui.Button("Set zone here")) hgSetZonePending = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Centers the shrinking zone on the block under your feet, on the selected place.");

            // Add a loot spot at the chosen tier
            ImGui.Spacing();
            if (tiers != null)
            {
                if (hgLootTierIdx >= tiers.Length) hgLootTierIdx = 0;
                ImGui.SetNextItemWidth(160);
                ImGui.Combo("Loot tier##hg", ref hgLootTierIdx, tiers, tiers.Length);
                ImGui.SameLine();
                ImGui.ColorButton("##hgtiercol", TierColorVec(hgLootTierIdx),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(19, 19));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This tier's marker colour - the block under your feet is\nhighlighted with it while this section is open.");

                hgPlacementMarkerWanted = true;
                hgPlacementTierIdx = hgLootTierIdx;

                if (setup.TierSummaries != null && hgLootTierIdx < setup.TierSummaries.Length)
                    ImGui.TextDisabled(setup.TierSummaries[hgLootTierIdx]);
                if (ImGui.Button("Add loot spot here"))
                {
                    networkHandler.SendAddHgLoot(place.Id, tiers[hgLootTierIdx]);
                    LogAction(true, "> add HG loot spot (" + tiers[hgLootTierIdx] + ") on '" + place.Name + "'");
                }
            }
            else
            {
                // No event config yet: fall back to a free-text tier so setup still works.
                hgPlacementMarkerWanted = true;
                hgPlacementTierIdx = -1; // neutral grey: tier colours unknown without the config
                ImGui.SetNextItemWidth(120);
                ImGui.InputText("Loot tier##hg", ref hgLootTier, 32);
                ImGui.SameLine();
                if (ImGui.Button("Add loot spot here"))
                {
                    networkHandler.SendAddHgLoot(place.Id, string.IsNullOrWhiteSpace(hgLootTier) ? null : hgLootTier.Trim());
                    LogAction(true, "> add HG loot spot on '" + place.Name + "'");
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Adds a chest spawn at the block under your feet, on the selected place.");

            // Existing loot spots: per-spot re-tier + remove
            ImGui.Spacing();
            if (haveSetup && setup.SpotX != null && setup.SpotX.Length > 0)
            {
                ImGui.Text("Loot spots: " + setup.SpotX.Length);
                for (int i = 0; i < setup.SpotX.Length; i++)
                {
                    ImGui.Text(string.Format("({0}, {1}, {2})", setup.SpotX[i], setup.SpotY[i], setup.SpotZ[i]));
                    ImGui.SameLine();
                    if (tiers != null)
                    {
                        int cur = Array.IndexOf(tiers, setup.SpotTier[i]);
                        if (cur < 0) cur = 0;
                        int sel = cur;
                        ImGui.SetNextItemWidth(130);
                        if (ImGui.Combo("##hgretier" + i, ref sel, tiers, tiers.Length) && sel != cur)
                        {
                            networkHandler.SendSetHgLootTier(place.Id, i, tiers[sel]);
                            LogAction(true, "> re-tier HG loot #" + i + " -> " + tiers[sel]);
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled(setup.SpotTier[i]);
                    }
                    ImGui.SameLine();
                    if (StopButton("x##hgrmloot" + i))
                    {
                        networkHandler.SendRemoveHgLoot(place.Id, i);
                        LogAction(true, "> remove HG loot #" + i + " on '" + place.Name + "'");
                    }
                }
            }
            else if (haveSetup)
            {
                ImGui.TextDisabled("No loot spots yet - add some above.");
            }
            else
            {
                ImGui.TextDisabled("Loading loot spots...");
            }

            if (ImGui.SmallButton("Refresh##hg")) networkHandler.SendRequestHgSetup(place.Id);
            ImGui.SameLine();
            if (StopButton("Clear all loot spots"))
            {
                networkHandler.SendClearHgLoot(place.Id);
                LogAction(true, "> clear HG loot spots on '" + place.Name + "'");
            }

            if (haveSetup) DrawHgConfigEditor(place, setup);
        }

        private void DrawHgConfigEditor(PlaceListEntryMsg place, HgSetupMsg s)
        {
            if (string.IsNullOrEmpty(s.EventCode)) return; // no event → nothing to edit
            if (!ImGui.CollapsingHeader("Edit loot tables & zone phases (advanced)##hgcfg")) return;

            if (hgEditLoadedFor != s.EventCode) HgLoadEditBuffers(s);
            if (string.IsNullOrEmpty(hgCfgEventCode)) hgCfgEventCode = s.EventCode;

            // Loot tables live on the EVENT, not the arena: variants have separate pools on one place.
            var codes = s.HgEventCodes != null && s.HgEventCodes.Length > 0
                ? s.HgEventCodes
                : new[] { s.EventCode };
            int idx = Array.IndexOf(codes, hgCfgEventCode);
            if (idx < 0) idx = Math.Max(0, Array.IndexOf(codes, s.EventCode));
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Event##hgcfgev", ref idx, codes, codes.Length))
            {
                hgCfgEventCode = codes[idx];
                hgCfgReqAfterMsg = null;   // re-ask for the newly picked variant
            }

            // Server answered for a different variant than selected: ask again, once per received
            // message rather than once per frame.
            if (!string.IsNullOrEmpty(hgCfgEventCode) && hgCfgEventCode != s.EventCode)
            {
                if (!ReferenceEquals(hgCfgReqAfterMsg, s))
                {
                    hgCfgReqAfterMsg = s;
                    networkHandler.SendRequestHgSetup(place.Id, hgCfgEventCode);
                }
                ImGui.TextDisabled("Loading config for '" + hgCfgEventCode + "'...");
                return;
            }

            ImGui.Checkbox("Apply to all HG variants##hgcfgall", ref hgCfgApplyToAll);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: saves these tables/phases into every Hunger Games event\n("
                    + string.Join(", ", codes) + "), so the arena plays the same loot in FFA, duo and ranked.\n"
                    + "Off: saves only '" + s.EventCode + "'.");

            // Spell out the write target: a save hitting the other variants is invisible until a
            // match rolls the wrong loot.
            if (hgCfgApplyToAll)
                ImGui.TextColored(ColWarn, "Saving writes ALL of: " + string.Join(", ", codes));
            else
                ImGui.TextDisabled("Saving writes '" + s.EventCode + "' only.");
            ImGui.TextDisabled("Stored in the event JSON; applies next match.");

            // Zone phases
            ImGui.Separator();
            ImGui.TextColored(ColAccent, "Zone phases");
            ImGui.TextDisabled("  ends(s)   radius   dps-outside");
            int removeP = -1;
            for (int i = 0; i < hgEditPhases.Count; i++)
            {
                var p = hgEditPhases[i];
                ImGui.PushID("hgph" + i);
                ImGui.SetNextItemWidth(80); ImGui.InputFloat("##end", ref p.End); ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.InputFloat("##rad", ref p.Radius); ImGui.SameLine();
                ImGui.SetNextItemWidth(70); ImGui.InputFloat("##dps", ref p.Dps); ImGui.SameLine();
                if (StopButton("x")) removeP = i;
                ImGui.PopID();
            }
            if (removeP >= 0) hgEditPhases.RemoveAt(removeP);
            if (ImGui.SmallButton("Add phase##hgphadd"))
                hgEditPhases.Add(new HgPhaseRow { End = 60f, Radius = 10f, Dps = 1f });

            // Loot tiers
            ImGui.Separator();
            ImGui.TextColored(ColAccent, "Loot tiers");
            int removeT = -1;
            for (int ti = 0; ti < hgEditTiers.Count; ti++)
            {
                var te = hgEditTiers[ti];
                ImGui.PushID("hgtier" + ti);
                if (ImGui.TreeNode(te.Name + " (" + te.Rows.Count + " items)##t" + ti))
                {
                    ImGui.TextDisabled("  code                              min  max  weight");
                    int removeR = -1;
                    for (int ri = 0; ri < te.Rows.Count; ri++)
                    {
                        var r = te.Rows[ri];
                        ImGui.PushID(ri);
                        ImGui.SetNextItemWidth(230); ImGui.InputText("##code", ref r.Code, 128); ImGui.SameLine();
                        ImGui.SetNextItemWidth(50); ImGui.InputInt("##min", ref r.Min, 0, 0); ImGui.SameLine();
                        ImGui.SetNextItemWidth(50); ImGui.InputInt("##max", ref r.Max, 0, 0); ImGui.SameLine();
                        ImGui.SetNextItemWidth(60); ImGui.InputFloat("##wt", ref r.Weight); ImGui.SameLine();
                        if (StopButton("x")) removeR = ri;
                        ImGui.PopID();
                    }
                    if (removeR >= 0) te.Rows.RemoveAt(removeR);
                    if (ImGui.SmallButton("Add item##add" + ti))
                        te.Rows.Add(new HgLootRow { Code = "game:", Min = 1, Max = 1, Weight = 1f });
                    ImGui.SameLine();

                    // Picking beats typing: a typo in an item code only surfaces at match start.
                    if (GoSmallButton("Add held item##addheld" + ti))
                    {
                        if (GuiItemUtil.TryReadHeldItem(capi, out var heldCode, out var heldQty))
                        {
                            int qty = Math.Max(1, heldQty);
                            var existing = te.Rows.Find(r => string.Equals(r.Code, heldCode, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                // Widen the range rather than duplicate the entry, which would
                                // double that item's effective weight.
                                if (qty < existing.Min) existing.Min = qty;
                                if (qty > existing.Max) existing.Max = qty;
                                LogAction(true, "> '" + heldCode + "' already in tier '" + te.Name + "', range now " + existing.Min + "-" + existing.Max);
                            }
                            else
                            {
                                // Held stack size becomes the roll amount; edit min/max for a range.
                                te.Rows.Add(new HgLootRow { Code = heldCode, Min = qty, Max = qty, Weight = 1f });
                            }
                        }
                        else
                        {
                            LogAction(false, "> hold the item you want to add first");
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Adds whatever you are holding to this tier, with its stack size as min/max.\nDon't forget to Save config below.");
                    ImGui.SameLine();
                    if (StopButton("Remove tier##rm" + ti)) removeT = ti;
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            if (removeT >= 0) hgEditTiers.RemoveAt(removeT);

            ImGui.SetNextItemWidth(140); ImGui.InputText("##hgnewtier", ref hgNewTierName, 48); ImGui.SameLine();
            if (ImGui.SmallButton("Add tier##hgaddtier") && !string.IsNullOrWhiteSpace(hgNewTierName))
            {
                string nm = hgNewTierName.Trim();
                if (!hgEditTiers.Any(t => string.Equals(t.Name, nm, StringComparison.OrdinalIgnoreCase)))
                    hgEditTiers.Add(new HgTierEdit { Name = nm });
                hgNewTierName = "";
            }

            // Save / Reload
            ImGui.Spacing();
            if (GoButton("Save config##hgcfgsave"))
            {
                networkHandler.SendSaveHgConfig(s.EventCode, HgLootToJson(), HgPhasesToJson(), place.Id, hgCfgApplyToAll);
                LogAction(true, "> save HG config for '"
                    + (hgCfgApplyToAll ? string.Join(", ", codes) : s.EventCode) + "'");
            }
            ImGui.SameLine();
            if (StopButton("Reload from saved##hgcfgreload")) HgLoadEditBuffers(s);
        }

        private void HgLoadEditBuffers(HgSetupMsg s)
        {
            hgEditPhases.Clear();
            hgEditTiers.Clear();
            try
            {
                if (!string.IsNullOrWhiteSpace(s.ZonePhasesJson)
                    && Newtonsoft.Json.Linq.JToken.Parse(s.ZonePhasesJson) is Newtonsoft.Json.Linq.JArray pa)
                    foreach (var p in pa)
                        if (p is Newtonsoft.Json.Linq.JObject po)
                            hgEditPhases.Add(new HgPhaseRow
                            {
                                End = po["endsAtSeconds"]?.Value<float>() ?? 0f,
                                Radius = po["radius"]?.Value<float>() ?? 0f,
                                Dps = po["outsideDps"]?.Value<float>() ?? 0f
                            });
            }
            catch { /* leave phases empty on malformed json */ }

            try
            {
                if (!string.IsNullOrWhiteSpace(s.LootTablesJson)
                    && Newtonsoft.Json.Linq.JToken.Parse(s.LootTablesJson) is Newtonsoft.Json.Linq.JObject lo)
                    foreach (var prop in lo.Properties())
                    {
                        var te = new HgTierEdit { Name = prop.Name };
                        if (prop.Value is Newtonsoft.Json.Linq.JArray ea)
                            foreach (var it in ea)
                                if (it is Newtonsoft.Json.Linq.JObject eo)
                                    te.Rows.Add(new HgLootRow
                                    {
                                        Code = eo["code"]?.Value<string>() ?? "",
                                        Min = eo["minQty"]?.Value<int>() ?? 1,
                                        Max = eo["maxQty"]?.Value<int>() ?? 1,
                                        Weight = eo["weight"]?.Value<float>() ?? 1f
                                    });
                        hgEditTiers.Add(te);
                    }
            }
            catch { /* leave tiers empty on malformed json */ }

            hgEditLoadedFor = s.EventCode;
        }

        private string HgPhasesToJson()
        {
            var arr = new Newtonsoft.Json.Linq.JArray();
            foreach (var p in hgEditPhases)
                arr.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["endsAtSeconds"] = p.End,
                    ["radius"] = p.Radius,
                    ["outsideDps"] = p.Dps
                });
            return arr.ToString();
        }

        private string HgLootToJson()
        {
            var obj = new Newtonsoft.Json.Linq.JObject();
            foreach (var te in hgEditTiers)
            {
                if (string.IsNullOrWhiteSpace(te.Name)) continue;
                var arr = new Newtonsoft.Json.Linq.JArray();
                foreach (var r in te.Rows)
                {
                    if (string.IsNullOrWhiteSpace(r.Code)) continue;
                    arr.Add(new Newtonsoft.Json.Linq.JObject
                    {
                        ["code"] = r.Code.Trim(),
                        ["minQty"] = r.Min,
                        ["maxQty"] = r.Max,
                        ["weight"] = r.Weight
                    });
                }
                obj[te.Name] = arr;
            }
            return obj.ToString();
        }

        // One-click arena. Reused in the wizard's Spawns step to satisfy the "needs N spawns" gate.
        private void DrawHgGenerateButton(PlaceListEntryMsg place)
        {
            ImGui.SetNextItemWidth(90);
            ImGui.InputFloat("Layout radius##hggen", ref hgRadius, 1f, 5f, "%.0f");
            if (hgRadius < 4f) hgRadius = 4f;
            // Deferred one frame from the click so a value still being typed has committed.
            if (hgGenSendPending)
            {
                hgGenSendPending = false;
                networkHandler.SendGenerateHgLayout(place.Id, hgRadius, hgSnapTerrain);
                LogAction(true, "> generate HG layout r=" + hgRadius.ToString("0", CultureInfo.InvariantCulture)
                    + " on '" + place.Name + "'");
            }
            ImGui.SameLine();
            ImGui.Checkbox("Snap to ground##hgsnap", ref hgSnapTerrain);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On: each spawn/chest follows the ground surface (natural, uneven arenas).\nOff: keeps your flat feet level - use for a schematic arena floor.");

            // Replacing an existing layout takes a second click to confirm.
            var s = networkHandler.HgSetup.Value;
            bool hasData = s != null && s.PlaceId == place.Id
                && (s.SpawnCount > 0 || (s.SpotX != null && s.SpotX.Length > 0));

            if (GoButton(hasData && hgGenArmed ? "Confirm: replace layout" : "Generate full layout here"))
            {
                if (hasData && !hgGenArmed)
                {
                    hgGenArmed = true;
                }
                else
                {
                    hgGenSendPending = true; // sent next frame, above, with the committed radius
                    hgGenArmed = false;
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Stand at the arena centre, then generate: a ring of tribute spawns, the zone here,\n"
                    + "and cornucopia/mid/outer loot chests. Tune anything afterwards.");

            if (hasData && hgGenArmed)
                ImGui.TextColored(ColWarn, string.Format(
                    "This overwrites {0} spawn(s) and {1} loot spot(s) - click again to confirm.",
                    s.SpawnCount, s.SpotX?.Length ?? 0));
        }

        // Match-readiness checklist + warnings from the server's HgSetup snapshot.
        private void DrawHgReadiness(HgSetupMsg s)
        {
            HgCheck(s.HasBounds, "Bounds");
            ImGui.SameLine();
            HgCheck(s.RequiredSpawns > 0 && s.SpawnCount >= s.RequiredSpawns,
                "Spawns " + s.SpawnCount + "/" + s.RequiredSpawns);
            ImGui.SameLine();
            HgCheck(s.ZoneSet, s.ZoneSet ? "Zone r=" + s.ZoneRadius.ToString("0.#") : "Zone");
            ImGui.SameLine();
            int lootN = s.SpotX?.Length ?? 0;
            HgCheck(lootN > 0, "Loot " + lootN);
            ImGui.SameLine();
            HgCheck(s.UsesCornucopiaTier, "Cornucopia");

            if (s.Warnings != null)
                foreach (var w in s.Warnings)
                    ImGui.TextColored(ColWarn, "! " + w);
        }

        private void HgCheck(bool ok, string label)
            => ImGui.TextColored(ok ? ColOk : ColWarn, (ok ? "[+] " : "[-] ") + label);
    }
}
