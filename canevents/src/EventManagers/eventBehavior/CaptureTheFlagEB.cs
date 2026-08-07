using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Capture the Flag: score by carrying an enemy flag to your own post while your flag is home.
    // Posts are per-place: BehaviorData["CaptureTheFlag"] = { Posts: [{X,Y,Z}, ...] }, index = team.
    // v1 has only two flag states (home / carried); killing the carrier sends the flag home.
    public class CaptureTheFlagEB : TeamScoreEB, IProvidesRespawnPoint, IStartCryProvider
    {
        private int captureLimit = 3;
        // Only the capture area is a radius; pickup is a right-click on the flag block.
        private float captureRadius = 3f;
        private bool showPostParticles = true;

        // Raise posts no team owns. Off in real matches; the solo test event needs a flag to take.
        private bool allowNeutralPosts;

        // Post position per team; null when the place configures fewer posts than there are teams.
        private BlockPos[] posts = new BlockPos[0];

        // carriedBy[t] = uid carrying team t's flag, null when home. Whole flag state in one array.
        private string[] carriedBy = new string[0];

        private long scanListenerId = -1;

        // uid -> match time after which this player may be told again that their own flag is away.
        private readonly Dictionary<string, float> ownFlagNotice = new Dictionary<string, float>();

        // Original block under each post, so the arena is restored on end.
        private readonly Dictionary<BlockPos, int> savedPostBlocks = new Dictionary<BlockPos, int>();
        private int flagBlockId;

        public CaptureTheFlagEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "CaptureTheFlag"; set { } }

        public string StartCryLangKey => "canevents:ctf-start-cry";

        public override void Initialize()
        {
            ParseTimeLimit(600f);
            captureLimit = Config.Int("captureLimit", 3);
            captureRadius = Config.Float("captureRadius", 3f);
            showPostParticles = Config.Bool("showPostParticles", true);
            allowNeutralPosts = Config.Bool("allowNeutralPosts", false);
            ParseLoadoutSets();
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            runningEvent.SetDuration(timeLimitSeconds);
            InitScores();
            InitTeamScores();

            ResolvePostsFromPlace(runningEvent.ParticipantsByGroupsBefore.Count);
            carriedBy = new string[posts.Length];
        }

        /// <summary>
        /// Reads one post per team from the place. Fewer posts than teams is a misconfiguration:
        /// warn loudly, leave those entries null, and let the match run instead of throwing mid-tick.
        /// </summary>
        private void ResolvePostsFromPlace(int teamCount)
        {
            posts = new BlockPos[0];

            var place = runningEvent.Place;
            var logger = runningEvent.sapi.World.Logger;

            if (place == null || !place.TryGetBehaviorData(BehaviorKeys.CaptureTheFlag, out var data)
                || !(data["Posts"] is Newtonsoft.Json.Linq.JArray arr) || arr.Count == 0)
            {
                logger.Warning("[CTF] Place id={0} has no flag posts configured. Nobody can capture. "
                    + "Use /canep ctfpost <team>.", place?.Id);
                return;
            }

            // Cap at team count: an undefended flag would be free points decided by footspeed.
            // allowNeutralPosts exists for the single-player test event.
            int usable = allowNeutralPosts ? arr.Count : Math.Min(arr.Count, Math.Max(1, teamCount));
            posts = new BlockPos[usable];

            int placed = 0;
            for (int t = 0; t < usable; t++)
            {
                if (!JsonPos.TryRead(arr[t], out var postPos)) continue;

                posts[t] = postPos;
                placed++;
            }

            if (placed < teamCount)
            {
                logger.Warning("[CTF] Place id={0} has {1} usable flag post(s) but the match has {2} team(s). "
                    + "Teams without a post cannot capture or be captured from.", place.Id, placed, teamCount);
            }

            WarnAboutPostsTooClose(logger, place.Id);
        }

        /// <summary>
        /// Posts closer than two capture radii have overlapping zones: grabbing a flag then scores
        /// instantly. Cheap to detect, baffling to debug in-game.
        /// </summary>
        private void WarnAboutPostsTooClose(ILogger logger, long placeId)
        {
            float minGap = captureRadius * 2f;

            for (int a = 0; a < posts.Length; a++)
            {
                if (posts[a] == null) continue;
                for (int b = a + 1; b < posts.Length; b++)
                {
                    if (posts[b] == null) continue;

                    double dx = posts[a].X - posts[b].X;
                    double dy = posts[a].Y - posts[b].Y;
                    double dz = posts[a].Z - posts[b].Z;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist >= minGap) continue;

                    logger.Warning(
                        "[CTF] Place id={0}: posts {1} and {2} are {3:0.#} blocks apart, closer than the "
                        + "{4:0.#} needed for separate capture zones. Taking a flag there scores immediately. "
                        + "Move them apart or lower captureRadius.",
                        placeId, a + 1, b + 1, dist, minGap);
                }
            }
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            PushScoreboard();

            // Resolved once: a missing block type means no flags rather than a failed SetBlock per post.
            flagBlockId = runningEvent.sapi.World.GetBlock(new AssetLocation("canevents:ctfflag-1"))?.BlockId ?? 0;
            if (flagBlockId == 0)
            {
                runningEvent.sapi.World.Logger.Error(
                    "[CTF] Block 'canevents:ctfflag-1' not found - flags will be invisible and cannot be clicked.");
            }
            else
            {
                for (int t = 0; t < posts.Length; t++) SetPostBlock(t, true);
                runningEvent.sapi.World.BlockAccessor.Commit();
            }

            if (AbortIfUnplayable()) return;

            // Faster than the 1 Hz behavior tick so pickup feels immediate (as in ParkourEB).
            scanListenerId = runningEvent.RegisterMatchTick(ScanTick, 250);
        }

        /// <summary>
        /// Ends the match at once when its rules cannot be satisfied, rather than running a full
        /// timer on an arena where scoring is impossible. Returns true when the match was aborted.
        /// </summary>
        private bool AbortIfUnplayable()
        {
            string reason = null;

            if (flagBlockId == 0)
            {
                // No flag block means no flags to right-click.
                reason = "canevents:ctf-abort-no-flagblock";
            }
            else
            {
                // A team without a post can neither be scored against nor bring a flag home.
                int teamCount = runningEvent.ParticipantsByGroupsBefore.Count;
                for (int t = 0; t < teamCount; t++)
                {
                    if (t >= posts.Length || posts[t] == null)
                    {
                        reason = "canevents:ctf-abort-no-post";
                        break;
                    }
                }
            }

            if (reason == null) return false;

            EventBroadcaster.Broadcast(runningEvent, Lang.Get(reason));
            runningEvent.sapi.World.Logger.Error(
                "[CTF] Aborting match on place id={0}: {1}", runningEvent.Place?.Id, reason);
            runningEvent.RequestFinish("ctf arena misconfigured");
            return true;
        }

        // The 1 Hz tick only paints; all flag logic lives in the 250 ms scan.
        public override void OnTick(float dt)
        {
            if (MatchResolved) return;
            if (!showPostParticles) return;

            DrawPosts();
        }

        /// <summary>
        /// Only capturing is proximity-based; picking a flag up is a deliberate right-click,
        /// see <see cref="TryTakeFlagAt"/>.
        /// </summary>
        private void ScanTick(float dt)
        {
            if (MatchResolved) return;

            for (int team = 0; team < posts.Length; team++)
            {
                if (posts[team] == null) continue;

                foreach (var uid in runningEvent.ParticipantsStillInEvent.ToArray())
                {
                    if (runningEvent.TeamIndexOf(uid) != team) continue; // own post only

                    var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (pl?.Entity == null || !pl.Entity.Alive) continue;

                    if (IsInCaptureZone(pl, posts[team])) TryCapture(pl, team);
                }
            }
        }

        /// <summary>
        /// Called by the flag block's right-click handler. Returns true when the position is one of
        /// our posts, so the block behavior only swallows the interaction when it handled it.
        /// </summary>
        public bool TryTakeFlagAt(IServerPlayer player, BlockPos pos)
        {
            if (MatchResolved || pos == null) return false;

            int team = TeamOfPostAt(pos);
            if (team < 0) return false;

            int playerTeam = runningEvent.TeamIndexOf(player.PlayerUID);
            if (playerTeam < 0) return false;

            if (playerTeam == team)
            {
                // Your own flag: nothing to take. Say why, so clicking it does not feel broken.
                player.SendMessage(0, Lang.Get("canevents:ctf-own-flag-click"), EnumChatType.Notification);
                return true;
            }

            if (carriedBy[team] != null) return true; // someone already has it

            // One flag per player: otherwise one runner could collect every enemy flag in a 3+ team
            // match and hold flags they cannot all deliver.
            int alreadyCarrying = FlagCarriedBy(player.PlayerUID);
            if (alreadyCarrying >= 0)
            {
                player.SendMessage(0,
                    Lang.Get("canevents:ctf-already-carrying", alreadyCarrying + 1),
                    EnumChatType.Notification);
                return true;
            }

            carriedBy[team] = player.PlayerUID;
            SetPostBlock(team, false); // the post stands empty while the flag is away
            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:ctf-flag-taken", player.PlayerName, team + 1));
            PushScoreboard();
            SyncCarriers();
            return true;
        }

        /// <summary>Index of the flag this player is carrying, or -1. At most one by rule.</summary>
        private int FlagCarriedBy(string uid)
        {
            for (int t = 0; t < carriedBy.Length; t++)
                if (carriedBy[t] == uid) return t;
            return -1;
        }

        /// <summary>Team whose post sits at this block, or -1.</summary>
        private int TeamOfPostAt(BlockPos pos)
        {
            for (int t = 0; t < posts.Length; t++)
            {
                var p = posts[t];
                if (p != null && p.X == pos.X && p.Y == pos.Y && p.Z == pos.Z && p.dimension == pos.dimension)
                    return t;
            }
            return -1;
        }

        /// <summary>
        /// Cylinder, not sphere: with a spherical radius a player on a bridge above the post would
        /// score without reaching it, so height is capped separately and tightly.
        /// </summary>
        private bool IsInCaptureZone(IServerPlayer pl, BlockPos post)
        {
            var p = pl.Entity.Pos;

            // Match TeamOfPostAt's dimension check; the Y comparison below is only meaningful
            // within one dimension.
            if (p.Dimension != post.dimension) return false;

            double dy = p.Y - post.Y;
            if (dy > CaptureHeightAbove || dy < -CaptureHeightBelow) return false;

            double dx = p.X - (post.X + 0.5);
            double dz = p.Z - (post.Z + 0.5);
            return dx * dx + dz * dz <= captureRadius * captureRadius;
        }

        // The flag block is one tall, so standing on top of the post still counts; anything higher
        // is a different floor. Below is tighter.
        private const double CaptureHeightAbove = 2.0;
        private const double CaptureHeightBelow = 1.0;

        /// <summary>
        /// Puts the flag block up or takes it down: its presence IS "flag at home", visible at a
        /// glance. The block underneath is saved on first placement and restored in OnEnd.
        /// </summary>
        private void SetPostBlock(int team, bool present)
        {
            var pos = posts[team];
            if (pos == null) return;

            var ba = runningEvent.sapi.World.BlockAccessor;

            if (present)
            {
                if (flagBlockId == 0) return;
                if (!savedPostBlocks.ContainsKey(pos)) savedPostBlocks[pos.Copy()] = ba.GetBlock(pos).BlockId;

                // Variant per team, so each base flies its own colour.
                var block = runningEvent.sapi.World.GetBlock(
                    new AssetLocation("canevents:ctfflag-" + Math.Min(team + 1, 6)));
                ba.SetBlock(block?.BlockId ?? flagBlockId, pos);
            }
            else
            {
                // Restore the original block, not air: a post can sit on a decorated base.
                ba.SetBlock(savedPostBlocks.TryGetValue(pos, out var original) ? original : 0, pos);
            }

            ba.MarkBlockDirty(pos);
        }

        /// <summary>
        /// True if this running match owns a flag post here. The flag block behavior uses it to tell
        /// a live (unbreakable) flag from mineable debris left by a crashed match.
        /// </summary>
        public bool OwnsFlagAt(BlockPos pos)
        {
            if (pos == null || posts == null) return false;
            for (int i = 0; i < posts.Length; i++)
                if (posts[i] != null && posts[i].Equals(pos)) return true;
            return false;
        }

        private void RestorePostBlocks()
        {
            if (savedPostBlocks.Count == 0) return;

            var ba = runningEvent.sapi.World.BlockAccessor;
            foreach (var kvp in savedPostBlocks) ba.SetBlock(kvp.Value, kvp.Key);
            ba.Commit();
            savedPostBlocks.Clear();
        }

        private void TryCapture(IServerPlayer pl, int playerTeam)
        {
            // Carrying check first: a defender on their own post must not be told anything.
            int flag = FlagCarriedBy(pl.PlayerUID);
            if (flag < 0 || flag == playerTeam) return;

            // Your own flag must be home, or both teams holding each other's flag would deadlock.
            // A team with no post has no flag to lose and is always free to score.
            if (playerTeam < carriedBy.Length && carriedBy[playerTeam] != null)
            {
                // Per player: one teammate hitting this must not mute the explanation for the rest.
                if (!ownFlagNotice.TryGetValue(pl.PlayerUID, out var until)
                    || runningEvent.ElapsedSeconds >= until)
                {
                    ownFlagNotice[pl.PlayerUID] = runningEvent.ElapsedSeconds + 5f;
                    pl.SendMessage(0, Lang.Get("canevents:ctf-own-flag-away"), EnumChatType.Notification);
                }
                return;
            }

            carriedBy[flag] = null; // enemy flag returns home on capture
            SetPostBlock(flag, true);
            teamScores[playerTeam]++;

            EventBroadcaster.Broadcast(runningEvent,
                Lang.Get("canevents:ctf-captured", pl.PlayerName, playerTeam + 1, teamScores[playerTeam]));

            PushScoreboard();
            SyncCarriers();
            if (teamScores[playerTeam] >= captureLimit) DeclareTeamWinner(playerTeam);
        }

        /// <summary>Sends home every flag this player carried; death or exit is the only way a
        /// carried flag returns in v1.</summary>
        private void ReturnFlagsCarriedBy(string uid, string reasonLangKey, string playerName)
        {
            bool any = false;
            for (int t = 0; t < carriedBy.Length; t++)
            {
                if (carriedBy[t] != uid) continue;
                carriedBy[t] = null;
                SetPostBlock(t, true);
                any = true;
                EventBroadcaster.Broadcast(runningEvent, Lang.Get(reasonLangKey, playerName, t + 1));
            }
            if (any) { PushScoreboard(); SyncCarriers(); }
        }

        // A carrier who dies sends the flag home. Runs after the base scoreboard push;
        // ReturnFlagsCarriedBy pushes again with the new flag state when a flag moved.
        protected override void AfterScoredDeath(IServerPlayer victim, KillInfo kill)
            => ReturnFlagsCarriedBy(victim.PlayerUID, "canevents:ctf-flag-returned", victim.PlayerName);

        public override void OnParticipantLeftGame()
        {
            if (MatchResolved) return;

            // A carrier who disconnected must not hold a flag hostage for the rest of the match.
            bool any = false;
            for (int t = 0; t < carriedBy.Length; t++)
            {
                var uid = carriedBy[t];
                if (uid == null) continue;
                if (runningEvent.IsActive(uid) && runningEvent.sapi.World.PlayerByUid(uid) != null) continue;

                carriedBy[t] = null;
                SetPostBlock(t, true);
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:ctf-flag-returned-left", t + 1));
                any = true;
            }

            // Once after the loop: a whole team leaving would otherwise send a packet per flag.
            if (any) { PushScoreboard(); SyncCarriers(); }
        }

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            if (!runningEvent.IsActive(player.PlayerUID)) return;
            GiveLoadoutAndHeal(player);
        }

        /// <summary>
        /// Respawn at your own team's spawn point: defending is impossible if death can drop you at
        /// the enemy's end. Delegates to the shared resolver, which mirrors TPThereAndBack's layout
        /// (indexing spawns by team number is wrong on per-player-spawn arenas).
        /// </summary>
        public bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos)
            => EventSpawns.TryGetTeamSpawn(runningEvent, playerUid, out pos);

        // Manager owns the time-up trigger. ResolveWinnerTeam gives survivors a forfeit win over a
        // wiped-out/deserting team instead of deciding on flag score.
        public override void OnBeforeFinish() => DeclareTeamWinner(ResolveWinnerTeam());

        /// <summary>
        /// Passes the capture limit so the HUD can draw progress bars; TeamScoreEB's default omits
        /// it because not every team mode has a limit.
        /// </summary>
        protected override void PushScoreboard()
            => runningEvent.NetworkHandler?.SendScoreboard(
                runningEvent, killCounts, deathCounts, teamScores, -1, captureLimit);

        /// <summary>
        /// A ring at each post marking the capture area, which is otherwise invisible.
        /// </summary>
        private void DrawPosts()
        {
            for (int t = 0; t < posts.Length; t++)
            {
                if (posts[t] == null) continue;

                ZoneVisualizer.DrawRing(runningEvent.sapi,
                    posts[t].X + 0.5, posts[t].Y, posts[t].Z + 0.5,
                    captureRadius, 16, ZoneVisualizer.OwnerColor(t));
            }
        }

        /// <summary>
        /// Pushes the carrier list to clients, which draw the flag on the carrier's model. Called on
        /// every change of who holds what, not on a timer - it changes that rarely.
        /// </summary>
        private void SyncCarriers()
        {
            var uids = new List<string>();
            var teams = new List<int>();

            for (int t = 0; t < carriedBy.Length; t++)
            {
                if (carriedBy[t] == null) continue;
                uids.Add(carriedBy[t]);
                teams.Add(t);
            }

            runningEvent.NetworkHandler?.SendCtfCarriers(runningEvent, uids.ToArray(), teams.ToArray());
        }

        public override void OnEnd()
        {
            // Always restore, even on an abandoned match, so no flag block is left behind.
            RestorePostBlocks();

            // The renderer has no idea the match ended, so clear carried flags on every client.
            for (int t = 0; t < carriedBy.Length; t++) carriedBy[t] = null;
            SyncCarriers();

            string score = string.Join(" : ", Array.ConvertAll(teamScores, s => s.ToString()));
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:ctf-ended", score));
        }
    }
}
