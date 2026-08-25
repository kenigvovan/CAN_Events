using ProtoBuf;

namespace canevents.src.Network
{
    // Place management messages

    /// <summary>Client requests the list of event places.</summary>
    [ProtoContract]
    public class RequestPlaceListMsg
    {
    }

    /// <summary>Server sends the full list of event places.</summary>
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

        // Which arena template this place came from; null = built directly in the world.
        [ProtoMember(19)]
        public string SchematicTemplateCode;

        [ProtoMember(20)]
        public bool SchematicOnDemand;

        // Human-readable digest of BehaviorData ("checkpoints: 7", "zone @ x,y,z r=40"); prose
        // rather than the raw JObject, so the panel needs no per-behavior schema.
        [ProtoMember(21)]
        public string[] BehaviorSummary;

        // Murder Mystery weapon-pickup spots, index-aligned with the stored array so the panel's
        // Remove buttons address the right one. Sent as coordinates rather than read back out of
        // BehaviorSummary: the panel also draws them in the world, and parsing its own prose to do
        // that is how a display string quietly becomes a wire format.
        [ProtoMember(22)]
        public int[] MmPickupX;
        [ProtoMember(23)]
        public int[] MmPickupY;
        [ProtoMember(24)]
        public int[] MmPickupZ;
    }

    /// <summary>Client requests to toggle the Enabled flag of an event place.</summary>
    [ProtoContract]
    public class TogglePlaceMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    /// <summary>Sets the spleef cage at the caller's position and its dimensions/block in BehaviorData["Spleef"].</summary>
    [ProtoContract]
    public class SetCagePosMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Width;
        [ProtoMember(3)] public int Depth;
        [ProtoMember(4)] public int Height;
        [ProtoMember(5)] public string Block;
    }

    /// <summary>Clears the spleef cage from a place and restores preview blocks.</summary>
    [ProtoContract]
    public class ClearCagePosMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Client enters/exits platform edit mode for a spleef place.</summary>
    [ProtoContract]
    public class PlatformEditStartMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Client saves or cancels platform edit session.</summary>
    [ProtoContract]
    public class PlatformEditStopMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Save;
    }

    /// <summary>Client requests fill shape (square or circle) at current player Y.</summary>
    [ProtoContract]
    public class PlatformFillShapeMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Circle; // false = square
        [ProtoMember(3)] public int Radius;
        [ProtoMember(4)] public string BlockCode;
    }

    /// <summary>Client requests flood-fill from player position, stops at non-air blocks.</summary>
    [ProtoContract]
    public class PlatformFloodFillMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int MaxRadius;
        [ProtoMember(3)] public string BlockCode;
    }

    /// <summary>Server sends current platform positions to client for display.</summary>
    [ProtoContract]
    public class PlatformEditStateMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Active;
        [ProtoMember(3)] public int Count;
    }

    /// <summary>Client requests to create a new place at the player's current position.</summary>
    [ProtoContract]
    public class CreatePlaceMsg
    {
        [ProtoMember(1)]
        public string Name;

        [ProtoMember(2)]
        public string[] Types;

        [ProtoMember(3)]
        public bool UseBounds;

        [ProtoMember(4)]
        public int BoundsMinX;
        [ProtoMember(5)]
        public int BoundsMinY;
        [ProtoMember(6)]
        public int BoundsMinZ;

        [ProtoMember(7)]
        public int BoundsMaxX;
        [ProtoMember(8)]
        public int BoundsMaxY;
        [ProtoMember(9)]
        public int BoundsMaxZ;

        /// <summary>Skip the spawn points the server would otherwise invent; the wizard places
        /// them deliberately in its own step, where guessed ones are just noise.</summary>
        [ProtoMember(10)]
        public bool NoAutoSpawns;
    }

    /// <summary>Sets (or replaces) the bounds of an existing place; without bounds the /canep
    /// commands that locate a place by its footprint can't find it.</summary>
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

        /// <summary>Replace existing spawn points with two derived from the new bounds.</summary>
        [ProtoMember(8)]
        public bool GenerateSpawns;

        /// <summary>Drop the bounds entirely instead of setting them; coordinates ignored.</summary>
        [ProtoMember(9)]
        public bool Clear;
    }

    /// <summary>Adds a spawn point at the caller's current position.</summary>
    [ProtoContract]
    public class AddSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    /// <summary>
    /// Sets a CTF flag post for one team to the caller's position. Addressed by place id, not by
    /// where the caller stands: position lookup picks the wrong one of two overlapping arenas.
    /// </summary>
    [ProtoContract]
    public class SetCtfPostMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Team;   // 1-based, as shown in the panel
    }

    /// <summary>Removes every CTF flag post from the given place.</summary>
    [ProtoContract]
    public class ClearCtfPostsMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Marks a Murder Mystery weapon-pickup spot at the caller's feet on the given place.</summary>
    [ProtoContract]
    public class AddMmPickupMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Removes every Murder Mystery pickup spot from the given place.</summary>
    [ProtoContract]
    public class ClearMmPickupsMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Removes the pickup spot at the given index (as shown in the panel list).</summary>
    [ProtoContract]
    public class RemoveMmPickupMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
    }

    /// <summary>
    /// Client asks for a Murder Mystery event's tunable settings (damage, weapon-spawn timers), to
    /// edit them in the panel. EventCode picks the variant; null/empty lets the server choose.
    /// </summary>
    [ProtoContract]
    public class RequestMmConfigMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string EventCode;
    }

    /// <summary>
    /// Server's answer to <see cref="RequestMmConfigMsg"/>: the editable attributes as a flat JSON
    /// object. JSON rather than a field per setting - the two sides would otherwise have to be kept
    /// in step through a protobuf contract every time a setting is added, and the panel already
    /// parses JSON for the Hunger Games editor.
    /// </summary>
    [ProtoContract]
    public class MmConfigMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string EventCode;
        /// <summary>Every Murder Mystery event that could run here, for the variant combo.</summary>
        [ProtoMember(3)] public string[] MmEventCodes;
        /// <summary>Only the keys the panel is allowed to edit (see MmConfigKeys); "{}" when there
        /// is no event to read.</summary>
        [ProtoMember(4)] public string AttributesJson;
    }

    /// <summary>
    /// Admin saves edited Murder Mystery settings. The payload is the same flat JSON; the server
    /// applies only known keys, so a stale client cannot wipe the loadouts or outfits it never sent.
    /// </summary>
    [ProtoContract]
    public class SaveMmConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string AttributesJson;
        [ProtoMember(3)] public long PlaceId;   // so the server can refresh the panel after saving
    }

    /// <summary>Asks the server to reply with the given place's Murder Mystery pickup spots.</summary>
    [ProtoContract]
    public class ListMmPickupsMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>
    /// Sets the Hunger Games storm centre to the caller's position and its initial radius.
    /// Addressed by place id for the same reason as <see cref="SetCtfPostMsg"/>.
    /// </summary>
    [ProtoContract]
    public class SetHgZoneMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public float Radius;
    }

    /// <summary>Adds a Hunger Games loot-chest spot at the caller's position (optional tier) on the given place.</summary>
    [ProtoContract]
    public class AddHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public string Tier;
    }

    /// <summary>Removes every Hunger Games loot spot from the given place.</summary>
    [ProtoContract]
    public class ClearHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>Asks the server to reply with the given place's Hunger Games loot spots.</summary>
    [ProtoContract]
    public class ListHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
    }

    /// <summary>
    /// Client asks for everything the HG setup panel shows for one place: loot tiers with previews,
    /// a plain-language match overview, and the place's current loot spots.
    /// </summary>
    [ProtoContract]
    public class RequestHgSetupMsg
    {
        [ProtoMember(1)] public long PlaceId;
        // Which HG event's config to load into the editor; null/empty = server picks. Every HG variant
        // (ffa / duo / ranked / solotest) keeps its OWN loot tables, hence the explicit choice.
        [ProtoMember(2)] public string EventCode;
    }

    /// <summary>
    /// Server's answer to <see cref="RequestHgSetupMsg"/>. Parallel arrays keep the payload flat so no
    /// nested type must be registered with protobuf-net; spot arrays are index-aligned with the panel list.
    /// </summary>
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

        // Match-readiness snapshot so the panel can show "will this play well?" at a glance.
        [ProtoMember(10)] public bool HasBounds;
        [ProtoMember(11)] public int SpawnCount;
        [ProtoMember(12)] public int RequiredSpawns;
        [ProtoMember(13)] public bool ZoneSet;
        [ProtoMember(14)] public bool UsesCornucopiaTier;
        [ProtoMember(15)] public string[] Warnings;

        // Raw config for the advanced editor as JSON, so nested loot entries and phases need no bespoke
        // wire shape. "{}"/"[]" when there is no event config to edit.
        [ProtoMember(16)] public string LootTablesJson;
        [ProtoMember(17)] public string ZonePhasesJson;

        // Saved zone radius, so the panel can show what the SERVER has rather than the local input box.
        [ProtoMember(18)] public float ZoneRadius;

        // Every HG event that can run on this place, for the panel's variant combo. EventCode is the
        // one currently loaded into LootTablesJson/ZonePhasesJson.
        [ProtoMember(19)] public string[] HgEventCodes;
    }

    /// <summary>
    /// One-click Hunger Games arena layout from the admin's feet: spawn ring, zone centre and
    /// cornucopia/mid/outer loot chests. Radius &lt;= 0 fits the place bounds.
    /// </summary>
    [ProtoContract]
    public class GenerateHgLayoutMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public float Radius;
        // Snap each spawn/loot to the ground surface (uneven natural arenas). Off keeps the caller's flat
        // Y, correct for a schematic arena whose floor isn't placed until match start.
        [ProtoMember(3)] public bool SnapToTerrain;
    }

    /// <summary>
    /// Toggles an in-world particle preview (zone ring + loot/spawn markers) for the requesting admin.
    /// </summary>
    [ProtoContract]
    public class HgPreviewMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public bool Enabled;
    }

    /// <summary>
    /// Admin saves edited Hunger Games loot tables (JObject of tier→entries) and/or zone phases
    /// (JArray) as JSON. A null/empty field leaves that part of the config untouched.
    /// </summary>
    [ProtoContract]
    public class SaveHgConfigMsg
    {
        [ProtoMember(1)] public string EventCode;
        [ProtoMember(2)] public string LootTablesJson;
        [ProtoMember(3)] public string ZonePhasesJson;
        [ProtoMember(4)] public long PlaceId;   // so the server can refresh this place's setup after saving
        // Write the same tables/phases into EVERY HG event, not just EventCode: admins tune loot per
        // arena and expect it to apply whichever variant is queued.
        [ProtoMember(5)] public bool ApplyToAllHgEvents;
    }

    /// <summary>Removes the loot spot at the given index (as shown in the panel list) from the place.</summary>
    [ProtoContract]
    public class RemoveHgLootMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
    }

    /// <summary>Re-tiers the loot spot at the given index on the place.</summary>
    [ProtoContract]
    public class SetHgLootTierMsg
    {
        [ProtoMember(1)] public long PlaceId;
        [ProtoMember(2)] public int Index;
        [ProtoMember(3)] public string Tier;
    }

    /// <summary>
    /// Who is currently carrying a flag, and whose flag it is. Pushed on change, not per tick; the
    /// client only draws the flag on the carrier. Parallel arrays avoid registering a nested DTO.
    /// </summary>
    [ProtoContract]
    public class CtfCarriersMsg
    {
        [ProtoMember(1)] public string[] CarrierUids;
        [ProtoMember(2)] public int[] FlagTeams;   // 0-based team whose flag each uid carries
    }

    /// <summary>
    /// Storm-zone state for the client-side dome renderer. Sent on match start, on each shrink-segment
    /// change, as a slow heartbeat (reconnect self-heal) and once with Active=false at the end; the
    /// client interpolates the radius itself, so there is no per-tick traffic.
    /// </summary>
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

    /// <summary>
    /// Moves one spawn point to the player's position, keeping its index: list order assigns
    /// spawns to teams, so remove-then-add would reshuffle them.
    /// </summary>
    [ProtoContract]
    public class MoveSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        [ProtoMember(2)]
        public int Index;
    }

    /// <summary>Client requests to remove a spawn point by index from a place.</summary>
    [ProtoContract]
    public class RemoveSpawnPointMsg
    {
        [ProtoMember(1)]
        public long PlaceId;

        [ProtoMember(2)]
        public int SpawnIndex;
    }

    /// <summary>Client requests to delete a place.</summary>
    [ProtoContract]
    public class DeletePlaceMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    /// <summary>
    /// Teleports the requesting admin to a place. A packet, not a /tp chat command, so the server
    /// picks the landing spot and no tp privilege or known coordinates are needed.
    /// </summary>
    [ProtoContract]
    public class TeleportToPlaceMsg
    {
        [ProtoMember(1)]
        public long PlaceId;
    }

    /// <summary>Server responds with success/error for a place operation.</summary>
    [ProtoContract]
    public class PlaceOperationResultMsg
    {
        [ProtoMember(1)]
        public bool Success;

        [ProtoMember(2)]
        public string Message;

        // Id of the place the operation acted on, 0 when not applicable; the New Arena wizard
        // needs it to follow the place it just created.
        [ProtoMember(3)]
        public long PlaceId;
    }
}
