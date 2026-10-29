using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public static class VoidFall
    {
        public static bool HasFallen(Entity body, float fallY)
            => body?.Pos != null && body.Pos.Y < fallY;

        public static void AnnounceInFeed(RunningEvent match, string victimName, string verb)
            => EventBroadcaster.KillFeed(match, Lang.Get("canevents:killer-the-void"), victimName, verb);
    }
}
