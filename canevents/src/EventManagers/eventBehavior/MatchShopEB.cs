using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Points for kills and round wins, spent on gear. Kit items with an upgrade chain (set in the loadout
    // editor) go up one level per purchase, and the level holds on every spawn until the match ends.
    // One-time items are handed out once and kept until lost. Works with the KillScoringEB modes, which
    // report kills and deaths and let the shop adjust the kit whenever they equip someone.
    public class MatchShopEB : CANEventBehavior
    {
        // On one-time purchases: the offer id. Survives a hand-over, so a picked-up purchase is the new owner's.
        public const string BoughtKey = "canevents_shopItem";

        public sealed class Offer
        {
            public string Id;
            public string Code;
            public int Quantity;
            public int Cost;
            // Per life.
            public int MaxBuys;
            public bool DropOnDeath;
            // Loadout set names it is sold to; empty = every kit.
            public string[] Kits;
        }

        private sealed class Wallet
        {
            public int Points;
            // Kit name + item id -> bought level. Kept for the match, also for a class the player left.
            public readonly Dictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Levels bought since the last kit; only the last of these can be undone.
            public readonly Dictionary<string, int> Unissued = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ThisLife = new Dictionary<string, int>();
            // One-time items bought while dead, for the next spawn.
            public readonly List<string> Queued = new List<string>();
            // Purchases carried into a round reset, handed back with the next kit.
            public readonly List<ItemStack> Stash = new List<ItemStack>();
            // Set of the last kit handed out.
            public string Kit;
        }

        private readonly List<Offer> offers = new List<Offer>();
        private readonly Dictionary<string, Wallet> wallets = new Dictionary<string, Wallet>();

        // Off keeps the configured items for later; the admin panel's switch flips this.
        private bool enabled = true;
        private int startPoints;
        private int pointsPerKill;
        private int pointsPerDeath;
        private int pointsPerRoundWin;
        private bool buyAnywhere;
        private float buyRadius;
        private bool showPreview;

        public MatchShopEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.MatchShop;

        public IReadOnlyList<Offer> Offers => offers;
        public bool Enabled => enabled;
        public int StartPoints => startPoints;
        public int PointsPerKill => pointsPerKill;
        public int PointsPerDeath => pointsPerDeath;
        public int PointsPerRoundWin => pointsPerRoundWin;
        public bool BuyAnywhere => buyAnywhere;
        public float BuyRadius => buyRadius;
        // Off: players only learn what's for sale once the match runs, and only for their own class.
        public bool ShowPreview => showPreview;

        // The match's shop, null when the event has none or it is switched off.
        public static MatchShopEB Of(RunningEvent runningEvent)
        {
            var shop = runningEvent?.GetBehavior<MatchShopEB>();
            return shop != null && shop.enabled ? shop : null;
        }

        public static bool HasChain(LoadoutEntry entry)
            => entry?.Id != null && entry.Upgrades != null && entry.Upgrades.Count > 0;

        public override void Initialize()
        {
            enabled = Config.Bool("enabled", true);
            startPoints = Math.Max(0, Config.Int("startPoints", 0));
            pointsPerKill = Config.Int("pointsPerKill", 1);
            pointsPerDeath = Config.Int("pointsPerDeath", 0);
            pointsPerRoundWin = Config.Int("pointsPerRoundWin", 0);
            buyAnywhere = Config.Bool("buyAnywhere", false);
            buyRadius = Math.Max(1f, Config.Float("buyRadius", 6f));
            showPreview = Config.Bool("showPreview", true);
            ParseOffers(Config.Raw("items"));
            Config.WarnUnknown();
        }

        private void ParseOffers(JsonObject items)
        {
            offers.Clear();
            // No items is fine: the kits' upgrade chains may be all there is.
            if (items?.Exists != true) return;
            if (!(items.Token is Newtonsoft.Json.Linq.JArray))
            {
                log.Warning("'items' is not a list; the shop sells no one-time items.");
                return;
            }

            foreach (var node in items.AsArray())
            {
                string code = node["code"].AsString(null);
                if (string.IsNullOrWhiteSpace(code))
                {
                    log.Warning("A shop item without a code is skipped.");
                    continue;
                }
                if (string.Equals(node["type"].AsString(null), "upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    log.Warning("Shop item '{0}' is an upgrade; upgrades are set on kit items in the loadout editor now. Skipped.", code);
                    continue;
                }

                var offer = new Offer
                {
                    Id = node["id"].AsString(null) ?? code,
                    Code = code,
                    Quantity = Math.Max(1, node["quantity"].AsInt(1)),
                    Cost = Math.Max(0, node["cost"].AsInt(1)),
                    MaxBuys = Math.Max(1, node["maxBuys"].AsInt(1)),
                    DropOnDeath = node["dropOnDeath"].AsBool(false),
                    Kits = ReadStrings(node["kits"])
                };

                if (Find(offer.Id) != null)
                {
                    log.Warning("Shop item id '{0}' is used twice; the second one is skipped.", offer.Id);
                    continue;
                }
                offers.Add(offer);
            }
        }

        private static string[] ReadStrings(JsonObject node)
        {
            if (node?.Exists != true) return new string[0];
            if (node.Token is Newtonsoft.Json.Linq.JArray)
                return node.AsArray().Select(n => n.AsString(null)).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            string single = node.AsString(null);
            return string.IsNullOrWhiteSpace(single) ? new string[0] : new[] { single };
        }

        public Offer Find(string id) => id == null ? null : offers.FirstOrDefault(o => o.Id == id);

        public override void OnBeforeStart() => wallets.Clear();

        // The HUD line tells everyone the shop is there.
        public override void OnStart()
        {
            if (!enabled) return;
            foreach (var uid in runningEvent.ParticipantsBefore) Push(uid);
        }

        public override void OnEnd()
        {
            if (!enabled) return;
            foreach (var player in runningEvent.OnlineParticipants)
                runningEvent.NetworkHandler?.SendShopClosed(runningEvent, player);
        }

        public override void OnParticipantAdmitted(IServerPlayer player)
        {
            if (enabled) Push(player.PlayerUID);
        }

        private bool Open => enabled && runningEvent.EventState == EventState.Running && !runningEvent.KeepOwnGear;

        private List<LoadoutSet> Kits()
            => LoadoutService.KitsOf(runningEvent.Event?.Code, runningEvent.Behaviors, runningEvent.LoadoutManager);

        // Same pick as LoadoutService.Give: a forced set, else the class picked for this match (may have changed
        // during an intermission), else the last kit given, else the first one, which an undecided player gets.
        public LoadoutSet KitOf(string uid)
        {
            var kits = Kits();
            bool modeAssigns = runningEvent.Behaviors.OfType<ILoadoutProvider>().FirstOrDefault()?.OffersPlayerChoice == false;
            string forced = modeAssigns ? null : runningEvent.Event?.MutatorValue(EventMutators.Loadout);
            var forcedKit = forced == null ? null
                : kits.FirstOrDefault(k => string.Equals(k.Name, forced, StringComparison.OrdinalIgnoreCase));
            if (forcedKit != null) return forcedKit;

            string name = runningEvent.PlayerLoadoutChoices.TryGetValue(uid, out var picked) ? picked : WalletOf(uid).Kit;
            return kits.FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? kits.FirstOrDefault();
        }

        public static bool Fits(Offer offer, string kit)
            => offer.Kits.Length == 0 || (kit != null && offer.Kits.Contains(kit, StringComparer.OrdinalIgnoreCase));

        // What the player's window lists: other classes' items are left out.
        public bool OffersTo(string uid, Offer offer) => Fits(offer, KitOf(uid)?.Name);

        public bool MayShop(string uid)
            => Open && uid != null && !Bots.BotIdentity.IsBot(uid)
               && runningEvent.HoldsPlayer(uid) && !runningEvent.LeftInPlay(uid);

        private Wallet WalletOf(string uid)
        {
            if (!wallets.TryGetValue(uid, out var wallet)) wallets[uid] = wallet = new Wallet { Points = startPoints };
            return wallet;
        }

        public int PointsOf(string uid) => WalletOf(uid).Points;

        public int BoughtOf(string uid, Offer offer) => Count(WalletOf(uid).ThisLife, offer.Id);

        private static string Key(string kit, string entryId) => kit + "|" + entryId;

        // Capped by the chain, which an admin may have shortened mid-match.
        private static int LevelOf(Wallet wallet, string kit, LoadoutEntry entry)
            => HasChain(entry) ? Math.Min(Count(wallet.Levels, Key(kit, entry.Id)), entry.Upgrades.Count) : 0;

        // The player's current kit, item by item, for every item with a chain.
        public IEnumerable<(LoadoutEntry entry, int level, bool refundable)> UpgradesOf(string uid)
        {
            var kit = KitOf(uid);
            if (kit == null) yield break;
            var wallet = WalletOf(uid);
            foreach (var entry in kit.Items.Where(HasChain))
            {
                int level = LevelOf(wallet, kit.Name, entry);
                yield return (entry, level, level > 0 && Count(wallet.Unissued, Key(kit.Name, entry.Id)) > 0);
            }
        }

        private static int Count(Dictionary<string, int> counts, string id)
            => counts.TryGetValue(id, out int n) ? n : 0;

        private static void Add(Dictionary<string, int> counts, string id, int delta)
        {
            int n = Count(counts, id) + delta;
            if (n > 0) counts[id] = n;
            else counts.Remove(id);
        }

        public void OnKill(string killerUid) => Award(killerUid, pointsPerKill);

        // One-time items are gone with the body, so the per-life limit starts over.
        public void OnDeath(string victimUid)
        {
            if (wallets.TryGetValue(victimUid, out var wallet))
            {
                wallet.ThisLife.Clear();
                wallet.Stash.Clear();
            }
            Award(victimUid, pointsPerDeath);
            Push(victimUid);
        }

        public void OnRoundWon(IEnumerable<string> uids)
        {
            foreach (var uid in uids) Award(uid, pointsPerRoundWin);
        }

        private void Award(string uid, int points)
        {
            if (points == 0 || uid == null || Bots.BotIdentity.IsBot(uid)) return;
            var wallet = WalletOf(uid);
            wallet.Points = Math.Max(0, wallet.Points + points);
            Push(uid, points);
        }

        // The kit as the mode picked it, every item at its bought level in its own place, plus the one-time
        // items bought while dead.
        public LoadoutSet Dress(string uid, LoadoutSet kit)
        {
            var wallet = WalletOf(uid);
            wallet.Kit = kit.Name;

            var set = new LoadoutSet { Name = kit.Name };
            foreach (var entry in kit.Items)
            {
                int level = LevelOf(wallet, kit.Name, entry);
                if (level == 0)
                {
                    set.Items.Add(entry);
                    continue;
                }
                var step = entry.Upgrades[level - 1];
                set.Items.Add(new LoadoutEntry
                {
                    Code = step.Code,
                    Quantity = step.Quantity > 0 ? step.Quantity : entry.Quantity
                });
                foreach (var extra in step.Extras ?? new List<UpgradeExtra>())
                    set.Items.Add(new LoadoutEntry { Code = extra.Code, Quantity = extra.Quantity });
            }

            foreach (var id in wallet.Queued)
            {
                var offer = Find(id);
                if (offer != null) set.Items.Add(OneTimeEntry(offer));
            }

            bool changed = wallet.Queued.Count > 0 || wallet.Unissued.Count > 0;
            wallet.Queued.Clear();
            wallet.Unissued.Clear();
            if (changed) Push(uid);
            return set;
        }

        // Before a round reset empties the inventory. Picked-up purchases count as the carrier's.
        public void Stash(IServerPlayer player)
        {
            var carried = PlayerInventoryBackup.CloneCarried(player, s => s.Attributes?.GetString(BoughtKey) != null);
            if (carried.Count > 0) WalletOf(player.PlayerUID).Stash.AddRange(carried);
        }

        // After the kit, so the stash doesn't take the slots the kit dresses.
        public void ReturnStash(IServerPlayer player)
        {
            if (!wallets.TryGetValue(player.PlayerUID, out var wallet) || wallet.Stash.Count == 0) return;
            foreach (var stack in wallet.Stash) InventoryUtil.Give(runningEvent.sapi, player, stack, "shop");
            wallet.Stash.Clear();
        }

        public static void MarkBought(ItemStack stack, string offerId)
            => stack?.Attributes?.SetString(BoughtKey, offerId);

        // Null when the player may shop and their kit has this item with a chain, else the reason.
        private string FindChainItem(string uid, string entryId, out LoadoutSet kit, out LoadoutEntry entry)
        {
            kit = null;
            entry = null;
            if (!MayShop(uid)) return Lang.Get("canevents:shop-closed");
            kit = KitOf(uid);
            entry = kit?.Items.FirstOrDefault(e => e.Id == entryId && HasChain(e));
            return entry == null ? Lang.Get("canevents:shop-wrong-kit") : null;
        }

        // Next level of a kit item; it lands with the next kit. Null on success, else the reason for the player.
        public string TryUpgrade(IServerPlayer player, string entryId)
        {
            string uid = player.PlayerUID;
            string refusal = FindChainItem(uid, entryId, out var kit, out var entry);
            if (refusal != null) return refusal;

            var wallet = WalletOf(uid);
            int level = LevelOf(wallet, kit.Name, entry);
            if (level >= entry.Upgrades.Count) return Lang.Get("canevents:shop-maxed");
            int cost = entry.Upgrades[level].Cost;
            if (wallet.Points < cost) return Lang.Get("canevents:shop-no-points", cost, wallet.Points);

            string key = Key(kit.Name, entryId);
            wallet.Points -= cost;
            wallet.Levels[key] = level + 1;
            Add(wallet.Unissued, key, 1);

            log.Debug("{0} upgraded {1}/{2} to level {3} for {4}, {5} left.",
                player.PlayerName, kit.Name, entry.Code, level + 1, cost, wallet.Points);
            Push(uid);
            return null;
        }

        // Takes back the last level, while it hasn't been handed out yet.
        public string TryDowngrade(IServerPlayer player, string entryId)
        {
            string uid = player.PlayerUID;
            string refusal = FindChainItem(uid, entryId, out var kit, out var entry);
            if (refusal != null) return refusal;

            var wallet = WalletOf(uid);
            string key = Key(kit.Name, entryId);
            int level = LevelOf(wallet, kit.Name, entry);
            if (level == 0 || Count(wallet.Unissued, key) == 0) return Lang.Get("canevents:shop-refund-issued");

            wallet.Points += entry.Upgrades[level - 1].Cost;
            Add(wallet.Levels, key, -1);
            Add(wallet.Unissued, key, -1);
            Push(uid);
            return null;
        }

        public string TryBuy(IServerPlayer player, string id)
        {
            string uid = player.PlayerUID;
            var offer = Find(id);
            if (offer == null || !MayShop(uid)) return Lang.Get("canevents:shop-closed");
            if (!OffersTo(uid, offer)) return Lang.Get("canevents:shop-wrong-kit");

            var wallet = WalletOf(uid);
            if (Count(wallet.ThisLife, id) >= offer.MaxBuys) return Lang.Get("canevents:shop-maxed-life");
            if (wallet.Points < offer.Cost) return Lang.Get("canevents:shop-no-points", offer.Cost, wallet.Points);

            bool inPlay = runningEvent.IsActive(uid) && player.Entity?.Alive == true;
            if (inPlay && !buyAnywhere && !AtSpawn(player))
                return Lang.Get("canevents:shop-go-to-spawn", (int)buyRadius);

            wallet.Points -= offer.Cost;
            Add(wallet.ThisLife, id, 1);
            if (inPlay) LoadoutService.GiveExtra(player, runningEvent,
                new LoadoutSet { Name = "shop", Items = { OneTimeEntry(offer) } });
            else wallet.Queued.Add(id);

            log.Debug("{0} bought {1} for {2}, {3} left.", player.PlayerName, offer.Id, offer.Cost, wallet.Points);
            Push(uid);
            return null;
        }

        // Near an arena spawn, or while a hold keeps everyone on their spawns (round intermission).
        private bool AtSpawn(IServerPlayer player)
        {
            if (runningEvent.HoldEndsAtElapsed > runningEvent.ElapsedSeconds) return true;

            var pos = player.Entity?.Pos;
            var spawns = runningEvent.Place?.spawnPositions;
            if (pos == null || spawns == null) return false;

            double r2 = buyRadius * buyRadius;
            foreach (var s in spawns)
            {
                if (s == null || s.dimension != pos.Dimension) continue;
                double dx = pos.X - (s.X + 0.5), dy = pos.Y - s.Y, dz = pos.Z - (s.Z + 0.5);
                if (dx * dx + dy * dy + dz * dz <= r2) return true;
            }
            return false;
        }

        private static LoadoutEntry OneTimeEntry(Offer offer) => new LoadoutEntry
        {
            Code = offer.Code,
            Quantity = offer.Quantity,
            ShopItem = offer.Id,
            DropOnDeath = offer.DropOnDeath
        };

        public void Push(string uid, int gained = 0, bool open = false)
        {
            if (runningEvent.sapi.World.PlayerByUid(uid) is IServerPlayer player)
                runningEvent.NetworkHandler?.SendShopState(runningEvent, this, player, gained, open);
        }
    }
}
