using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    public class JoinPad
    {
        public int Id;
        public string EventCode;
        // "join", "leave" or "side"
        public string Action;
        public int Team;
        // Side zones: the place whose waiting room they lie in. 0 = any (zones made before this existed).
        public long PlaceId;
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ, Dim;

        public bool Contains(BlockPos p)
            => p.dimension == Dim
               && p.X >= MinX && p.X <= MaxX
               && p.Y >= MinY && p.Y <= MaxY
               && p.Z >= MinZ && p.Z <= MaxZ;

        public bool IsLeave => Action == JoinPads.LeaveAction;
        public bool IsSide => Action == JoinPads.SideAction;
    }

    public class JoinPadData
    {
        public int NextId = 1;
        public List<JoinPad> Pads = new List<JoinPad>();
    }

    // Floor zones in the world: stand on one to walk into an open match, out of it, or onto a side
    // in a waiting room. Hand-built in the world; this only remembers the boxes
    // (ModConfig/canevents/joinpads.json).
    public class JoinPads : Scoring.JsonFileStore<JoinPadData>
    {
        public const string JoinAction = "join";
        public const string LeaveAction = "leave";
        public const string SideAction = "side";

        private const int ScanMs = 250;
        private const long RetriggerMs = 3000;

        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private long listenerId = -1;

        // uid -> pad they stand on (fires again only after stepping off) and when it last fired.
        private readonly Dictionary<string, (int padId, long firedAtMs)> standing = new Dictionary<string, (int, long)>();

        // Position at the previous scan; a jump bigger than a sprint step is a teleport.
        private readonly Dictionary<string, Vec3d> lastSeen = new Dictionary<string, Vec3d>();
        private const double WalkStepSq = 4.0 * 4.0;

        public JoinPads(ICoreServerAPI sapi, CANEventManager manager) : base(sapi, "joinpads.json", "join pads")
        {
            this.sapi = sapi;
            this.manager = manager;
        }

        public void Start()
        {
            if (listenerId >= 0) return;
            AdoptOrphanSideZones();
            listenerId = sapi.Event.RegisterGameTickListener(_ => Scan(), ScanMs);
            sapi.Event.PlayerDisconnect += Forget;
        }

        // Side zones made before zones knew their room: give each the place whose waiting room is
        // nearest (within 64 blocks), so they serve every event held in that room.
        private void AdoptOrphanSideZones()
        {
            lock (lockObj)
            {
                bool changed = false;
                foreach (var pad in Data.Pads.Where(p => p.IsSide && p.PlaceId == 0))
                {
                    long best = 0;
                    double bestSq = 64 * 64;
                    foreach (var place in manager.eventPlacesManager.EventPlaces)
                    {
                        if (!EventPlacesManagers.Places.PlaceConfigService.TryGetWaitingRoom(place, out var room)) continue;
                        if (room.dimension != pad.Dim) continue;
                        double sq = room.DistanceSqTo((pad.MinX + pad.MaxX) / 2.0, pad.MinY, (pad.MinZ + pad.MaxZ) / 2.0);
                        if (sq < bestSq) { bestSq = sq; best = place.Id; }
                    }
                    if (best == 0) continue;
                    pad.PlaceId = best;
                    changed = true;
                }
                if (changed) Save();
            }
        }

        public void Stop()
        {
            if (listenerId < 0) return;
            sapi.Event.UnregisterGameTickListener(listenerId);
            sapi.Event.PlayerDisconnect -= Forget;
            listenerId = -1;
        }

        // Session state only; a returning player starts clean (and lands by teleport, so no pad fires).
        private void Forget(IServerPlayer player)
        {
            string uid = player?.PlayerUID;
            if (uid == null) return;
            standing.Remove(uid);
            lastSeen.Remove(uid);
            viewers.Remove(uid);
            selections.Remove(uid);
            editing.Remove(uid);
        }

        public List<JoinPad> All()
        {
            lock (lockObj) return Data.Pads.ToList();
        }

        // How long an admin's zone cubes stay up after their last zone action.
        public const int ViewSeconds = 300;

        // Floor blocks a and b are opposite corners; the box reaches two blocks above the higher one.
        public JoinPad Add(string eventCode, string action, int team, BlockPos a, BlockPos b, long placeId = 0)
        {
            JoinPad pad;
            lock (lockObj)
            {
                pad = new JoinPad
                {
                    Id = Data.NextId++,
                    EventCode = eventCode,
                    Action = action,
                    Team = team,
                    PlaceId = placeId,
                    MinX = Math.Min(a.X, b.X), MaxX = Math.Max(a.X, b.X),
                    MinY = Math.Min(a.Y, b.Y), MaxY = Math.Max(a.Y, b.Y) + 2,
                    MinZ = Math.Min(a.Z, b.Z), MaxZ = Math.Max(a.Z, b.Z),
                    Dim = a.dimension
                };
                Data.Pads.Add(pad);
                Save();
            }
            return pad;
        }

        // /eventadmin pad pos1|pos2, per admin, session only.
        private readonly Dictionary<string, BlockPos[]> selections = new Dictionary<string, BlockPos[]>();

        public void SetCorner(string uid, int index, BlockPos floor)
        {
            if (!selections.TryGetValue(uid, out var sel)) selections[uid] = sel = new BlockPos[2];
            sel[index] = floor.Copy();
        }

        // Pad an admin picked to edit (stood on it, or by id), per admin, session only.
        private readonly Dictionary<string, int> editing = new Dictionary<string, int>();

        public JoinPad Picked(string uid)
        {
            if (!editing.TryGetValue(uid, out int id)) return null;
            lock (lockObj) return Data.Pads.FirstOrDefault(p => p.Id == id);
        }

        public JoinPad Pick(string uid, JoinPad pad)
        {
            if (pad == null) editing.Remove(uid);
            else editing[uid] = pad.Id;
            return pad;
        }

        public JoinPad ById(int id)
        {
            lock (lockObj) return Data.Pads.FirstOrDefault(p => p.Id == id);
        }

        // New corners from the pos1/pos2 selection; same height rule as Add.
        public bool Reshape(JoinPad pad, BlockPos a, BlockPos b)
        {
            lock (lockObj)
            {
                if (!Data.Pads.Contains(pad)) return false;
                pad.MinX = Math.Min(a.X, b.X); pad.MaxX = Math.Max(a.X, b.X);
                pad.MinY = Math.Min(a.Y, b.Y); pad.MaxY = Math.Max(a.Y, b.Y) + 2;
                pad.MinZ = Math.Min(a.Z, b.Z); pad.MaxZ = Math.Max(a.Z, b.Z);
                pad.Dim = a.dimension;
                Save();
            }
            return true;
        }

        public bool SetTeam(JoinPad pad, int team)
        {
            lock (lockObj)
            {
                if (!Data.Pads.Contains(pad) || pad.IsLeave) return false;
                pad.Team = team;
                Save();
            }
            return true;
        }

        // Edit flow shared by /eventadmin pad and the admin panel: pick, then reshape / team / delete.
        public (bool ok, string message) EditPick(string uid, BlockPos feet, int id = 0)
        {
            var pad = id > 0 ? ById(id) : At(feet);
            if (pad == null)
                return (false, id > 0 ? Lang.Get("canevents:pad-not-found", id) : Lang.Get("canevents:pad-none-here"));
            Pick(uid, pad);
            return (true, Lang.Get("canevents:pad-picked", Describe(pad)));
        }

        public (bool ok, string message) EditReshape(string uid)
        {
            var pad = Picked(uid);
            if (pad == null) return (false, Lang.Get("canevents:pad-pick-first"));
            if (!TakeSelection(uid, out var a, out var b)) return (false, Lang.Get("canevents:pad-need-corners"));
            Reshape(pad, a, b);
            return (true, Lang.Get("canevents:pad-reshaped", Describe(pad)));
        }

        public (bool ok, string message) EditTeam(string uid, int team)
        {
            var pad = Picked(uid);
            if (pad == null) return (false, Lang.Get("canevents:pad-pick-first"));
            if (pad.IsLeave) return (false, Lang.Get("canevents:pad-leave-no-team"));

            int teams = CANEventManager.TeamCountOf(manager.FindEventByCode(pad.EventCode));
            if (team < 0 || team >= teams) return (false, Lang.Get("canevents:live-no-such-team", teams));
            SetTeam(pad, team);
            return (true, Lang.Get("canevents:pad-reshaped", Describe(pad)));
        }

        // Join and side zones from the admin's pos1/pos2 selection. requireSelection=false falls back to
        // DefaultArea (commands); the panel always draws its own box.
        public (bool ok, string message) AddZone(string uid, string action, string code, int team, long placeId,
            BlockPos feet, bool requireSelection)
        {
            var ev = manager.FindEventByCode(code);
            if (ev == null) return (false, Lang.Get("canevents:event-not-found", code));

            if (action == SideAction && ev.GetBehavior<eventBehavior.WaitingRoomEB>() == null)
                return (false, Lang.Get("canevents:pad-side-no-room", ev.Code));
            if (action == JoinAction && CANEventManager.OpenModeOf(ev) == null)
                return (false, Lang.Get("canevents:live-not-open-event", ev.Code));

            int teams = CANEventManager.TeamCountOf(ev);
            if (team < 0 || team >= teams) return (false, Lang.Get("canevents:live-no-such-team", teams));

            if (!TakeSelection(uid, out var a, out var b))
            {
                if (requireSelection) return (false, Lang.Get("canevents:pad-need-corners"));
                DefaultArea(feet, out a, out b);
            }

            var pad = Add(ev.Code, action, team, a, b, action == SideAction ? placeId : 0);
            string key = action == SideAction ? "canevents:pad-added-side" : "canevents:pad-added-join";
            return (true, Lang.Get(key, pad.Id, ev.Code, TeamPalette.NameOf(team)));
        }

        public (bool ok, string message) AddExit(string uid, BlockPos feet, bool requireSelection)
        {
            if (!TakeSelection(uid, out var a, out var b))
            {
                if (requireSelection) return (false, Lang.Get("canevents:pad-need-corners"));
                DefaultArea(feet, out a, out b);
            }
            var pad = Add(null, LeaveAction, -1, a, b);
            return (true, Lang.Get("canevents:pad-added-leave", pad.Id));
        }

        // 3x3 around the block under the feet.
        public static void DefaultArea(BlockPos feet, out BlockPos a, out BlockPos b)
        {
            a = feet.AddCopy(-1, 0, -1);
            b = feet.AddCopy(1, 0, 1);
        }

        public (bool ok, string message) EditDelete(string uid)
        {
            var pad = Picked(uid);
            if (pad == null) return (false, Lang.Get("canevents:pad-pick-first"));
            Remove(pad.Id);
            return (true, Lang.Get("canevents:pad-removed", pad.Id));
        }

        public static string Describe(JoinPad p)
            => "#" + p.Id + " " + (p.IsLeave ? "leave" : p.Action + " " + p.EventCode + " " + TeamPalette.NameOf(p.Team))
               + " [" + p.MinX + "," + p.MinY + "," + p.MinZ + " .. " + p.MaxX + "," + p.MaxY + "," + p.MaxZ + "]";

        public bool HasSelection(string uid)
            => selections.TryGetValue(uid, out var sel) && (sel[0] != null || sel[1] != null);

        // Both corners, or one corner used twice; consumed on success.
        public bool TakeSelection(string uid, out BlockPos a, out BlockPos b)
        {
            a = b = null;
            if (!selections.TryGetValue(uid, out var sel) || (sel[0] == null && sel[1] == null)) return false;
            a = sel[0] ?? sel[1];
            b = sel[1] ?? sel[0];
            selections.Remove(uid);
            return true;
        }

        // Zones are shown as highlighted floor blocks (the same cubes the arena editor uses), per viewer:
        // admins while they edit, players in a waiting room. A highlight stays until replaced, so it is
        // only resent when what this viewer should see changes.
        private const int HighlightSlot = 7804; // clear of PlaceHighlighter's 4180-4184 and 7802
        private const int MaxHighlightBlocks = 4000;
        private readonly Dictionary<string, (long untilMs, string onlyEvent, long onlyPlace, int drawnHash)> viewers
            = new Dictionary<string, (long, string, long, int)>();
        private long lastDrawMs;

        // onlyEvent: just that event's side zones in that place's room, without edit marks (waiting-room players).
        public void ShowTo(string uid, int seconds, string onlyEvent = null, long onlyPlace = 0)
        {
            if (uid == null) return;
            viewers[uid] = (sapi.World.ElapsedMilliseconds + Math.Max(1, seconds) * 1000L, onlyEvent, onlyPlace, 0);
        }

        // A zone made in a place's waiting room serves every event played there (battle, its bot
        // variant, a debug bot match); an older zone without a place only its own event.
        private static bool SideZoneOf(JoinPad p, string eventCode, long placeId)
            => p.IsSide && (p.PlaceId != 0
                ? p.PlaceId == placeId
                : string.Equals(p.EventCode, eventCode, StringComparison.OrdinalIgnoreCase));

        public void HideFrom(string uid)
        {
            if (uid == null || !viewers.Remove(uid)) return;
            if (sapi.World.PlayerByUid(uid) is IServerPlayer player)
                sapi.World.HighlightBlocks(player, HighlightSlot, new List<BlockPos>(), new List<int>());
        }

        private void DrawOutlines(List<JoinPad> pads, long now)
        {
            if (viewers.Count == 0 || now - lastDrawMs < 500) return;
            lastDrawMs = now;

            foreach (var uid in viewers.Keys.ToList())
            {
                var v = viewers[uid];
                if (!(sapi.World.PlayerByUid(uid) is IServerPlayer player)) { viewers.Remove(uid); continue; }
                if (now >= v.untilMs) { HideFrom(uid); continue; }

                var blocks = new List<BlockPos>();
                var colors = new List<int>();
                int picked = v.onlyEvent == null && editing.TryGetValue(uid, out int id) ? id : 0;

                foreach (var p in pads)
                {
                    if (v.onlyEvent != null && !SideZoneOf(p, v.onlyEvent, v.onlyPlace)) continue;
                    int color = p.Id == picked ? PickedColor : p.IsLeave ? LeaveColor : Translucent(TeamPalette.Packed(p.Team));
                    AddFloor(blocks, colors, p.MinX, p.MinZ, p.MaxX, p.MaxZ, p.MinY, p.Dim, color);
                }

                if (v.onlyEvent == null && selections.TryGetValue(uid, out var sel) && (sel[0] != null || sel[1] != null))
                {
                    var a = sel[0] ?? sel[1];
                    var b = sel[1] ?? sel[0];
                    AddFloor(blocks, colors, Math.Min(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.X, b.X), Math.Max(a.Z, b.Z),
                        Math.Max(a.Y, b.Y), a.dimension, SelectionColor);
                }

                int hash = 17;
                unchecked
                {
                    for (int i = 0; i < blocks.Count; i++)
                        hash = ((hash * 31 + blocks[i].X) * 31 + blocks[i].Y) * 31 + blocks[i].Z ^ colors[i];
                }
                if (hash == v.drawnHash) continue;
                viewers[uid] = (v.untilMs, v.onlyEvent, v.onlyPlace, hash);
                sapi.World.HighlightBlocks(player, HighlightSlot, blocks, colors);
            }
        }

        // The floor layer of a zone, one highlighted cube per block.
        private static void AddFloor(List<BlockPos> blocks, List<int> colors, int x0, int z0, int x1, int z1, int y, int dim, int color)
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (blocks.Count >= MaxHighlightBlocks) return;
                    // Dim 0: the highlight renderer adds the dimension offset itself (see PlaceHighlighter.Send).
                    blocks.Add(new BlockPos(x, y, z, 0));
                    colors.Add(color);
                }
        }

        // Highlight colours are ABGR with alpha in the top byte, like TeamPalette.Packed.
        private static int Translucent(int packed) => (packed & 0x00FFFFFF) | (120 << 24);
        private static readonly int LeaveColor = ColorUtil.ToRgba(120, 255, 255, 255);
        private static readonly int PickedColor = ColorUtil.ToRgba(170, 40, 170, 255);
        private static readonly int SelectionColor = ColorUtil.ToRgba(150, 255, 60, 220);

        public bool Remove(int id)
        {
            lock (lockObj)
            {
                if (Data.Pads.RemoveAll(p => p.Id == id) == 0) return false;
                Save();
            }
            foreach (var uid in editing.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) editing.Remove(uid);
            return true;
        }

        // Side zones of a deleted place would stay on the room's blocks and shadow a new place's zones.
        public int RemoveForPlace(long placeId)
        {
            List<int> ids;
            lock (lockObj)
            {
                ids = Data.Pads.Where(p => p.IsSide && p.PlaceId == placeId).Select(p => p.Id).ToList();
                if (ids.Count == 0) return 0;
                Data.Pads.RemoveAll(p => ids.Contains(p.Id));
                Save();
            }
            foreach (var uid in editing.Where(kv => ids.Contains(kv.Value)).Select(kv => kv.Key).ToList()) editing.Remove(uid);
            return ids.Count;
        }

        public JoinPad At(BlockPos pos)
        {
            lock (lockObj) return Data.Pads.FirstOrDefault(p => p.Contains(pos));
        }

        private void Scan()
        {
            List<JoinPad> pads;
            lock (lockObj) pads = Data.Pads.ToList();

            long now = sapi.World.ElapsedMilliseconds;
            DrawOutlines(pads, now);
            if (pads.Count == 0) { standing.Clear(); lastSeen.Clear(); return; }

            foreach (var player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                var entity = player.Entity;
                if (entity == null || !entity.Alive) continue;

                string uid = player.PlayerUID;
                var at = entity.Pos.XYZ;
                bool walked = lastSeen.TryGetValue(uid, out var before) && before.SquareDistanceTo(at) < WalkStepSq;
                lastSeen[uid] = at;

                var pos = entity.Pos.AsBlockPos;
                var pad = pads.FirstOrDefault(p => p.Contains(pos));

                if (pad == null)
                {
                    standing.Remove(uid);
                    continue;
                }

                if (standing.TryGetValue(uid, out var last)
                    && (last.padId == pad.Id || now - last.firedAtMs < RetriggerMs)) continue;

                standing[uid] = (pad.Id, now);
                // Landed here by teleport (sent home after leaving, login): wait until they step off and back on.
                if (!walked) continue;
                // Overlaps are gathered only on a step that fires, not on every scan.
                var here = pads.Where(p => p.Contains(pos)).ToList();
                manager.log.Safely("join pad " + pad.Id + " for " + player.PlayerName, () => Fire(player, here));
            }
        }

        public bool HasSidePads(string eventCode, long placeId)
        {
            lock (lockObj) return Data.Pads.Any(p => SideZoneOf(p, eventCode, placeId));
        }

        // Rooms shared by several arenas carry one set of side zones per place on the same blocks (or a
        // deleted place's leftovers): only the zone of the lobby the player is in answers, the rest give null.
        private void Fire(IServerPlayer player, List<JoinPad> here)
        {
            var pad = here[0];
            bool ok;
            string message;
            if (pad.IsSide)
            {
                (ok, message) = (false, null);
                foreach (var side in here.Where(p => p.IsSide))
                {
                    (ok, message) = manager.PickSideInLobby(player, side.EventCode, side.Team, side.PlaceId);
                    if (message != null) break;
                }
            }
            else
            {
                (ok, message) = pad.IsLeave ? manager.LeaveOpenMatch(player)
                    : manager.JoinOpenMatch(player, pad.EventCode, pad.Team);
            }
            if (!string.IsNullOrEmpty(message))
                player.SendMessage(0, message, ok ? EnumChatType.Notification : EnumChatType.CommandError);
        }
    }
}
