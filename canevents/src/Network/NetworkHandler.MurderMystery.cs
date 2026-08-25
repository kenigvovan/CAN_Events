using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventManagers.events;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json.Linq;
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

        private void OnListMmPickups(IServerPlayer player, ListMmPickupsMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            ReplyOk(player, PlaceConfig.DescribeMurderMysteryPickups(place), place.Id);
        }

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
                ReplyFail(player, "Murder Mystery settings could not be read: " + e.Message);
                return;
            }

            var (ok, message) = eventManager.UpdateEventMmConfig(msg.EventCode, settings);
            Reply(player, ok, message, msg.PlaceId);

            // Send the saved state back rather than trusting the panel's copy: the event may have
            // rejected part of it, and the admin should see what is actually stored.
            if (ok && TryResolvePlace(player, msg.PlaceId, out var place))
                SendMmConfig(player, place, msg.EventCode);
        }

        private void SendMmConfig(IServerPlayer player, EventPlace place, string eventCode)
        {
            var candidates = MmEventsForPlace(place);
            var ev = ResolveMmEvent(candidates, eventCode);
            var attrs = ev?.GetBehavior<MurderMysteryEB>()?.Attributes;

            serverChannel.SendPacket(new MmConfigMsg
            {
                PlaceId = place.Id,
                EventCode = ev?.Code,
                MmEventCodes = candidates.Select(e => e.Code).ToArray(),
                AttributesJson = EditableMmAttributes(attrs).ToString()
            }, player);
        }

        /// <summary>The editable settings only, so the panel never holds - and cannot send back - the
        /// loadout sets and outfits that share this object.</summary>
        private static JObject EditableMmAttributes(Vintagestory.API.Datastructures.JsonObject attrs)
        {
            var result = new JObject();
            if (!(attrs?.Token is JObject src)) return result;

            foreach (var key in MmConfigKeys.Editable)
                if (src[key] != null) result[key] = src[key];
            return result;
        }

        /// <summary>
        /// Every Murder Mystery event that could run on this place, ordered by code so the panel's
        /// list and default pick are stable across restarts (event files load in directory order).
        /// Falls back to all of them when none match the place's tags.
        /// </summary>
        private List<CANEvent> MmEventsForPlace(EventPlace place)
        {
            var events = eventManager.Events
                .Where(e => e.GetBehavior<MurderMysteryEB>() != null)
                .OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (place?.placeTypes == null) return events;

            var matching = events.Where(e => e.EventTypes != null && e.EventTypes.Count > 0
                && e.EventTypes.All(t => place.placeTypes.Contains(t))).ToList();
            return matching.Count > 0 ? matching : events;
        }

        /// <summary>The variant the admin named when it is a real MM event, else the first applicable
        /// one: every variant (plain / bots) keeps its own settings.</summary>
        private CANEvent ResolveMmEvent(List<CANEvent> candidates, string preferredCode)
        {
            if (!string.IsNullOrEmpty(preferredCode))
            {
                var picked = candidates.FirstOrDefault(
                    e => e.Code.Equals(preferredCode, StringComparison.OrdinalIgnoreCase));
                if (picked == null)
                {
                    var byCode = eventManager.FindEventByCode(preferredCode);
                    if (byCode?.GetBehavior<MurderMysteryEB>() != null) picked = byCode;
                }
                if (picked != null) return picked;
            }
            return candidates.FirstOrDefault();
        }
    }
}
