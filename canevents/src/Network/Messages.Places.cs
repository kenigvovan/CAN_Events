using ProtoBuf;

namespace canevents.src.Network
{
    [ProtoContract]
    public class RequestPlaceListMsg
    {
    }

    [ProtoContract]
    public class PlaceListMsg
    {
        [ProtoMember(1)]
        public PlaceListEntryMsg[] Places;
    }

    [ProtoContract]
    public class PlaceListEntryMsg
    {
        [ProtoMember(1)]
        public long Id;

        [ProtoMember(2)]
        public string Name;

        [ProtoMember(3)]
        public string[] Types;

        [ProtoMember(4)]
        public int SpawnCount;

        [ProtoMember(5)]
        public bool Occupied;

        [ProtoMember(6)]
        public bool Enabled;

        [ProtoMember(7)]
        public int[] SpawnX;

        [ProtoMember(8)]
        public int[] SpawnY;

        [ProtoMember(9)]
        public int[] SpawnZ;

        [ProtoMember(10)]
        public bool HasBounds;
        [ProtoMember(11)]
        public int BoundsMinX;
        [ProtoMember(12)]
        public int BoundsMinY;
        [ProtoMember(13)]
        public int BoundsMinZ;
        [ProtoMember(14)]
        public int BoundsMaxX;
        [ProtoMember(15)]
        public int BoundsMaxY;
        [ProtoMember(16)]
        public int BoundsMaxZ;

        // KOTH hill geometry packed as "x,y,z,r,h"; null when the place has none.
        [ProtoMember(17)]
        public string KothHill;

        [ProtoMember(18)]
        public string Description;

        // null = built directly in the world, not from a template.
        [ProtoMember(19)]
        public string SchematicTemplateCode;

        [ProtoMember(20)]
        public bool SchematicOnDemand;

        // Display text only ("checkpoints: 7"), never parsed.
        [ProtoMember(21)]
        public string[] BehaviorSummary;

        // MM pickup spots, index-aligned with the stored array.
        [ProtoMember(22)]
        public int[] MmPickupX;
        [ProtoMember(23)]
        public int[] MmPickupY;
        [ProtoMember(24)]
        public int[] MmPickupZ;

        // "x,y,z,dim,index,finish" each. Strings because protobuf-net drops all-default int arrays.
        [ProtoMember(25)]
        public string[] ParkourCheckpoints;

        // 0 = use the event's default.
        [ProtoMember(26)]
        public int ParkourLives;

        // From PlaceSetupCheck, already worded.
        [ProtoMember(27)]
        public string[] SetupGaps;

        // 0 = normal world.
        [ProtoMember(28)]
        public int Dim;

        [ProtoMember(29)]
        public bool HgZoneSet;

        // 0 = no custom platform.
        [ProtoMember(30)]
        public int SpleefPlatformBlocks;

        // Team order, "x,y,z" or "" if the team has no post.
        [ProtoMember(31)]
        public string[] CtfPosts;

        // "x,y,z", null when the place has no waiting room.
        [ProtoMember(32)]
        public string WaitingRoom;
    }

    [ProtoContract]
    public class TogglePlaceMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    // All fields applied: blank description clears it, tags replace the old list.
    [ProtoContract]
    public class EditPlaceInfoMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public string Description;
        [ProtoMember(4)] public string[] Tags;
    }

    public enum BulkEnabledMode
    {
        Leave = 0,
        Enable = 1,
        Disable = 2
    }

    // Null or empty tag = no change.
    [ProtoContract]
    public class BulkEditPlacesMsg
    {
        [ProtoMember(1)] public long[] PlaceIds;
        [ProtoMember(2)] public BulkEnabledMode EnabledMode;
        [ProtoMember(3)] public string AddType;
        [ProtoMember(4)] public string RemoveType;
    }

    // Cage goes at the caller's position, stored in BehaviorData["Spleef"].
    [ProtoContract]
    public class SetCagePosMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Width;
        [ProtoMember(3)] public int Depth;
        [ProtoMember(4)] public int Height;
        [ProtoMember(5)] public string Block;
    }

    [ProtoContract]
    public class ClearCagePosMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    [ProtoContract]
    public class PlatformEditStartMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    [ProtoContract]
    public class PlatformEditStopMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Save;
    }

    // Filled at the player's current Y.
    [ProtoContract]
    public class PlatformFillShapeMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Circle; // false = square
        [ProtoMember(3)] public int Radius;
        [ProtoMember(4)] public string BlockCode;
    }

    // Flood fill from the player, stops at non-air.
    [ProtoContract]
    public class PlatformFloodFillMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int MaxRadius;
        [ProtoMember(3)] public string BlockCode;
    }

    [ProtoContract]
    public class PlatformEditStateMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Active;
        [ProtoMember(3)] public int Count;
    }

    // Created at the player's position; bounds come later via SetPlaceBoundsMsg.
    [ProtoContract]
    public class CreatePlaceMsg
    {
        [ProtoMember(1)]
        public string Name;

        [ProtoMember(2)]
        public string[] Types;

        // Wizard sets spawns in its own step, so skip the one at the creator's feet.
        [ProtoMember(3)]
        public bool NoAutoSpawns;
    }

    [ProtoContract]
    public class SetPlaceBoundsMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        [ProtoMember(2)]
        public int BoundsMinX;
        [ProtoMember(3)]
        public int BoundsMinY;
        [ProtoMember(4)]
        public int BoundsMinZ;

        [ProtoMember(5)]
        public int BoundsMaxX;
        [ProtoMember(6)]
        public int BoundsMaxY;
        [ProtoMember(7)]
        public int BoundsMaxZ;

        // Replaces existing spawns with two derived from the bounds.
        [ProtoMember(8)]
        public bool GenerateSpawns;

        // Removes the bounds; coordinates are ignored.
        [ProtoMember(9)]
        public bool Clear;
    }

    [ProtoContract]
    public class AddSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    // By place id, since position lookup can't tell overlapping arenas apart.
    [ProtoContract]
    public class SetCtfPostMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Team;   // 1-based, as shown in the panel
    }

    [ProtoContract]
    public class ClearCtfPostsMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    [ProtoContract]
    public class AddMmPickupMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    [ProtoContract]
    public class ClearMmPickupsMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    // Index as shown in the panel list.
    [ProtoContract]
    public class RemoveMmPickupMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
    }

    // Empty EventCode lets the server pick the variant.
    [ProtoContract]
    public class RequestMmConfigMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string EventCode;
    }

    // Editable attributes as flat JSON, so new settings need no contract change.
    [ProtoContract]
    public class MmConfigMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public string[] MmEventCodes;
        // Only MmConfigKeys; "{}" when there is no event.
        [ProtoMember(4)] public string AttributesJson;
    }

    // Server applies only known keys, so a stale client can't wipe loadouts or outfits.
    [ProtoContract]
    public class SaveMmConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string AttributesJson;
        [ProtoMember(3)] public long PlaceId;   // to refresh the panel after saving
    }

    // Centre at the caller's position.
    [ProtoContract]
    public class SetHgZoneMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public float Radius;
        [ProtoMember(3)] public HgZoneMode Mode;
    }

    // Values match the int this enum replaced; keep them for wire compat.
    public enum HgZoneMode
    {
        AtFeet = 0,
        // Keep the stored centre, change only the radius.
        KeepCentre = 1,
        // Centre of the bounds, radius to the nearest edge.
        FitBounds = 2
    }

    [ProtoContract]
    public class AddHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string Tier;
    }

    [ProtoContract]
    public class ClearHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    [ProtoContract]
    public class RequestHgSetupMsg
    {
        [ProtoMember(1)] public long PlaceId;
        // Each HG variant has its own loot tables. Empty = server picks.
        [ProtoMember(2)] public string EventCode;
    }

    // Spot arrays are index-aligned with the panel list.
    [ProtoContract]
    public class HgSetupMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public string Overview;
        [ProtoMember(4)] public string[] Tiers;
        [ProtoMember(5)] public string[] TierSummaries;
        [ProtoMember(6)] public int[] SpotX;
        [ProtoMember(7)] public int[] SpotY;
        [ProtoMember(8)] public int[] SpotZ;
        [ProtoMember(9)] public string[] SpotTier;

        // Readiness snapshot.
        [ProtoMember(10)] public bool HasBounds;
        [ProtoMember(11)] public int SpawnCount;
        [ProtoMember(12)] public int RequiredSpawns;
        [ProtoMember(13)] public bool ZoneSet;
        [ProtoMember(14)] public bool UsesCornucopiaTier;
        [ProtoMember(15)] public string[] Warnings;

        // "{}"/"[]" when there is no event config.
        [ProtoMember(16)] public string LootTablesJson;
        [ProtoMember(17)] public string ZonePhasesJson;

        // Server-side value, not whatever is in the panel's input box.
        [ProtoMember(18)] public float ZoneRadius;

        // EventCode is the one loaded into the JSON fields above.
        [ProtoMember(19)] public string[] HgEventCodes;

        // One of BehaviorConfigKeys.ScoreboardVisibilities.
        [ProtoMember(20)] public string ScoreboardVisibility;
    }

    // Spawn ring, zone centre and loot chests around the admin. Radius <= 0 fits the bounds.
    [ProtoContract]
    public class GenerateHgLayoutMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public float Radius;
        // Off for schematic arenas: their floor isn't placed until match start.
        [ProtoMember(3)] public bool SnapToTerrain;
        // 0 = event's max tribute count.
        [ProtoMember(4)] public int SpawnCount;
    }

    [ProtoContract]
    public class HgPreviewMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Enabled;
    }

    // Null or empty field leaves that part of the config as is.
    [ProtoContract]
    public class SaveHgConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string LootTablesJson;
        [ProtoMember(3)] public string ZonePhasesJson;
        [ProtoMember(4)] public long PlaceId;   // to refresh the setup panel after saving
        // Write into every HG event, not just EventCode.
        [ProtoMember(5)] public bool ApplyToAllHgEvents;
        [ProtoMember(6)] public string ScoreboardVisibility;
    }

    [ProtoContract]
    public class RemoveHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
    }

    [ProtoContract]
    public class SetHgLootTierMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
        [ProtoMember(3)] public string Tier;
    }

    // Sent on change only.
    [ProtoContract]
    public class CtfCarriersMsg
    {
        [ProtoMember(1)] public string[] CarrierUids;
        [ProtoMember(2)] public int[] FlagTeams;   // 0-based team whose flag each uid carries
    }

    // Who is on which team, for the pennants over their heads (TeamMarkersEB). Sent on change only;
    // an empty list clears that match's pennants.
    [ProtoContract]
    public class TeamMarkersMsg
    {
        [ProtoMember(1)] public long EventId;
        [ProtoMember(2)] public string[] Uids;
        [ProtoMember(3)] public int[] Teams;       // 0-based
        [ProtoMember(4)] public bool OnHead;       // false: on the back, like the CTF carrier flag
    }

    // Sent on start, per shrink segment and as a slow heartbeat; client interpolates the radius.
    [ProtoContract]
    public class HgZoneStateMsg
    {
        [ProtoMember(1)] public bool Active;
        [ProtoMember(2)] public double X;
        [ProtoMember(3)] public double Y;
        [ProtoMember(4)] public double Z;
        [ProtoMember(5)] public float RadiusNow;
        [ProtoMember(6)] public float RadiusTarget;
        [ProtoMember(7)] public float SecondsToTarget;
        [ProtoMember(8)] public int AliveCount;
    }

    // Keeps the index: list order assigns spawns to teams.
    [ProtoContract]
    public class MoveSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        [ProtoMember(2)]
        public int Index;
    }

    [ProtoContract]
    public class RemoveSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        [ProtoMember(2)]
        public int SpawnIndex;
    }

    // Restores what a template stamp replaced; places with a running match are skipped.
    [ProtoContract]
    public class DeletePlacesMsg
    {
        [ProtoMember(1)]
        public long[] PlaceIds;
    }

    // Server picks the landing spot, no tp privilege needed.
    [ProtoContract]
    public class TeleportToPlaceMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        // 1-based; 0 lets the server pick, as before the field existed.
        [ProtoMember(2)]
        public int SpawnNumber;
    }

    [ProtoContract]
    public class TeleportToHgLootSpotMsg
    {
        [ProtoMember(1)] public long PlaceId;
        // 0-based, panel order.
        [ProtoMember(2)] public int SpotIndex;
    }

    // Block under the admin's feet becomes the next checkpoint, or the finish.
    [ProtoContract]
    public class SetParkourCheckpointMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool IsFinish;
    }

    // By position, not index, so a stale panel list can't remove the wrong one.
    [ProtoContract]
    public class RemoveParkourCheckpointMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
        [ProtoMember(5)] public int Dim;
    }

    [ProtoContract]
    public class SetParkourCheckpointIndexMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int X;
        [ProtoMember(3)] public int Y;
        [ProtoMember(4)] public int Z;
        [ProtoMember(5)] public int Dim;
        [ProtoMember(6)] public int Index;
    }

    [ProtoContract]
    public class SetParkourLivesMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Lives;
    }

    // Waiting room goes on the block under the admin's feet.
    [ProtoContract]
    public class SetWaitingRoomMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    // Floor zones (JoinPads) from the admin panel, at the admin's feet.
    // Op: "pos1", "pos2", "side" (pos1/pos2 area -> side zone for Team), "show",
    // and editing: "pick" (the pad under the admin), then "reshape", "team" (to Team) or "delete".
    [ProtoContract]
    public class PadEditMsg
    {
        [ProtoMember(1)] public string Op;
        [ProtoMember(2)] public string EventCode;
        [ProtoMember(3)] public int Team;

        // pos1/pos2: the block the admin looks at. HasTarget false = the block under their feet.
        [ProtoMember(4)] public bool HasTarget;
        [ProtoMember(5)] public int X;
        [ProtoMember(6)] public int Y;
        [ProtoMember(7)] public int Z;

        // Row actions from the zone list: "pickid", "tp", "deleteid". "list" asks for PadListMsg.
        [ProtoMember(8)] public int PadId;

        // side/reshape from the panel: the box the admin marked (X,Y,Z)-(X2,Y2,Z2), drawn client-side.
        [ProtoMember(9)] public bool HasBox;
        [ProtoMember(10)] public int X2;
        [ProtoMember(11)] public int Y2;
        [ProtoMember(12)] public int Z2;

        // side: the place whose waiting room the zone is in.
        [ProtoMember(13)] public long PlaceId;
    }

    // Server -> admin: every zone, resent after each change.
    [ProtoContract]
    public class PadListMsg
    {
        [ProtoMember(1)] public PadEntryMsg[] Pads;
        // Zone this admin has picked for editing, 0 = none.
        [ProtoMember(2)] public int PickedId;
    }

    [ProtoContract]
    public class PadEntryMsg
    {
        [ProtoMember(1)] public int Id;
        [ProtoMember(2)] public string Action;
        [ProtoMember(3)] public string EventCode;
        [ProtoMember(4)] public int Team;
        [ProtoMember(5)] public int MinX;
        [ProtoMember(6)] public int MinY;
        [ProtoMember(7)] public int MinZ;
        [ProtoMember(8)] public int MaxX;
        [ProtoMember(9)] public int MaxY;
        [ProtoMember(10)] public int MaxZ;
        // Side zones: the place whose waiting room they are in, 0 = any.
        [ProtoMember(11)] public long PlaceId;
    }

    // Hill goes on the block under the admin's feet.
    [ProtoContract]
    public class SetKothHillMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public float Radius;
        // <= 0 = default height.
        [ProtoMember(3)] public float Height;
    }

    [ProtoContract]
    public class PlaceOperationResultMsg
    {
        [ProtoMember(1)]
        public bool Success;

        [ProtoMember(2)]
        public string Message;

        // 0 if n/a. The New Arena wizard uses it to follow the place it created.
        [ProtoMember(3)]
        public long PlaceId;
    }
}
