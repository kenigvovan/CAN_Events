namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Implemented by a mode where being on another team is not enough to make someone a target.
    /// Asked by the bot targeting rule (BotMatchLink.IsOpponent), which otherwise knows only about
    /// teams - and in a free-for-all everybody is their own team, so a "harmless" bot would still
    /// charge at the first thing it saw.
    ///
    /// Answers about uids rather than roles, so the roles themselves stay private to the mode.
    /// </summary>
    public interface IBotCombatRoles
    {
        /// <summary>May this participant attack that one at all? Asked for bots only; a player is
        /// held back by damage hooks instead, which never fire for a bot body.</summary>
        bool MayAttack(string attackerUid, string victimUid);

        /// <summary>Should this participant run from that one? The other side of the same coin: a
        /// townsfolk bot that cannot fight back has nothing to do but get away.</summary>
        bool ShouldFlee(string fleeingUid, string fromUid) => MayAttack(fromUid, fleeingUid);
    }
}
