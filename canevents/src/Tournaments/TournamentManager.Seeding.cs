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
        // Capped by the smallest MaxPartiesCount among the events.
        public int ResolveArity(Tournament t)
        {
            if (t.PartiesPerMatch >= 2) return CapByEvents(t, t.PartiesPerMatch);

            int seats = 0;
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                int max = ev?.AssemblingEB?.MaxPartiesCount ?? 2;
                if (max > seats) seats = max;
            }
            return CapByEvents(t, Math.Max(2, seats));
        }

        private int CapByEvents(Tournament t, int wanted)
        {
            foreach (var code in t.EventCodes)
            {
                var ev = manager.FindEventByCode(code);
                int max = ev?.AssemblingEB?.MaxPartiesCount ?? 2;
                if (max >= 2 && max < wanted) wanted = max;
            }
            return Math.Max(2, wanted);
        }

        // Random, or by average Elo across the tournament's events.
        private List<Entrant> SeedEntrants(Tournament t)
        {
            var list = new List<Entrant>(t.Entrants);
            if (t.SeedMode == 1 && manager.Leaderboard != null)
                list = list.OrderByDescending(RatingOf).ToList();
            else
                BracketBuilder.Shuffle(list, rng);

            for (int i = 0; i < list.Count; i++) list[i].Seed = i + 1;
            return list;

            int RatingOf(Entrant e)
            {
                int sum = 0, n = 0;
                foreach (var uid in e.Uids)
                    foreach (var code in t.EventCodes)
                    {
                        sum += manager.Leaderboard.GetRating(uid, code);
                        n++;
                    }
                return n == 0 ? Scoring.EloCalculator.DefaultRating : sum / n;
            }
        }

        private List<string> ResolveEntrantRoster(Tournament t, IServerPlayer player, out string error)
        {
            error = null;
            if (t.TeamSize <= 1) return new List<string> { player.PlayerUID };

            var party = manager.PartyRosterOf(player, out bool isLeader);
            if (party == null || party.Count != t.TeamSize)
            {
                error = Lang.Get("canevents:tournament-signup-party", t.TeamSize);
                return null;
            }
            if (!isLeader)
            {
                error = Lang.Get("canevents:queue-party-leader-only");
                return null;
            }
            foreach (var uid in party)
            {
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer))
                {
                    error = Lang.Get("canevents:tournament-signup-offline", NameOf(uid));
                    return null;
                }
                var other = OpenTournamentOf(uid);
                if (other != null)
                {
                    error = Lang.Get("canevents:tournament-signup-other", other.Name);
                    return null;
                }
            }
            return new List<string>(party);
        }

        private Entrant FindEntrantLoose(Tournament t, string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            var byId = t.FindEntrant(idOrName);
            if (byId != null) return byId;
            foreach (var e in t.Entrants)
            {
                if (string.Equals(e.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase)) return e;
                foreach (var uid in e.Uids)
                    if (string.Equals(NameOf(uid), idOrName, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        // Slot letter, slot number, entrant id or player name.
        private string ResolveSide(Tournament t, BracketMatch m, string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            var filled = m.FilledSlots;
            if (token.Length == 1 && char.IsLetter(token[0]))
            {
                int index = char.ToLowerInvariant(token[0]) - 'a';
                if (index >= 0 && index < filled.Count) return filled[index];
            }
            if (int.TryParse(token, out int slot) && slot >= 1 && slot <= filled.Count)
                return filled[slot - 1];

            var e = FindEntrantLoose(t, token);
            return e != null && m.Contains(e.Id) ? e.Id : null;
        }
    }
}
