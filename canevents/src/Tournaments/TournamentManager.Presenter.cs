using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    public partial class TournamentManager
    {
        private void Announce(string message) => EventBroadcaster.BroadcastToServer(sapi, message);

        private void Tell(Tournament t, BracketMatch m, string message)
        {
            foreach (var id in m.ActiveSides)
            {
                var e = t.FindEntrant(id);
                if (e?.Uids == null) continue;
                foreach (var uid in e.Uids)
                    (sapi.World.PlayerByUid(uid) as IServerPlayer)?
                        .SendMessage(0, message, EnumChatType.Notification);
            }
        }

        public List<string> Describe()
        {
            var lines = new List<string>();
            foreach (var t in store.ActiveSnapshot())
                lines.Add(string.Format("#{0} '{1}' [{2}] {3} entrant(s), events: {4}, pot: {5}",
                    t.Id, t.Name, t.State, t.Entrants.Count, string.Join("/", t.EventCodes),
                    DescribePot(t.Pot)));
            if (lines.Count == 0) lines.Add("no active tournaments");
            return lines;
        }

        public List<string> DescribeBracket(long id)
        {
            var t = store.FindActive(id) ?? store.Read(d => d.History.FirstOrDefault(x => x.Id == id));
            if (t == null) return new List<string> { "no tournament #" + id };

            var lines = new List<string>
            {
                string.Format("#{0} '{1}' [{2}] - {3} entrant(s), pot: {4}",
                    t.Id, t.Name, t.State, t.Entrants.Count, DescribePot(t.Pot))
            };

            if (t.Matches.Count == 0)
            {
                foreach (var e in t.Entrants) lines.Add("  - " + e.DisplayName);
                return lines;
            }

            int rounds = BracketBuilder.TotalRounds(t.Matches);
            foreach (var group in t.Matches.GroupBy(m => m.Round).OrderBy(g => g.Key))
            {
                lines.Add("-- " + BracketBuilder.RoundLabel(group.First(), rounds) + " --");
                foreach (var m in group.OrderBy(m => m.SlotInRound))
                {
                    string sides = m.Slots.Count == 0
                        ? "-"
                        : string.Join(" vs ", m.Slots.Select(id =>
                            string.IsNullOrEmpty(id) ? "TBD" : t.NameOfEntrant(id)));
                    string mark = m.State == BracketMatchState.Live ? " <LIVE>"
                        : m.State == BracketMatchState.AwaitingReady
                            ? " <ready " + Math.Max(0, m.ReadyDeadlineUnix - Now) + "s>"
                            : m.IsClosed && m.WinnerEntrantId != null
                                ? " -> " + t.NameOfEntrant(m.WinnerEntrantId) + " (" + m.ResultKind + ")"
                                : m.State == BracketMatchState.Void ? " -> void" : "";
                    string label = m.IsThirdPlace ? "[3rd] " : "";
                    lines.Add(string.Format("  #{0} {1}{2}{3}", m.Id, label, sides, mark));
                }
            }

            if (t.FinalPlacement.Count > 0)
                lines.Add("Final: " + string.Join(", ", t.FinalPlacement
                    .Select((eid, i) => (i + 1) + ". " + t.NameOfEntrant(eid))));

            return lines;
        }
    }
}
