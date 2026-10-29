using System;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Team deathmatch that never ends: players join through pads or /canlive, respawn at their team's
    // spawn and leave whenever they like. Spawn N belongs to team N. The class is picked in the lobby
    // on the way in; changing it means walking out and back in.
    public class OpenDeathmatchEB : TeamScoreEB, IOpenMatchMode, ISpawnOrderMatters
    {
        public int Teams { get; private set; } = 2;
        public int MaxImbalance { get; private set; } = 2;

        public OpenDeathmatchEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.OpenDeathmatch;

        protected override void ReadModeConfig()
        {
            Teams = Math.Max(2, Config.Int("teams", 2));
            MaxImbalance = Math.Max(0, Config.Int("maxImbalance", 2));
        }

        // The open-match flags come from CANEventManager.StartOpenMatch, before the match is listed.
        public override void OnBeforeStart() => BeginMatch();

        protected override void OnScoredDeath(string victimUid, Entity victimBody, KillInfo kill)
            => CreditKillerTeam(kill);

        // Dropped kit is removed on the spot (CANEventManager.PolicyOf); this catches whatever slipped past.
        public override bool WantsTick => true;
        private float sinceSweep;

        public override void OnTick(float dt)
        {
            sinceSweep += dt;
            if (sinceSweep < 60f) return;
            sinceSweep = 0f;
            IssuedGearSweep.Sweep(runningEvent.sapi, runningEvent.Place, log);
        }

        // Admin stop only; nobody wins an open match.
        public override void OnBeforeFinish() => ResolveMatch(null, "open match closed", requestFinish: false);

        protected override string EndedLangKey => "canevents:open-match-ended";
    }
}
