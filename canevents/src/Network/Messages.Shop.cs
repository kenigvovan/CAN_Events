using ProtoBuf;

namespace canevents.src.Network
{
    // Match shop (MatchShopEB, protocol v10). The server resends the whole state on every change.
    // Upgrade chains live on the kit items (loadout editor); the shop itself sells one-time items.

    // Nested in ShopStateMsg only, not registered.
    [ProtoContract]
    public class ShopOfferMsg
    {
        [ProtoMember(1)] public string Id;
        [ProtoMember(2)] public string Code;
        [ProtoMember(3)] public int Quantity;
        [ProtoMember(4)] public int Cost;
        // Per life.
        [ProtoMember(5)] public int MaxBuys;
        [ProtoMember(6)] public int Bought;
    }

    // One item of the player's kit that has a chain. Level 0 = as the kit gives it.
    [ProtoContract]
    public class ShopUpgradeMsg
    {
        [ProtoMember(1)] public string EntryId;
        [ProtoMember(2)] public string Code;
        [ProtoMember(3)] public int Quantity;
        [ProtoMember(4)] public int Level;
        [ProtoMember(5)] public UpgradeStepMsg[] Steps;
        // The last level was bought since the last spawn and can still be undone.
        [ProtoMember(6)] public bool Refundable;
    }

    // Server -> participant.
    [ProtoContract]
    public class ShopStateMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public int Points;
        [ProtoMember(4)] public ShopOfferMsg[] Offers;
        [ProtoMember(5)] public bool BuyAnywhere;
        [ProtoMember(6)] public int BuyRadius;

        // Points just earned, for the HUD pop-up. 0 = a plain refresh.
        [ProtoMember(7)] public int Gained;

        // Asked for with the hotkey: open the window.
        [ProtoMember(8)] public bool Open;

        // Match over: take the window and the HUD line down.
        [ProtoMember(9)] public bool Closed;

        [ProtoMember(10)] public ShopUpgradeMsg[] Upgrades;
        [ProtoMember(11)] public string Kit;
        // The preview of every class is allowed.
        [ProtoMember(12)] public bool Preview;
    }

    // Player -> server, from the hotkey.
    [ProtoContract]
    public class ShopRequestMsg { }

    // Player -> server. Upgrade: OfferId is a kit item's EntryId, Refund undoes its last level.
    [ProtoContract]
    public class ShopActionMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string OfferId;
        [ProtoMember(3)] public bool Refund;
        [ProtoMember(4)] public bool Upgrade;
    }

    // Player -> server: the shop of an event, to look at before joining.
    [ProtoContract]
    public class ShopPreviewRequestMsg
    {
        [ProtoMember(1)] public string EventCode;
    }

    // A kit item's chain, for the preview.
    [ProtoContract]
    public class KitChainMsg
    {
        [ProtoMember(1)] public string Kit;
        [ProtoMember(2)] public string Code;
        [ProtoMember(3)] public int Quantity;
        [ProtoMember(4)] public UpgradeStepMsg[] Steps;
    }

    // Server -> player. Read-only: every kit's chains and the items, plus how points are earned.
    [ProtoContract]
    public class ShopPreviewMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public ShopOfferConfigMsg[] Offers;
        [ProtoMember(3)] public int StartPoints;
        [ProtoMember(4)] public int PointsPerKill;
        [ProtoMember(5)] public int PointsPerDeath;
        [ProtoMember(6)] public int PointsPerRoundWin;
        [ProtoMember(7)] public bool BuyAnywhere;
        [ProtoMember(8)] public int BuyRadius;
        [ProtoMember(9)] public string[] KitNames;
        [ProtoMember(10)] public KitChainMsg[] Chains;
    }

    // Admin editor (admin channel). One item of the "items" list, as stored in the event JSON.
    // Also nested in ShopPreviewMsg.
    [ProtoContract]
    public class ShopOfferConfigMsg
    {
        [ProtoMember(1)] public string Id;
        [ProtoMember(2)] public string Code;
        // No initializers here or below: protobuf-net skips zeros, so a 0 would arrive as the initializer.
        [ProtoMember(3)] public int Quantity;
        [ProtoMember(4)] public int Cost;
        [ProtoMember(5)] public int MaxBuys;
        [ProtoMember(6)] public bool DropOnDeath;
        // Loadout set names it is sold to; empty = every kit.
        [ProtoMember(7)] public string[] Kits;
    }

    [ProtoContract]
    public class RequestShopConfigMsg
    {
        // Empty: the first event that can have a shop.
        [ProtoMember(1)] public string EventCode;
    }

    // Server -> admin: the event's shop. Admin -> server: saved as is (the read-only fields are ignored).
    [ProtoContract]
    public class ShopConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public ShopOfferConfigMsg[] Offers;
        [ProtoMember(3)] public int StartPoints;
        [ProtoMember(4)] public int PointsPerKill;
        [ProtoMember(5)] public int PointsPerDeath;
        [ProtoMember(6)] public int PointsPerRoundWin;
        [ProtoMember(7)] public bool BuyAnywhere;
        [ProtoMember(8)] public int BuyRadius;

        // Read-only: events whose mode can run a shop, whether this one has it on, its kits with how many
        // of their items have an upgrade chain, and whether the kits are in the loadout editor at all
        // (inline JSON kits can't carry chains until imported).
        [ProtoMember(9)] public string[] EventCodes;
        [ProtoMember(10)] public bool Enabled;
        [ProtoMember(11)] public string[] KitNames;
        [ProtoMember(12)] public int[] KitChains;
        [ProtoMember(13)] public bool KitsManaged;

        // Players may look at the shop before the match (and at other classes' upgrades during it).
        [ProtoMember(14)] public bool ShowPreview;
    }

    [ProtoContract]
    public class ShopEnableMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public bool Enable;
    }
}
