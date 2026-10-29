using System;
using System.Collections.Generic;
using canevents.src.Tournaments;

namespace canevents.src.Network
{
    public partial class ClientNetworkHandler
    {
        public readonly ServerState<TournamentListMsg> TournamentList = new ServerState<TournamentListMsg>();

        // Whichever bracket was opened last.
        public readonly ServerState<TournamentBracketMsg> TournamentBracket = new ServerState<TournamentBracketMsg>();

        public event Action<TournamentReadyRequestMsg> OnTournamentReadyRequested;
        public event Action<TournamentReadyClosedMsg> OnTournamentReadyClosed;

        private void OnTournamentReadyRequest(TournamentReadyRequestMsg msg) => OnTournamentReadyRequested?.Invoke(msg);

        private void HandleTournamentReadyClosed(TournamentReadyClosedMsg msg) => OnTournamentReadyClosed?.Invoke(msg);

        // Last list that included history; broadcasts overwrite TournamentList with active ones only.
        public TournamentListMsg TournamentHistory { get; private set; }

        // The reply has no history flag, so the next list while this is > 0 counts as the answer.
        private int pendingHistoryLists;

        public void RequestTournamentList(bool includeHistory = false)
        {
            if (includeHistory) pendingHistoryLists++;
            Send(new RequestTournamentListMsg { IncludeHistory = includeHistory });
        }

        private void OnTournamentList(TournamentListMsg msg)
        {
            // Finished entries only appear in history lists.
            bool hasHistory = HasFinished(msg);
            if (pendingHistoryLists > 0 || hasHistory)
            {
                if (pendingHistoryLists > 0) pendingHistoryLists--;
                TournamentHistory = msg;
            }
            TournamentList.Set(msg);
        }

        private static bool HasFinished(TournamentListMsg list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (IsFinished(list.StateAt(i))) return true;
            return false;
        }

        private static bool IsFinished(int state)
            => state == (int)TournamentState.Finished || state == (int)TournamentState.Cancelled;

        // Finished and cancelled, from the current list first, then the history list.
        public List<(TournamentListMsg list, int index)> TournamentHistoryEntries()
        {
            var result = new List<(TournamentListMsg, int)>();
            var seen = new HashSet<long>();
            foreach (var list in new[] { TournamentList.Value, TournamentHistory })
            {
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                    if (IsFinished(list.StateAt(i)) && seen.Add(list.Ids[i])) result.Add((list, i));
            }
            return result;
        }

        public void RequestTournamentBracket(long tournamentId)
            => Send(new RequestTournamentBracketMsg { TournamentId = tournamentId });

        public void SendTournamentSignup(long tournamentId, bool join, string teamName = null)
            => Send(new TournamentSignupMsg
            {
                TournamentId = tournamentId,
                Join = join,
                TeamName = teamName
            });

        public void SendTournamentLoadout(long tournamentId, string setName)
            => Send(new TournamentLoadoutMsg { TournamentId = tournamentId, SetName = setName });

        public void SendTournamentTeamName(long tournamentId, string teamName)
            => Send(new TournamentRenameMsg { TournamentId = tournamentId, TeamName = teamName });

        public void SendTournamentReady(long tournamentId, int matchId, bool accept)
            => Send(new TournamentReadyResponseMsg
            {
                TournamentId = tournamentId,
                MatchId = matchId,
                Accept = accept
            });

        public void SendTournamentAdminAction(TournamentAdminActionMsg msg)
            => Send(msg);
    }
}
