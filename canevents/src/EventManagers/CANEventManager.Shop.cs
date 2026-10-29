using System;
using System.Linq;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace canevents.src.EventManagers
{
    // Match shop from the admin panel. Switching off keeps the behavior and its items ("enabled": false),
    // so turning it back on loses nothing. Like the settings editor, applied from the next match.
    public partial class CANEventManager
    {
        // The shop hooks into the kill-scoring modes' kit and kill handling.
        public static bool CanHostShop(CANEvent ev) => ev?.Behaviors.Any(b => b is KillScoringEB) == true;

        public string[] ShopEventCodes()
            => Events.Where(CanHostShop).Select(e => e.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();

        public (bool ok, string message) SetMatchShopEnabled(string code, bool enable)
        {
            var ev = FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));
            if (!CanHostShop(ev)) return (false, Lang.Get("canevents:shopcfg-no-mode", ev.Code));

            string savedKey = enable ? "canevents:shopcfg-enabled" : "canevents:shopcfg-disabled";
            if (ev.Behaviors.OfType<MatchShopEB>().Any())
                return EditEventBehaviorAttributes(ev.Code, BehaviorNames.MatchShop, attr => attr["enabled"] = enable, savedKey);
            if (!enable) return (true, Lang.Get(savedKey, ev.Code));

            var attrs = new JObject { ["items"] = new JArray() };
            var shop = new MatchShopEB(BehaviorNames.MatchShop, new JsonObject(attrs), null);
            shop.Initialize();
            ev.Behaviors.Add(shop);

            if (!eventConfigStore.Edit(ev.Code, obj =>
                {
                    if (!((obj["behaviors"] ?? obj["Behaviors"]) is JArray behs)) return "no behaviors array";
                    behs.Add(new JObject { ["name"] = BehaviorNames.MatchShop, ["attributes"] = attrs.DeepClone() });
                    return null;
                }, out string err))
                return (true, Lang.Get("canevents:rewardcfg-save-failed", err));
            return (true, Lang.Get(savedKey, ev.Code));
        }
    }
}
