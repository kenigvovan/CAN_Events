using ProtoBuf;

namespace canevents.src.Network
{
    // Item-set management messages. One set of packets serves every kind of store (loadouts,
    // outfits - see ItemSetKinds); `Kind` picks it, and an absent Kind means loadouts.
    //
    // LoadoutEventMsg, LoadoutSetMsg and LoadoutDefaultEntryMsg only ever travel inside another
    // message, so they are plain ProtoContracts and are not registered on any channel
    // (see CanEventsProtocol).

    [ProtoContract]
    public class RequestLoadoutListMsg
    {
        [ProtoMember(9)]
        public string Kind;
    }

    [ProtoContract]
    public class LoadoutListMsg
    {
        [ProtoMember(1)]
        public LoadoutEventMsg[] Events;

        [ProtoMember(9)]
        public string Kind;
    }

    [ProtoContract]
    public class LoadoutEventMsg
    {
        [ProtoMember(1)]
        public string EventCode;

        [ProtoMember(2)]
        public LoadoutSetMsg[] Sets;
    }

    [ProtoContract]
    public class LoadoutSetMsg
    {
        [ProtoMember(1)]
        public string Name;

        [ProtoMember(2)]
        public string[] ItemCodes;

        [ProtoMember(3)]
        public int[] ItemQuantities;

        // Loadouts only, parallel to ItemCodes; null for outfits.
        [ProtoMember(4)]
        public string[] ItemIds;

        // Every item's chain, in level order; UpgradeStepMsg.Item is the index into ItemCodes.
        [ProtoMember(5)]
        public UpgradeStepMsg[] Upgrades;
    }

    [ProtoContract]
    public class UpgradeStepMsg
    {
        [ProtoMember(1)] public string Code;
        // 0 = as many as the kit item.
        [ProtoMember(2)] public int Quantity;
        [ProtoMember(3)] public int Cost;
        [ProtoMember(4)] public int Item;
        // Handed out along with Code, parallel arrays.
        [ProtoMember(5)] public string[] ExtraCodes;
        [ProtoMember(6)] public int[] ExtraQuantities;

        public int ExtraCount => ExtraCodes?.Length ?? 0;

        public int ExtraQuantityAt(int i)
            => ExtraQuantities != null && i < ExtraQuantities.Length ? ExtraQuantities[i] : 1;

        public UpgradeStepMsg CopyFor(int item) => new UpgradeStepMsg
        {
            Item = item,
            Code = Code,
            Quantity = Quantity,
            Cost = Cost,
            ExtraCodes = (string[])ExtraCodes?.Clone(),
            ExtraQuantities = (int[])ExtraQuantities?.Clone()
        };
    }

    [ProtoContract]
    public class SaveLoadoutMsg
    {
        [ProtoMember(1)]
        public string EventCode;

        [ProtoMember(2)]
        public string SetName;

        [ProtoMember(3)]
        public string[] ItemCodes;

        [ProtoMember(4)]
        public int[] ItemQuantities;

        // Ids of items loaded from a saved set (null for new ones), so their upgrades keep their place.
        [ProtoMember(5)]
        public string[] ItemIds;

        [ProtoMember(6)]
        public UpgradeStepMsg[] Upgrades;

        [ProtoMember(9)]
        public string Kind;
    }

    [ProtoContract]
    public class DeleteLoadoutSetMsg
    {
        [ProtoMember(1)]
        public string EventCode;

        [ProtoMember(2)]
        public string SetName;

        [ProtoMember(9)]
        public string Kind;
    }

    [ProtoContract]
    public class ImportInlineSetsMsg
    {
        [ProtoMember(1)]
        public string EventCode;

        [ProtoMember(9)]
        public string Kind;
    }

    // Match-start loadout pick messages

    // ItemCodes is packed "code:qty;code:qty".
    [ProtoContract]
    public class LoadoutSetInfoMsg
    {
        [ProtoMember(1)] public string Name;
        [ProtoMember(2)] public string ItemCodes;
        [ProtoMember(3)] public string Description;

        // The three parallel arrays of ILoadoutProvider.
        public static LoadoutSetInfoMsg[] FromArrays(string[] names, string[] itemCodes, string[] descriptions)
        {
            if (names == null) return null;
            var result = new LoadoutSetInfoMsg[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                result[i] = new LoadoutSetInfoMsg
                {
                    Name = names[i],
                    ItemCodes = (itemCodes != null && i < itemCodes.Length) ? itemCodes[i] : null,
                    Description = (descriptions != null && i < descriptions.Length) ? descriptions[i] : null
                };
            }
            return result;
        }
    }

    // Server asks a participant to pick a loadout for an upcoming match.
    [ProtoContract]
    public class LoadoutPickRequestMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public LoadoutSetInfoMsg[] Sets;
        [ProtoMember(6)] public float TimeoutSeconds;
        [ProtoMember(7)] public string CurrentDefault; // null if none
        [ProtoMember(8)] public string RegistrationGuid; // non-empty for queue-time picks
        [ProtoMember(9)] public string CountdownLangKey; // null = "match starts in"
        [ProtoMember(10)] public string Preselected; // set to highlight; null = CurrentDefault
    }

    // Optionally saved as the default.
    [ProtoContract]
    public class LoadoutPickResponseMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public string SetName;
        [ProtoMember(4)] public bool MakeDefault;
        [ProtoMember(5)] public string RegistrationGuid; // echo back
    }

    [ProtoContract]
    public class ClearLoadoutDefaultMsg
    {
        [ProtoMember(1)] public string EventCode;
    }

    [ProtoContract]
    public class RequestLoadoutDefaultsMsg { }

    [ProtoContract]
    public class LoadoutDefaultsListMsg
    {
        [ProtoMember(1)] public LoadoutDefaultEntryMsg[] Entries;
    }

    [ProtoContract]
    public class LoadoutDefaultEntryMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string SetName;
    }
}
