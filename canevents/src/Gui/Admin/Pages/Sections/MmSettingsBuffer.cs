using System;
using System.Collections.Generic;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.Gui.Admin.Widgets;
using canevents.src.Network;
using Newtonsoft.Json.Linq;
using D = canevents.src.EventManagers.eventBehavior.Shared.MmConfigKeys.Defaults;
using K = canevents.src.EventManagers.eventBehavior.Shared.MmConfigKeys;
using P = canevents.src.EventManagers.eventBehavior.Shared.PickupSpawner;

namespace canevents.src.Gui.Admin.Pages.Sections
{
    // Values live in the event JSON, so they belong to a variant, not an arena.
    public sealed class MmSettingsBuffer
    {
        public static readonly string[] Penalties = { "death", "disarm", "none" };

        // -1 = ask on the next build.
        public long CfgReqPlace = -1;

        public MmConfigMsg CfgAtRequest;

        // Compared by reference: a save reply must refresh fields the server clamped or rejected.
        public MmConfigMsg CfgLoadedFrom;

        private string loadedJson;

        public double TimeLimit, DisarmSeconds, DisarmSlowness, MurdererSpeed;
        public double Stalemate, StalemateGrace;
        public int MurdererCount, SheriffCount, ExtraMurdererPer;
        public bool InnocentsCanFight, ConcealWeapons, LockOutfit;
        public bool SheriffDropsWeapon, MurdererDropsWeapon;
        public string Penalty = "death";
        public double BowDamage, SheriffBowDamage, MurdererBowDamage, MurdererMeleeDamage;
        public string PickupItem = "";
        public int PickupQuantity, PickupMaxActive, PickupRequired;
        public double PickupFirst, PickupInterval, PickupMinDistance, PickupLifetime;
        public bool PickupMakesSheriff;
        public readonly List<RewardItem> Reward = new List<RewardItem>();

        public readonly ItemListInput RewardInput = new ItemListInput();

        public void Invalidate()
        {
            CfgReqPlace = -1;
            CfgAtRequest = null;
            CfgLoadedFrom = null;
            loadedJson = null;
        }

        public bool IsDirty => CfgLoadedFrom != null && loadedJson != null && ToJson().ToString() != loadedJson;

        // Missing keys get the behavior's own defaults, so a save cannot silently change them.
        public void Load(MmConfigMsg cfg)
        {
            CfgLoadedFrom = cfg;
            Reward.Clear();

            JObject a;
            try
            {
                a = string.IsNullOrWhiteSpace(cfg.AttributesJson)
                    ? new JObject()
                    : JObject.Parse(cfg.AttributesJson);
            }
            catch { a = new JObject(); }

            double F(string key, double fallback) => a[key]?.Value<double>() ?? fallback;
            int I(string key, int fallback) => a[key]?.Value<int>() ?? fallback;
            bool B(string key, bool fallback) => a[key]?.Value<bool>() ?? fallback;

            // PickupSpawner key suffixes behind the MM prefix.
            string Pk(string suffix) => K.PickupPrefix + suffix;

            TimeLimit = F(K.TimeLimitSeconds, D.TimeLimitSeconds);
            MurdererCount = I(K.MurdererCount, D.MurdererCount);
            SheriffCount = I(K.SheriffCount, D.SheriffCount);
            ExtraMurdererPer = I(K.ExtraMurdererPerPlayers, D.ExtraMurdererPerPlayers);
            InnocentsCanFight = B(K.InnocentsCanFight, D.InnocentsCanFight);
            ConcealWeapons = B(K.ConcealWeapons, D.ConcealWeapons);
            LockOutfit = B(K.LockOutfit, D.LockOutfit);
            DisarmSeconds = F(K.SheriffDisarmSeconds, D.SheriffDisarmSeconds);
            DisarmSlowness = F(K.SheriffDisarmSlowness, D.SheriffDisarmSlowness);
            MurdererSpeed = F(K.MurdererSpeed, D.MurdererSpeed);
            SheriffDropsWeapon = B(K.SheriffDropsWeapon, D.SheriffDropsWeapon);
            MurdererDropsWeapon = B(K.MurdererDropsWeapon, D.MurdererDropsWeapon);
            Stalemate = F(K.StalemateSeconds, D.StalemateSeconds);
            StalemateGrace = F(K.StalemateGraceSeconds, D.StalemateGraceSeconds);
            BowDamage = F(K.BowDamage, D.Damage);
            SheriffBowDamage = F(K.SheriffBowDamage, D.Damage);
            MurdererBowDamage = F(K.MurdererBowDamage, D.Damage);
            MurdererMeleeDamage = F(K.MurdererMeleeDamage, D.Damage);
            PickupItem = a[Pk(P.ItemKey)]?.Value<string>() ?? P.DefaultTokenCode;
            PickupQuantity = I(Pk(P.QuantityKey), P.DefaultQuantity);
            PickupRequired = I(Pk(P.RequiredKey), P.DefaultRequired);
            PickupFirst = F(Pk(P.FirstSecondsKey), P.DefaultFirstSeconds);
            PickupInterval = F(Pk(P.IntervalSecondsKey), P.DefaultIntervalSeconds);
            PickupMaxActive = I(Pk(P.MaxActiveKey), P.DefaultMaxActive);
            PickupMinDistance = F(Pk(P.MinDistanceKey), P.DefaultMinDistance);
            PickupLifetime = F(Pk(P.LifetimeSecondsKey), P.DefaultLifetimeSeconds);
            PickupMakesSheriff = B(K.PickupMakesSheriff, D.PickupMakesSheriff);

            string penalty = a[K.SheriffPenalty]?.Value<string>() ?? D.SheriffPenalty;
            int idx = Array.FindIndex(Penalties, p => string.Equals(p, penalty, StringComparison.OrdinalIgnoreCase));
            Penalty = Penalties[idx < 0 ? 0 : idx];

            if (a[Pk(P.RewardKey)] is JArray rewardArr)
                foreach (var entry in rewardArr)
                    Reward.Add(new RewardItem
                    {
                        Code = entry["code"]?.Value<string>() ?? "",
                        Quantity = Math.Max(1, entry["quantity"]?.Value<int>() ?? 1)
                    });

            loadedJson = ToJson().ToString();
        }

        // Clamped so a typo cannot break the next match.
        public JObject ToJson()
        {
            var reward = new JArray();
            foreach (var row in Reward)
            {
                if (string.IsNullOrWhiteSpace(row.Code)) continue;
                reward.Add(new JObject
                {
                    ["code"] = row.Code.Trim(),
                    ["quantity"] = Math.Max(1, row.Quantity)
                });
            }

            string penalty = Array.Find(Penalties, p => string.Equals(p, Penalty, StringComparison.OrdinalIgnoreCase))
                             ?? Penalties[0];

            // Floats on the wire.
            float Pos(double v, float min) => Math.Max(min, (float)v);
            string Pk(string suffix) => K.PickupPrefix + suffix;

            return new JObject
            {
                [K.TimeLimitSeconds] = Pos(TimeLimit, 1f),
                [K.MurdererCount] = Math.Max(0, MurdererCount),
                [K.SheriffCount] = Math.Max(0, SheriffCount),
                [K.ExtraMurdererPerPlayers] = Math.Max(0, ExtraMurdererPer),
                [K.InnocentsCanFight] = InnocentsCanFight,
                [K.ConcealWeapons] = ConcealWeapons,
                [K.LockOutfit] = LockOutfit,
                [K.SheriffPenalty] = penalty,
                [K.SheriffDisarmSeconds] = Pos(DisarmSeconds, 0f),
                [K.SheriffDisarmSlowness] = Pos(DisarmSlowness, 0f),
                [K.MurdererSpeed] = Pos(MurdererSpeed, 0.1f),
                [K.SheriffDropsWeapon] = SheriffDropsWeapon,
                [K.MurdererDropsWeapon] = MurdererDropsWeapon,
                [K.StalemateSeconds] = Pos(Stalemate, 0f),
                [K.StalemateGraceSeconds] = Pos(StalemateGrace, 0f),
                [K.BowDamage] = Pos(BowDamage, 0f),
                [K.SheriffBowDamage] = Pos(SheriffBowDamage, 0f),
                [K.MurdererBowDamage] = Pos(MurdererBowDamage, 0f),
                [K.MurdererMeleeDamage] = Pos(MurdererMeleeDamage, 0f),
                [Pk(P.ItemKey)] = PickupItem?.Trim() ?? "",
                [Pk(P.QuantityKey)] = Math.Max(1, PickupQuantity),
                [Pk(P.RequiredKey)] = Math.Max(1, PickupRequired),
                [Pk(P.FirstSecondsKey)] = Pos(PickupFirst, 0f),
                [Pk(P.IntervalSecondsKey)] = Pos(PickupInterval, 0f),
                [Pk(P.MaxActiveKey)] = Math.Max(1, PickupMaxActive),
                [Pk(P.MinDistanceKey)] = Pos(PickupMinDistance, 0f),
                [Pk(P.LifetimeSecondsKey)] = Pos(PickupLifetime, 0f),
                [K.PickupMakesSheriff] = PickupMakesSheriff,
                [Pk(P.RewardKey)] = reward
            };
        }
    }
}
