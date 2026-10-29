using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior
{
    // Holds the line-up in the place's waiting room during the start countdown: inventory is taken
    // on the way in, nobody takes damage, each player picks a side and then a class. The queue's teams
    // are thrown away and rebuilt from the picks in FinalizeTeams, before the start's playability check.
    public class WaitingRoomEB : CANEventBehavior, IPreStartLobby
    {
        private int lobbySeconds = 60;
        private int maxImbalance = 1;
        private int earlyStartSeconds = 5;

        private bool hasRoom;
        private bool open;
        private bool sidePads;
        private readonly DamageHookSet hooks = new DamageHookSet();

        // Players who confirmed a class (or had nothing to choose).
        private readonly HashSet<string> classChosen = new HashSet<string>();

        public WaitingRoomEB(string name, JsonObject attributes, RunningEvent runningEvent)
            : base(name, attributes, runningEvent) { }

        public override string Name => BehaviorNames.WaitingRoom;

        public int LobbySeconds => hasRoom ? lobbySeconds : 0;

        private JoinPads Pads => runningEvent.sapi.ModLoader.GetModSystem<canevents>()?.JoinPads;

        // The start countdown is set only after OnBeforeStart; until then the lobby length is the answer.
        private float SecondsLeft => runningEvent.CountdownSeconds > 0f ? runningEvent.CountdownSeconds : lobbySeconds;

        public override void Initialize()
        {
            lobbySeconds = Math.Max(10, Config.Int("lobbySeconds", 60));
            maxImbalance = Math.Max(0, Config.Int("maxImbalance", 1));
            earlyStartSeconds = Math.Max(1, Config.Int("earlyStartSeconds", 5));
            Config.WarnUnknown();
        }

        public override void OnBeforeStart()
        {
            if (!PlaceConfigService.TryGetWaitingRoom(runningEvent.Place, out var room))
            {
                Announce("canevents:waitroom-missing", runningEvent.Place?.Name ?? "?");
                return;
            }
            hasRoom = true;
            open = true;
            // Carpets in this place's room decide the side; the dialog would only get in the way.
            sidePads = Pads?.HasSidePads(runningEvent.Event.Code, runningEvent.Place.Id) == true;

            var save = runningEvent.GetBehavior<SavePlayerAttributes>();
            var tp = runningEvent.GetBehavior<TPThereAndBack>();

            foreach (var player in runningEvent.ActiveOnlineParticipants.ToList())
            {
                string uid = player.PlayerUID;
                save?.SnapshotNow(player);
                // Refused snapshot = sat out, keeps their own gear and stays where they are.
                if (!runningEvent.HoldsPlayer(uid) || !runningEvent.IsActive(uid)) continue;

                tp?.RememberReturnPos(player);
                EventTeleport.Send(runningEvent.sapi, player, room.X + 0.5, room.Y + 1, room.Z + 0.5, room.dimension,
                    "waiting room " + runningEvent.Event.Code);
            }

            // Only those actually in the room; someone sat out is back in the world and must stay hittable.
            hooks.HookAll(runningEvent, runningEvent.ParticipantsBefore.Where(runningEvent.IsActive),
                (uid, damage, source) => open && runningEvent.IsActive(uid) ? 0f : damage);

            if (sidePads)
            {
                foreach (var player in runningEvent.ActiveOnlineParticipants)
                    Pads?.ShowTo(player.PlayerUID, lobbySeconds + 5, runningEvent.Event.Code, runningEvent.Place.Id);
                Announce("canevents:waitroom-stand-on-side");
            }
            PushState();
        }

        public (bool ok, string message) TryPick(IServerPlayer player, int team)
        {
            string uid = player.PlayerUID;
            if (!open || !runningEvent.IsActive(uid)) return (false, Lang.Get("canevents:waitroom-closed"));
            int teams = runningEvent.ParticipantsByGroupsBefore.Count;
            if (team < 0 || team >= teams) return (false, Lang.Get("canevents:live-no-such-team", teams));
            if (runningEvent.TeamChoices.TryGetValue(uid, out int current) && current == team) return (true, null);

            if (maxImbalance > 0)
            {
                var counts = PickCounts(teams, without: uid);
                if (counts[team] - counts.Min() >= maxImbalance)
                    return (false, Lang.Get("canevents:waitroom-team-full", TeamPalette.NameOf(team)));
            }

            bool first = !runningEvent.TeamChoices.ContainsKey(uid);
            runningEvent.TeamChoices[uid] = team;

            // Side first, then class: one dialog at a time. The class counts as chosen only once confirmed.
            if (first)
            {
                bool asked = runningEvent.NetworkHandler?.RequestLoadoutPickFor(runningEvent, player,
                    Math.Max(1f, SecondsLeft), "canevents:gui-lobby-starts-in", OnClassConfirmed) ?? false;
                if (!asked) classChosen.Add(uid);
            }

            MaybeStartEarly();
            PushState();
            return (true, Lang.Get("canevents:waitroom-side-picked", TeamPalette.NameOf(team)));
        }

        private void OnClassConfirmed(IServerPlayer player)
        {
            if (!open) return;
            classChosen.Add(player.PlayerUID);
            MaybeStartEarly();
        }

        // Cut the wait only when everyone has both a side and a confirmed class.
        private void MaybeStartEarly()
        {
            if (!EveryonePicked() || runningEvent.CountdownSeconds <= earlyStartSeconds) return;
            runningEvent.CountdownSeconds = earlyStartSeconds;
            // HUD and the side dialog run their own clocks; tell them about the cut.
            runningEvent.NetworkHandler?.SendStateUpdate(runningEvent);
            PushState();
        }

        // Active players only, so the dialog counts match what TryPick enforces.
        private int[] PickCounts(int teams, string without = null)
        {
            var counts = new int[teams];
            foreach (var kv in runningEvent.TeamChoices)
                if (kv.Key != without && runningEvent.IsActive(kv.Key) && kv.Value >= 0 && kv.Value < teams) counts[kv.Value]++;
            return counts;
        }

        private bool EveryonePicked()
            => runningEvent.ParticipantsStillInEvent.All(uid => Bots.BotIdentity.IsBot(uid)
                || (runningEvent.TeamChoices.ContainsKey(uid) && classChosen.Contains(uid)));

        private void PushState(bool closed = false)
        {
            if (sidePads) return;
            foreach (var player in runningEvent.OnlineParticipants)
            {
                if (!runningEvent.IsActive(player.PlayerUID) && !closed) continue;
                runningEvent.NetworkHandler?.SendTeamPickState(runningEvent, player, SecondsLeft, maxImbalance, closed);
            }
        }

        // Teams from the picks; whoever didn't pick fills the smallest live team. Players already out
        // (dropped, sat out) still get a team for the records, after everyone who plays.
        public void FinalizeTeams()
        {
            if (!hasRoom || !open) return;
            open = false;
            hooks.UnhookAll();
            HideZones();
            PushState(closed: true);

            int teams = runningEvent.ParticipantsByGroupsBefore.Count;
            var groups = Enumerable.Range(0, teams).Select(_ => new HashSet<string>()).ToList();
            var unpicked = new List<string>();

            foreach (var uid in runningEvent.ParticipantsBefore)
            {
                bool picked = runningEvent.TeamChoices.TryGetValue(uid, out int t) && t >= 0 && t < teams;
                if (picked && runningEvent.IsActive(uid)) groups[t].Add(uid);
                else unpicked.Add(uid);
            }

            foreach (var uid in unpicked.OrderBy(u => runningEvent.IsActive(u) ? 0 : 1)
                         .ThenBy(_ => runningEvent.sapi.World.Rand.Next()))
            {
                var smallest = groups.OrderBy(g => g.Count(runningEvent.IsActive)).ThenBy(g => g.Count).First();
                smallest.Add(uid);
                runningEvent.TeamChoices[uid] = groups.IndexOf(smallest);
            }

            runningEvent.Roster.Regroup(groups);
        }

        public override void OnEnd()
        {
            hooks.UnhookAll();
            if (open)
            {
                HideZones();
                PushState(closed: true);
            }
            open = false;
        }

        private void HideZones()
        {
            if (!sidePads) return;
            foreach (var uid in runningEvent.ParticipantsBefore) Pads?.HideFrom(uid);
        }
    }
}
