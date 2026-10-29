using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Match shop: the state out, purchases back (rules live in MatchShopEB), and the admin editor.
    public partial class ServerNetworkHandler
    {
        private const int MaxShopOffers = 60;
        private const int MaxShopCost = 100000;
        private const int MaxShopBuys = 999;
        private const int MaxUpgradeSteps = 10;
        private const int MaxUpgradeExtras = 8;

        private void RegisterShopHandlers()
        {
            Handle<ShopRequestMsg>(ProtocolChannel.Main, false, OnShopRequest, minIntervalMs: 250);
            Handle<ShopActionMsg>(ProtocolChannel.Main, false, OnShopAction, minIntervalMs: 150);
            Handle<ShopPreviewRequestMsg>(ProtocolChannel.Main, false, OnShopPreviewRequest, minIntervalMs: 500);

            Handle<RequestShopConfigMsg>(ProtocolChannel.Admin, true, (player, msg) => SendShopConfig(player, msg?.EventCode));
            Handle<ShopConfigMsg>(ProtocolChannel.Admin, true, OnSaveShopConfig);
            Handle<ShopEnableMsg>(ProtocolChannel.Admin, true, OnShopEnable);
        }

        private void OnShopEnable(IServerPlayer player, ShopEnableMsg msg)
        {
            var (ok, message) = eventManager.SetMatchShopEnabled(msg?.EventCode, msg?.Enable == true);
            Reply(player, ok, message);
            SendShopConfig(player, msg?.EventCode);
        }

        // The whole list replaces the stored one; every other key of the behavior is kept.
        private void OnSaveShopConfig(IServerPlayer player, ShopConfigMsg msg)
        {
            var ev = eventManager.FindEventByCode(msg?.EventCode);
            if (ev == null)
            {
                ReplyFail(player, Lang.Get("canevents:event-not-found", msg?.EventCode ?? ""));
                return;
            }
            if (!ev.Behaviors.OfType<MatchShopEB>().Any())
            {
                ReplyFail(player, Lang.Get("canevents:shopcfg-not-on", ev.Code));
                return;
            }

            var offers = msg.Offers ?? new ShopOfferConfigMsg[0];
            if (offers.Length > MaxShopOffers)
            {
                ReplyFail(player, Lang.Get("canevents:shopcfg-too-many", MaxShopOffers));
                return;
            }

            var items = new JArray();
            var ids = new HashSet<string>();
            foreach (var o in offers)
            {
                string code = ClipCode(o.Code);
                string id = ClipCode(o.Id) ?? code;
                if (code == null || !ids.Add(id))
                {
                    ReplyFail(player, Lang.Get("canevents:shopcfg-bad-item", id ?? "?"));
                    return;
                }

                var item = new JObject
                {
                    ["id"] = id,
                    ["code"] = code,
                    ["quantity"] = Math.Clamp(o.Quantity, 1, MaxItemQuantity),
                    ["cost"] = Math.Clamp(o.Cost, 0, MaxShopCost),
                    ["maxBuys"] = Math.Clamp(o.MaxBuys, 1, MaxShopBuys)
                };
                var kits = ClipCodes(o.Kits);
                if (kits.Length > 0) item["kits"] = new JArray(kits);
                if (o.DropOnDeath) item["dropOnDeath"] = true;
                items.Add(item);
            }

            var (ok, message) = eventManager.EditEventBehaviorAttributes(ev.Code, BehaviorNames.MatchShop, attr =>
            {
                attr["items"] = items;
                attr["startPoints"] = Math.Max(0, msg.StartPoints);
                attr["pointsPerKill"] = msg.PointsPerKill;
                attr["pointsPerDeath"] = msg.PointsPerDeath;
                attr["pointsPerRoundWin"] = msg.PointsPerRoundWin;
                attr["buyAnywhere"] = msg.BuyAnywhere;
                attr["buyRadius"] = Math.Max(1, msg.BuyRadius);
                attr["showPreview"] = msg.ShowPreview;
            }, "canevents:shopcfg-saved");
            Reply(player, ok, message);
            if (ok) SendShopConfig(player, ev.Code);
        }

        private static string ClipCode(string s)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length > MaxLoadoutItemCodeLen ? s.Substring(0, MaxLoadoutItemCodeLen) : s;
        }

        private static string[] ClipCodes(string[] list)
            => (list ?? new string[0]).Select(ClipCode).Where(s => s != null).Distinct().Take(MaxLoadoutItems).ToArray();

        private void SendShopConfig(IServerPlayer player, string eventCode)
        {
            var codes = eventManager.ShopEventCodes();
            var ev = eventManager.FindEventByCode(eventCode);
            if (!CANEventManager.CanHostShop(ev)) ev = eventManager.FindEventByCode(codes.FirstOrDefault());

            var msg = new ShopConfigMsg
            {
                EventCodes = codes,
                EventCode = ev?.Code,
                Offers = new ShopOfferConfigMsg[0],
                PointsPerKill = 1,
                BuyRadius = 6,
                ShowPreview = true
            };

            if (ev != null)
            {
                var kits = LoadoutService.KitsOf(ev.Code, ev.Behaviors, eventManager.LoadoutManager);
                msg.KitNames = kits.Select(k => k.Name).ToArray();
                msg.KitChains = kits.Select(k => k.Items.Count(MatchShopEB.HasChain)).ToArray();
                msg.KitsManaged = eventManager.LoadoutManager?.GetSets(ev.Code).Count > 0;

                var shop = ev.Behaviors.OfType<MatchShopEB>().FirstOrDefault();
                if (shop != null)
                {
                    msg.Enabled = shop.Enabled;
                    msg.StartPoints = shop.StartPoints;
                    msg.PointsPerKill = shop.PointsPerKill;
                    msg.PointsPerDeath = shop.PointsPerDeath;
                    msg.PointsPerRoundWin = shop.PointsPerRoundWin;
                    msg.BuyAnywhere = shop.BuyAnywhere;
                    msg.BuyRadius = (int)shop.BuyRadius;
                    msg.ShowPreview = shop.ShowPreview;
                    msg.Offers = ConfigOffers(shop);
                }
            }

            adminChannel.SendPacket(msg, player);
        }

        private static ShopOfferConfigMsg[] ConfigOffers(MatchShopEB shop)
            => shop.Offers.Select(o => new ShopOfferConfigMsg
            {
                Id = o.Id,
                Code = o.Code,
                Quantity = o.Quantity,
                Cost = o.Cost,
                MaxBuys = o.MaxBuys,
                DropOnDeath = o.DropOnDeath,
                Kits = o.Kits
            }).ToArray();

        private static UpgradeStepMsg[] StepsOf(LoadoutEntry entry) => entry.Upgrades.Select(s => StepMsg(s)).ToArray();

        private static UpgradeStepMsg StepMsg(UpgradeStep step, int item = 0) => new UpgradeStepMsg
        {
            Item = item,
            Code = step.Code,
            Quantity = step.Quantity,
            Cost = step.Cost,
            ExtraCodes = step.Extras?.Select(e => e.Code).ToArray(),
            ExtraQuantities = step.Extras?.Select(e => e.Quantity).ToArray()
        };

        // Read from the event template, so it works with no match running.
        private void OnShopPreviewRequest(IServerPlayer player, ShopPreviewRequestMsg msg)
        {
            var ev = eventManager.FindEventByCode(msg?.EventCode);
            var shop = ev?.Behaviors.OfType<MatchShopEB>().FirstOrDefault();
            if (shop == null || !shop.Enabled)
            {
                player.SendMessage(0, Lang.Get("canevents:shop-none"), EnumChatType.Notification);
                return;
            }
            if (!shop.ShowPreview)
            {
                player.SendMessage(0, Lang.Get("canevents:shop-preview-hidden"), EnumChatType.Notification);
                return;
            }

            // Hidden-role modes never name their sets to players.
            var provider = ev.Behaviors.OfType<ILoadoutProvider>().FirstOrDefault();
            var kits = provider?.OffersPlayerChoice == false
                ? new List<LoadoutSet>()
                : LoadoutService.KitsOf(ev.Code, ev.Behaviors, eventManager.LoadoutManager);
            serverChannel.SendPacket(new ShopPreviewMsg
            {
                EventCode = ev.Code,
                Offers = ConfigOffers(shop),
                StartPoints = shop.StartPoints,
                PointsPerKill = shop.PointsPerKill,
                PointsPerDeath = shop.PointsPerDeath,
                PointsPerRoundWin = shop.PointsPerRoundWin,
                BuyAnywhere = shop.BuyAnywhere,
                BuyRadius = (int)shop.BuyRadius,
                KitNames = kits.Select(k => k.Name).ToArray(),
                Chains = kits.SelectMany(k => k.Items.Where(MatchShopEB.HasChain).Select(e => new KitChainMsg
                {
                    Kit = k.Name,
                    Code = e.Code,
                    Quantity = e.Quantity,
                    Steps = StepsOf(e)
                })).ToArray()
            }, player);
        }

        private void OnShopRequest(IServerPlayer player, ShopRequestMsg msg)
        {
            var re = eventManager.FindLiveEventOf(player.PlayerUID);
            var shop = re?.GetBehavior<MatchShopEB>();
            if (shop == null || !shop.MayShop(player.PlayerUID))
            {
                player.SendMessage(0, Lang.Get("canevents:shop-none"), EnumChatType.Notification);
                return;
            }
            SendShopState(re, shop, player, 0, open: true);
        }

        private void OnShopAction(IServerPlayer player, ShopActionMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.OfferId)) return;
            var re = eventManager.FindRunningEvent(msg.EventId);
            var shop = re?.GetBehavior<MatchShopEB>();
            if (shop == null) return;

            string error = !msg.Upgrade ? shop.TryBuy(player, msg.OfferId)
                : msg.Refund ? shop.TryDowngrade(player, msg.OfferId)
                : shop.TryUpgrade(player, msg.OfferId);
            if (error != null) player.SendMessage(0, error, EnumChatType.Notification);
        }

        public void SendShopState(RunningEvent re, MatchShopEB shop, IServerPlayer player, int gained, bool open = false)
        {
            if (re == null || shop == null || player == null) return;
            string uid = player.PlayerUID;
            string kit = shop.KitOf(uid)?.Name;

            serverChannel.SendPacket(new ShopStateMsg
            {
                EventId = re.Id,
                EventCode = re.Event?.Code ?? "",
                Points = shop.PointsOf(uid),
                Offers = shop.Offers.Where(o => MatchShopEB.Fits(o, kit)).Select(o => new ShopOfferMsg
                {
                    Id = o.Id,
                    Code = o.Code,
                    Quantity = o.Quantity,
                    Cost = o.Cost,
                    MaxBuys = o.MaxBuys,
                    Bought = shop.BoughtOf(uid, o)
                }).ToArray(),
                Upgrades = shop.UpgradesOf(uid).Select(u => new ShopUpgradeMsg
                {
                    EntryId = u.entry.Id,
                    Code = u.entry.Code,
                    Quantity = u.entry.Quantity,
                    Level = u.level,
                    Steps = StepsOf(u.entry),
                    Refundable = u.refundable
                }).ToArray(),
                Kit = kit,
                Preview = shop.ShowPreview,
                BuyAnywhere = shop.BuyAnywhere,
                BuyRadius = (int)shop.BuyRadius,
                Gained = gained,
                Open = open
            }, player);
        }

        public void SendShopClosed(RunningEvent re, IServerPlayer player)
        {
            if (re == null || player == null) return;
            serverChannel.SendPacket(new ShopStateMsg { EventId = re.Id, Closed = true }, player);
        }
    }
}
