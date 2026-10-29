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
    // Team with the most players in the hill scores each second; a tie scores nothing.
    // Hill is per-place: BehaviorData["KingOfTheHill"] = { X, Y, Z, Radius, Height }.
    public class KingOfTheHillEB : TeamScoreEB
    {
        // Also read by the client HUD.
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

        private int[] zoneCounts;
        private int hillOwnerTeam = OwnerNeutral;
        // Separate from hillOwnerTeam so a change during the announce cooldown still gets announced.
        private int announcedOwnerTeam = OwnerNeutral;

        private float ownerNotifyCooldown = 0f;

        public KingOfTheHillEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.KingOfTheHill;

        protected override float DefaultTimeLimitSeconds => 480f;

        protected override void ReadModeConfig()
        {
            scoreLimit = Config.Int(BehaviorConfigKeys.ScoreLimit, 200);
            hillRadius = Config.Float("defaultRadius", 5f);
            hillHeight = Config.Float("defaultHeight", 5f);
            pointsPerSecond = Config.Int("pointsPerSecond", 1);
            advantageBonusPerPlayer = Config.Int("advantageBonusPerPlayer", 1);
            maxAdvantageBonus = Config.Int("maxAdvantageBonus", 2);
            showZoneParticles = Config.Bool(BehaviorConfigKeys.ShowZoneParticles, true);
        }

        public override void OnBeforeStart()
        {
            BeginMatch();
            zoneCounts = new int[runningEvent.ParticipantsByGroupsBefore.Count];

            var place = runningEvent.Place;
            var origin = PlaceZone.TryResolveCentre(place, BehaviorKeys.KingOfTheHill,
                out hillX, out hillY, out hillZ);
            hillSet = origin != ZoneOrigin.None;

            if (origin == ZoneOrigin.Configured
                && place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out var data))
            {
                if (data["Radius"] != null) hillRadius = data.Value<float>("Radius");
                if (data["Height"] != null) hillHeight = data.Value<float>("Height");
            }
            else if (origin == ZoneOrigin.BoundsCentre)
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
        }

        public override void OnStart()
        {
            GiveLoadoutAndHealAll();
            PushScoreboard();
        }

        public override bool WantsTick => true;

        // Manager ticks once a second; that is the scoring period.
        public override void OnTick(float dt)
        {
            if (MatchResolved) return;
            if (ownerNotifyCooldown > 0) ownerNotifyCooldown -= dt;

            if (hillSet)
            {
                ScoreTick();
                if (showZoneParticles) DrawHillRing();
            }
            PushScoreboard();

            int reached = Array.FindIndex(teamScores, s => s >= scoreLimit);
            if (reached >= 0) DeclareTeamWinner(reached);
            // Time-up winner is resolved in OnBeforeFinish (TeamScoreEB).
        }

        private void ScoreTick()
        {
            Array.Clear(zoneCounts, 0, zoneCounts.Length);

            // Bodies, so bots count too.
            foreach (var (uid, body) in runningEvent.LiveBodies())
            {
                int team = runningEvent.TeamIndexOf(uid);
                if (team < 0 || team >= zoneCounts.Length) continue;
                // From one block below the floor.
                if (CylinderZone.Contains(body.Pos, hillX, hillY, hillZ, hillRadius, 1.0, hillHeight))
                    zoneCounts[team]++;
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
                newOwner = OwnerContested;
            }
            else
            {
                newOwner = topTeam;
                int pts = pointsPerSecond;
                // Advantage bonus only when contested.
                if (second > 0)
                {
                    pts += advantageBonusPerPlayer * Math.Min(top - second, maxAdvantageBonus);
                }
                teamScores[topTeam] += pts;
            }

            AnnounceOwnerChange(newOwner);
            hillOwnerTeam = newOwner;
        }

        // 3s cooldown against flapping.
        private void AnnounceOwnerChange(int newOwner)
        {
            if (newOwner == announcedOwnerTeam) return;
            if (ownerNotifyCooldown > 0f) return;
            ownerNotifyCooldown = 3f;
            announcedOwnerTeam = newOwner;

            if (newOwner >= 0) Announce("canevents:koth-captured", newOwner + 1);
            else if (newOwner == OwnerContested) Announce("canevents:koth-contested");
            else Announce("canevents:koth-neutral");
        }

        private void DrawHillRing()
        {
            int segments = Math.Min(32, Math.Max(12, (int)(hillRadius * 4)));
            ZoneVisualizer.DrawRing(runningEvent.sapi, hillX, hillY, hillZ, hillRadius, segments,
                ZoneVisualizer.OwnerColor(hillOwnerTeam));
        }

        public override ScoreboardData BuildScoreboard()
        {
            var data = base.BuildScoreboard();
            data.ZoneOwnerTeam = hillOwnerTeam;
            data.TeamScoreLimit = scoreLimit;
            return data;
        }

        protected override string EndedLangKey => "canevents:koth-ended";
    }
}
