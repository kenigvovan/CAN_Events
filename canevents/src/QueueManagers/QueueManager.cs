using System;
using System.Collections.Generic;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;

namespace canevents.src.QueueManagers
{
    public abstract class QueueManager
    {
        // Injected by CANEventManager once services are ready; the delegate reads SpectatorManager
        // and CurrentlyRunningEvents at call time, so post-constructor injection is safe.
        private Func<string, bool> _isPlayerBusy;

        // (uid, eventCode) -> Elo rating. Only the ranked matchmakers read it.
        private Func<string, string, int> _getRating;

        internal void SetPlayerBusyCheck(Func<string, bool> check) => _isPlayerBusy = check;

        internal void SetRatingProvider(Func<string, string, int> getRating) => _getRating = getRating;

        protected bool IsPlayerBusy(string uid) => _isPlayerBusy?.Invoke(uid) == true;

        /// <summary>Average Elo of a shard's members under <paramref name="eventCode"/> (its side rating).</summary>
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

        /// <summary>
        /// Every complete TeamAssembly formable right now (possibly several). Used registrations are
        /// already <see cref="Registration.MarkedUsed"/>; the caller removes them from the store and
        /// calls <see cref="OnRegistrationRemoved"/> for each.
        /// </summary>
        public abstract IReadOnlyList<TeamAssembly> TryAssemble(CANEvent canEvent, IReadOnlyList<Registration> pending);

        public abstract (bool ok, string message) CheckRegistrationIsSuitable(Registration registration);

        /// <summary>
        /// Called after a registration leaves the store for any reason; implementations must evict it
        /// from incomplete partial assemblies. No-op unless the manager keeps cross-tick state.
        /// </summary>
        public virtual void OnRegistrationRemoved(Registration registration) { }
    }
}
