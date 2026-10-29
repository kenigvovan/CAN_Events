using System;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    // Open matches: /eventadmin live, /eventadmin pad, /canlive.
    public partial class CommandsImplementations
    {
        private JoinPads joinPads => sapi.ModLoader.GetModSystem<canevents>()?.JoinPads;

        public TextCommandResult LiveStart(TextCommandCallingArgs args)
        {
            string code = (string)args.Parsers[0].GetValue();
            var ev = canEventManager.FindEventByCode(code);
            if (ev == null) return TextCommandResult.Error(Lang.Get("canevents:event-not-found", code));
            return Wrap(canEventManager.StartOpenMatch(ev));
        }

        public TextCommandResult LiveAddBot(TextCommandCallingArgs args)
        {
            int count = args.Parsers[1].IsMissing ? 1 : (int)args.Parsers[1].GetValue();
            return Wrap(canEventManager.AddBotsToOpenMatch((string)args.Parsers[0].GetValue(), count));
        }

        public TextCommandResult LiveRemoveBots(TextCommandCallingArgs args)
            => Wrap(canEventManager.RemoveBotsFromOpenMatch((string)args.Parsers[0].GetValue()));

        public TextCommandResult LiveStop(TextCommandCallingArgs args)
            => Wrap(canEventManager.StopOpenMatch((string)args.Parsers[0].GetValue()));

        public TextCommandResult LiveList(TextCommandCallingArgs args)
        {
            var lines = canEventManager.RunningEventsSnapshot()
                .Where(re => re.IsOpenMatch && re.EventState != EventState.Finished)
                .Select(re => re.Event.Code + " (" + re.EventState + "): "
                    + string.Join(", ", Enumerable.Range(0, re.ParticipantsByGroupsBefore.Count)
                        .Select(t => TeamPalette.NameOf(t) + " " + re.CountInTeam(t))))
                .ToList();
            return TextCommandResult.Success(lines.Count == 0
                ? Lang.Get("canevents:live-none")
                : string.Join("\n", lines));
        }

        // Any /eventadmin pad command shows the caller the zone cubes for a while.
        private IServerPlayer ZoneAdmin(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            joinPads.ShowTo(player?.PlayerUID, JoinPads.ViewSeconds);
            return player;
        }

        public TextCommandResult PadPos1(TextCommandCallingArgs args) => PadCorner(args, 0);
        public TextCommandResult PadPos2(TextCommandCallingArgs args) => PadCorner(args, 1);

        private TextCommandResult PadCorner(TextCommandCallingArgs args, int index)
        {
            var player = ZoneAdmin(args);
            // The block in the crosshair, else the one underfoot.
            var pos = player.CurrentBlockSelection?.Position?.Copy() ?? FeetBlock(player);
            joinPads.SetCorner(player.PlayerUID, index, pos);
            return TextCommandResult.Success(Lang.Get("canevents:pad-corner-set", index + 1, pos.X, pos.Y, pos.Z));
        }

        // Commands fall back to 3x3 under the feet when no pos1/pos2 was marked.
        public TextCommandResult PadAddJoin(TextCommandCallingArgs args)
        {
            var player = ZoneAdmin(args);
            return Wrap(joinPads.AddZone(player.PlayerUID, JoinPads.JoinAction, (string)args.Parsers[0].GetValue(),
                TeamPalette.Parse((string)args.Parsers[1].GetValue()), 0, FeetBlock(player), requireSelection: false));
        }

        public TextCommandResult PadAddSide(TextCommandCallingArgs args)
        {
            var player = ZoneAdmin(args);
            return Wrap(joinPads.AddZone(player.PlayerUID, JoinPads.SideAction, (string)args.Parsers[0].GetValue(),
                TeamPalette.Parse((string)args.Parsers[1].GetValue()), RoomPlaceFor(player), FeetBlock(player),
                requireSelection: false));
        }

        // Which place's waiting room a side zone belongs to: the /canep select one, else the place whose
        // waiting room is nearest (within 64 blocks), else none (the zone then serves every room).
        private long RoomPlaceFor(IServerPlayer player)
        {
            if (canEventManager.SelectedPlaceByPlayer.TryGetValue(player.PlayerUID, out long selected)) return selected;

            var feet = FeetBlock(player);
            long best = 0;
            double bestSq = 64 * 64;
            foreach (var place in placeManager.EventPlaces)
            {
                if (!EventPlacesManagers.Places.PlaceConfigService.TryGetWaitingRoom(place, out var room)) continue;
                if (room.dimension != feet.dimension) continue;
                double sq = room.DistanceSqTo(feet.X, feet.Y, feet.Z);
                if (sq < bestSq) { bestSq = sq; best = place.Id; }
            }
            return best;
        }

        public TextCommandResult PadAddLeave(TextCommandCallingArgs args)
        {
            var player = ZoneAdmin(args);
            return Wrap(joinPads.AddExit(player.PlayerUID, FeetBlock(player), requireSelection: false));
        }

        public TextCommandResult PadShow(TextCommandCallingArgs args)
        {
            int seconds = args.Parsers[0].IsMissing ? JoinPads.ViewSeconds : (int)args.Parsers[0].GetValue();
            joinPads.ShowTo((args.Caller.Player as IServerPlayer)?.PlayerUID, seconds);
            return TextCommandResult.Success(Lang.Get("canevents:pad-showing", seconds));
        }

        public TextCommandResult PadList(TextCommandCallingArgs args)
        {
            ZoneAdmin(args);
            var pads = joinPads.All();
            if (pads.Count == 0) return TextCommandResult.Success(Lang.Get("canevents:pad-none"));
            return TextCommandResult.Success(string.Join("\n", pads.Select(JoinPads.Describe)));
        }

        public TextCommandResult PadRemove(TextCommandCallingArgs args)
        {
            ZoneAdmin(args);
            int id = (int)args.Parsers[0].GetValue();
            return joinPads.Remove(id)
                ? TextCommandResult.Success(Lang.Get("canevents:pad-removed", id))
                : TextCommandResult.Error(Lang.Get("canevents:pad-not-found", id));
        }

        public TextCommandResult PadHere(TextCommandCallingArgs args)
        {
            var player = ZoneAdmin(args);
            var pad = joinPads.At(player.Entity.Pos.AsBlockPos);
            return pad == null
                ? TextCommandResult.Error(Lang.Get("canevents:pad-none-here"))
                : TextCommandResult.Success(JoinPads.Describe(pad));
        }

        public TextCommandResult PadEdit(TextCommandCallingArgs args)
        {
            var player = ZoneAdmin(args);
            int id = args.Parsers[0].IsMissing ? 0 : (int)args.Parsers[0].GetValue();
            return Wrap(joinPads.EditPick(player.PlayerUID, player.Entity.Pos.AsBlockPos, id));
        }

        public TextCommandResult PadReshape(TextCommandCallingArgs args)
            => Wrap(joinPads.EditReshape(ZoneAdmin(args).PlayerUID));

        public TextCommandResult PadTeam(TextCommandCallingArgs args)
            => Wrap(joinPads.EditTeam(ZoneAdmin(args).PlayerUID, TeamPalette.Parse((string)args.Parsers[0].GetValue())));

        public TextCommandResult PadDelete(TextCommandCallingArgs args)
            => Wrap(joinPads.EditDelete(ZoneAdmin(args).PlayerUID));

        public TextCommandResult LiveJoin(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            string code = (string)args.Parsers[0].GetValue();
            int team = TeamPalette.Parse((string)args.Parsers[1].GetValue());
            return Wrap(canEventManager.JoinOpenMatch(player, code, team));
        }

        public TextCommandResult LiveLeave(TextCommandCallingArgs args)
            => Wrap(canEventManager.LeaveOpenMatch(args.Caller.Player as IServerPlayer));
    }
}
