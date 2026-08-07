using System;

namespace canevents.src.Network
{
    // Client cache for tournament state: like every other panel feed, the last snapshot is kept and
    // an event tells the GUI to recompose - nothing polls.
    public partial class ClientNetworkHandler
    {
        /// <summary>Last tournament list the server sent (null until requested).</summary>
        public readonly ServerState<TournamentListMsg> TournamentList = new ServerState<TournamentListMsg>();

        /// <summary>Last bracket snapshot, for whichever tournament the client last opened.</summary>
        public readonly ServerState<TournamentBracketMsg> TournamentBracket = new ServerState<TournamentBracketMsg>();

        /// <summary>The ready check currently being asked of this player, or null.</summary>
        public TournamentReadyRequestMsg PendingTournamentReady { get; private set; }
        public event Action<TournamentReadyRequestMsg> OnTournamentReadyRequested;
        public event Action<TournamentReadyClosedMsg> OnTournamentReadyClosed;

        private void OnTournamentReadyRequest(TournamentReadyRequestMsg msg)
        {
            PendingTournamentReady = msg;
            OnTournamentReadyRequested?.Invoke(msg);
        }

        // Named apart from the event it raises: a handler and an event cannot share a name.
        private void HandleTournamentReadyClosed(TournamentReadyClosedMsg msg)
        {
            if (PendingTournamentReady != null
                && PendingTournamentReady.TournamentId == msg.TournamentId
                && PendingTournamentReady.MatchId == msg.MatchId)
            {
                PendingTournamentReady = null;
            }
            OnTournamentReadyClosed?.Invoke(msg);
        }

        /// <summary>Clears the pending prompt when the dialog is dismissed without answering.</summary>
        public void DismissTournamentReady() => PendingTournamentReady = null;

        public void RequestTournamentList(bool includeHistory = false)
            => clientChannel.SendPacket(new RequestTournamentListMsg { IncludeHistory = includeHistory });

        public void RequestTournamentBracket(long tournamentId)
            => clientChannel.SendPacket(new RequestTournamentBracketMsg { TournamentId = tournamentId });

        public void SendTournamentSignup(long tournamentId, bool join, string teamName = null)
            => clientChannel.SendPacket(new TournamentSignupMsg
            {
                TournamentId = tournamentId,
                Join = join,
                TeamName = teamName
            });

        /// <summary>Stores the class this player will use for the whole tournament.</summary>
        public void SendTournamentLoadout(long tournamentId, string setName)
            => clientChannel.SendPacket(new TournamentSignupMsg
            {
                TournamentId = tournamentId,
                Join = true,
                LoadoutOnly = true,
                LoadoutSet = setName
            });

        /// <summary>Renames an entry that already exists, without re-joining.</summary>
        public void SendTournamentTeamName(long tournamentId, string teamName)
            => clientChannel.SendPacket(new TournamentSignupMsg
            {
                TournamentId = tournamentId,
                Join = true,
                RenameOnly = true,
                TeamName = teamName
            });

        public void SendTournamentReady(long tournamentId, int matchId, bool accept)
            => clientChannel.SendPacket(new TournamentReadyResponseMsg
            {
                TournamentId = tournamentId,
                MatchId = matchId,
                Accept = accept
            });

        public void SendTournamentAdminAction(TournamentAdminActionMsg msg)
            => clientChannel.SendPacket(msg);
    }
}
