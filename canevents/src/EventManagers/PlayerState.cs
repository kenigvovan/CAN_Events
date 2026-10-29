using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public enum PlayerAvailability
    {
        Free,
        Offline,
        // Playing or watching a match, or (unless the caller says otherwise) called for a tournament match.
        Busy
    }

    // The one definition of "online and not busy" for duels, tournament calls and direct starts.
    // A bot uid is always free: nothing else can hold it.
    public sealed class PlayerState
    {
        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;

        public PlayerState(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
        }

        // ignoreTournamentCall: for the tournament driver, whose own call reserved this player; then only
        // a match or a spectator seat counts as busy.
        public PlayerAvailability Check(string uid, bool ignoreTournamentCall = false)
        {
            if (Bots.BotIdentity.IsBot(uid)) return PlayerAvailability.Free;
            if (!(sapi.World.PlayerByUid(uid) is IServerPlayer)) return PlayerAvailability.Offline;

            bool busy = ignoreTournamentCall ? manager.IsOccupiedByAMatch(uid) : manager.IsBusy(uid);
            return busy ? PlayerAvailability.Busy : PlayerAvailability.Free;
        }

        public bool IsFree(string uid, bool ignoreTournamentCall = false)
            => Check(uid, ignoreTournamentCall) == PlayerAvailability.Free;
    }
}
