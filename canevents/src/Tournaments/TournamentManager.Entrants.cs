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
        public (bool ok, string message) Signup(IServerPlayer player, long id, string teamName = null)
            => Transition(id, t =>
        {
            if (t.State != TournamentState.Signup) return (false, Lang.Get("canevents:tournament-signup-closed"));
            if (t.Entrants.Count >= t.MaxEntrants) return (false, Lang.Get("canevents:tournament-signup-full"));
            if (t.EntrantOfPlayer(player.PlayerUID) != null)
                return (false, Lang.Get("canevents:tournament-signup-already"));

            var other = OpenTournamentOf(player.PlayerUID, t.Id);
            if (other != null) return (false, Lang.Get("canevents:tournament-signup-other", other.Name));

            var uids = ResolveEntrantRoster(t, player, out string rosterError);
            if (uids == null) return (false, rosterError);

            var fee = ChargeEntryFee(t, uids, out string feeError);
            if (!fee) return (false, feeError);

            var entrant = new Entrant
            {
                Id = Guid.NewGuid().ToString(),
                Uids = uids,
                TeamName = CleanShortName(teamName),
                MembersText = string.Join(", ", uids.Select(NameOf))
            };
            entrant.DisplayName = LabelFor(entrant);
            if (!IsTeamNameFree(t, entrant.TeamName, entrant.Id))
            {
                entrant.TeamName = null;
                entrant.DisplayName = LabelFor(entrant);
            }
            t.Entrants.Add(entrant);
            return (true, Lang.Get("canevents:tournament-signup-ok", t.Name, t.Entrants.Count, t.MaxEntrants));
        });

        public (bool ok, string message) Withdraw(IServerPlayer player, long id) => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));
            if (t.State != TournamentState.Signup && t.State != TournamentState.Seeded)
                return (false, Lang.Get("canevents:tournament-withdraw-late"));
            if (!IsEntrantLeader(t, e.Id, player.PlayerUID, out string leaderName))
                return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

            RefundContributions(t, e.Uids);
            t.Entrants.Remove(e);
            return (true, Lang.Get("canevents:tournament-withdraw-ok", t.Name));
        });

        public (bool ok, string message) SetTeamName(IServerPlayer player, long id, string name)
            => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));
            if (t.State != TournamentState.Signup && t.State != TournamentState.Seeded)
                return (false, Lang.Get("canevents:tournament-name-late"));
            if (!IsEntrantLeader(t, e.Id, player.PlayerUID, out string leaderName))
                return (false, Lang.Get("canevents:tournament-leader-only", leaderName));

            string clean = CleanShortName(name);
            if (!IsTeamNameFree(t, clean, e.Id))
                return (false, Lang.Get("canevents:tournament-name-taken"));

            e.TeamName = clean;
            e.DisplayName = LabelFor(e);
            return (true, Lang.Get("canevents:tournament-name-ok", e.DisplayName));
        });

        public (bool ok, string message) SetLoadout(IServerPlayer player, long id, string setName)
            => Transition(id, t =>
        {
            var e = t.EntrantOfPlayer(player.PlayerUID);
            if (e == null) return (false, Lang.Get("canevents:tournament-not-entered"));

            if (t.LockLoadout && t.State == TournamentState.Running
                && e.LoadoutChoices.ContainsKey(player.PlayerUID))
                return (false, Lang.Get("canevents:tournament-class-locked"));

            string clean = CleanShortName(setName);
            if (string.IsNullOrEmpty(clean))
            {
                e.LoadoutChoices.Remove(player.PlayerUID);
                return (true, Lang.Get("canevents:tournament-class-cleared"));
            }

            if (!t.AllowsLoadout(clean))
                return (false, Lang.Get("canevents:tournament-class-banned",
                    string.Join(", ", t.AllowedLoadouts)));

            // Not checked against the event, other modes in the rotation may have this set.
            e.LoadoutChoices[player.PlayerUID] = clean;
            return (true, Lang.Get("canevents:tournament-class-ok", clean));
        });

        private static string CleanShortName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var cleaned = new string(name.Trim().Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray());
            if (cleaned.Length > 24) cleaned = cleaned.Substring(0, 24);
            return cleaned.Length == 0 ? null : cleaned;
        }

        private static bool IsTeamNameFree(Tournament t, string name, string ownEntrantId)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var e in t.Entrants)
                if (e.Id != ownEntrantId
                    && string.Equals(e.TeamName, name, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private string LabelFor(Entrant e)
        {
            if (!string.IsNullOrEmpty(e.TeamName)) return e.TeamName;
            if (e.Uids == null || e.Uids.Count == 0) return "?";
            if (e.Uids.Count == 1) return NameOf(e.Uids[0]);
            return NameOf(e.Uids[0]) + " +" + (e.Uids.Count - 1);
        }
    }
}
