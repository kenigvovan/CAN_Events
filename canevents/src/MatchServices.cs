using canevents.src.EventManagers;
using canevents.src.Network;
using canevents.src.Scoring;

namespace canevents.src
{
    /// <summary>
    /// Read-only bag of server-side services injected into RunningEvent at construction time.
    /// Available from OnBeforeStart onward; behaviors may use them in any lifecycle hook.
    /// </summary>
    public sealed class MatchServices
    {
        public ServerNetworkHandler NetworkHandler { get; init; }
        public LeaderboardData      Leaderboard    { get; init; }
        public LoadoutManager       LoadoutManager { get; init; }
        public LoadoutManager       OutfitManager  { get; init; }
        public SpectatorManager     Spectator      { get; init; }
        public Config               Config         { get; init; }
    }
}
