using System;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server side of the Hunger Games setup panel. The work - layout generation, the zone, the
    // checklist, the config editor's save and the in-world preview - is HungerGamesSetupService's;
    // this file resolves the place, relays the result and copies the setup picture onto the wire.
    public partial class ServerNetworkHandler
    {
        private HungerGamesSetupService HgSetup => eventManager.HungerGamesSetup;

        private void OnRequestHgSetup(IServerPlayer player, RequestHgSetupMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;
            SendHgSetup(player, place, msg.EventCode);
        }

        // Place setup: zone and loot spots. Addressed by place id, coordinates from the admin's feet;
        // the edits live in PlaceConfigService, shared with the /canep commands.

        // A HG place edit is the shared PlaceEdit flow plus this mode's own panel refresh.
        private void HgEdit(IServerPlayer player, long placeId, Func<EventPlace, PlaceEditResult> edit,
            bool refreshOnFailure = false)
            => PlaceEdit(player, placeId, edit, place => SendHgSetup(player, place), refreshOnFailure);

        private void OnSetHgZone(IServerPlayer player, SetHgZoneMsg msg)
            => HgEdit(player, msg.PlaceId, p => HgSetup.SetZone(p, FeetBlock(player), msg.Radius,
                keepCentre: msg.Mode == HgZoneMode.KeepCentre,
                fitBounds: msg.Mode == HgZoneMode.FitBounds));

        private void OnAddHgLoot(IServerPlayer player, AddHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.AddHungerGamesLoot(p, FeetBlock(player), msg.Tier));

        private void OnClearHgLoot(IServerPlayer player, ClearHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.ClearHungerGamesLoot(p));

        // Index-addressed: refresh even on rejection, see PlaceEdit.
        private void OnRemoveHgLoot(IServerPlayer player, RemoveHgLootMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.RemoveHungerGamesLoot(p, msg.Index), refreshOnFailure: true);

        private void OnSetHgLootTier(IServerPlayer player, SetHgLootTierMsg msg)
            => HgEdit(player, msg.PlaceId, p => PlaceConfig.SetHungerGamesLootTier(p, msg.Index, msg.Tier), refreshOnFailure: true);

        // One-click arena: ring of spawns + zone + tiered loot, all from the admin's feet.
        private void OnGenerateHgLayout(IServerPlayer player, GenerateHgLayoutMsg msg)
            => HgEdit(player, msg.PlaceId, p => HgSetup.GenerateLayout(p, player.Entity.Pos.AsBlockPos,
                msg.Radius, msg.SnapToTerrain, msg.SpawnCount));

        // On top of the spot: the chest replaces the spot block itself.
        private void OnTeleportToHgLootSpot(IServerPlayer player, TeleportToHgLootSpotMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            if (!HungerGamesSetupService.TryLootSpot(place, msg.SpotIndex, out var spot))
            {
                ReplyFail(player, Lang.Get("canevents:hg-no-loot-spot", place.Name, msg.SpotIndex + 1));
                return;
            }

            EventTeleport.Send(sapi, player, spot.X + 0.5, spot.Y + 1, spot.Z + 0.5, spot.dimension,
                "admin go-to '" + place.Name + "' loot spot");
            ReplyOk(player, Lang.Get("canevents:hg-teleported-loot-spot", place.Name, msg.SpotIndex + 1), place.Id);
        }

        private void OnHgPreview(IServerPlayer player, HgPreviewMsg msg)
        {
            if (!RequireAdmin(player)) return;

            if (msg.Enabled)
            {
                if (!TryResolvePlace(player, msg.PlaceId, out _)) return;
                HgSetup.StartPreview(player.PlayerUID, msg.PlaceId);
            }
            else
            {
                HgSetup.StopPreview(player.PlayerUID);
            }
        }

        private void OnSaveHgConfig(IServerPlayer player, SaveHgConfigMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (msg == null) return;

            var place = eventManager.eventPlacesManager.FindById(msg.PlaceId);
            var (ok, message) = HgSetup.SaveConfig(place, msg.EventCode, msg.LootTablesJson, msg.ZonePhasesJson,
                msg.ApplyToAllHgEvents, msg.ScoreboardVisibility);

            Reply(player, ok, message);
            if (!ok) return;

            // Reflect the saved config back into the panel (tiers/preview/editor buffers).
            if (place != null) SendHgSetup(player, place, msg.EventCode);
            BroadcastEventList(); // the browser preview reads event config too
        }

        // preferredCode is the variant picked in the panel; null falls back to the place's default event.
        private void SendHgSetup(IServerPlayer player, EventPlace place, string preferredCode = null)
        {
            var s = HgSetup.Describe(place, preferredCode);
            adminChannel.SendPacket(new HgSetupMsg
            {
                PlaceId = s.PlaceId,
                EventCode = s.EventCode,
                HgEventCodes = s.EventCodes,
                Overview = s.Overview,
                Tiers = s.Tiers,
                TierSummaries = s.TierSummaries,
                SpotX = s.SpotX,
                SpotY = s.SpotY,
                SpotZ = s.SpotZ,
                SpotTier = s.SpotTier,
                HasBounds = s.HasBounds,
                SpawnCount = s.SpawnCount,
                RequiredSpawns = s.RequiredSpawns,
                ZoneSet = s.ZoneSet,
                ZoneRadius = s.ZoneRadius,
                UsesCornucopiaTier = s.UsesCornucopiaTier,
                Warnings = s.Warnings,
                LootTablesJson = s.LootTablesJson,
                ZonePhasesJson = s.ZonePhasesJson,
                ScoreboardVisibility = s.ScoreboardVisibility
            }, player);
        }
    }
}
