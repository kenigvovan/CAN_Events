using System;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.commands
{
    // /canep commands. Edits go to the caller's target place (see PlaceTargeting) via PlaceConfigService.
    // Markers go on the block under the caller's feet, spawn points where the caller stands.
    public partial class CommandsImplementations
    {
        public TextCommandResult NewEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (!targeting.InProgress.ContainsKey(player.PlayerUID))
                targeting.InProgress.Add(player.PlayerUID, new EventPlace());

            // Drop the old selection so the next edits go to the new place.
            targeting.Unselect(player.PlayerUID);
            targeting.ClearPendingCorner(player.PlayerUID);
            return TextCommandResult.Success(Lang.Get("canevents:place-created"));
        }

        public TextCommandResult ArenaAddSpawnPoint(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            var pos = player.Entity.Pos.AsBlockPos;
            if (!TryResolveTarget(player, pos, out var target, out var error)) return error;

            return Wrap(placeConfig.AddSpawn(target, pos));
        }

        // Admins see 1-based spawn numbers, the service takes a 0-based index.
        public TextCommandResult RemoveSpawnPoint(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            int index = (int)args.Parsers[0].GetValue() - 1;
            if (!TryResolveTarget(player, player.Entity.Pos.AsBlockPos, out var target, out var error)) return error;

            return Wrap(placeConfig.RemoveSpawn(target, index));
        }

        public TextCommandResult MoveSpawnPoint(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            int index = (int)args.Parsers[0].GetValue() - 1;
            var pos = player.Entity.Pos.AsBlockPos;
            if (!TryResolveTarget(player, pos, out var target, out var error)) return error;

            return Wrap(placeConfig.MoveSpawn(target, index, pos));
        }

        public TextCommandResult SelectPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            long id = (long)args.Parsers[0].GetValue();

            EventPlace place;
            if (id >= 0)
            {
                place = placeManager.FindById(id);
                if (place == null) return TextCommandResult.Error(Lang.Get("canevents:place-not-found-id", id));
            }
            else
            {
                var feet = FeetBlock(player);
                var here = targeting.PlacesAt(feet);
                if (here.Count == 0) return TextCommandResult.Error(Lang.Get("canevents:select-none-here"));
                if (here.Count > 1)
                    return TextCommandResult.Error(targeting.NoTargetMessage(
                        new PlaceTarget(null, false, here), feet));
                place = here[0];
            }

            targeting.Select(player.PlayerUID, place.Id);
            return TextCommandResult.Success(Lang.Get("canevents:place-selected", place.Name, place.Id)
                + "\n" + PlaceConfigService.Describe(place));
        }

        public TextCommandResult UnselectPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            targeting.Unselect(player.PlayerUID);
            return TextCommandResult.Success(Lang.Get("canevents:place-unselected"));
        }

        public TextCommandResult PlaceInfo(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            long id = (long)args.Parsers[0].GetValue();

            EventPlace place;
            if (id >= 0)
            {
                place = placeManager.FindById(id);
                if (place == null) return TextCommandResult.Error(Lang.Get("canevents:place-not-found-id", id));
            }
            else if (!TryResolveTarget(player, FeetBlock(player), out place, out var error)) return error;

            return TextCommandResult.Success(PlaceConfigService.Describe(place));
        }

        public TextCommandResult ListPlaces(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            if (placeManager.EventPlaces.Count == 0) return TextCommandResult.Success(Lang.Get("canevents:place-list-empty"));

            bool hasSelection = targeting.TryGetSelection(player.PlayerUID, out long selected);

            var sb = new System.Text.StringBuilder();
            foreach (var p in placeManager.EventPlaces.OrderBy(p => p.Id))
            {
                var gaps = PlaceSetupCheck.Gaps(p);
                string state = !p.Enabled ? "off"
                    : (p.spawnPositions?.Count ?? 0) == 0 ? "no spawns"
                    : gaps.Count > 0 ? "missing: " + string.Join("; ", gaps)
                    : p.Occupied ? "in use" : "ready";
                sb.AppendLine(string.Format("{0}#{1} {2} [{3}] - {4}",
                    // Not '>': chat is VTML and a bare '>' reads as a closing tag.
                    hasSelection && selected == p.Id ? "* " : "  ", p.Id, p.Name,
                    p.placeTypes == null ? "" : string.Join(",", p.placeTypes), state));
            }
            return TextCommandResult.Success(sb.ToString().TrimEnd());
        }

        public TextCommandResult TeleportToPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            long id = (long)args.Parsers[0].GetValue();
            var place = placeManager.FindById(id);
            if (place == null) return TextCommandResult.Error(Lang.Get("canevents:place-not-found-id", id));

            if (!PlaceConfigService.TryFindTeleportPoint(place, out double x, out double y, out double z,
                    out int dim, out string what))
                return TextCommandResult.Error(Lang.Get("canevents:place-nowhere-to-tp", place.Name));

            EventTeleport.Send(sapi, player, x, y, z, dim, "/canep tp '" + place.Name + "'");
            return TextCommandResult.Success(Lang.Get("canevents:place-teleported", place.Name, what));
        }

        // Mode is stored on the stack, so each wand keeps its own.
        public TextCommandResult GiveWand(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string mode = args.Parsers[0].GetValue() as string;

            var held = player.InventoryManager.ActiveHotbarSlot;
            if (held?.Itemstack?.Collectible is Items.ItemEventWand)
            {
                if (string.IsNullOrEmpty(mode))
                    return TextCommandResult.Success(Lang.Get("canevents:wand-mode-is", Items.ItemEventWand.ModeOf(held.Itemstack)));
                held.Itemstack.Attributes.SetString(Items.ItemEventWand.ModeAttr, mode);
                held.MarkDirty();
                return TextCommandResult.Success(Lang.Get("canevents:wand-mode-set", mode));
            }

            var item = sapi.World.GetItem(new AssetLocation("canevents", "eventwand"));
            if (item == null) return TextCommandResult.Error("Item canevents:eventwand is not loaded.");

            var stack = new ItemStack(item);
            stack.Attributes.SetString(Items.ItemEventWand.ModeAttr, string.IsNullOrEmpty(mode) ? "spawn" : mode);
            InventoryUtil.Give(sapi, player, stack, "event wand");
            return TextCommandResult.Success(Lang.Get("canevents:wand-given", Items.ItemEventWand.ModeOf(stack)));
        }

        public TextCommandResult DescribeEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string text = args.Parsers[0].GetValue() as string;
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var error)) return error;

            return Wrap(placeConfig.SetDescription(target, text));
        }

        public TextCommandResult RemovePlaceType(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string tag = (string)args.Parsers[0].GetValue();
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var error)) return error;

            return Wrap(placeConfig.RemoveTag(target, tag));
        }

        public TextCommandResult DeleteEventPlace(TextCommandCallingArgs args)
        {
            long id = (long)args.Parsers[0].GetValue();
            return Wrap(placeConfig.DeletePlace(placeManager.FindById(id)));
        }

        public TextCommandResult SetNameEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string val = (string)args.Parsers[0].GetValue();
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var error)) return error;

            return Wrap(placeConfig.Rename(target, val));
        }

        public TextCommandResult Mark1(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            var pos = FeetBlock(player);
            var resolved = targeting.ResolveForCorner(player.PlayerUID, pos);
            if (!resolved.Found) return TextCommandResult.Error(targeting.NoTargetMessage(resolved, pos));
            var target = resolved.Place;

            targeting.SetPendingCorner(player.PlayerUID, pos, target);
            return TextCommandResult.Success(Lang.Get("canevents:mark1-set", target.Name ?? "?", pos.X, pos.Y, pos.Z));
        }

        // Edge spawns are generated only if the place has none, unless the arg is "replace" or "keep".
        public TextCommandResult Mark2(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            var corner2 = FeetBlock(player);
            // Use the place resolved at mark1, not the current target.
            if (!targeting.TryGetPendingCorner(player.PlayerUID, out var corner1, out var eventPlace))
                return TextCommandResult.Error(Lang.Get("canevents:mark1-first"));
            if (eventPlace == null)
            {
                targeting.ClearPendingCorner(player.PlayerUID);
                return TextCommandResult.Error(Lang.Get("canevents:mark1-target-gone"));
            }

            string spawnsArg = (args.Parsers[0].GetValue() as string)?.Trim().ToLowerInvariant();
            bool replaceSpawns;
            switch (spawnsArg)
            {
                case null:
                case "":
                    replaceSpawns = eventPlace.spawnPositions == null || eventPlace.spawnPositions.Count == 0;
                    break;
                case "replace":
                    replaceSpawns = true;
                    break;
                case "keep":
                    replaceSpawns = false;
                    break;
                default:
                    return TextCommandResult.Error(Lang.Get("canevents:mark2-spawns-arg"));
            }

            // Block accessor lets generated spawns snap to the floor.
            var result = placeConfig.SetBounds(eventPlace, corner1, corner2, replaceSpawns, sapi.World.BlockAccessor);
            if (!result.Ok) return TextCommandResult.Error(result.Message);
            targeting.ClearPendingCorner(player.PlayerUID);

            bool unsaved = targeting.InProgress.TryGetValue(player.PlayerUID, out var building) && building == eventPlace;
            return TextCommandResult.Success(unsaved ? result.Message + " " + Lang.Get("canevents:mark2-save-hint") : result.Message);
        }

        public TextCommandResult SaveEventPlace(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (targeting.InProgress.TryGetValue(player.PlayerUID, out var eventPlace))
            {
                eventPlace.Name ??= "Unnamed";
                if (!placeManager.AddPlace(eventPlace))
                    return TextCommandResult.Error(Lang.Get("canevents:place-save-failed"));

                // Reusing the entry made AddPlace reassign the Id and duplicate files on disk.
                targeting.InProgress.Remove(player.PlayerUID);
                targeting.Select(player.PlayerUID, eventPlace.Id);
                return TextCommandResult.Success(Lang.Get("canevents:place-saved") + "\n"
                    + Lang.Get("canevents:place-selected", eventPlace.Name, eventPlace.Id) + "\n"
                    + PlaceConfigService.Describe(eventPlace));
            }
            return TextCommandResult.Error(Lang.Get("canevents:no-place-in-progress"));
        }

        public TextCommandResult AddPlaceType(TextCommandCallingArgs args)
        {
            IServerPlayer player = args.Caller.Player as IServerPlayer;
            string val = (string)args.Parsers[0].GetValue();
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var error)) return error;

            return Wrap(placeConfig.AddTag(target, val));
        }

        public TextCommandResult TplEdit(TextCommandCallingArgs args)
        {
            var p = args.Caller.Player as IServerPlayer;
            var result = canEventManager.TemplateEditor.Start(p, (string)args.Parsers[0].GetValue());
            // /canep edits during the session land on the test place; tplsave copies them into the template.
            var session = canEventManager.TemplateEditor.GetSession(p);
            if (result.ok && session != null) targeting.Select(p.PlayerUID, session.PlaceId);
            return Wrap(result);
        }

        public TextCommandResult TplAddSp(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.AddSpawn(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplClearSp(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.ClearSpawns(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplList(TextCommandCallingArgs args)
        {
            var p = args.Caller.Player as IServerPlayer;
            var session = canEventManager.TemplateEditor.GetSession(p);
            if (session == null) return TextCommandResult.Error("No edit session.");
            var offsets = canEventManager.TemplateEditor.SpawnOffsetsOf(session);
            if (offsets.Count == 0) return TextCommandResult.Success("No spawn offsets in session.");
            var sb = new System.Text.StringBuilder();
            sb.Append("Spawn offsets for '").Append(session.TemplateCode).Append("':\n");
            for (int i = 0; i < offsets.Count; i++)
            {
                var o = offsets[i];
                // 1-based, like every other spawn number admins see.
                sb.AppendFormat("  [{0}] {1},{2},{3}\n", i + 1, o.X, o.Y, o.Z);
            }
            return TextCommandResult.Success(sb.ToString());
        }

        public TextCommandResult TplSave(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Save(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplCancel(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Cancel(args.Caller.Player as IServerPlayer));
        }

        public TextCommandResult TplFinish(TextCommandCallingArgs args)
        {
            return Wrap(canEventManager.TemplateEditor.Finish(args.Caller.Player as IServerPlayer));
        }

        private static BlockPos FeetBlock(IServerPlayer player) => player.Entity.Pos.AsBlockPos.DownCopy();

        // System.Func spelled out: Vintagestory.API.Common declares a Func<> too.
        private TextCommandResult EditAtFeet(TextCommandCallingArgs args,
            System.Func<EventPlace, BlockPos, PlaceEditResult> edit)
        {
            var player = args.Caller.Player as IServerPlayer;
            var pos = FeetBlock(player);
            var resolved = targeting.Resolve(player.PlayerUID, pos);
            if (!resolved.Found) return TextCommandResult.Error(targeting.NoTargetMessage(resolved, pos));

            var result = edit(resolved.Place, pos);
            return result.Ok
                ? TextCommandResult.Success(result.Message + PlaceTargeting.SelectionNote(resolved, pos))
                : TextCommandResult.Error(result.Message);
        }

        private TextCommandResult EditTarget(TextCommandCallingArgs args, System.Func<EventPlace, PlaceEditResult> edit)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var noPlace)) return noPlace;

            return Wrap(edit(target));
        }

        private TextCommandResult DescribeAtFeet(TextCommandCallingArgs args, System.Func<EventPlace, string> describe)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (!TryResolveTarget(player, FeetBlock(player), out var target, out var noPlace)) return noPlace;

            return TextCommandResult.Success(describe(target));
        }

        private bool TryResolveTarget(IServerPlayer player, BlockPos pos,
            out EventPlace target, out TextCommandResult error)
        {
            var resolved = targeting.Resolve(player.PlayerUID, pos);
            target = resolved.Place;
            error = resolved.Found ? null : TextCommandResult.Error(targeting.NoTargetMessage(resolved, pos));
            return resolved.Found;
        }

        // No index appends after the last checkpoint.
        public TextCommandResult ParkourSetCheckpoint(TextCommandCallingArgs args)
        {
            int index = (int)args.Parsers[0].GetValue();
            return EditCheckpointFloor(args, (target, pos) => index < 0
                ? placeConfig.AddParkourCheckpoint(target, pos, isFinish: false)
                : placeConfig.SetParkourCheckpoint(target, pos, index));
        }

        public TextCommandResult ParkourSetFinish(TextCommandCallingArgs args)
            => EditCheckpointFloor(args, (target, pos) => placeConfig.AddParkourCheckpoint(target, pos, isFinish: true));

        public TextCommandResult ParkourEditCheckpoint(TextCommandCallingArgs args)
        {
            int index = (int)args.Parsers[0].GetValue();
            return EditAtFeet(args, (target, pos) => placeConfig.SetParkourCheckpointIndex(target, pos, index));
        }

        public TextCommandResult ParkourRemoveCheckpoint(TextCommandCallingArgs args)
            => EditAtFeet(args, (target, pos) => placeConfig.RemoveParkourCheckpoint(target, pos));

        public TextCommandResult ParkourSetLives(TextCommandCallingArgs args)
        {
            int lives = (int)args.Parsers[0].GetValue();
            return EditTarget(args, target => placeConfig.SetParkourLives(target, lives));
        }

        // No arg uses the caller's Y, "clear" resets to default.
        public TextCommandResult ParkourSetFallY(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            string arg = (args.Parsers[0].GetValue() as string)?.Trim().ToLowerInvariant();

            int? fallY;
            if (string.IsNullOrEmpty(arg)) fallY = player.Entity.Pos.AsBlockPos.Y;
            else if (arg == "clear") fallY = null;
            else if (int.TryParse(arg, out int y)) fallY = y;
            else return TextCommandResult.Error(Lang.Get("canevents:parkour-fally-arg"));

            return EditTarget(args, target => placeConfig.SetParkourFallY(target, fallY));
        }

        public TextCommandResult ParkourListCheckpoints(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeParkourCheckpoints(target));

        // Checkpoint BE is spawned on the existing block at match start, so air is refused.
        private TextCommandResult EditCheckpointFloor(TextCommandCallingArgs args,
            System.Func<EventPlace, BlockPos, PlaceEditResult> edit)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (sapi.World.BlockAccessor.GetBlockId(FeetBlock(player)) == 0)
                return TextCommandResult.Error(Lang.Get("canevents:parkour-no-floor"));
            return EditAtFeet(args, edit);
        }

        public TextCommandResult HgZoneSet(TextCommandCallingArgs args)
        {
            float radius = Convert.ToSingle(args.Parsers[0].GetValue());
            return EditAtFeet(args, (target, pos) => placeConfig.SetHungerGamesZone(target, pos, radius));
        }

        public TextCommandResult HgLootAdd(TextCommandCallingArgs args)
        {
            string tier = args.Parsers[0].GetValue() as string;
            return EditAtFeet(args, (target, pos) => placeConfig.AddHungerGamesLoot(target, pos, tier));
        }

        public TextCommandResult HgLootClear(TextCommandCallingArgs args)
            => EditTarget(args, target => placeConfig.ClearHungerGamesLoot(target));

        public TextCommandResult HgLootList(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeHungerGamesLoot(target));

        public TextCommandResult KothHillSet(TextCommandCallingArgs args)
        {
            float radius = Convert.ToSingle(args.Parsers[0].GetValue());

            // Omitted height is null; the service treats <= 0 as default.
            var rawHeight = args.Parsers[1].GetValue();
            float height = rawHeight == null ? 0f : Convert.ToSingle(rawHeight);

            return EditAtFeet(args, (target, pos) => placeConfig.SetKothHill(target, pos, radius, height));
        }

        public TextCommandResult WaitingRoomSet(TextCommandCallingArgs args)
            => EditAtFeet(args, (target, pos) => placeConfig.SetWaitingRoom(target, pos));

        // Keyed by team index, so re-running moves the post instead of adding another.
        public TextCommandResult CtfPostSet(TextCommandCallingArgs args)
        {
            int team = (int)args.Parsers[0].GetValue();
            return EditAtFeet(args, (target, pos) => placeConfig.SetCtfPost(target, pos, team));
        }

        public TextCommandResult CtfPostList(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeCtfPosts(target));

        public TextCommandResult CtfPostClear(TextCommandCallingArgs args)
            => EditTarget(args, target => placeConfig.ClearCtfPosts(target));

        public TextCommandResult MmPickupAdd(TextCommandCallingArgs args)
            => EditAtFeet(args, (target, pos) => placeConfig.AddMurderMysteryPickup(target, pos));

        public TextCommandResult MmPickupClear(TextCommandCallingArgs args)
            => EditTarget(args, target => placeConfig.ClearMurderMysteryPickups(target));

        public TextCommandResult MmPickupList(TextCommandCallingArgs args)
            => DescribeAtFeet(args, target => placeConfig.DescribeMurderMysteryPickups(target));

        public TextCommandResult MmPickupRemove(TextCommandCallingArgs args)
        {
            int index = (int)args.Parsers[0].GetValue();
            return EditTarget(args, target => placeConfig.RemoveMurderMysteryPickup(target, index));
        }
    }
}
