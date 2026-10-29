using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using canevents.src.HarmonyPatches;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace canevents.src.EventManagers.eventBehavior
{
    // Posts per place: BehaviorData["CaptureTheFlag"] = { Posts: [{X,Y,Z}, ...] }, index = team.
    // ISpawnOrderMatters: spawn points and posts share the team index, so no shuffling.
    public class CaptureTheFlagEB : TeamScoreEB, IStartCryProvider, ISpawnOrderMatters
    {
        private int captureLimit = 3;
        private float captureRadius = 3f;
        private bool showPostParticles = true;

        // Posts with no owning team; only for the solo test event.
        private bool allowNeutralPosts;

        // Null entries when the place has fewer posts than teams.
        private BlockPos[] posts = new BlockPos[0];

        // carriedBy[t] = uid carrying team t's flag, null when home.
        private string[] carriedBy = new string[0];

        private long scanListenerId = -1;

        // uid -> match time when the "own flag is away" notice may repeat.
        private readonly Dictionary<string, float> ownFlagNotice = new Dictionary<string, float>();

        private readonly Dictionary<BlockPos, int> savedPostBlocks = new Dictionary<BlockPos, int>();
        private int flagBlockId;

        public CaptureTheFlagEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.CaptureTheFlag;

        public string StartCryLangKey => "canevents:ctf-start-cry";

        protected override float DefaultTimeLimitSeconds => 600f;

        protected override void ReadModeConfig()
        {
            captureLimit = Config.Int("captureLimit", 3);
            captureRadius = Config.Float("captureRadius", 3f);
            showPostParticles = Config.Bool("showPostParticles", true);
            allowNeutralPosts = Config.Bool("allowNeutralPosts", false);
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            ResolvePostsFromPlace(runningEvent.ParticipantsByGroupsBefore.Count);
            carriedBy = new string[posts.Length];
        }

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

            // Cap at team count, an undefended flag is free points.
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

        // Overlapping capture zones make a pickup score instantly.
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

            flagBlockId = runningEvent.sapi.World.GetBlock(new AssetLocation("canevents:ctfflag-1"))?.BlockId ?? 0;
            if (flagBlockId == 0)
            {
                runningEvent.sapi.World.Logger.Error(
                    "[CTF] Block 'canevents:ctfflag-1' not found - flags will be invisible and cannot be clicked.");
            }
            else if (JournalPosts())
            {
                for (int t = 0; t < posts.Length; t++) SetPostBlock(t, true);
                runningEvent.sapi.World.BlockAccessor.Commit();
            }
            else
            {
                flagBlockId = 0;   // no record of what is under the posts: raise no flags
            }

            if (AbortIfUnplayable()) return;

            // Faster than the 1 Hz behavior tick, as in ParkourEB.
            scanListenerId = runningEvent.RegisterMatchTick(ScanTick, 250);
        }

        private bool AbortIfUnplayable()
        {
            string reason = null;

            if (flagBlockId == 0)
            {
                reason = "canevents:ctf-abort-no-flagblock";
            }
            else
            {
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

            Announce(reason);
            runningEvent.sapi.World.Logger.Error(
                "[CTF] Aborting match on place id={0}: {1}", runningEvent.Place?.Id, reason);
            runningEvent.RequestFinish("ctf arena misconfigured");
            return true;
        }

        public override bool WantsTick => true;

        // Only draws; flag logic is in the 250 ms scan.
        public override void OnTick(float dt)
        {
            if (MatchResolved) return;
            if (!showPostParticles) return;

            DrawPosts();
        }

        private void ScanTick(float dt)
        {
            if (MatchResolved) return;

            foreach (var (uid, body) in runningEvent.LiveBodies())
            {
                if (MatchResolved) return;
                int team = runningEvent.TeamIndexOf(uid);
                if (team < 0) continue;

                // Bots can't right-click, they take a flag by touching its post.
                if (Bots.BotIdentity.IsBot(uid)) TryBotTakeFlag(uid, team, body);

                if (team < posts.Length && posts[team] != null && IsInCaptureZone(body, posts[team]))
                    TryCapture(uid, team);
            }
        }

        private const double BotPickupRadius = 1.5;

        private void TryBotTakeFlag(string uid, int botTeam, Entity body)
        {
            if (FlagCarriedBy(uid) >= 0) return;

            var p = body.Pos;
            for (int t = 0; t < posts.Length; t++)
            {
                if (t == botTeam || posts[t] == null || carriedBy[t] != null) continue;
                if (!IsNearPost(p, posts[t], BotPickupRadius)) continue;

                TakeFlag(uid, t);
                return;
            }
        }

        // From the flag block's right-click. True means the interaction was ours and is swallowed.
        public bool TryTakeFlagAt(IServerPlayer player, BlockPos pos)
        {
            if (MatchResolved || pos == null) return false;

            int team = TeamOfPostAt(pos);
            if (team < 0) return false;

            int playerTeam = runningEvent.TeamIndexOf(player.PlayerUID);
            if (playerTeam < 0) return false;

            if (playerTeam == team)
            {
                player.SendMessage(0, Lang.Get("canevents:ctf-own-flag-click"), EnumChatType.Notification);
                return true;
            }

            if (carriedBy[team] != null) return true;

            // One flag per player, matters with 3+ teams.
            int alreadyCarrying = FlagCarriedBy(player.PlayerUID);
            if (alreadyCarrying >= 0)
            {
                player.SendMessage(0,
                    Lang.Get("canevents:ctf-already-carrying", alreadyCarrying + 1),
                    EnumChatType.Notification);
                return true;
            }

            TakeFlag(player.PlayerUID, team);
            return true;
        }

        private void TakeFlag(string uid, int team)
        {
            carriedBy[team] = uid;
            SetPostBlock(team, false);
            Announce("canevents:ctf-flag-taken", runningEvent.NameOf(uid), team + 1);
            FlagsMoved();
        }

        private void ReturnFlagHome(int team)
        {
            carriedBy[team] = null;
            SetPostBlock(team, true);
        }

        private void FlagsMoved()
        {
            PushScoreboard();
            SyncCarriers();
        }

        private int FlagCarriedBy(string uid)
        {
            for (int t = 0; t < carriedBy.Length; t++)
                if (carriedBy[t] == uid) return t;
            return -1;
        }

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

        // Cylinder, not sphere, so a bridge above the post doesn't count.
        private bool IsInCaptureZone(Entity body, BlockPos post) => IsNearPost(body.Pos, post, captureRadius);

        private static bool IsNearPost(EntityPos p, BlockPos post, double radius)
            => p.Dimension == post.dimension
               && CylinderZone.Contains(p, post.X + 0.5, post.Y, post.Z + 0.5, radius, CaptureHeightBelow, CaptureHeightAbove);

        // Above allows standing on the 1-tall flag block.
        private const double CaptureHeightAbove = 2.0;
        private const double CaptureHeightBelow = 1.0;

        // Flag block present == flag at home.
        private void SetPostBlock(int team, bool present)
        {
            var pos = posts[team];
            if (pos == null) return;

            var ba = runningEvent.sapi.World.BlockAccessor;

            if (present)
            {
                if (flagBlockId == 0) return;
                if (!savedPostBlocks.ContainsKey(pos)) savedPostBlocks[pos.Copy()] = ba.GetBlock(pos).BlockId;

                // ctfflag-1..6, one colour per team.
                var block = runningEvent.sapi.World.GetBlock(
                    new AssetLocation("canevents:ctfflag-" + Math.Min(team + 1, 6)));
                ba.SetBlock(block?.BlockId ?? flagBlockId, pos);
            }
            else
            {
                ba.SetBlock(savedPostBlocks.TryGetValue(pos, out var original) ? original : 0, pos);
            }

            ba.MarkBlockDirty(pos);
        }

        // Lets the block behavior tell a live flag from leftovers of a crashed match.
        public bool OwnsFlagAt(BlockPos pos) => pos != null && TeamOfPostAt(pos) >= 0;

        private bool JournalPosts()
        {
            var ba = runningEvent.sapi.World.BlockAccessor;
            var originals = new Dictionary<BlockPos, int>();
            foreach (var pos in posts)
                if (pos != null) originals[pos.Copy()] = ba.GetBlock(pos).BlockId;

            if (!ArenaBlockJournal.Record(runningEvent.Place?.Id, ArenaBlockJournal.TagCtfPosts, originals))
            {
                runningEvent.sapi.World.Logger.Error("[CTF] could not record the blocks under the flag posts of place {0}.",
                    runningEvent.Place?.Id);
                return false;
            }

            foreach (var kvp in originals) savedPostBlocks[kvp.Key] = kvp.Value;
            return true;
        }

        private void RestorePostBlocks()
        {
            if (savedPostBlocks.Count == 0) return;
            ArenaBlockJournal.RestoreOr(runningEvent.Place?.Id, ArenaBlockJournal.TagCtfPosts,
                runningEvent.sapi.World.BlockAccessor, savedPostBlocks);
            savedPostBlocks.Clear();
        }

        private void TryCapture(string uid, int playerTeam)
        {
            int flag = FlagCarriedBy(uid);
            if (flag < 0 || flag == playerTeam) return;

            // Own flag must be home to score.
            if (playerTeam < carriedBy.Length && carriedBy[playerTeam] != null)
            {
                if (!ownFlagNotice.TryGetValue(uid, out var until)
                    || runningEvent.ElapsedSeconds >= until)
                {
                    ownFlagNotice[uid] = runningEvent.ElapsedSeconds + 5f;
                    (runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer)?.SendMessage(0,
                        Lang.Get("canevents:ctf-own-flag-away"), EnumChatType.Notification);
                }
                return;
            }

            ReturnFlagHome(flag);
            teamScores[playerTeam]++;

            Announce("canevents:ctf-captured", runningEvent.NameOf(uid), playerTeam + 1, teamScores[playerTeam]);

            FlagsMoved();
            if (teamScores[playerTeam] >= captureLimit) DeclareTeamWinner(playerTeam);
        }

        // Runs after the base scoreboard push, so a moved flag pushes again.
        protected override void AfterScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
        {
            string victimName = runningEvent.NameOf(victimUid);
            ReturnFlagsWhere(carrier => carrier == victimUid,
                t => Announce("canevents:ctf-flag-returned", victimName, t + 1));
        }

        public override void OnParticipantLeftGame()
        {
            if (MatchResolved) return;
            ReturnFlagsWhere(carrier => !runningEvent.IsActive(carrier) || runningEvent.BodyOf(carrier) == null,
                t => Announce("canevents:ctf-flag-returned-left", t + 1));
        }

        private void ReturnFlagsWhere(System.Func<string, bool> carrierMatches, Action<int> announce)
        {
            bool any = false;
            for (int t = 0; t < carriedBy.Length; t++)
            {
                if (carriedBy[t] == null || !carrierMatches(carriedBy[t])) continue;
                ReturnFlagHome(t);
                announce(t);
                any = true;
            }
            if (any) FlagsMoved();
        }

        // Capture limit drives the HUD progress bars.
        public override ScoreboardData BuildScoreboard()
        {
            var data = base.BuildScoreboard();
            data.TeamScoreLimit = captureLimit;
            return data;
        }

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

        // Clients draw the flag on the carrier's model. Sent on change, not on a timer.
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

            EventBroadcaster.FlagCarriers(runningEvent, uids.ToArray(), teams.ToArray());
        }

        public override void OnEnd()
        {
            RestorePostBlocks();

            // Client renderer doesn't know the match ended.
            Array.Clear(carriedBy, 0, carriedBy.Length);
            SyncCarriers();

            base.OnEnd();
        }

        protected override string EndedLangKey => "canevents:ctf-ended";
    }
}
