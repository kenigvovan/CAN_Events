using canevents.src.EventManagers;
using canevents.src.Network;
using canevents.src.Scoring;

namespace canevents.src
{
    // Server services passed to RunningEvent; available from OnBeforeStart on.
    public sealed class MatchServices
    {
        public ServerNetworkHandler NetworkHandler { get; init; }
        public LeaderboardData      Leaderboard    { get; init; }
        public LoadoutManager       LoadoutManager { get; init; }
        public LoadoutManager       OutfitManager  { get; init; }
        public SpectatorManager     Spectator      { get; init; }
        public Config               Config         { get; init; }
        public System.Action<Vintagestory.API.Server.IServerPlayer, RunningEvent> SeatOutOfPlay { get; init; }
    }
}
