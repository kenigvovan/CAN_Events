using System.Linq;
using canevents.src.EventManagers.events;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    /// <summary>
    /// Joining and leaving event queues. Signing up is not a single call - it is add-to-queue, prompt
    /// every other party member for their loadout, then push the refreshed event list to everyone's
    /// browser. Front ends supply only the player and the code; the sequence lives here.
    /// </summary>
    public class QueueService
    {
        private readonly CANEventManager manager;

        public QueueService(CANEventManager manager)
        {
            this.manager = manager;
        }

        /// <summary>Signs the player (and their party, if registered as one) up. A null/empty code
        /// means "the first event", which is what a bare /canreg does.</summary>
        public (bool ok, string message) Join(IServerPlayer player, string eventCode, string loadoutSet = null)
        {
            if (player == null) return (false, Lang.Get("canevents:specify-event-code"));

            CANEvent target;
            if (string.IsNullOrEmpty(eventCode))
            {
                target = manager.Events.FirstOrDefault();
                if (target == null) return (false, Lang.Get("canevents:no-events"));
            }
            else
            {
                target = manager.FindEventByCode(eventCode);
                // The code came from a human typing it, so list what would have worked.
                if (target == null)
                    return (false, Lang.Get("canevents:event-not-found", eventCode) + " "
                        + string.Join(", ", manager.Events.Select(e => e.Code)));
            }

            var (ok, message) = manager.AddToQueue(target, player, loadoutSet);
            if (!ok) return (false, message);

            var net = manager.NetworkHandler;
            var reg = manager.FindRegistration(player.PlayerUID, target);
            if (reg != null && reg.PlayersGuids.Count > 1) net?.RequestQueueLoadoutPicks(reg);
            net?.BroadcastEventList();

            return (true, message);
        }

        /// <summary>Drops the player from one queue, or from every queue when the code is empty.</summary>
        public (bool ok, string message) Leave(IServerPlayer player, string eventCode = null)
        {
            if (player == null) return (false, Lang.Get("canevents:not-in-queue"));

            bool removed = false;
            string firstReason = null;

            foreach (var ev in manager.Events)
            {
                if (!string.IsNullOrEmpty(eventCode)
                    && !ev.Code.Equals(eventCode, System.StringComparison.OrdinalIgnoreCase)) continue;

                var (ok, reason) = manager.RemoveFromQueue(ev, player);
                if (ok) removed = true;
                else if (firstReason == null && reason != null) firstReason = reason;
            }

            if (removed)
            {
                manager.NetworkHandler?.BroadcastEventList();
                return (true, Lang.Get("canevents:left-queue"));
            }

            // A refusal ("your party leader registered you", say) explains more than the generic line.
            return (false, firstReason ?? Lang.Get("canevents:not-in-queue"));
        }
    }
}
