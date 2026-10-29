namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Holds the line-up somewhere before the start. The start countdown runs at least LobbySeconds,
    // and the lobby asks for classes itself, so the usual start-of-match pick popup is skipped.
    public interface IPreStartLobby
    {
        // 0 = no lobby this match (e.g. the place has no waiting room).
        int LobbySeconds { get; }

        // Countdown over: settle the teams. Runs before the start's playability check, so that check
        // sees the teams the match will really be played with.
        void FinalizeTeams();
    }
}
