using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // A pennant in the team's colour over every fighter still in play, so friend and foe tell apart at a
    // glance (the CTF flag, small, over the head). Rechecked once a second and sent only on change:
    // deaths, round resets and open-match entries move people in and out.
    public class TeamMarkersEB : CANEventBehavior
    {
        public TeamMarkersEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.TeamMarkers;
        public override bool WantsTick => true;

        private bool enemiesVisible = true;
        // "back" (default) or "head"; CTF ships with head, its carriers already wear a flag on the back.
        private bool onHead;

        // Last list sent, as "uid=team" so a change of team counts too.
        private HashSet<string> sent = new HashSet<string>();
        private readonly HashSet<string> sentTo = new HashSet<string>();
        private bool started;

        public override void Initialize()
        {
            enemiesVisible = Config.Bool("teamMarkersForEnemies", true);
            onHead = string.Equals(Config.String("teamMarkerPosition", "back"), "head", System.StringComparison.OrdinalIgnoreCase);
            Config.WarnUnknown();
        }

        public override void OnStart()
        {
            started = true;
            Push(force: true);
        }

        public override void OnTick(float dt)
        {
            if (started) Push(force: false);
        }

        // Someone who left the audience would keep the last pennants on screen.
        public override void OnParticipantWithdrawn(IServerPlayer player) => ClearFor(player);

        public override void OnEnd()
        {
            started = false;
            foreach (var player in runningEvent.HudAudience) ClearFor(player);
            foreach (var uid in sentTo)
                if (runningEvent.sapi.World.PlayerByUid(uid) is IServerPlayer p) ClearFor(p);
            sentTo.Clear();
            sent.Clear();
        }

        private void Push(bool force)
        {
            var now = new List<(string uid, int team)>();
            var groups = runningEvent.ParticipantsByGroupsBefore;
            for (int team = 0; team < groups.Count; team++)
                foreach (var uid in groups[team])
                    if (runningEvent.IsActive(uid)) now.Add((uid, team));

            var key = new HashSet<string>(now.Select(x => x.uid + "=" + x.team));
            var audience = runningEvent.HudAudience.ToList();
            bool newViewer = audience.Any(p => !sentTo.Contains(p.PlayerUID));
            if (!force && !newViewer && key.SetEquals(sent)) return;
            sent = key;

            var allUids = now.Select(x => x.uid).ToArray();
            var allTeams = now.Select(x => x.team).ToArray();
            foreach (var player in audience)
            {
                sentTo.Add(player.PlayerUID);
                int own = runningEvent.IsParticipant(player.PlayerUID) ? runningEvent.TeamIndexOf(player.PlayerUID) : -1;
                // Observers always see every team.
                if (enemiesVisible || own < 0)
                {
                    runningEvent.NetworkHandler?.SendTeamMarkersTo(runningEvent, player, allUids, allTeams, onHead);
                    continue;
                }
                var mine = now.Where(x => x.team == own).ToList();
                runningEvent.NetworkHandler?.SendTeamMarkersTo(runningEvent, player,
                    mine.Select(x => x.uid).ToArray(), mine.Select(x => x.team).ToArray(), onHead);
            }
        }

        private void ClearFor(IServerPlayer player)
        {
            if (player == null) return;
            runningEvent.NetworkHandler?.SendTeamMarkersTo(runningEvent, player, new string[0], new int[0]);
        }
    }
}
