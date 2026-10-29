using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;

namespace canevents.src.Gui.Admin.Pages
{
    // Economy > Rewards. Sent back whole by ToMsg.
    public sealed class RewardEditorBuffer
    {
        // Selector order; placement tiers follow from FirstTierSet.
        public const int SetWinner = 0, SetParticipation = 1, SetFee = 2, FirstTierSet = 3;

        public string EventCode = "";

        // null re-asks on the next build.
        public string Requested;

        // Compared by reference: a save reply must refresh the fields.
        public EventRewardConfigMsg LoadedFrom;

        private string loadedSignature;

        public readonly List<RewardItem> Winner = new List<RewardItem>();
        public readonly List<RewardItem> Participation = new List<RewardItem>();
        public readonly List<RewardItem> Fee = new List<RewardItem>();
        public readonly List<List<RewardItem>> Tiers = new List<List<RewardItem>>();

        // Tri-states: 0 inherit / 1 on / 2 off.
        public int ParticipationMode, RateCapExempt, SeparateCap;
        public int Rake;
        public int MinMatch;
        public bool MinMatchInherit = true;

        public int SelectedSet;

        public readonly ItemListInput Input = new ItemListInput();

        public bool IsLoaded => LoadedFrom != null && LoadedFrom.EventCode == EventCode;

        public bool IsDirty => IsLoaded && loadedSignature != null && Signature() != loadedSignature;

        // null for an index that no longer exists.
        public List<RewardItem> SetAt(int index)
        {
            switch (index)
            {
                case SetWinner: return Winner;
                case SetParticipation: return Participation;
                case SetFee: return Fee;
                default:
                    int tier = index - FirstTierSet;
                    return tier >= 0 && tier < Tiers.Count ? Tiers[tier] : null;
            }
        }

        public void Load(EventRewardConfigMsg m)
        {
            if (m == null) return;
            LoadedFrom = m;
            CopyInto(Winner, ItemSetUtil.Parse(m.WinnerItems));
            CopyInto(Participation, ItemSetUtil.Parse(m.ParticipationItems));
            CopyInto(Fee, ItemSetUtil.Parse(m.EntryFeeItems));
            Tiers.Clear();
            if (m.Placement != null)
                foreach (var t in m.Placement.OrderBy(t => t.Place))
                    Tiers.Add(ItemSetUtil.Parse(t.Items));
            ParticipationMode = m.Participation;
            RateCapExempt = m.RateCapExempt;
            SeparateCap = m.SeparateRewardCap;
            Rake = m.EntryFeeRake;
            MinMatchInherit = m.MinMatchSeconds < 0;
            MinMatch = m.MinMatchSeconds < 0 ? 0 : m.MinMatchSeconds;
            if (SetAt(SelectedSet) == null) SelectedSet = SetWinner;
            loadedSignature = Signature();
        }

        public EventRewardConfigMsg ToMsg()
        {
            return new EventRewardConfigMsg
            {
                EventCode = LoadedFrom?.EventCode ?? EventCode,
                WinnerItems = ItemSetUtil.Pack(Winner),
                Placement = Tiers.Count > 0
                    ? Tiers.Select((t, i) => new RewardTierMsg { Place = i + 1, Items = ItemSetUtil.Pack(t) }).ToArray()
                    : null,
                Participation = ParticipationMode,
                ParticipationItems = ItemSetUtil.Pack(Participation),
                EntryFeeItems = ItemSetUtil.Pack(Fee),
                EntryFeeRake = Math.Clamp(Rake, 0, 100),
                MinMatchSeconds = MinMatchInherit ? -1 : Math.Max(0, MinMatch),
                RateCapExempt = RateCapExempt,
                SeparateRewardCap = SeparateCap,
            };
        }

        private string Signature()
        {
            var m = ToMsg();
            string tiers = m.Placement == null ? "" : string.Join("/", m.Placement.Select(t => t.Items ?? ""));
            return string.Join("|", m.WinnerItems, tiers, m.Participation, m.ParticipationItems, m.EntryFeeItems,
                m.EntryFeeRake, m.MinMatchSeconds, m.RateCapExempt, m.SeparateRewardCap);
        }

        private static void CopyInto(List<RewardItem> dst, List<RewardItem> src)
        {
            dst.Clear();
            dst.AddRange(src);
        }
    }

    // Economy > Limits, including the server default reward items.
    public sealed class LimitsEditorBuffer
    {
        public RewardLimitsMsg LoadedFrom;
        private string loadedSignature;

        // Only a reply to our own Save/Reload may overwrite unsaved edits, not the re-request on tab show.
        public bool AcceptNext;

        public int RateBurst;
        public double RateRefillHours;
        public bool RatePerEvent;
        public int MinMatch;
        public bool RefundVoid;
        public bool Participation;
        public string WinnerCode = "";
        public int WinnerQty;
        public string PartCode = "";
        public int PartQty;

        public bool IsLoaded => LoadedFrom != null;

        public bool IsDirty => LoadedFrom != null && loadedSignature != null && Signature() != loadedSignature;

        public void Load(RewardLimitsMsg m)
        {
            if (m == null) return;
            LoadedFrom = m;
            RateBurst = m.RateCapBurst;
            // Round-trip through float (wire type) so the field shows what Save will send.
            RateRefillHours = (float)m.RateCapRefillHours;
            RatePerEvent = m.RateCapPerEvent;
            MinMatch = m.MinMatchSeconds;
            RefundVoid = m.RefundForfeitOnVoid;
            Participation = m.Participation;
            WinnerCode = m.WinnerItemCode ?? "";
            WinnerQty = m.WinnerQuantity;
            PartCode = m.ParticipationItemCode ?? "";
            PartQty = m.ParticipationQuantity;
            loadedSignature = Signature();
        }

        public RewardLimitsMsg ToMsg()
        {
            return new RewardLimitsMsg
            {
                RateCapBurst = Math.Max(0, RateBurst),
                RateCapRefillHours = (float)Math.Max(0.01, RateRefillHours),
                RateCapPerEvent = RatePerEvent,
                MinMatchSeconds = Math.Max(0, MinMatch),
                RefundForfeitOnVoid = RefundVoid,
                Participation = Participation,
                WinnerItemCode = WinnerCode ?? "",
                WinnerQuantity = Math.Max(0, WinnerQty),
                ParticipationItemCode = PartCode ?? "",
                ParticipationQuantity = Math.Max(0, PartQty),
            };
        }

        private string Signature()
        {
            var m = ToMsg();
            return string.Join("|", m.RateCapBurst, m.RateCapRefillHours, m.RateCapPerEvent, m.MinMatchSeconds,
                m.RefundForfeitOnVoid, m.Participation, m.WinnerItemCode, m.WinnerQuantity,
                m.ParticipationItemCode, m.ParticipationQuantity);
        }
    }
}
