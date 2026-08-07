using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using canevents.src.EventManagers.events;
using canevents.src.Network;

namespace canevents.src.Gui
{
    // Economy tab: per-event rewards and entry fees, plus the server-wide limits they fall back
    // to. Both editors load a server snapshot into fields and send the whole thing back on Save.
    public partial class AdminPanelDialog
    {
        // Rewards / entry-fee editor
        private string rewardEventCode = "";   // event chosen in the combo
        private string rewardLoadedFor = "";   // event the editor fields currently hold
        private string rewardRequested;        // event we last sent a config request for (auto-load guard)
        private readonly List<RewardItem> rwWinner = new List<RewardItem>();
        private readonly List<RewardItem> rwParticipation = new List<RewardItem>();
        private readonly List<RewardItem> rwFee = new List<RewardItem>();
        private List<List<RewardItem>> rwTiers = new List<List<RewardItem>>();
        private int rwParticipationMode;   // tri 0 inherit / 1 on / 2 off
        private int rwRateCapExempt;       // tri
        private int rwSeparateCap;         // tri
        private int rwRake;
        private int rwMinMatch;
        private bool rwMinMatchInherit = true;

        private static readonly string[] TriLabels = { "inherit", "on", "off" };

        private static string Ordinal(int n) => GuiItemUtil.Ordinal(n);

        /// <summary>
        /// Shown under an empty item set, which falls back to the default item from the Limits tab -
        /// otherwise the empty list reads as "pays nothing".
        /// </summary>
        private void DrawInheritedItemNote(string code, int qty)
        {
            if (string.IsNullOrWhiteSpace(code) || qty <= 0)
            {
                ImGui.TextColored(RwDim, "Empty and no server default set - this event pays nothing here.");
                return;
            }

            DrawItemIcon(code, 22f);
            ImGui.SameLine();
            ImGui.TextColored(RwDim, string.Format("Empty -> falls back to the server default: {0} x{1}", code, qty));
        }

        /// <summary>
        /// Three-state override: inherit the server-wide setting, or force it on/off for this event.
        /// Shows what "inherit" resolves to, since that setting lives on a different sub-tab.
        /// </summary>
        private void TriOverride(string label, string id, ref int value, bool? globalValue, string help)
        {
            ImGui.SetNextItemWidth(110);
            ImGui.Combo(label + "##" + id, ref value, TriLabels, TriLabels.Length);
            HelpMarker(help);

            ImGui.SameLine();
            if (value == 0)
            {
                ImGui.TextColored(RwDim, globalValue == null
                    ? "-> follows the global setting"
                    : "-> global: " + (globalValue.Value ? "on" : "off"));
            }
            else
            {
                // An explicit choice keeps its value even if the global one changes later.
                ImGui.TextColored(ColWarn, "-> overrides the global setting");
            }
        }
        private static readonly Vector4 RwGold = ColAccent;
        private static readonly Vector4 RwDim = ColMuted;

        // Loads a server config snapshot into the editor fields (keeps the user's combo selection).
        private void LoadRewardEditor(EventRewardConfigMsg m)
        {
            if (m == null) return;
            rewardLoadedFor = m.EventCode;
            CopyInto(rwWinner, ItemSetUtil.Parse(m.WinnerItems));
            CopyInto(rwParticipation, ItemSetUtil.Parse(m.ParticipationItems));
            CopyInto(rwFee, ItemSetUtil.Parse(m.EntryFeeItems));
            rwTiers = new List<List<RewardItem>>();
            if (m.Placement != null)
                foreach (var t in m.Placement.OrderBy(t => t.Place))
                    rwTiers.Add(ItemSetUtil.Parse(t.Items));
            rwParticipationMode = m.Participation;
            rwRateCapExempt = m.RateCapExempt;
            rwSeparateCap = m.SeparateRewardCap;
            rwRake = m.EntryFeeRake;
            rwMinMatchInherit = m.MinMatchSeconds < 0;
            rwMinMatch = m.MinMatchSeconds < 0 ? 0 : m.MinMatchSeconds;
        }

        private static void CopyInto(List<RewardItem> dst, List<RewardItem> src)
        {
            dst.Clear();
            dst.AddRange(src);
        }

        private void DrawRewardsTab()
        {
            Heading("Reward & Entry-Fee Editor");
            ImGui.TextDisabled("Per-event payouts. Changes are saved back to the event's JSON in ModConfig.");
            ImGui.TextDisabled("Fields left on 'inherit' follow the global values in the Limits sub-tab.");
            ImGui.Spacing();

            var adminList = networkHandler.AdminEventList.Value;
            string[] codes = adminList?.Events != null
                ? adminList.Events.Select(e => e.Code).ToArray() : new string[0];
            if (codes.Length == 0)
            {
                ImGui.TextColored(RwDim, "No events loaded.");
                if (ImGui.SmallButton("Refresh##rwev")) networkHandler.RequestAdminEventList();
                return;
            }

            if (string.IsNullOrEmpty(rewardEventCode)) rewardEventCode = codes[0];
            int idx = System.Array.IndexOf(codes, rewardEventCode);
            if (idx < 0) idx = 0;
            rewardEventCode = codes[idx]; // keep the selection valid if the event list changed
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Event##rwsel", ref idx, codes, codes.Length))
                rewardEventCode = codes[idx];
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload##rw")) rewardRequested = null; // force a re-request below

            // Auto-request the config once per selected event.
            if (rewardEventCode != rewardRequested)
            {
                networkHandler.SendRequestEventRewardConfig(rewardEventCode);
                rewardRequested = rewardEventCode;
            }

            if (rewardLoadedFor != rewardEventCode)
            {
                ImGui.Spacing();
                ImGui.TextColored(RwDim, "Loading config for '" + rewardEventCode + "'...");
                return;
            }

            ImGui.Separator();

            var limits = networkHandler.RewardLimits.Value;

            ImGui.TextColored(RwGold, "Winner reward");
            ImGui.TextDisabled("Given to everyone on the winning side.");
            DrawItemListEditor("rwwin", rwWinner);
            if (rwWinner.Count == 0) DrawInheritedItemNote(limits?.WinnerItemCode, limits?.WinnerQuantity ?? 0);

            ImGui.Spacing();
            ImGui.TextColored(RwGold, "Participation reward");
            ImGui.TextDisabled("Given to players who did NOT win, just for taking part.");
            TriOverride("Pay it", "rwpm", ref rwParticipationMode, limits?.Participation,
                "inherit - use the server-wide setting from the Limits sub-tab.\n"
                + "on - always pay it here, even if the server default is off.\n"
                + "off - never pay it here, even if the server default is on.\n\n"
                + "'off' is a decision that sticks: change the global setting later and this event "
                + "keeps ignoring it. 'inherit' would follow it.");
            DrawItemListEditor("rwpart", rwParticipation);
            if (rwParticipation.Count == 0) DrawInheritedItemNote(limits?.ParticipationItemCode, limits?.ParticipationQuantity ?? 0);

            ImGui.Spacing();
            ImGui.TextColored(RwGold, "Entry fee");
            ImGui.TextDisabled("Charged when a player queues. All stakes form a pot paid to the winner(s).");
            DrawItemListEditor("rwfee", rwFee);
            ImGui.SetNextItemWidth(80); ImGui.InputInt("Rake %##rwrake", ref rwRake, 0);
            rwRake = System.Math.Clamp(rwRake, 0, 100);
            HelpMarker("Share of the pot destroyed before payout.\n"
                + "0 = the winner takes the whole pot.\n"
                + "100 = the entire pot is burned (a pure entry barrier, the normal reward still applies).");
            ImGui.SameLine();
            ImGui.TextColored(RwDim, rwFee.Count == 0
                ? "no fee set - this event is free to enter"
                : rwRake == 0 ? "winner takes the whole pot" : "winners split " + (100 - rwRake) + "% of the pot");

            ImGui.Spacing();
            ImGui.TextColored(RwGold, "Anti-abuse");

            ImGui.Checkbox("Use global minimum match length", ref rwMinMatchInherit);
            HelpMarker("Matches shorter than this pay no winner reward (participation still applies).\n"
                + "Stops two players from queueing, instantly conceding, and farming the payout.");
            if (rwMinMatchInherit)
            {
                ImGui.SameLine();
                ImGui.TextColored(RwDim, limits != null
                    ? "-> global: " + (limits.MinMatchSeconds > 0 ? limits.MinMatchSeconds + "s" : "no minimum")
                    : "-> follows the global setting");
            }
            else
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80); ImGui.InputInt("seconds##rwmm", ref rwMinMatch, 0);
                rwMinMatch = System.Math.Max(0, rwMinMatch);
                ImGui.SameLine();
                ImGui.TextColored(ColWarn, rwMinMatch == 0 ? "-> no minimum for this event" : "-> overrides the global setting");
            }

            TriOverride("Ignore the reward rate-cap", "rwrce", ref rwRateCapExempt, false,
                "The rate-cap limits how often one player can be paid for winning.\n"
                + "on - this event always pays, no matter how often they win it (use for tournaments).\n"
                + "off / inherit - the cap applies.");

            TriOverride("Own rate-cap budget", "rwsc", ref rwSeparateCap, limits?.RateCapPerEvent,
                "on - wins here count against a budget of their own.\n"
                + "off / inherit - they share one budget with every other event.\n\n"
                + "Give a busy event its own budget so winning it does not exhaust the player's "
                + "allowance everywhere else.");

            ImGui.Spacing();
            ImGui.TextColored(RwGold, "Rewards by place (optional)");
            if (rwTiers.Count > 0)
            {
                // Filling this in silently disables the winner reward above.
                ImGui.TextColored(ColWarn, "Set: the Winner reward above is IGNORED for this event.");
                ImGui.TextDisabled("Remove every place below to go back to using it.");
            }
            else
            {
                ImGui.TextDisabled("Leave empty to pay the flat Winner reward above.");
                ImGui.TextDisabled("Add places to pay 1st/2nd/3rd differently - that replaces it.");
            }

            int removeTier = -1;
            for (int i = 0; i < rwTiers.Count; i++)
            {
                ImGui.PushID("tier" + i);
                ImGui.TextColored(RwGold, Ordinal(i + 1) + " place");
                ImGui.SameLine();
                if (StopSmallButton("remove")) removeTier = i;
                DrawItemListEditor("rwt" + i, rwTiers[i]);
                ImGui.PopID();
            }
            if (removeTier >= 0) rwTiers.RemoveAt(removeTier);
            if (GoSmallButton("+ Add place " + Ordinal(rwTiers.Count + 1)))
                rwTiers.Add(new List<RewardItem>());
            HelpMarker("Needs an event that produces a finishing order (FFA by kills, Parkour by time, "
                + "Spleef by elimination order, team modes by team score).\n"
                + "Modes that cannot be ranked fall back to the flat Winner reward.");

            ImGui.Spacing();
            ImGui.Separator();
            if (GoButton("Save", new Vector2(120, 0))) SaveRewardConfig();
            ImGui.SameLine();
            ImGui.TextColored(RwDim, "saves '" + rewardEventCode + "' only");

            DrawServerDefaultsSection();
        }

        /// <summary>
        /// The server-wide fallback reward, shown here because this is where its effect is visible.
        /// Has its own Save: it is not part of the selected event and changes every event inheriting it.
        /// </summary>
        private void DrawServerDefaultsSection()
        {
            ImGui.Spacing();
            ImGui.Separator();
            Heading("Server default reward");
            ImGui.TextColored(ColWarn, "Applies to EVERY event that leaves the matching set empty.");
            ImGui.TextDisabled("One item each. For multi-item payouts, fill the per-event sets above.");
            ImGui.Spacing();

            if (!limitsLoaded)
            {
                ImGui.TextColored(RwDim, "Loading...");
                return;
            }

            ImGui.Checkbox("Pay a participation reward by default##lmp", ref lmParticipation);
            HelpMarker("Used by every event whose Participation is set to 'inherit'.");

            ImGui.Spacing();
            DrawDefaultItemRow("Winner", "lmw", ref lmWinnerCode, ref lmWinnerQty);
            DrawDefaultItemRow("Participation", "lmp2", ref lmPartCode, ref lmPartQty);

            ImGui.Spacing();
            if (GoButton("Save defaults", new Vector2(140, 0))) SaveLimits();
            ImGui.SameLine();
            ImGui.TextColored(ColWarn, "affects all events");
        }

        private void SaveRewardConfig()
        {
            var msg = new EventRewardConfigMsg
            {
                EventCode = rewardEventCode,
                WinnerItems = ItemSetUtil.Pack(rwWinner),
                Placement = rwTiers.Count > 0
                    ? rwTiers.Select((t, i) => new RewardTierMsg { Place = i + 1, Items = ItemSetUtil.Pack(t) }).ToArray()
                    : null,
                Participation = rwParticipationMode,
                ParticipationItems = ItemSetUtil.Pack(rwParticipation),
                EntryFeeItems = ItemSetUtil.Pack(rwFee),
                EntryFeeRake = rwRake,
                MinMatchSeconds = rwMinMatchInherit ? -1 : System.Math.Max(0, rwMinMatch),
                RateCapExempt = rwRateCapExempt,
                SeparateRewardCap = rwSeparateCap,
            };
            networkHandler.SendSaveEventRewardConfig(msg);
        }

        // Editable item set: + Hotbar / Clear, then icon | code | qty | remove. Mutates `items`.
        private void DrawItemListEditor(string id, List<RewardItem> items)
        {
            ImGui.PushID(id);
            if (ImGui.SmallButton("+ Hotbar")) AddInventoryTo(items);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) items.Clear();

            if (items.Count > 0 && ImGui.BeginTable("##il", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthFixed, 30f);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Qty",  ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthFixed, 24f);
                int removeAt = -1;
                for (int i = 0; i < items.Count; i++)
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, 30f);
                    ImGui.PushID(i);
                    ImGui.TableSetColumnIndex(0); DrawItemIcon(items[i].Code, 26f);
                    ImGui.TableSetColumnIndex(1); ImGui.Text(items[i].Code ?? "");
                    ImGui.TableSetColumnIndex(2);
                    int qty = items[i].Quantity;
                    ImGui.SetNextItemWidth(55);
                    if (ImGui.InputInt("##q", ref qty, 0)) items[i].Quantity = System.Math.Max(1, qty);
                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton("x")) removeAt = i;
                    ImGui.PopID();
                }
                if (removeAt >= 0) items.RemoveAt(removeAt);
                ImGui.EndTable();
            }
            else if (items.Count == 0)
            {
                ImGui.TextColored(RwDim, "(empty)");
            }
            ImGui.PopID();
        }

        // Takes the inventory class so "add my armor" is possible here too.
        private void AddInventoryTo(List<RewardItem> items,
            string inventoryClassName = Vintagestory.API.Config.GlobalConstants.hotBarInvClassName)
        {
            foreach (var (code, qty) in GuiItemUtil.ReadInventory(capi, inventoryClassName))
            {
                var existing = items.FirstOrDefault(x => x.Code == code);
                if (existing != null) existing.Quantity += qty;
                else items.Add(new RewardItem { Code = code, Quantity = qty });
            }
        }

        // Global reward / anti-farm limits
        private bool limitsLoaded;
        private int lmRateBurst;
        private float lmRateRefillHours;
        private bool lmRatePerEvent;
        private int lmMinMatch;
        private bool lmRefundVoid;
        private bool lmParticipation;
        private string lmWinnerCode = "";
        private int lmWinnerQty;
        private string lmPartCode = "";
        private int lmPartQty;

        private void LoadLimitsEditor(RewardLimitsMsg m)
        {
            if (m == null) return;
            lmRateBurst = m.RateCapBurst;
            lmRateRefillHours = (float)m.RateCapRefillHours;
            lmRatePerEvent = m.RateCapPerEvent;
            lmMinMatch = m.MinMatchSeconds;
            lmRefundVoid = m.RefundForfeitOnVoid;
            lmParticipation = m.Participation;
            lmWinnerCode = m.WinnerItemCode ?? "";
            lmWinnerQty = m.WinnerQuantity;
            lmPartCode = m.ParticipationItemCode ?? "";
            lmPartQty = m.ParticipationQuantity;
            limitsLoaded = true;
        }

        private void DrawLimitsTab()
        {
            Heading("Reward & Anti-Farm Limits");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload##lm")) networkHandler.SendRequestRewardLimits();
            ImGui.TextDisabled("How often and how easily rewards can be earned, server-wide. An event follows");
            ImGui.TextDisabled("these unless it overrides them on the Rewards tab - that is what 'inherit' means.");
            ImGui.Spacing();
            if (!limitsLoaded)
            {
                ImGui.TextColored(RwDim, "Loading...");
                return;
            }
            ImGui.Separator();

            Heading("Reward rate-cap (anti-farm)");
            ImGui.TextDisabled("Caps how many rewards one player can collect before having to wait.");
            ImGui.SetNextItemWidth(130); ImGui.InputInt("Burst (0 = off)##lmb", ref lmRateBurst, 1);
            HelpMarker("How many rewards a player may collect back-to-back before the refill timer gates them.");
            lmRateBurst = System.Math.Max(0, lmRateBurst);
            ImGui.SetNextItemWidth(130); ImGui.InputFloat("Refill hours / slot##lmr", ref lmRateRefillHours, 0f);
            HelpMarker("How long it takes to earn back one slot of that burst.");
            if (lmRateRefillHours < 0.01f) lmRateRefillHours = 0.01f;
            ImGui.Checkbox("Separate bucket per (player, event)##lmpe", ref lmRatePerEvent);
            HelpMarker("On: farming one event does not eat the allowance for the others.");

            ImGui.Spacing();
            Heading("Anti-throw / wager");
            ImGui.TextDisabled("Stops two players trading wins in instant matches.");
            ImGui.SetNextItemWidth(130); ImGui.InputInt("Min match seconds (0 = off)##lmm", ref lmMinMatch, 1);
            lmMinMatch = System.Math.Max(0, lmMinMatch);
            ImGui.Checkbox("Refund a forfeited stake when the match ends void##lmrf", ref lmRefundVoid);

            ImGui.Spacing();
            ImGui.TextDisabled("The fallback reward itself lives on the Rewards tab, next to the per-event sets");
            ImGui.TextDisabled("it stands in for.");

            ImGui.Spacing();
            ImGui.Separator();
            if (GoButton("Save", new Vector2(120, 0))) SaveLimits();
        }

        /// <summary>
        /// One fallback item: icon, held-item picker, quantity, and the code as a last-resort text
        /// field. The icon doubles as validation - an unresolvable code shows here, not at payout.
        /// </summary>
        private void DrawDefaultItemRow(string label, string id, ref string code, ref int qty)
        {
            ImGui.PushID(id);

            DrawItemIcon(code, 26f);
            ImGui.SameLine();
            ImGui.TextColored(RwGold, label);
            ImGui.SameLine(140);

            if (GoSmallButton("Use held item"))
            {
                if (GuiItemUtil.TryReadHeldItem(capi, out var heldCode, out var heldQty))
                {
                    code = heldCode;
                    qty = System.Math.Max(1, heldQty);
                }
                else
                {
                    LogAction(false, "> hold the item you want as the default first");
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Takes whatever you are holding, with its stack size as the quantity.");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("Qty##q", ref qty, 0);
            qty = System.Math.Max(0, qty);

            ImGui.SameLine();
            if (string.IsNullOrWhiteSpace(code)) ImGui.TextColored(RwDim, "(none)");
            else if (GuiItemUtil.Resolve(capi, code) == null) ImGui.TextColored(ColBad, "unknown item");
            else if (qty <= 0) ImGui.TextColored(ColWarn, "qty 0 - nothing given");
            else ImGui.TextColored(RwDim, code);

            // For cases picking cannot cover: an item this client lacks, or a code from another server.
            if (ImGui.TreeNode("edit code by hand##raw"))
            {
                ImGui.SetNextItemWidth(260);
                ImGui.InputText("##c", ref code, 128);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        private void SaveLimits()
        {
            networkHandler.SendSaveRewardLimits(new RewardLimitsMsg
            {
                RateCapBurst = lmRateBurst,
                RateCapRefillHours = lmRateRefillHours,
                RateCapPerEvent = lmRatePerEvent,
                MinMatchSeconds = lmMinMatch,
                RefundForfeitOnVoid = lmRefundVoid,
                Participation = lmParticipation,
                WinnerItemCode = lmWinnerCode,
                WinnerQuantity = lmWinnerQty,
                ParticipationItemCode = lmPartCode,
                ParticipationQuantity = lmPartQty,
            });
        }
    }
}
