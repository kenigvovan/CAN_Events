using System;

namespace canevents.src.Network
{
    // Client half of the match shop.
    public partial class ClientNetworkHandler
    {
        // Null outside a match with a shop.
        public ShopStateMsg Shop { get; private set; }

        public event Action OnShopChanged;
        public event Action OnShopOpenRequested;
        // Points just earned, for the HUD pop-up.
        public event Action<int> OnShopPointsGained;

        private void RegisterShopHandlers()
        {
            SetHandler<ShopStateMsg>(OnShopState);
            SetHandler<ShopConfigMsg>(ShopConfig.Set);
            SetHandler<ShopPreviewMsg>(msg => { if (msg != null) OnShopPreview?.Invoke(msg); });
        }

        // Answer to RequestShopPreview: opens the read-only window.
        public event Action<ShopPreviewMsg> OnShopPreview;

        public void RequestShopPreview(string eventCode) => Send(new ShopPreviewRequestMsg { EventCode = eventCode });

        private void OnShopState(ShopStateMsg msg)
        {
            if (msg == null) return;
            // A late close for an older match must not take down a newer one.
            if (msg.Closed && (Shop == null || Shop.EventId != msg.EventId)) return;

            Shop = msg.Closed ? null : msg;
            OnShopChanged?.Invoke();
            if (Shop == null) return;
            if (msg.Gained > 0) OnShopPointsGained?.Invoke(msg.Gained);
            if (msg.Open) OnShopOpenRequested?.Invoke();
        }

        // Match ended without a close (server stopped the event mid-flight).
        private void DropShopFor(long eventId)
        {
            if (Shop == null || Shop.EventId != eventId) return;
            Shop = null;
            OnShopChanged?.Invoke();
        }

        public void RequestShop() => Send(new ShopRequestMsg());

        // Admin editor.
        public readonly ServerState<ShopConfigMsg> ShopConfig = new ServerState<ShopConfigMsg>();

        public void RequestShopConfig(string eventCode) => Send(new RequestShopConfigMsg { EventCode = eventCode });

        public void SendSaveShopConfig(ShopConfigMsg msg) => Send(msg);

        public void SendShopEnable(string eventCode, bool enable)
            => Send(new ShopEnableMsg { EventCode = eventCode, Enable = enable });

        // upgrade: offerId is a kit item's EntryId.
        public void SendShopAction(string offerId, bool refund, bool upgrade)
        {
            if (Shop == null) return;
            Send(new ShopActionMsg { EventId = Shop.EventId, OfferId = offerId, Refund = refund, Upgrade = upgrade });
        }
    }
}
