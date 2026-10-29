using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    public abstract class QueueManager
    {
        // Set by CANEventManager after construction.
        private Func<string, bool> _isPlayerBusy;

        // (uid, eventCode) -> Elo.
        private Func<string, string, int> _getRating;

        internal void SetPlayerBusyCheck(Func<string, bool> check) => _isPlayerBusy = check;

        internal void SetRatingProvider(Func<string, string, int> getRating) => _getRating = getRating;

        protected bool IsPlayerBusy(string uid) => _isPlayerBusy?.Invoke(uid) == true;

        // Average Elo of the members.
        protected int GetShardRating(Registration reg, string eventCode)
        {
            if (_getRating == null || reg?.PlayersGuids == null || reg.PlayersGuids.Count == 0)
                return Scoring.EloCalculator.DefaultRating;
            long sum = 0;
            foreach (var uid in reg.PlayersGuids) sum += _getRating(uid, eventCode);
            return (int)(sum / reg.PlayersGuids.Count);
        }

        protected bool AnyPlayerBusy(Registration reg)
        {
            foreach (var uid in reg.PlayersGuids)
                if (IsPlayerBusy(uid)) return true;
            return false;
        }

        // (teams, size) shapes a solo pool can fill, most teams first.
        protected static IEnumerable<(int teams, int teamSize, bool largest)> SoloLobbyShapes(
            EventManagers.eventBehavior.abstracts.PartyAssemblingEB eb, int pool)
        {
            int maxTeamSize = Math.Max(1, eb.MaxPartySize);
            int minTeamSize = Math.Max(1, eb.MinPartySize);
            int minTeams = Math.Max(1, eb.MinPartiesCount);
            int maxTeams = Math.Max(minTeams, eb.MaxPartiesCount);

            for (int tc = maxTeams; tc >= minTeams; tc--)
            {
                int ts = Math.Min(maxTeamSize, pool / tc);
                if (ts >= minTeamSize) yield return (tc, ts, tc == maxTeams);
            }
        }

        // Used registrations are MarkedUsed; the caller removes them from the store.
        public abstract IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending);

        public abstract (bool ok, string message) CheckRegistrationIsSuitable(Registration registration);

        // Only needed by managers that keep partial assemblies across ticks.
        public virtual void OnRegistrationRemoved(Registration registration) { }
    }
}
