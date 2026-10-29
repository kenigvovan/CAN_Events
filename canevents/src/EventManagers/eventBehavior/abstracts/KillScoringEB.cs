using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    // Base for loadout + kill/death scoring modes. Subclasses add win rules.
    public abstract class KillScoringEB : CANEventBehavior, ILoadoutProvider, IMatchScorer, IOutfitWardrobe
    {
        protected List<LoadoutSet> loadoutSets = new List<LoadoutSet>();

        protected float timeLimitSeconds;
        protected readonly Dictionary<string, int> killCounts = new Dictionary<string, int>();
        protected readonly Dictionary<string, int> deathCounts = new Dictionary<string, int>();

        protected KillScoringEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        // Effective wardrobe (outfit store, else JSON), resolved in InitScores.
        protected List<string[]> outfits = new List<string[]>();

        private List<string[]> jsonOutfits = new List<string[]>();

        // true: unique outfit per player this match; false: stable per-uid pick across matches.
        private bool distinctOutfits;

        // uid -> outfit index
        private Dictionary<string, int> outfitPicks;

        private bool announceDeaths = true;

        // Who sees the HUD scoreboard during the match: all, eliminated (out + spectators), spectators, none.
        private string scoreboardVisibility = "all";

        // Sealed so WarnUnknown always runs after the mode has read its own keys.
        public sealed override void Initialize()
        {
            if (DefaultTimeLimitSeconds > 0f) ParseTimeLimit(DefaultTimeLimitSeconds);
            ParseLoadoutSets();
            ReadModeConfig();
            Config.WarnUnknown();
        }

        // 0 = no time limit
        protected virtual float DefaultTimeLimitSeconds => 0f;

        protected virtual void ReadModeConfig() { }

        private void ParseLoadoutSets()
        {
            // Mark as read for WarnUnknown; parsed elsewhere.
            Config.Has(BehaviorConfigKeys.LoadoutSets);
            Config.Has(BehaviorConfigKeys.Outfits);
            loadoutSets = LoadoutService.ParseFromAttributes(Attributes);
            jsonOutfits = EventOutfit.Parse(Attributes);
            outfits = jsonOutfits;
            distinctOutfits = Config.Bool("distinctOutfits", false);
            announceDeaths = Config.Bool(BehaviorConfigKeys.AnnounceDeaths, true);
            ReadScoreboardVisibility();
        }

        private void ReadScoreboardVisibility()
        {
            var allowed = BehaviorConfigKeys.ScoreboardVisibilities;
            string value = (Config.String(BehaviorConfigKeys.ScoreboardVisibility, allowed[0]) ?? allowed[0]).Trim().ToLowerInvariant();
            if (allowed.Contains(value))
            {
                scoreboardVisibility = value;
                return;
            }
            log.Warning("scoreboardVisibility '{0}' is not one of {1}, using '{2}'.", value, string.Join("/", allowed), allowed[0]);
            scoreboardVisibility = allowed[0];
        }

        public bool ShowsScoreboardTo(string uid)
        {
            switch (scoreboardVisibility)
            {
                case "none": return false;
                case "spectators": return !runningEvent.IsParticipant(uid);
                case "eliminated": return !runningEvent.IsActive(uid);
                default: return true;
            }
        }

        // Applied in BeginMatch.
        private void ParseTimeLimit(float defaultSeconds)
            => timeLimitSeconds = Config.Float(BehaviorConfigKeys.TimeLimitSeconds, defaultSeconds);

        // ILoadoutProvider
        public string[] GetLoadoutSetNames(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetNames(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        public string[] GetLoadoutSetItemCodes(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetItemCodes(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        public string[] GetLoadoutSetDescriptions(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetDescriptions(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        // Virtual here so modes can override the interface defaults.
        public virtual bool OffersPlayerChoice => true;
        public virtual bool HidesIndividualScores => false;
        public virtual bool HidesKillerIdentity => false;

        public (string name, LoadoutEntry[] items)[] GetInlineLoadoutSets()
        {
            var result = new (string name, LoadoutEntry[] items)[loadoutSets.Count];
            for (int i = 0; i < loadoutSets.Count; i++)
                result[i] = (loadoutSets[i].Name, loadoutSets[i].Items.ToArray());
            return result;
        }

        // Call first in OnBeforeStart: InitScores resets the resolution latch.
        // Modes without a time limit call InitScores directly.
        protected virtual void BeginMatch()
        {
            runningEvent.SetDuration(timeLimitSeconds);
            InitScores();
        }

        protected void InitScores()
        {
            ResetMatchResolution();
            killCounts.Clear();
            deathCounts.Clear();
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                killCounts[uid] = 0;
                deathCounts[uid] = 0;
            }
            // Before the deal: the outfit count decides it.
            outfits = EventOutfit.GetEffectiveOutfits(runningEvent.Event?.Code,
                runningEvent.OutfitManager, jsonOutfits);
            // Dealt for the whole line-up at once so distinct picks are possible.
            outfitPicks = EventOutfit.Assign(runningEvent.ParticipantsBefore, outfits.Count,
                stable: !distinctOutfits, rand: runningEvent.sapi?.World?.Rand);

            if (distinctOutfits && outfits.Count > 0 && outfits.Count < runningEvent.ParticipantsBefore.Count)
                log.Warning("distinctOutfits is on but the wardrobe has {0} outfit(s) for {1} player(s) - "
                    + "{2} of them will be dressed alike. Add outfits or lower maxPartiesCount.",
                    outfits.Count, runningEvent.ParticipantsBefore.Count,
                    runningEvent.ParticipantsBefore.Count - outfits.Count);
        }

        // Late entry: counts start at zero, stable outfit pick (OutfitIndexOf = -1).
        public override void OnParticipantAdmitted(IServerPlayer player)
        {
            SeedCounts(player.PlayerUID);
            GiveLoadoutAndHeal(player);
            PushScoreboard();
        }

        // Late entrant (player or bot): RecordDeath only counts uids it already knows.
        public void SeedCounts(string uid)
        {
            if (!killCounts.ContainsKey(uid)) killCounts[uid] = 0;
            if (!deathCounts.ContainsKey(uid)) deathCounts[uid] = 0;
        }

        public override void OnParticipantWithdrawn(IServerPlayer player) => PushScoreboard();

        protected void GiveLoadoutAndHealAll()
        {
            // Pre-start dropouts stay on the roster; a kit given to them would never be taken back.
            foreach (var player in runningEvent.ActiveOnlineParticipants)
                GiveLoadoutAndHeal(player);
        }

        // Round resets: shop purchases still carried come back with the next kit, everything else goes.
        protected void ClearForNextRound(IServerPlayer player)
        {
            MatchShopEB.Of(runningEvent)?.Stash(player);
            PlayerInventoryBackup.ClearInventory(player);
        }

        protected void GiveLoadoutAndHeal(IServerPlayer player)
        {
            // KeepOwnGear: inventory isn't backed up, a kit would be duped and EquipSet would wipe armour.
            if (runningEvent.KeepOwnGear)
            {
                EventBroadcaster.Heal(player);
                EventBroadcaster.ApplyStatMutators(player, runningEvent.Event);
                return;
            }

            var shop = MatchShopEB.Of(runningEvent);
            LoadoutService.Give(player, runningEvent, loadoutSets,
                shop == null ? null : kit => shop.Dress(player.PlayerUID, kit));
            shop?.ReturnStash(player);
            // After the loadout: outfit only fills empty clothing slots.
            EventOutfit.Dress(player, runningEvent.sapi, outfits, OutfitIndexOf(player.PlayerUID));
            EventBroadcaster.Heal(player);
            // After the heal, or a reduced start health gets undone.
            EventBroadcaster.ApplyStatMutators(player, runningEvent.Event);
        }

        protected KillInfo RecordDeath(string victimUid, DamageSource source)
        {
            if (deathCounts.ContainsKey(victimUid)) deathCounts[victimUid]++;

            var shop = MatchShopEB.Of(runningEvent);
            shop?.OnDeath(victimUid);

            var kill = KillAttribution.Resolve(runningEvent, victimUid, source);
            if (kill.IsClean)
            {
                if (killCounts.ContainsKey(kill.KillerUid))
                {
                    killCounts[kill.KillerUid]++;
                    shop?.OnKill(kill.KillerUid);
                }
                // Leaderboard: rated only, roster members only, no bots on either side.
                if (runningEvent.Event.Rated
                    && !runningEvent.SuppressRating
                    && killCounts.ContainsKey(kill.KillerUid)
                    && !Bots.BotIdentity.IsBot(kill.KillerUid)
                    && !Bots.BotIdentity.IsBot(victimUid))
                    runningEvent.Leaderboard?.RecordKill(kill.KillerUid, runningEvent.Event.Code);
            }
            return kill;
        }

        // IOutfitWardrobe: ArenaBotsEB dresses bots from the same deal.
        public List<string[]> Wardrobe => outfits;

        // -1 = stable per-uid pick
        public int OutfitIndexOf(string uid)
            => outfitPicks != null && uid != null && outfitPicks.TryGetValue(uid, out var i) ? i : -1;

        protected void SendKillFeed(string killerName, string victimName, string verb)
            => EventBroadcaster.KillFeed(runningEvent, killerName, victimName, verb);

        protected virtual bool SendsKillFeed => announceDeaths;

        protected virtual string KillFeedVerb => "killed";

        protected virtual string KillFeedKillerName(KillInfo kill)
            => HidesKillerIdentity ? Lang.Get("canevents:killer-someone") : kill.KillerName;

        // Before the kill feed. Victim may be a bot, so use uid/body, not a player lookup.
        protected virtual void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill) { }

        // After feed and scoreboard are out, so it's safe to end the match here.
        protected virtual void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill) { }

        // Kill feed for bots too.
        public override bool AnnouncesDeaths => true;

        public sealed override void OnParticipantDeath(string victimUid, Entity victimBody, DamageSource source)
        {
            if (victimUid == null || !runningEvent.IsActive(victimUid)) return;

            var kill = RecordDeath(victimUid, source);
            OnScoredDeath(victimUid, victimBody, kill);
            if (SendsKillFeed) SendKillFeed(KillFeedKillerName(kill), runningEvent.NameOf(victimUid), KillFeedVerb);
            PushScoreboard();
            AfterScoredDeath(victimUid, victimBody, kill);

            // The push above went out while the victim was still in; an eliminated player is now allowed to see it.
            if (scoreboardVisibility == "eliminated" && !runningEvent.IsActive(victimUid)
                && runningEvent.sapi.World.PlayerByUid(victimUid) is IServerPlayer victim)
                runningEvent.NetworkHandler?.SendScoreboardTo(runningEvent, victim);
        }

        private bool matchResolved;

        protected bool MatchResolved => matchResolved;

        // Only the first call wins, so time-up can't overwrite a score win with a draw.
        // requestFinish=false on the time-up path, where the manager is already finishing.
        protected bool ResolveMatch(IEnumerable<string> winners, string reason, bool requestFinish = true)
        {
            if (matchResolved) return false;
            matchResolved = true;
            runningEvent.SetWinners(winners); // null => draw
            if (requestFinish) runningEvent.RequestFinish(reason);
            return true;
        }

        // Leaves winners to the default GetWinners() (last group standing).
        protected void MarkMatchResolved() => matchResolved = true;

        protected void ResetMatchResolution() => matchResolved = false;

        protected void PushScoreboard() => EventBroadcaster.Scoreboard(runningEvent, BuildScoreboard());

        // IMatchScorer
        public virtual ScoreboardData BuildScoreboard()
            => new ScoreboardData { Kills = killCounts, Deaths = deathCounts };

        public Dictionary<string, int> GetFinalKillCounts() => killCounts;
        public Dictionary<string, int> GetFinalDeathCounts() => deathCounts;

        protected int KillsOf(string uid) => ScoringUtil.CountOf(killCounts, uid);
        protected int DeathsOf(string uid) => ScoringUtil.CountOf(deathCounts, uid);

        // FFA: won, kills desc, deaths asc. Teams: won, team kills. TeamScoreEB overrides.
        public virtual List<HashSet<string>> GetFinalRanking()
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            var winners = runningEvent.GetWinners();

            if (groups != null && groups.Any(g => g.Count > 1))
            {
                // Tied teams share one bracket, otherwise a 0-0 draw paid 1st place to the first team.
                return groups
                    .GroupBy(g => (
                        won: g.Any(uid => winners.Contains(uid)),
                        kills: g.Sum(KillsOf)))
                    .OrderByDescending(grp => grp.Key.won)
                    .ThenByDescending(grp => grp.Key.kills)
                    .Select(grp =>
                    {
                        var bracket = new HashSet<string>();
                        foreach (var g in grp) bracket.UnionWith(g);
                        return bracket;
                    })
                    .ToList();
            }

            // FFA: ties share a bracket too.
            return runningEvent.ParticipantsBefore
                .GroupBy(uid => (
                    won: winners.Contains(uid) ? 1 : 0,
                    kills: KillsOf(uid),
                    deaths: DeathsOf(uid)))
                .OrderByDescending(grp => grp.Key.won)
                .ThenByDescending(grp => grp.Key.kills)
                .ThenBy(grp => grp.Key.deaths)
                .Select(grp => new HashSet<string>(grp))
                .ToList();
        }
    }
}
