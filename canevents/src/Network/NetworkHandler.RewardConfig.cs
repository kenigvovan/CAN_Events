using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side admin editor for an event's reward + entry-fee, shaped like the loadout editor:
    // privilege-gated request/save, a result toast, and a refresh with the persisted shape. Item
    // sets travel packed (ItemSetUtil); nullable spec fields use a tri-state int, or -1 for ints.
    public partial class ServerNetworkHandler
    {
        private void OnRequestEventRewardConfig(IServerPlayer player, RequestEventRewardConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendEventRewardConfig(player, msg?.EventCode);
        }

        private void SendEventRewardConfig(IServerPlayer player, string code)
        {
            var ev = eventManager.FindEventByCode(code);
            if (ev == null)
            {
                ReplyFail(player, Lang.Get("canevents:event-not-found", code ?? ""));
                return;
            }
            adminChannel.SendPacket(BuildRewardConfigMsg(ev), player);
        }

        private void OnSaveEventRewardConfig(IServerPlayer player, EventRewardConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (msg == null || string.IsNullOrEmpty(msg.EventCode))
            {
                ReplyFail(player, Lang.Get("canevents:event-not-found", ""));
                return;
            }

            var (ok, message) = eventManager.UpdateEventRewardConfig(msg.EventCode, BuildRewardSpec(msg), BuildEntryFeeSpec(msg));
            Reply(player, ok, message);
            if (ok)
            {
                SendEventRewardConfig(player, msg.EventCode); // echo back the persisted shape
                BroadcastEventList();                          // refresh every player's browser (fee/prizes changed)
            }
        }

        // Spec <-> wire

        private static EventRewardConfigMsg BuildRewardConfigMsg(CANEvent ev)
        {
            var r = ev.Reward;
            var f = ev.EntryFee;
            RewardTierMsg[] placement = null;
            if (r?.PlacementRewards != null && r.PlacementRewards.Count > 0)
                placement = r.PlacementRewards
                    .Select((t, i) => new RewardTierMsg { Place = i + 1, Items = ItemSetUtil.Pack(t?.Items) })
                    .ToArray();

            return new EventRewardConfigMsg
            {
                EventCode = ev.Code,
                WinnerItems = ItemSetUtil.Pack(r?.WinnerItems),
                Placement = placement,
                Participation = BoolToTri(r?.Participation),
                ParticipationItems = ItemSetUtil.Pack(r?.ParticipationItems),
                EntryFeeItems = ItemSetUtil.Pack(f?.Items),
                EntryFeeRake = f?.RakePercent ?? 0,
                MinMatchSeconds = r?.MinMatchSeconds ?? -1,
                RateCapExempt = BoolToTri(r?.RateCapExempt),
                SeparateRewardCap = BoolToTri(r?.SeparateRewardCap),
            };
        }

        private static RewardSpec BuildRewardSpec(EventRewardConfigMsg m)
        {
            var winner = ItemSetUtil.Parse(m.WinnerItems);
            var participation = ItemSetUtil.Parse(m.ParticipationItems);

            // Placement stays positional (tier index = place - 1); empty intermediate tiers are kept,
            // and the whole list is dropped only when no tier has items.
            List<RewardTier> tiers = null;
            if (m.Placement != null && m.Placement.Length > 0)
            {
                int maxPlace = m.Placement.Max(t => t.Place);
                if (maxPlace > 0)
                {
                    var arr = new RewardTier[maxPlace];
                    foreach (var t in m.Placement)
                        if (t.Place >= 1 && t.Place <= maxPlace)
                            arr[t.Place - 1] = new RewardTier { Items = ItemSetUtil.Parse(t.Items) };
                    tiers = arr.Select(t => t ?? new RewardTier { Items = new List<RewardItem>() }).ToList();
                    if (tiers.All(t => t.Items == null || t.Items.Count == 0)) tiers = null;
                }
            }

            return new RewardSpec
            {
                WinnerItems = winner.Count > 0 ? winner : null,
                PlacementRewards = tiers,
                Participation = TriToBool(m.Participation),
                ParticipationItems = participation.Count > 0 ? participation : null,
                MinMatchSeconds = m.MinMatchSeconds >= 0 ? m.MinMatchSeconds : (int?)null,
                RateCapExempt = TriToBool(m.RateCapExempt),
                SeparateRewardCap = TriToBool(m.SeparateRewardCap),
            };
        }

        private static EntryFeeSpec BuildEntryFeeSpec(EventRewardConfigMsg m)
        {
            var items = ItemSetUtil.Parse(m.EntryFeeItems);
            if (items.Count == 0) return null; // no fee
            return new EntryFeeSpec { Items = items, RakePercent = Math.Clamp(m.EntryFeeRake, 0, 100) };
        }

        private static int BoolToTri(bool? v) => v == null ? 0 : (v.Value ? 1 : 2);
        private static bool? TriToBool(int v) => v == 1 ? true : v == 2 ? (bool?)false : null;

        // Global reward / anti-farm limits

        private Config GlobalConfig => sapi.ModLoader.GetModSystem<canevents>().config;

        private void OnRequestRewardLimits(IServerPlayer player, RequestRewardLimitsMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendRewardLimits(player);
        }

        private void SendRewardLimits(IServerPlayer player)
        {
            var c = GlobalConfig;
            adminChannel.SendPacket(new RewardLimitsMsg
            {
                RateCapBurst = c.rewardRateCapBurst,
                RateCapRefillHours = c.rewardRateCapRefillHours,
                RateCapPerEvent = c.rewardRateCapPerEvent,
                MinMatchSeconds = c.minMatchDurationSecondsForReward,
                RefundForfeitOnVoid = c.entryFeeRefundForfeitOnVoid,
                Participation = c.rewardParticipation,
                WinnerItemCode = c.rewardWinnerItemCode,
                WinnerQuantity = c.rewardWinnerQuantity,
                ParticipationItemCode = c.rewardParticipationItemCode,
                ParticipationQuantity = c.rewardParticipationQuantity,
            }, player);
        }

        private void OnSaveRewardLimits(IServerPlayer player, RewardLimitsMsg m)
        {
            if (!RequireAdmin(player) || m == null) return;
            eventManager.ApplyRewardLimitsConfig(
                m.RateCapBurst, m.RateCapRefillHours, m.RateCapPerEvent,
                m.MinMatchSeconds, m.RefundForfeitOnVoid, m.Participation,
                m.WinnerItemCode, m.WinnerQuantity, m.ParticipationItemCode, m.ParticipationQuantity);
            ReplyOk(player, Lang.Get("canevents:rewardlimits-saved"));
            SendRewardLimits(player);  // echo back the persisted values
            BroadcastEventList();      // global defaults / rate-cap feed the browser preview
        }
    }
}
