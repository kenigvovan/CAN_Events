using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public readonly struct SnapshotPair
    {
        public readonly InventoryBackupInfo Active;
        public readonly InventoryBackupInfo Parked;
        public readonly PlayerModDataAccess.Source Source;

        public SnapshotPair(InventoryBackupInfo active, InventoryBackupInfo parked, PlayerModDataAccess.Source source)
        {
            Active = active;
            Parked = parked;
            Source = source;
        }

        // Distinct from "holds nothing".
        public bool Unreadable => Source == PlayerModDataAccess.Source.Unavailable && !Active.Present && !Parked.Present;

        public bool HoldsAnything => Active.Present || Parked.Present;
    }

    // Shared by /eventadmin inv and the audit panel.
    public static class InventorySnapshots
    {
        // Each DB read parses a save record on the main thread.
        public const int MaxDatabaseReadsPerSweep = 250;

        public static string KeyFor(bool parked)
            => parked ? PlayerInventoryBackup.FailedModDataKey : PlayerInventoryBackup.ModDataKey;

        public static InventoryBackupInfo Read(ICoreServerAPI sapi, string uid, bool parked,
            out PlayerModDataAccess.Source source)
        {
            var data = PlayerModDataAccess.Read(sapi, uid, KeyFor(parked), out source);
            return PlayerInventoryBackup.Describe(data, sapi.World, parked);
        }

        // Parked restore needs the player online.
        public static bool TryOnlineForRestore(ICoreServerAPI sapi, string uid, out IServerPlayer player,
            out string error)
        {
            player = sapi.World.PlayerByUid(uid) as IServerPlayer;
            error = player != null ? null
                : PlayerModDataAccess.NameOf(sapi, uid)
                  + " is offline - a parked snapshot can only be handed to a connected player.";
            return player != null;
        }

        // Past the DB budget, offline players come back Unreadable.
        public sealed class Sweep
        {
            private readonly ICoreServerAPI sapi;
            private int dbReads;

            public Sweep(ICoreServerAPI sapi) { this.sapi = sapi; }

            public SnapshotPair Read(string uid)
            {
                bool online = sapi.World.PlayerByUid(uid) is IServerPlayer;
                bool allowDb = online || dbReads < MaxDatabaseReadsPerSweep;

                var (activeData, parkedData) = PlayerModDataAccess.ReadPair(sapi, uid,
                    KeyFor(false), KeyFor(true), out var source, allowDb);
                if (source == PlayerModDataAccess.Source.Database) dbReads++;

                return new SnapshotPair(
                    PlayerInventoryBackup.Describe(activeData, sapi.World),
                    PlayerInventoryBackup.Describe(parkedData, sapi.World, parked: true),
                    source);
            }
        }
    }
}
