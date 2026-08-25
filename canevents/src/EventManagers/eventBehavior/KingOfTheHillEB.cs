using System;
using System.Collections.Generic;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // King of the Hill: teams score per second while they outnumber every other team inside the hill
    // zone; parity is a deadlock and nobody scores. Win = first to scoreLimit, else highest at time up.
    // Hill geometry is per-place: BehaviorData["KingOfTheHill"] = { X, Y, Z, Radius, Height }.
    public class KingOfTheHillEB : TeamScoreEB, HarmonyPatches.IProvidesRespawnPoint
    {
        // Sentinel values for hillOwnerTeam, shared with the HUD over the scoreboard packet.
        public const int OwnerNeutral = -1;
        public const int OwnerContested = -2;

        private int scoreLimit = 200;
        private float hillRadius = 5f;
        private float hillHeight = 5f;

        private int pointsPerSecond = 1;
        private int advantageBonusPerPlayer = 1;
        private int maxAdvantageBonus = 2;
        private bool showZoneParticles = true;

        private double hillX, hillY, hillZ;
        private bool hillSet = false;
        private float hillRadiusSq;

        // Reused every tick so scoring allocates nothing.
        private int[] zoneCounts;
        private int hillOwnerTeam = OwnerNeutral;
        // Last owner actually broadcast. Kept apart from hillOwnerTeam so a change arriving during the
        // announce cooldown is still announced once it lifts.
        private int announcedOwnerTeam = OwnerNeutral;

        private float scoreTickAccum = 0f;
        private float ownerNotifyCooldown = 0f;

        public KingOfTheHillEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name { get => "KingOfTheHill"; set { } }

        public override void Initialize()
        {
            ParseTimeLimit(480f);
            scoreLimit = Config.Int("scoreLimit", 200);
            hillRadius = Config.Float("defaultRadius", 5f);
            hillHeight = Config.Float("defaultHeight", 5f);
            pointsPerSecond = Config.Int("pointsPerSecond", 1);
            advantageBonusPerPlayer = Config.Int("advantageBonusPerPlayer", 1);
            maxAdvantageBonus = Config.Int("maxAdvantageBonus", 2);
            showZoneParticles = Config.Bool("showZoneParticles", true);
            ParseLoadoutSets();
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            zoneCounts = new int[runningEvent.ParticipantsByGroupsBefore.Count];

            // Radius/height are this mode's own settings and apply only when the hill was configured.
            var place = runningEvent.Place;
            var origin = Shared.PlaceZone.TryResolveCentre(place, BehaviorKeys.KingOfTheHill,
                out hillX, out hillY, out hillZ);
            hillSet = origin != Shared.ZoneOrigin.None;

            if (origin == Shared.ZoneOrigin.Configured
                && place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var data))
            {
                if (data["Radius"] != null) hillRadius = data.Value<float>("Radius");
                if (data["Height"] != null) hillHeight = data.Value<float>("Height");
            }
            else if (origin == Shared.ZoneOrigin.BoundsCentre)
            {
                runningEvent.sapi.World.Logger.Notification(
                    "[KOTH] Place id={0} has no hill configured; using bounds center r={1} h={2}.",
                    place.Id, hillRadius, hillHeight);
            }
            else
            {
                runningEvent.sapi.World.Logger.Warning(
                    "[KOTH] No hill configured for place id={0} and no bounds to fall back to. "
                    + "Nobody can score. Use /caneventplace kothhill <radius> [height].",
                    place?.Id);
            }
            hillRadiusSq = hillRadius * hillRadius;
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            // Initial scoreboard push so HUD shows team rows from t=0.
            PushScoreboard();
        }

        // Deaths only feed the kill tally (handled by the base): the hill, not frags, decides the match.

        public override void OnParticipantRespawn(IServerPlayer player)
        {
            if (!runningEvent.IsActive(player.PlayerUID)) return;
            GiveLoadoutAndHeal(player);
        }

        // Respawn mode: without this the engine sends the dead player to their bed / world spawn and
        // the team loses a body for the rest of the match.
        public bool TryGetRespawnPoint(string playerUid, out PlayerSpawnPos pos)
            => EventSpawns.TryGetTeamSpawn(runningEvent, playerUid, out pos);

        public override void OnTick(float dt)
        {
            if (MatchResolved) return;
            if (ownerNotifyCooldown > 0) ownerNotifyCooldown -= dt;

            scoreTickAccum += dt;
            if (scoreTickAccum >= 1f)
            {
                scoreTickAccum -= 1f;
                if (hillSet)
                {
                    ScoreTick();
                    if (showZoneParticles) DrawHillRing();
                }
                PushScoreboard();

                for (int i = 0; i < teamScores.Length; i++)
                {
                    if (teamScores[i] >= scoreLimit)
                    {
                        DeclareTeamWinner(i);
                        return;
                    }
                }
            }
            // Time-up is the manager's job (MaxDurationSeconds); the winner is resolved in
            // OnBeforeFinish to avoid racing its tick.
        }

        // OnBeforeFinish (lone surviving team, else the highest score) comes from TeamScoreEB.

        private void ScoreTick()
        {
            var groups = runningEvent.ParticipantsByGroupsBefore;
            Array.Clear(zoneCounts, 0, zoneCounts.Length);

            for (int i = 0; i < groups.Count; i++)
            {
                foreach (var uid in groups[i])
                {
                    if (!runningEvent.IsActive(uid)) continue;
                    var pl = runningEvent.sapi.World.PlayerByUid(uid) as IServerPlayer;
                    if (pl?.Entity == null || !pl.Entity.Alive) continue;
                    var pos = pl.Entity.Pos;
                    double dx = pos.X - hillX;
                    double dz = pos.Z - hillZ;
                    double dy = pos.Y - hillY;
                    // Cylinder check: XZ radius + vertical range [-1, hillHeight]
                    if (dx * dx + dz * dz <= hillRadiusSq && dy >= -1.0 && dy <= hillHeight)
                    {
                        zoneCounts[i]++;
                    }
                }
            }

            int top = 0, second = 0, topTeam = -1;
            for (int i = 0; i < zoneCounts.Length; i++)
            {
                int n = zoneCounts[i];
                if (n > top) { second = top; top = n; topTeam = i; }
                else if (n > second) second = n;
            }

            int newOwner;
            if (top == 0)
            {
                newOwner = OwnerNeutral;
            }
            else if (top == second)
            {
                // Numerical parity is a deadlock: break it by killing, not by standing.
                newOwner = OwnerContested;
            }
            else
            {
                newOwner = topTeam;
                int pts = pointsPerSecond;
                // The bonus pays only against actual opposition, so stacking an empty hill gains
                // nothing. Surplus is (top - second): a 2-vs-1 hold is one player of advantage.
                if (second > 0)
                {
                    pts += advantageBonusPerPlayer * Math.Min(top - second, maxAdvantageBonus);
                }
                teamScores[topTeam] += pts;
            }

            AnnounceOwnerChange(newOwner);
            hillOwnerTeam = newOwner;
        }

        // Transitions only; the cooldown swallows flapping. Dedup is against the last ANNOUNCED owner,
        // not the live one, so a flip landing during the cooldown is still announced afterward.
        private void AnnounceOwnerChange(int newOwner)
        {
            if (newOwner == announcedOwnerTeam) return;
            if (ownerNotifyCooldown > 0f) return;
            ownerNotifyCooldown = 3f;
            announcedOwnerTeam = newOwner;

            if (newOwner >= 0)
            {
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:koth-captured", newOwner + 1));
            }
            else if (newOwner == OwnerContested)
            {
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:koth-contested"));
            }
            else
            {
                EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:koth-neutral"));
            }
        }

        private void DrawHillRing()
        {
            int segments = Math.Min(32, Math.Max(12, (int)(hillRadius * 4)));
            ZoneVisualizer.DrawRing(runningEvent.sapi, hillX, hillY, hillZ, hillRadius, segments,
                ZoneVisualizer.OwnerColor(hillOwnerTeam));
        }

        protected override void PushScoreboard()
            => runningEvent.NetworkHandler?.SendScoreboard(
                runningEvent, killCounts, deathCounts, teamScores, hillOwnerTeam, scoreLimit);

        public override void OnEnd()
        {
            EventBroadcaster.Broadcast(runningEvent, Lang.Get("canevents:koth-ended", ScoreLine()));
        }
    }
}
