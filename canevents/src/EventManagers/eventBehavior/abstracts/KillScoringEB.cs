using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.abstracts
{
    /// <summary>
    /// Base for events that equip players from loadout sets and tally kills/deaths (ArenaFight,
    /// Deathmatch, KingOfTheHill, HungerGames, Juggernaut). Owns loadouts, kill attribution +
    /// leaderboard credit and the scoreboard push; concrete events only add their win rules.
    /// </summary>
    public abstract class KillScoringEB : CANEventBehavior, ILoadoutProvider, IMatchScorer, IOutfitWardrobe
    {
        protected List<LoadoutSet> loadoutSets = new List<LoadoutSet>();

        /// <summary>How long a match may run; a mode supplies only its default
        /// (see <see cref="ParseTimeLimit"/>).</summary>
        protected float timeLimitSeconds;
        protected readonly Dictionary<string, int> killCounts = new Dictionary<string, int>();
        protected readonly Dictionary<string, int> deathCounts = new Dictionary<string, int>();

        protected KillScoringEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        /// <summary>Plain clothes handed out with the loadout - see <see cref="EventOutfit"/>.
        /// Set to the effective wardrobe (managed store, else the JSON one) in InitScores.</summary>
        protected List<string[]> outfits = new List<string[]>();

        /// <summary>The wardrobe as the event's JSON declares it, the fallback when the outfit
        /// store has nothing for this event.</summary>
        private List<string[]> jsonOutfits = new List<string[]>();

        /// <summary>Everyone in this match looks different, instead of each player keeping the same
        /// outfit across matches. Set by the `distinctOutfits` attribute.</summary>
        private bool distinctOutfits;

        /// <summary>uid -> outfit index for this match, dealt in OnBeforeStart.</summary>
        private Dictionary<string, int> outfitPicks;

        /// <summary>Deaths are announced in the kill feed. Set by the `announceDeaths` attribute,
        /// available to every event built on this base (the bots behavior has its own knob of the
        /// same name for bot deaths).</summary>
        private bool announceDeaths = true;

        /// <summary>
        /// Parse the inline loadout sets from Attributes. Call from Initialize().
        /// The two keys are declared as read (though parsed elsewhere) so the unknown-attribute
        /// reporter does not flag every combat config's loadouts as a misspelling.
        /// </summary>
        protected void ParseLoadoutSets()
        {
            Config.Has("loadoutSets");
            Config.Has("outfits");
            loadoutSets = LoadoutService.ParseFromAttributes(Attributes);
            jsonOutfits = EventOutfit.Parse(Attributes);
            outfits = jsonOutfits;
            distinctOutfits = Config.Bool("distinctOutfits", false);
            announceDeaths = Config.Bool("announceDeaths", true);
        }

        /// <summary>Read the match time limit, falling back to this mode's own default.
        /// Call from Initialize(); apply it with SetDuration in OnBeforeStart().</summary>
        protected void ParseTimeLimit(float defaultSeconds)
            => timeLimitSeconds = Config.Float("timeLimitSeconds", defaultSeconds);

        // ILoadoutProvider
        public string[] GetLoadoutSetNames(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetNames(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        public string[] GetLoadoutSetItemCodes(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetItemCodes(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        public string[] GetLoadoutSetDescriptions(string eventCode = null, LoadoutManager lm = null)
            => LoadoutService.GetSetDescriptions(LoadoutService.GetEffectiveSets(eventCode, lm, loadoutSets));

        // Explicit class members rather than leaning on the interface defaults, so a mode can
        // override them the ordinary way.
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

        /// <summary>
        /// The opening of a match, in the order every mode needs it: the time limit, then the score
        /// reset. Call it FIRST from OnBeforeStart and do mode-specific setup after - InitScores
        /// clears the match-resolution latch, so anything that ran before it would be undone.
        ///
        /// A mode without a time limit (ArenaFight) calls <see cref="InitScores"/> directly: passing
        /// it through here would hand it a deadline it is not supposed to have.
        /// </summary>
        protected virtual void BeginMatch()
        {
            runningEvent.SetDuration(timeLimitSeconds);
            InitScores();
        }

        /// <summary>Zero-init kill/death tallies for every participant. Call from OnBeforeStart().</summary>
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
            // Resolved once per match, before the outfits are dealt: a managed wardrobe usually has
            // a different number of outfits than the JSON one, and the count decides the deal.
            outfits = EventOutfit.GetEffectiveOutfits(runningEvent.Event?.Code,
                runningEvent.OutfitManager, jsonOutfits);
            // Dealt here rather than at dress time: the whole line-up has to be known at once for
            // "everyone looks different" to mean anything.
            outfitPicks = EventOutfit.Assign(runningEvent.ParticipantsBefore, outfits.Count,
                stable: !distinctOutfits, rand: runningEvent.sapi?.World?.Rand);

            // Distinct outfits are a promise the wardrobe has to be able to keep: with fewer
            // outfits than players some of them WILL be dressed alike, and in a hidden-role mode
            // that is a broken clue, not a cosmetic slip. Said out loud rather than silently
            // wrapping around, because only the config can fix it.
            if (distinctOutfits && outfits.Count > 0 && outfits.Count < runningEvent.ParticipantsBefore.Count)
                log.Warning("distinctOutfits is on but the wardrobe has {0} outfit(s) for {1} player(s) - "
                    + "{2} of them will be dressed alike. Add outfits or lower maxPartiesCount.",
                    outfits.Count, runningEvent.ParticipantsBefore.Count,
                    runningEvent.ParticipantsBefore.Count - outfits.Count);
        }

        /// <summary>Equip the chosen loadout and full-heal every participant. Call from OnStart().</summary>
        protected void GiveLoadoutAndHealAll()
        {
            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                // A pre-start dropout stays on the roster for rewards but never enters the arena;
                // a kit handed to them would be minted outside the match - nothing takes it back.
                if (!runningEvent.IsActive(uid)) continue;
                var player = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (player == null) continue;
                GiveLoadoutAndHeal(player);
            }
        }

        /// <summary>Re-equip + full-heal a single (respawned) player.</summary>
        protected void GiveLoadoutAndHeal(IServerPlayer player)
        {
            // Bring-your-own match: inventory is never cleared or restored, so a kit here would be
            // a straight item mint, and EquipSet would destroy worn armour. Only the heal applies.
            if (runningEvent.KeepOwnGear)
            {
                EventBroadcaster.Heal(player);
                EventBroadcaster.ApplyStatMutators(player, runningEvent.Event);
                return;
            }

            LoadoutService.Give(player, runningEvent, loadoutSets);
            // After the loadout, never before: the outfit only fills clothing slots the loadout
            // left empty, so issued armour keeps its place.
            EventOutfit.Dress(player, runningEvent.sapi, outfits, OutfitIndexOf(player.PlayerUID));
            EventBroadcaster.Heal(player);
            // Strictly after the heal, which would otherwise undo a reduced start health. The match
            // start applies these too (see ActuallyStartEvent); this call is what covers a respawn.
            EventBroadcaster.ApplyStatMutators(player, runningEvent.Event);
        }

        /// <summary>Tally a death and credit a clean kill (no self-kill, no team-kill) to the killer
        /// and the leaderboard; returns the resolved info for the concrete event to react on.</summary>
        protected KillInfo RecordDeath(IServerPlayer victim, DamageSource source)
        {
            string victimUid = victim.PlayerUID;
            if (deathCounts.ContainsKey(victimUid)) deathCounts[victimUid]++;

            var kill = KillAttribution.Resolve(runningEvent, victim, source);
            if (kill.HasKiller && !kill.TeamKill && kill.KillerUid != victimUid)
            {
                if (killCounts.ContainsKey(kill.KillerUid)) killCounts[kill.KillerUid]++;
                // The persistent leaderboard counts only the rated track, and only actual
                // participants: without the roster check a bystander farms kills from outside the
                // arena, and a bot killer leaves an immortal `bot:...` row in the standings.
                if (runningEvent.Event.Rated
                    && !runningEvent.SuppressRating
                    && killCounts.ContainsKey(kill.KillerUid)
                    && !Bots.BotIdentity.IsBot(kill.KillerUid))
                    runningEvent.Leaderboard?.RecordKill(kill.KillerUid, runningEvent.Event.Code);
            }
            return kill;
        }

        // IOutfitWardrobe: the deal covers every participant, bots included, so ArenaBotsEB can
        // dress its bodies from it instead of picking on its own and colliding with a player.

        public List<string[]> Wardrobe => outfits;

        /// <summary>Which outfit this player wears in this match, or -1 for the stable per-uid pick.</summary>
        public int OutfitIndexOf(string uid)
            => outfitPicks != null && uid != null && outfitPicks.TryGetValue(uid, out var i) ? i : -1;

        protected void SendKillFeed(string killerName, string victimName, string verb)
            => runningEvent.NetworkHandler?.SendKillFeed(runningEvent, killerName, victimName, verb);

        // Death handling: one fixed shape for every kill-scoring mode (active check, tally, feed,
        // scoreboard); modes fill in only the middle, because per-mode copies drifted.

        /// <summary>Whether a death goes to the kill feed at all. Off by config for an event that
        /// wants its deaths to pass unremarked - in a hidden-role mode even "somebody died" is a
        /// clue, because it says the killer is within reach of whoever just vanished.</summary>
        protected virtual bool SendsKillFeed => announceDeaths;

        /// <summary>What the kill feed says this mode does to the victim ("killed", "eliminated").</summary>
        protected virtual string KillFeedVerb => "killed";

        /// <summary>Who the kill feed names as the killer. Anonymous while the mode hides killers,
        /// so the audience still sees that somebody died without learning who did it.</summary>
        protected virtual string KillFeedKillerName(KillInfo kill)
            => HidesKillerIdentity ? Lang.Get("canevents:killer-someone") : kill.KillerName;

        /// <summary>Mode-specific scoring and announcement, before the kill feed goes out.</summary>
        protected virtual void OnScoredDeath(IServerPlayer victim, KillInfo kill) { }

        /// <summary>Mode-specific follow-up once the death is on record (elimination, win checks).
        /// Split from <see cref="OnScoredDeath"/> so the feed and scoreboard are already out when a
        /// mode ends the match from here.</summary>
        protected virtual void AfterScoredDeath(IServerPlayer victim, KillInfo kill) { }

        public sealed override void OnParticipantDeath(IServerPlayer victim, DamageSource source)
        {
            // A player already eliminated (or never in this match) must not score or be scored on.
            if (!runningEvent.IsActive(victim.PlayerUID)) return;

            var kill = RecordDeath(victim, source);
            OnScoredDeath(victim, kill);
            if (SendsKillFeed) SendKillFeed(KillFeedKillerName(kill), victim.PlayerName, KillFeedVerb);
            PushScoreboard();
            AfterScoredDeath(victim, kill);
        }

        // Match resolution latch

        private bool matchResolved;

        /// <summary>Whether a winner has already been declared. Modes that keep scoring on a timer
        /// check this so a resolved match stops reacting.</summary>
        protected bool MatchResolved => matchResolved;

        /// <summary>
        /// Declares the winners (null = draw) and asks the manager to finish, once, so a second
        /// resolution cannot overwrite the first (e.g. time-up declaring a draw over a score win).
        /// Returns whether this call resolved the match.
        /// </summary>
        /// <param name="requestFinish">False when the match is already finishing and only the winners
        /// are still open - the time-up path, where the manager called OnBeforeFinish itself.</param>
        protected bool ResolveMatch(IEnumerable<string> winners, string reason, bool requestFinish = true)
        {
            if (matchResolved) return false;
            matchResolved = true;
            runningEvent.SetWinners(winners); // null => draw
            if (requestFinish) runningEvent.RequestFinish(reason);
            return true;
        }

        /// <summary>Latches "resolved" without touching the winners: for a mode whose winner the
        /// default <c>GetWinners()</c> fallback (last group standing) already answers correctly.</summary>
        protected void MarkMatchResolved() => matchResolved = true;

        /// <summary>Clears the latch for a fresh match. Call from OnBeforeStart(): behaviors are
        /// re-parsed per match, but a reused instance would otherwise start already resolved.</summary>
        protected void ResetMatchResolution() => matchResolved = false;

        protected virtual void PushScoreboard()
            => runningEvent.NetworkHandler?.SendScoreboard(runningEvent, killCounts, deathCounts);

        // IMatchScorer
        public Dictionary<string, int> GetFinalKillCounts() => killCounts;
        public Dictionary<string, int> GetFinalDeathCounts() => deathCounts;

        /// <summary>
        /// Kill-based placement: FFA ranks each player by winner, then kills desc / deaths asc; team
        /// mode (any group &gt; 1) ranks brackets of teams by winner, then total team kills.
        /// Score-based team modes override this in <see cref="TeamScoreEB"/>.
        /// </summary>
        public virtual List<HashSet<string>> GetFinalRanking()
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            var winners = runningEvent.GetWinners();

            if (groups != null && groups.Any(g => g.Count > 1))
            {
                // Teams sharing the same (won, kills) key must share ONE bracket: without the merge
                // the stable sort ordered tied teams strictly, and the first-listed team collected a
                // full win's Elo and the 1st-place reward on a 0-0 draw.
                return groups
                    .GroupBy(g => (
                        won: g.Any(uid => winners.Contains(uid)),
                        kills: g.Sum(uid => killCounts.TryGetValue(uid, out var k) ? k : 0)))
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

            // FFA: players sharing the full key tie into one bracket (same reasoning as the team path).
            return runningEvent.ParticipantsBefore
                .GroupBy(uid => (
                    won: winners.Contains(uid) ? 1 : 0,
                    kills: killCounts.TryGetValue(uid, out var k) ? k : 0,
                    deaths: deathCounts.TryGetValue(uid, out var d) ? d : 0))
                .OrderByDescending(grp => grp.Key.won)
                .ThenByDescending(grp => grp.Key.kills)
                .ThenBy(grp => grp.Key.deaths)
                .Select(grp => new HashSet<string>(grp))
                .ToList();
        }
    }
}
