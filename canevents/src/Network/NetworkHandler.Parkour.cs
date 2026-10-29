using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Parkour and King of the Hill place setup. Addressed by place id like the other per-behavior
    // handlers - unlike /canep cp and /canep kothhill, which find the place from where the caller
    // stands and so need them inside its bounds. The edits live in PlaceConfigService.
    public partial class ServerNetworkHandler
    {
        private void OnSetParkourCheckpoint(IServerPlayer player, SetParkourCheckpointMsg msg)
        {
            if (!RequireAdmin(player)) return;
            var floor = FeetBlock(player);

            // Nothing is placed in the world here - the checkpoint's block entity is spawned on this
            // block at match start - so the block has to exist.
            if (sapi.World.BlockAccessor.GetBlockId(floor) == 0)
            {
                ReplyFail(player, Lang.Get("canevents:parkour-no-floor"));
                return;
            }

            PlaceEdit(player, msg.PlaceId, p => WithBoundsNote(p, floor,
                PlaceConfig.AddParkourCheckpoint(p, floor, msg.IsFinish)));
        }

        // Position-addressed: refresh even on rejection, so a panel showing a stale list is corrected.
        private void OnRemoveParkourCheckpoint(IServerPlayer player, RemoveParkourCheckpointMsg msg)
            => PlaceEdit(player, msg.PlaceId,
                p => PlaceConfig.RemoveParkourCheckpoint(p, new BlockPos(msg.X, msg.Y, msg.Z, msg.Dim)),
                refreshOnFailure: true);

        private void OnSetParkourCheckpointIndex(IServerPlayer player, SetParkourCheckpointIndexMsg msg)
            => PlaceEdit(player, msg.PlaceId,
                p => PlaceConfig.SetParkourCheckpointIndex(p, new BlockPos(msg.X, msg.Y, msg.Z, msg.Dim), msg.Index),
                refreshOnFailure: true);

        private void OnSetParkourLives(IServerPlayer player, SetParkourLivesMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.SetParkourLives(p, msg.Lives));

        private void OnSetKothHill(IServerPlayer player, SetKothHillMsg msg)
        {
            var floor = FeetBlock(player);
            PlaceEdit(player, msg.PlaceId, p => WithBoundsNote(p, floor,
                PlaceConfig.SetKothHill(p, floor, msg.Radius, msg.Height)));
        }

        private void OnPadEdit(IServerPlayer player, PadEditMsg msg)
        {
            var pads = sapi.ModLoader.GetModSystem<canevents>()?.JoinPads;
            if (pads == null || msg == null) return;
            var floor = FeetBlock(player);

            // The panel sends the box it drew; it stands in for pos1/pos2.
            if (msg.HasBox)
            {
                pads.SetCorner(player.PlayerUID, 0, new BlockPos(msg.X, msg.Y, msg.Z, floor.dimension));
                pads.SetCorner(player.PlayerUID, 1, new BlockPos(msg.X2, msg.Y2, msg.Z2, floor.dimension));
            }

            switch (msg.Op)
            {
                case "list":
                    break;
                case "pos1":
                case "pos2":
                    var corner = LookedAtOr(player, msg, floor);
                    pads.SetCorner(player.PlayerUID, msg.Op == "pos1" ? 0 : 1, corner);
                    ReplyOk(player, Lang.Get("canevents:pad-corner-set", msg.Op == "pos1" ? 1 : 2, corner.X, corner.Y, corner.Z));
                    break;
                case "show":
                    ReplyOk(player, Lang.Get("canevents:pad-showing", EventManagers.JoinPads.ViewSeconds));
                    break;
                case "pick":
                    Reply(player, pads.EditPick(player.PlayerUID, player.Entity.Pos.AsBlockPos));
                    break;
                case "pickid":
                    Reply(player, pads.EditPick(player.PlayerUID, null, msg.PadId));
                    break;
                case "tp":
                    var pad = pads.ById(msg.PadId);
                    if (pad == null) ReplyFail(player, Lang.Get("canevents:pad-not-found", msg.PadId));
                    else EventTeleport.Send(sapi, player, (pad.MinX + pad.MaxX + 1) / 2.0, pad.MinY + 1,
                        (pad.MinZ + pad.MaxZ + 1) / 2.0, pad.Dim, "zone " + pad.Id);
                    break;
                case "deleteid":
                    if (pads.Remove(msg.PadId)) ReplyOk(player, Lang.Get("canevents:pad-removed", msg.PadId));
                    else ReplyFail(player, Lang.Get("canevents:pad-not-found", msg.PadId));
                    break;
                case "reshape":
                    Reply(player, pads.EditReshape(player.PlayerUID));
                    break;
                case "team":
                    Reply(player, pads.EditTeam(player.PlayerUID, msg.Team));
                    break;
                case "delete":
                    Reply(player, pads.EditDelete(player.PlayerUID));
                    break;
                // The panel always draws its own box, so no 3x3 fallback here.
                case "side":
                    Reply(player, pads.AddZone(player.PlayerUID, EventManagers.JoinPads.SideAction, msg.EventCode,
                        msg.Team, msg.PlaceId, floor, requireSelection: true));
                    break;
                case "join":
                    Reply(player, pads.AddZone(player.PlayerUID, EventManagers.JoinPads.JoinAction, msg.EventCode,
                        msg.Team, 0, floor, requireSelection: true));
                    break;
                case "leave":
                    Reply(player, pads.AddExit(player.PlayerUID, floor, requireSelection: true));
                    break;
                default:
                    return;
            }
            // Every zone action keeps this admin's zone cubes up while they work; merely opening a page doesn't.
            if (msg.Op != "list") pads.ShowTo(player.PlayerUID, EventManagers.JoinPads.ViewSeconds);
            SendPadList(player, pads);
        }

        private void SendPadList(IServerPlayer player, EventManagers.JoinPads pads)
        {
            adminChannel.SendPacket(new PadListMsg
            {
                Pads = pads.All().Select(p => new PadEntryMsg
                {
                    Id = p.Id, Action = p.Action, EventCode = p.EventCode, Team = p.Team,
                    MinX = p.MinX, MinY = p.MinY, MinZ = p.MinZ,
                    MaxX = p.MaxX, MaxY = p.MaxY, MaxZ = p.MaxZ,
                    PlaceId = p.PlaceId
                }).ToArray(),
                PickedId = pads.Picked(player.PlayerUID)?.Id ?? 0
            }, player);
        }

        private const double MaxCornerReach = 32;

        // The client's crosshair block, if it sent one within reach; otherwise the fallback.
        private static BlockPos LookedAtOr(IServerPlayer player, PadEditMsg msg, BlockPos fallback)
        {
            if (!msg.HasTarget || player.Entity == null) return fallback;
            var target = new BlockPos(msg.X, msg.Y, msg.Z, fallback.dimension);
            var pos = player.Entity.Pos;
            double dx = target.X + 0.5 - pos.X, dy = target.Y + 0.5 - pos.Y, dz = target.Z + 0.5 - pos.Z;
            return dx * dx + dy * dy + dz * dz <= MaxCornerReach * MaxCornerReach ? target : fallback;
        }

        private void Reply(IServerPlayer player, (bool ok, string message) result)
            => Reply(player, result.ok, result.message);

        // Usually outside the arena on purpose, so no bounds note.
        private void OnSetWaitingRoom(IServerPlayer player, SetWaitingRoomMsg msg)
        {
            var floor = FeetBlock(player);
            PlaceEdit(player, msg.PlaceId, p => PlaceConfig.SetWaitingRoom(p, floor));
        }

        // A warning, not a refusal: the place is addressed by id, so standing elsewhere is allowed,
        // just rarely what was meant.
        private static PlaceEditResult WithBoundsNote(EventPlace place, BlockPos pos, PlaceEditResult result)
            => result.Ok ? PlaceEditResult.Success(result.Message + PlaceConfigService.OutsideBoundsNote(place, pos)) : result;
    }
}
