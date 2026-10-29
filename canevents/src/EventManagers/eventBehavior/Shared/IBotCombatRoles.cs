namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // For modes where another team is not automatically a target. Used by BotMatchLink.IsOpponent.
    public interface IBotCombatRoles
    {
        // Bots only; players are limited by damage hooks, which don't fire for bot bodies.
        bool MayAttack(string attackerUid, string victimUid);

        bool ShouldFlee(string fleeingUid, string fromUid) => MayAttack(fromUid, fleeingUid);
    }
}
