using System.Linq;
using System.Text;
using canevents.src.EventManagers;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.Items
{
    // Right click marks the block for the current mode, sneak + right click removes. Mode set via /canep wand.
    public class ItemEventWand : Item
    {
        public const string ModeAttr = "canevents:wandmode";

        public static readonly string[] Modes = { "spawn", "checkpoint", "finish", "hill", "ctf", "loot", "pickup", "info" };

        public static string ModeOf(ItemStack stack)
        {
            string mode = stack?.Attributes?.GetString(ModeAttr);
            return Modes.Contains(mode) ? mode : "info";
        }

        public override string GetHeldItemName(ItemStack itemStack)
            => base.GetHeldItemName(itemStack) + " (" + ModeOf(itemStack) + ")";

        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            dsc.AppendLine(Lang.Get("canevents:wand-help", ModeOf(inSlot.Itemstack), string.Join(", ", Modes)));
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel,
            EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            // Don't open the chest or door being marked.
            handling = EnumHandHandling.PreventDefault;
            if (!firstEvent || blockSel == null) return;
            if (api.Side != EnumAppSide.Server) return;

            var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
            if (player == null) return;
            if (!player.HasPrivilege(Privilege.controlserver))
            {
                Tell(player, false, Lang.Get("canevents:admin-no-privilege"));
                return;
            }

            var mod = api.ModLoader.GetModSystem<canevents>();
            var manager = mod?.CANEventManager;
            var targeting = mod?.PlaceTargeting;
            if (manager == null || targeting == null) return;

            var pos = blockSel.Position.Copy();
            var resolved = targeting.Resolve(player.PlayerUID, pos);
            if (!resolved.Found)
            {
                Tell(player, false, resolved.Ambiguous != null
                    ? targeting.NoTargetMessage(resolved, pos)
                    : Lang.Get("canevents:wand-no-place", pos.X, pos.Y, pos.Z));
                return;
            }

            string mode = ModeOf(slot.Itemstack);
            bool remove = byEntity.Controls.ShiftKey;
            var result = Apply(manager.placeConfig, mode, resolved.Place, pos, remove);
            // Spawn edits already warn about bounds; info writes nothing.
            string note = result.Ok && !remove && mode != "info" && mode != "spawn"
                ? EventPlacesManagers.PlaceTargeting.SelectionNote(resolved, pos)
                : "";
            Tell(player, result.Ok, result.Message + note);
        }

        private static PlaceEditResult Apply(PlaceConfigService config, string mode, EventPlace place, BlockPos pos, bool remove)
        {
            switch (mode)
            {
                case "spawn":
                    return remove ? config.RemoveSpawnNear(place, pos) : config.AddSpawn(place, pos.UpCopy());
                case "checkpoint":
                case "finish":
                    return remove ? config.RemoveParkourCheckpoint(place, pos)
                                  : config.AddParkourCheckpoint(place, pos, isFinish: mode == "finish");
                case "hill":
                {
                    // Keep existing radius/height, default 5x5.
                    float radius = 5f, height = 5f;
                    if (place.TryGetBehaviorData(BehaviorKeys.KingOfTheHill, out JObject koth))
                    {
                        if (koth["Radius"] != null) radius = koth.Value<float>("Radius");
                        if (koth["Height"] != null) height = koth.Value<float>("Height");
                    }
                    return config.SetKothHill(place, pos, radius, height);
                }
                case "ctf":
                    return remove ? config.RemoveCtfPostAt(place, pos) : config.SetNextCtfPost(place, pos);
                case "loot":
                    return remove ? config.RemoveHungerGamesLootAt(place, pos) : config.AddHungerGamesLoot(place, pos, null);
                case "pickup":
                    return remove ? config.RemoveMurderMysteryPickupAt(place, pos) : config.AddMurderMysteryPickup(place, pos);
                default:
                    return PlaceEditResult.Success(PlaceConfigService.Describe(place));
            }
        }

        private static void Tell(IServerPlayer player, bool ok, string message)
            => player.SendMessage(GlobalConstants.GeneralChatGroup, message,
                ok ? EnumChatType.CommandSuccess : EnumChatType.CommandError);
    }
}
