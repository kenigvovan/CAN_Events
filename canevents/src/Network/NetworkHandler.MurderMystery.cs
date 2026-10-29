using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Murder Mystery place setup: the spots where the mode's weapon token can appear. Like the other
    // per-behavior setup handlers the place is addressed by id (unlike /canep mmpickup, which derives
    // it from the caller's position and picks the wrong one of two overlapping arenas) and the
    // coordinates come from the admin's feet. The edits themselves live in PlaceConfigService,
    // shared with those commands.
    public partial class ServerNetworkHandler
    {
        private void OnAddMmPickup(IServerPlayer player, AddMmPickupMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.AddMurderMysteryPickup(p, FeetBlock(player)));

        private void OnClearMmPickups(IServerPlayer player, ClearMmPickupsMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.ClearMurderMysteryPickups(p));

        // Index-addressed: refresh even on rejection, so a panel working off a stale list is corrected.
        private void OnRemoveMmPickup(IServerPlayer player, RemoveMmPickupMsg msg)
            => PlaceEdit(player, msg.PlaceId, p => PlaceConfig.RemoveMurderMysteryPickup(p, msg.Index),
                refreshOnFailure: true);

        // The settings editor: damage, weapon-spawn timers and the reward, read from and written back
        // to the event JSON. Only MmConfigKeys.Editable travels either way.

        private void OnRequestMmConfig(IServerPlayer player, RequestMmConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            SendMmConfig(player, place, msg.EventCode);
        }

        private void OnSaveMmConfig(IServerPlayer player, SaveMmConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;

            JObject settings;
            try
            {
                settings = string.IsNullOrWhiteSpace(msg.AttributesJson)
                    ? null
                    : JObject.Parse(msg.AttributesJson);
            }
            catch (Exception e)
            {
                ReplyFail(player, Lang.Get("canevents:mmcfg-unreadable", e.Message));
                return;
            }

            if (settings == null)
            {
                Reply(player, false, Lang.Get("canevents:mmcfg-nothing"), msg.PlaceId);
                return;
            }

            // Only MmConfigKeys are touched: loadouts and outfits share that object and must survive.
            var (ok, message) = eventManager.EditEventBehaviorAttributes(msg.EventCode, BehaviorNames.MurderMystery,
                attr => MmConfigKeys.CopyEditable(attr, settings), "canevents:mmcfg-saved");
            Reply(player, ok, message, msg.PlaceId);

            // Send the saved state back rather than trusting the panel's copy: the event may have
            // rejected part of it, and the admin should see what is actually stored.
            if (ok && TryResolvePlace(player, msg.PlaceId, out var place))
                SendMmConfig(player, place, msg.EventCode);
        }

        private void SendMmConfig(IServerPlayer player, EventPlace place, string eventCode)
        {
            var candidates = eventManager.EventsForPlace<MurderMysteryEB>(place);
            var ev = eventManager.ResolveEventForPlace<MurderMysteryEB>(place, eventCode);
            var attrs = ev?.GetBehavior<MurderMysteryEB>()?.Attributes;

            adminChannel.SendPacket(new MmConfigMsg
            {
                PlaceId = place.Id,
                EventCode = ev?.Code,
                MmEventCodes = candidates.Select(e => e.Code).ToArray(),
                AttributesJson = EditableMmAttributes(attrs).ToString()
            }, player);
        }

        // Editable settings only: the panel must not hold, and send back, the loadout sets and outfits
        // that share this object.
        private static JObject EditableMmAttributes(Vintagestory.API.Datastructures.JsonObject attrs)
        {
            var result = new JObject();
            if (!(attrs?.Token is JObject src)) return result;

            foreach (var key in MmConfigKeys.Editable)
                if (src[key] != null) result[key] = src[key];
            return result;
        }
    }
}
