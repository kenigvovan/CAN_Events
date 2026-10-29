using Vintagestory.API.Server;

namespace canevents.src
{
    // Admins get these automatically; grant to other roles with /role <role> grant caneventsspectate.
    public static class CanEventsPrivileges
    {
        // Spectator seat plus broadcaster tools (positions, health, teleport to fighter).
        public const string Spectate = "caneventsspectate";

        public static void RegisterAll(ICoreServerAPI sapi)
        {
            sapi.Permissions.RegisterPrivilege(Spectate,
                "Spectate live CAN events and use the broadcaster camera");
        }
    }
}
