using ProtoBuf;

namespace canevents.src.Network
{
    // Item-set management messages. One set of packets serves every kind of store (loadouts,
    // outfits - see ItemSetKinds); `Kind` picks it, and an absent Kind means loadouts, which is
    // exactly what an older client sends.

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
    }

    [ProtoContract]
    public class SaveLoadoutFromInvMsg
    {
        [ProtoMember(1)]
        public string EventCode;

        [ProtoMember(2)]
        public string SetName;

        [ProtoMember(9)]
        public string Kind;
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

    /// <summary>One selectable loadout set on the wire; ItemCodes is semicolon-packed
    /// ("code:qty;code:qty").</summary>
    [ProtoContract]
    public class LoadoutSetInfoMsg
    {
        [ProtoMember(1)] public string Name;
        [ProtoMember(2)] public string ItemCodes;
        [ProtoMember(3)] public string Description;

        /// <summary>Zip three parallel arrays (as produced by ILoadoutProvider) into infos.</summary>
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

    /// <summary>Server asks a participant to pick a loadout for an upcoming match.</summary>
    [ProtoContract]
    public class LoadoutPickRequestMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public LoadoutSetInfoMsg[] Sets;
        [ProtoMember(6)] public float TimeoutSeconds;
        [ProtoMember(7)] public string CurrentDefault; // null if none
        [ProtoMember(8)] public string RegistrationGuid; // non-empty for queue-time picks
    }

    /// <summary>Client picks a loadout (and optionally saves it as default).</summary>
    [ProtoContract]
    public class LoadoutPickResponseMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public string SetName;
        [ProtoMember(4)] public bool MakeDefault;
        [ProtoMember(5)] public string RegistrationGuid; // echo back
    }

    /// <summary>Client requests to forget a saved default for an event.</summary>
    [ProtoContract]
    public class ClearLoadoutDefaultMsg
    {
        [ProtoMember(1)] public string EventCode;
    }

    /// <summary>Client requests its own saved defaults.</summary>
    [ProtoContract]
    public class RequestLoadoutDefaultsMsg { }

    /// <summary>Server returns the player's saved defaults.</summary>
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
