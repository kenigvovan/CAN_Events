using Vintagestory.API.Server;

namespace canevents.src
{
    /// <summary>
    /// Privileges this mod defines. Registered with <c>adminAutoGrant</c>, so an admin has them
    /// without setup, and a server owner can hand one to a moderator role on its own
    /// (<c>/role &lt;role&gt; grant caneventsspectate</c>) without also handing over controlserver.
    /// </summary>
    public static class CanEventsPrivileges
    {
        /// <summary>Watch a live match from the spectator seat, with the broadcaster tools: live
        /// participant positions and health, and teleporting to a fighter to film them.</summary>
        public const string Spectate = "caneventsspectate";

        public static void RegisterAll(ICoreServerAPI sapi)
        {
            sapi.Permissions.RegisterPrivilege(Spectate,
                "Spectate live CAN events and use the broadcaster camera");
        }
    }
}
