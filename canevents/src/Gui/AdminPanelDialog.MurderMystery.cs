using System.Collections.Generic;
using canevents.src.Network;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    // Murder Mystery arena setup: the spots the mode's weapon token can appear on. There is no setup
    // packet of its own - the spots ride along in the place list's BehaviorSummary, the same way the
    // CTF posts do, so this section is drawn straight off the list the tab already has.
    public partial class AdminPanelDialog
    {
        /// <summary>Deferred one frame, like the CTF post button: the click is handled before the
        /// place list refresh that follows it, and sending inside the draw call reads a list that is
        /// about to be replaced.</summary>
        private bool mmAddPickupPending;

        private void DrawMurderMysterySection(PlaceListEntryMsg place)
        {
            ImGui.Spacing();
            Heading("Murder Mystery - " + (place.Name ?? "?") + " (#" + place.Id + ")");

            if (ImGui.CollapsingHeader("How the weapon spawns work##mmhelp"))
            {
                ImGui.BulletText("The event config names an ordinary item to drop (bowPickupItem) - no new item needed.");
                ImGui.BulletText("Players collect it; every bowPickupRequired of them turns into bowPickupReward");
                ImGui.BulletText("  (a bow and arrows by default). Leftovers above the price stay in the bag.");
                ImGui.BulletText("Until then it is a real item they carry - it can be dropped, or looted off a body.");
                ImGui.BulletText("Spots marked here are the only places it can appear; one is chosen at random.");
                ImGui.BulletText("Never two tokens within bowPickupMinDistance of each other, so one corner");
                ImGui.BulletText("  cannot pile up a stash while the rest of the arena stays empty.");
                ImGui.BulletText("Timing is bowPickupFirstSeconds, then every bowPickupIntervalSeconds.");
                ImGui.BulletText("Anything left lying at the end of the match is taken back off the arena.");
            }

            int count = MmPickupCount(place);
            if (count == 0)
                ImGui.TextColored(ColWarn, "No pickup spots marked: the weapon spawns are off on this arena.");
            else
                ImGui.TextColored(ColOk, count + " pickup spot(s) marked, drawn in the world in yellow.");

            int removeIdx = -1;
            for (int i = 0; i < count; i++)
            {
                ImGui.PushID(i);
                bool inside = PositionInsideBounds(place, place.MmPickupX[i], place.MmPickupZ[i]);
                ImGui.TextColored(inside ? ColMuted : ColBad, string.Format("  #{0}  ({1}, {2}, {3}){4}",
                    i, place.MmPickupX[i], place.MmPickupY[i], place.MmPickupZ[i],
                    inside ? "" : "  OUTSIDE THE ARENA"));
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) removeIdx = i;
                ImGui.PopID();
            }
            if (removeIdx >= 0)
            {
                networkHandler.SendRemoveMmPickup(place.Id, removeIdx);
                LogAction(true, "> remove MM pickup spot #" + removeIdx);
            }

            ImGui.Spacing();
            if (ImGui.SmallButton("Refresh##mm")) networkHandler.RequestPlaceList();

            // The spot lands at your feet, so standing outside the bounds puts it off the arena.
            if (place.HasBounds && !PlayerInsideBounds(place))
            {
                ImGui.TextColored(ColWarn, "You are OUTSIDE this place's bounds:");
                ImGui.TextColored(ColWarn, "the spot would be marked off the arena.");
            }

            if (mmAddPickupPending)
            {
                mmAddPickupPending = false;
                networkHandler.SendAddMmPickup(place.Id);
                LogAction(true, "> add MM pickup spot on '" + place.Name + "'");
            }
            if (GoButton("Mark a pickup spot at the block under my feet"))
                mmAddPickupPending = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Writes to this place by id - standing inside another arena's bounds does not matter.");
            ImGui.SameLine();
            if (StopButton("Clear all spots"))
            {
                networkHandler.SendClearMmPickups(place.Id);
                LogAction(true, "> clear MM pickup spots on '" + place.Name + "'");
            }

            DrawMmSettings(place);
        }

        // Settings editor. The values live in the event JSON, not on the place, so this section is
        // about a variant (murdermystery / murdermystery_bots) rather than about this arena - which
        // is why it opens with the variant picker.

        private sealed class MmRewardRow { public string Code = ""; public int Quantity = 1; }

        private long mmCfgReqPlace = -1;
        /// <summary>The exact packet the edit buffers were filled from. Compared by reference, not by
        /// event code: the server also answers a save with the stored state, and that answer must
        /// refresh the fields - otherwise a value it clamped or rejected would keep showing as typed.
        /// Reloading every frame instead would fight the admin's typing.</summary>
        private MmConfigMsg mmCfgLoadedFrom;
        private int mmCfgEventIdx;

        private float mmTimeLimit, mmDisarmSeconds, mmDisarmSlowness, mmMurdererSpeed;
        private float mmStalemate, mmStalemateGrace;
        private int mmMurdererCount, mmSheriffCount, mmExtraMurdererPer;
        private bool mmInnocentsCanFight, mmConcealWeapons, mmLockOutfit;
        private bool mmSheriffDropsWeapon, mmMurdererDropsWeapon;
        private int mmPenaltyIdx;
        private float mmBowDamage, mmSheriffBowDamage, mmMurdererBowDamage, mmMurdererMeleeDamage;
        private string mmPickupItem = "";
        private int mmPickupQuantity, mmPickupMaxActive, mmPickupRequired;
        private float mmPickupFirst, mmPickupInterval, mmPickupMinDistance, mmPickupLifetime;
        private bool mmPickupMakesSheriff;
        private readonly List<MmRewardRow> mmReward = new List<MmRewardRow>();

        private static readonly string[] MmPenalties = { "death", "disarm", "none" };

        private void DrawMmSettings(PlaceListEntryMsg place)
        {
            // Ask once per selection; the server pushes a fresh copy after every save.
            if (mmCfgReqPlace != place.Id)
            {
                mmCfgReqPlace = place.Id;
                mmCfgLoadedFrom = null;
                mmCfgEventIdx = 0;
                networkHandler.SendRequestMmConfig(place.Id);
            }

            var cfg = networkHandler.MmConfig.Value;
            if (cfg == null || cfg.PlaceId != place.Id) return;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(ColAccent, "Settings (event config)");

            var codes = cfg.MmEventCodes ?? System.Array.Empty<string>();
            if (codes.Length == 0)
            {
                ImGui.TextColored(ColBad, "No Murder Mystery event found to configure.");
                return;
            }

            // Variant picker. Each variant keeps its own numbers, so switching reloads the buffers.
            int idx = System.Array.IndexOf(codes, cfg.EventCode);
            if (idx >= 0) mmCfgEventIdx = idx;
            if (mmCfgEventIdx >= codes.Length) mmCfgEventIdx = 0;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Event##mmcfgev", ref mmCfgEventIdx, codes, codes.Length)
                && codes[mmCfgEventIdx] != cfg.EventCode)
            {
                networkHandler.SendRequestMmConfig(place.Id, codes[mmCfgEventIdx]);
                return;   // the buffers are filled from the answer, not from the old config
            }

            if (!ReferenceEquals(mmCfgLoadedFrom, cfg)) MmLoadEditBuffers(cfg);

            ImGui.TextDisabled("Written to the event JSON in ModConfig; applies to the next match.");

            if (ImGui.TreeNode("Roles and pacing##mmroles"))
            {
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Match seconds##mmtl", ref mmTimeLimit, 10f, 60f, "%.0f");
                ImGui.SetNextItemWidth(110); ImGui.InputInt("Murderers##mmmc", ref mmMurdererCount, 1);
                ImGui.SetNextItemWidth(110); ImGui.InputInt("Sheriffs##mmsc", ref mmSheriffCount, 1);
                ImGui.SetNextItemWidth(110); ImGui.InputInt("+1 murderer per N players##mmex", ref mmExtraMurdererPer, 1);
                HelpMarker("0 turns the extra murderers off - the count above is then the whole of it.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Murderer speed##mmspd", ref mmMurdererSpeed, 0.05f, 0.1f, "%.2f");
                HelpMarker("Walk-speed multiplier. 1 = off. A small edge in a chase, and a tell for anyone watching.");
                ImGui.Checkbox("Innocents can fight##mmicf", ref mmInnocentsCanFight);
                ImGui.Checkbox("Conceal weapons##mmcw", ref mmConcealWeapons);
                ImGui.Checkbox("Lock outfit##mmlo", ref mmLockOutfit);
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Stalemate after (s)##mmstm", ref mmStalemate, 10f, 30f, "%.0f");
                HelpMarker("Nobody has died for this long -> the clock is cut. 0 = off.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Stalemate grace (s)##mmstmg", ref mmStalemateGrace, 10f, 30f, "%.0f");
                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Weapon damage##mmdmg"))
            {
                ImGui.TextDisabled("0 leaves the weapon's own damage alone.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Sheriff's bow##mmsbd", ref mmSheriffBowDamage, 0.5f, 1f, "%.1f");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Murderer's bow##mmmbd", ref mmMurdererBowDamage, 0.5f, 1f, "%.1f");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Any bow (fallback)##mmbd", ref mmBowDamage, 0.5f, 1f, "%.1f");
                HelpMarker("Used for a role that has no figure of its own above.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Murderer's melee##mmmmd", ref mmMurdererMeleeDamage, 0.5f, 1f, "%.1f");
                ImGui.Separator();
                ImGui.SetNextItemWidth(140);
                ImGui.Combo("Sheriff penalty##mmpen", ref mmPenaltyIdx, MmPenalties, MmPenalties.Length);
                HelpMarker("What happens to a sheriff who shoots an innocent: die for it, lose the bow for a while, or nothing.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Disarm seconds##mmds", ref mmDisarmSeconds, 1f, 5f, "%.0f");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Disarm slowness##mmdsl", ref mmDisarmSlowness, 0.05f, 0.1f, "%.2f");
                HelpMarker("Speed multiplier while disarmed; between 0 and 1, else ignored.");
                ImGui.Checkbox("Sheriff drops weapon on death##mmsdw", ref mmSheriffDropsWeapon);
                ImGui.Checkbox("Murderer drops weapon on death##mmmdw", ref mmMurdererDropsWeapon);
                ImGui.TreePop();
            }

            if (ImGui.TreeNode("Weapon spawns##mmspawn"))
            {
                ImGui.SetNextItemWidth(240); ImGui.InputText("Item dropped##mmpi", ref mmPickupItem, 128);
                HelpMarker("Any ordinary item code - no new item has to be made. Empty turns the spawns off.");
                ImGui.SameLine();
                if (GoSmallButton("Held##mmpiheld"))
                {
                    if (GuiItemUtil.TryReadHeldItem(capi, out var heldCode, out _)) mmPickupItem = heldCode;
                    else LogAction(false, "> hold the item you want to use first");
                }
                ImGui.SetNextItemWidth(110); ImGui.InputInt("Per spawn##mmpq", ref mmPickupQuantity, 1);
                HelpMarker("How many go into one drop. Usually 1 - the point is walking to the next spot.");
                ImGui.SetNextItemWidth(110); ImGui.InputInt("Needed for a bow##mmpreq", ref mmPickupRequired, 1);
                HelpMarker("Collect this many and the reward below is handed over automatically.\nLeftovers stay in the bag: 12 with a price of 10 buys one bow and keeps two.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("First spawn (s)##mmpf", ref mmPickupFirst, 10f, 30f, "%.0f");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Every (s)##mmpint", ref mmPickupInterval, 10f, 30f, "%.0f");
                ImGui.SetNextItemWidth(110); ImGui.InputInt("Max lying around##mmpma", ref mmPickupMaxActive, 1);
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Min distance apart##mmpmd", ref mmPickupMinDistance, 1f, 5f, "%.1f");
                HelpMarker("Never a second token within this many blocks of one already lying there,\nso a single corner cannot become a stash.");
                ImGui.SetNextItemWidth(110); ImGui.InputFloat("Expires after (s)##mmpl", ref mmPickupLifetime, 10f, 30f, "%.0f");
                HelpMarker("0 = it waits until someone takes it.");
                ImGui.Checkbox("Finder becomes the sheriff##mmpms", ref mmPickupMakesSheriff);

                ImGui.Spacing();
                ImGui.TextColored(ColAccent, "What finding it pays");
                ImGui.TextDisabled("  code                              qty");
                int removeR = -1;
                for (int i = 0; i < mmReward.Count; i++)
                {
                    var row = mmReward[i];
                    ImGui.PushID("mmrw" + i);
                    ImGui.SetNextItemWidth(230); ImGui.InputText("##code", ref row.Code, 128); ImGui.SameLine();
                    ImGui.SetNextItemWidth(60); ImGui.InputInt("##qty", ref row.Quantity, 0, 0); ImGui.SameLine();
                    if (StopButton("x")) removeR = i;
                    ImGui.PopID();
                }
                if (removeR >= 0) mmReward.RemoveAt(removeR);
                if (ImGui.SmallButton("Add item##mmrwadd"))
                    mmReward.Add(new MmRewardRow { Code = "game:", Quantity = 1 });
                ImGui.SameLine();
                if (GoSmallButton("Add held item##mmrwheld"))
                {
                    if (GuiItemUtil.TryReadHeldItem(capi, out var heldCode, out var heldQty))
                        mmReward.Add(new MmRewardRow { Code = heldCode, Quantity = System.Math.Max(1, heldQty) });
                    else
                        LogAction(false, "> hold the item you want to add first");
                }
                ImGui.TreePop();
            }

            ImGui.Spacing();
            if (GoButton("Save settings##mmcfgsave"))
            {
                networkHandler.SendSaveMmConfig(place.Id, cfg.EventCode, MmSettingsToJson().ToString());
                LogAction(true, "> save Murder Mystery settings for '" + cfg.EventCode + "'");
            }
            ImGui.SameLine();
            if (StopButton("Reload from saved##mmcfgreload")) MmLoadEditBuffers(cfg);
        }

        /// <summary>Fills the edit buffers from what the server has. Anything the config leaves out
        /// shows the same default the behavior itself would use, so saving cannot silently change it.</summary>
        private void MmLoadEditBuffers(MmConfigMsg cfg)
        {
            mmCfgLoadedFrom = cfg;
            mmReward.Clear();

            JObject a;
            try
            {
                a = string.IsNullOrWhiteSpace(cfg.AttributesJson)
                    ? new JObject()
                    : JObject.Parse(cfg.AttributesJson);
            }
            catch { a = new JObject(); }

            float F(string key, float fallback) => a[key]?.Value<float>() ?? fallback;
            int I(string key, int fallback) => a[key]?.Value<int>() ?? fallback;
            bool B(string key, bool fallback) => a[key]?.Value<bool>() ?? fallback;

            mmTimeLimit = F("timeLimitSeconds", 600f);
            mmMurdererCount = I("murdererCount", 1);
            mmSheriffCount = I("sheriffCount", 1);
            mmExtraMurdererPer = I("extraMurdererPerPlayers", 0);
            mmInnocentsCanFight = B("innocentsCanFight", false);
            mmConcealWeapons = B("concealWeapons", true);
            mmLockOutfit = B("lockOutfit", true);
            mmDisarmSeconds = F("sheriffDisarmSeconds", 20f);
            mmDisarmSlowness = F("sheriffDisarmSlowness", 0.6f);
            mmMurdererSpeed = F("murdererSpeed", 1f);
            mmSheriffDropsWeapon = B("sheriffDropsWeapon", true);
            mmMurdererDropsWeapon = B("murdererDropsWeapon", false);
            mmStalemate = F("stalemateSeconds", 0f);
            mmStalemateGrace = F("stalemateGraceSeconds", 60f);
            mmBowDamage = F("bowDamage", 0f);
            mmSheriffBowDamage = F("sheriffBowDamage", 0f);
            mmMurdererBowDamage = F("murdererBowDamage", 0f);
            mmMurdererMeleeDamage = F("murdererMeleeDamage", 0f);
            mmPickupItem = a["bowPickupItem"]?.Value<string>()
                ?? EventManagers.eventBehavior.Shared.PickupSpawner.DefaultTokenCode;
            mmPickupQuantity = I("bowPickupQuantity", 1);
            mmPickupRequired = I("bowPickupRequired", 10);
            mmPickupFirst = F("bowPickupFirstSeconds", 60f);
            mmPickupInterval = F("bowPickupIntervalSeconds", 90f);
            mmPickupMaxActive = I("bowPickupMaxActive", 1);
            mmPickupMinDistance = F("bowPickupMinDistance", 12f);
            mmPickupLifetime = F("bowPickupLifetimeSeconds", 0f);
            mmPickupMakesSheriff = B("bowPickupMakesSheriff", false);

            string penalty = a["sheriffPenalty"]?.Value<string>() ?? "death";
            mmPenaltyIdx = System.Array.FindIndex(MmPenalties,
                p => string.Equals(p, penalty, System.StringComparison.OrdinalIgnoreCase));
            if (mmPenaltyIdx < 0) mmPenaltyIdx = 0;

            if (a["bowPickupReward"] is JArray rewardArr)
                foreach (var entry in rewardArr)
                    mmReward.Add(new MmRewardRow
                    {
                        Code = entry["code"]?.Value<string>() ?? "",
                        Quantity = System.Math.Max(1, entry["quantity"]?.Value<int>() ?? 1)
                    });
        }

        /// <summary>The buffers as the flat settings object the server expects. Values are clamped to
        /// what the behavior can actually use, so a typo cannot break the next match.</summary>
        private JObject MmSettingsToJson()
        {
            var reward = new JArray();
            foreach (var row in mmReward)
            {
                if (string.IsNullOrWhiteSpace(row.Code)) continue;
                reward.Add(new JObject
                {
                    ["code"] = row.Code.Trim(),
                    ["quantity"] = System.Math.Max(1, row.Quantity)
                });
            }

            return new JObject
            {
                ["timeLimitSeconds"] = System.Math.Max(1f, mmTimeLimit),
                ["murdererCount"] = System.Math.Max(0, mmMurdererCount),
                ["sheriffCount"] = System.Math.Max(0, mmSheriffCount),
                ["extraMurdererPerPlayers"] = System.Math.Max(0, mmExtraMurdererPer),
                ["innocentsCanFight"] = mmInnocentsCanFight,
                ["concealWeapons"] = mmConcealWeapons,
                ["lockOutfit"] = mmLockOutfit,
                ["sheriffPenalty"] = MmPenalties[System.Math.Max(0, System.Math.Min(MmPenalties.Length - 1, mmPenaltyIdx))],
                ["sheriffDisarmSeconds"] = System.Math.Max(0f, mmDisarmSeconds),
                ["sheriffDisarmSlowness"] = System.Math.Max(0f, mmDisarmSlowness),
                ["murdererSpeed"] = System.Math.Max(0.1f, mmMurdererSpeed),
                ["sheriffDropsWeapon"] = mmSheriffDropsWeapon,
                ["murdererDropsWeapon"] = mmMurdererDropsWeapon,
                ["stalemateSeconds"] = System.Math.Max(0f, mmStalemate),
                ["stalemateGraceSeconds"] = System.Math.Max(0f, mmStalemateGrace),
                ["bowDamage"] = System.Math.Max(0f, mmBowDamage),
                ["sheriffBowDamage"] = System.Math.Max(0f, mmSheriffBowDamage),
                ["murdererBowDamage"] = System.Math.Max(0f, mmMurdererBowDamage),
                ["murdererMeleeDamage"] = System.Math.Max(0f, mmMurdererMeleeDamage),
                ["bowPickupItem"] = mmPickupItem?.Trim() ?? "",
                ["bowPickupQuantity"] = System.Math.Max(1, mmPickupQuantity),
                ["bowPickupRequired"] = System.Math.Max(1, mmPickupRequired),
                ["bowPickupFirstSeconds"] = System.Math.Max(0f, mmPickupFirst),
                ["bowPickupIntervalSeconds"] = System.Math.Max(0f, mmPickupInterval),
                ["bowPickupMaxActive"] = System.Math.Max(1, mmPickupMaxActive),
                ["bowPickupMinDistance"] = System.Math.Max(0f, mmPickupMinDistance),
                ["bowPickupLifetimeSeconds"] = System.Math.Max(0f, mmPickupLifetime),
                ["bowPickupMakesSheriff"] = mmPickupMakesSheriff,
                ["bowPickupReward"] = reward
            };
        }

        /// <summary>How many pickup spots this place has, counting only what came through whole: the
        /// three arrays are index-aligned, and a short one would throw in the loop above.</summary>
        internal static int MmPickupCount(PlaceListEntryMsg place)
        {
            if (place?.MmPickupX == null || place.MmPickupY == null || place.MmPickupZ == null) return 0;
            return System.Math.Min(place.MmPickupX.Length,
                System.Math.Min(place.MmPickupY.Length, place.MmPickupZ.Length));
        }

        /// <summary>The spots as blocks, for the in-world overlay.</summary>
        internal static List<BlockPos> MmPickupBlocks(PlaceListEntryMsg place)
        {
            var blocks = new List<BlockPos>();
            int count = MmPickupCount(place);
            for (int i = 0; i < count; i++)
                blocks.Add(new BlockPos(place.MmPickupX[i], place.MmPickupY[i], place.MmPickupZ[i], 0));
            return blocks;
        }
    }
}
