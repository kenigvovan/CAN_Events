using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.events;
using canevents.src.EventManagers.eventBehavior.abstracts;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side event-list push: the player-facing queue/running list and the admin
    // enable/disable list.
    public partial class ServerNetworkHandler
    {
        private void OnRequestAdminEventList(IServerPlayer player, RequestAdminEventListMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendAdminEventListTo(player);
        }

        private void SendAdminEventListTo(IServerPlayer player)
        {
            var places = eventManager.eventPlacesManager.EventPlaces;
            var entries = eventManager.Events
                .OrderBy(ev => ev.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(ev => ev.Code, StringComparer.OrdinalIgnoreCase)
                .Select(ev =>
                {
                    var capacity = ev.Behaviors.OfType<IPartyCapacityProvider>().FirstOrDefault();
                    bool enabled = eventManager.IsEventEnabled(ev.Code);
                    var (readiness, detail) = ComputeReadiness(ev, enabled, places);
                    return new AdminEventListEntryMsg
                    {
                        Code = ev.Code,
                        Category = ev.Category ?? "",
                        Enabled = enabled,
                        EventPlaceTypes = ev.EventTypes?.ToArray() ?? Array.Empty<string>(),
                        MinPlayers = capacity?.GetMinPlayers() ?? 0,
                        MaxPlayers = capacity?.GetMaxPlayers() ?? 0,
                        Rated = ev.Rated,
                        Readiness = readiness,
                        ReadinessDetail = detail,
                        RequiredSpawns = ResolveRequiredSpawns(ev)
                    };
                })
                .ToArray();
            serverChannel.SendPacket(new AdminEventListMsg { Events = entries }, player);
        }

        /// <summary>Can this event start right now, and if not, what is missing? The success path
        /// must stay identical to DefaultEventPlacesManager.TryFindEventPlace.</summary>
        private static (EventReadiness, string) ComputeReadiness(
            CANEvent ev, bool eventEnabled, IEnumerable<EventPlacesManagers.Places.EventPlace> places)
        {
            var required = ev.EventTypes?.ToArray() ?? Array.Empty<string>();
            if (!eventEnabled) return (EventReadiness.EventDisabled, "event is turned off");

            var matching = places
                .Where(p => p.placeTypes != null && required.All(t => p.placeTypes.Contains(t)))
                .ToList();

            if (matching.Count == 0)
                return (EventReadiness.NoPlaceWithTags, string.Join("+", required));

            var usable = matching.Where(p => p.Enabled && p.spawnPositions != null && p.spawnPositions.Count > 0).ToList();

            var free = usable.FirstOrDefault(p => !p.Occupied);
            if (free != null) return (EventReadiness.Ready, free.Name ?? ("#" + free.Id));

            if (usable.Count > 0)
                return (EventReadiness.AllBusy, usable.Count + " matching place(s) in use");

            var noSpawns = matching.FirstOrDefault(p => p.Enabled && (p.spawnPositions == null || p.spawnPositions.Count == 0));
            if (noSpawns != null)
                return (EventReadiness.NoSpawns, (noSpawns.Name ?? ("#" + noSpawns.Id)) + " has no spawn points");

            return (EventReadiness.AllDisabled, matching.Count + " matching place(s), all disabled");
        }

        /// <summary>Spawn points a full match wants, one per team.</summary>
        private static int ResolveRequiredSpawns(CANEvent ev)
        {
            var assembling = ev.Behaviors.OfType<PartyAssemblingEB>().FirstOrDefault();
            return assembling != null ? Math.Max(1, assembling.MaxPartiesCount) : 1;
        }

        private void OnToggleEventEnabled(IServerPlayer player, ToggleEventEnabledMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (string.IsNullOrEmpty(msg.Code)) return;

            if (eventManager.FindEventByCode(msg.Code) == null)
            {
                ReplyFail(player, Lang.Get("canevents:event-not-found", msg.Code));
                return;
            }

            bool wasEnabled = eventManager.IsEventEnabled(msg.Code);
            eventManager.SetEventEnabled(msg.Code, !wasEnabled);
            ReplyOk(player, Lang.Get(wasEnabled ? "canevents:event-disabled" : "canevents:event-enabled", msg.Code));
            SendAdminEventListTo(player);
            BroadcastEventList();
        }

        private void OnEventListRequest(IServerPlayer player, RequestEventListMsg msg)
        {
            serverChannel.SendPacket(new EventListMsg { Events = BuildEventListFor(player) }, player);
        }

        private EventListEntryMsg[] BuildEventListFor(IServerPlayer player)
        {
            var entries = new List<EventListEntryMsg>();
            string playerUid = player.PlayerUID;
            var queueSnapshot = eventManager.SnapshotRegistrations();

            foreach (var ev in eventManager.Events)
            {
                // No queue entry for admin-disabled events, but still surface any running match
                // below so participants can find their HUD.
                bool isDisabled = !eventManager.IsEventEnabled(ev.Code);
                int currentPlayers = 0;
                bool isQueued = false;
                // How long this player has been waiting, so the card can say it. Taken from their own
                // registration rather than the oldest in the queue: "you have waited 12 minutes" is
                // what decides whether to keep waiting, and someone else's wait says nothing about it.
                double queuedSeconds = 0;

                if (queueSnapshot.TryGetValue(ev, out var regs))
                {
                    foreach (var r in regs)
                    {
                        currentPlayers += r.PlayersCount;
                        if (!r.PlayersGuids.Contains(playerUid)) continue;

                        isQueued = true;
                        queuedSeconds = Math.Max(0,
                            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - r.TimeStampCreated) / 1000.0);
                    }
                }

                var capacity = ev.Behaviors.OfType<IPartyCapacityProvider>().FirstOrDefault();
                int maxPlayers = capacity?.GetMaxPlayers() ?? 0;
                int minPlayers = capacity?.GetMinPlayers() ?? 0;

                string[] loadoutSetNames = null;
                string[] loadoutSetDescriptions = null;
                string[] loadoutSetItemCodes = null;
                var loadoutProvider = ev.GetBehavior<ILoadoutProvider>();
                if (loadoutProvider != null)
                {
                    var lm = eventManager.LoadoutManager;
                    var names = loadoutProvider.GetLoadoutSetNames(ev.Code, lm);
                    if (names.Length > 1)
                    {
                        loadoutSetNames = names;
                        loadoutSetDescriptions = loadoutProvider.GetLoadoutSetDescriptions(ev.Code, lm);
                        loadoutSetItemCodes = loadoutProvider.GetLoadoutSetItemCodes(ev.Code, lm);
                    }
                }

                string category = string.IsNullOrWhiteSpace(ev.Category) ? "Other" : ev.Category;

                if (!isDisabled)
                {
                    // Entry fee + payout preview so the player sees the cost and the prizes before joining.
                    string entryFeeItems = ItemSetUtil.Pack(ev.EntryFee?.Items);
                    int rake = ev.EntryFee != null ? Math.Clamp(ev.EntryFee.RakePercent, 0, 100) : 0;

                    string winnerItems = null, participationItems = null;
                    RewardTierMsg[] placement = null;
                    var preview = eventManager.RewardDistributor?.GetRewardPreview(ev);
                    if (preview != null)
                    {
                        winnerItems = ItemSetUtil.Pack(preview.WinnerItems);
                        participationItems = ItemSetUtil.Pack(preview.ParticipationItems);
                        if (preview.Placement != null && preview.Placement.Count > 0)
                            placement = preview.Placement
                                .Select(t => new RewardTierMsg { Place = t.place, Items = ItemSetUtil.Pack(t.items) })
                                .ToArray();
                    }

                    // Per-viewer reward rate-cap status for this event (own bucket when it has a
                    // separate cap, else the shared one); the distributor owns the policy.
                    var (capActive, rewardsRemaining, secondsToNext) =
                        eventManager.RewardDistributor?.GetRewardCapStatus(playerUid, ev) ?? (false, 0, 0.0);

                    entries.Add(new EventListEntryMsg
                    {
                        GameModeCode = ev.Code,
                        Category = category,
                        Rated = ev.Rated,
                        ArenaName = "",
                        CurrentPlayers = currentPlayers,
                        MaxPlayers = maxPlayers,
                        MinPlayers = minPlayers,
                        State = 0,
                        EventId = 0,
                        IsQueued = isQueued,
                        QueuedSeconds = queuedSeconds,
                        LoadoutSets = LoadoutSetInfoMsg.FromArrays(loadoutSetNames, loadoutSetItemCodes, loadoutSetDescriptions),
                        EntryFeeItems = entryFeeItems,
                        EntryFeeRakePercent = rake,
                        WinnerItems = winnerItems,
                        PlacementRewards = placement,
                        ParticipationItems = participationItems,
                        RewardCapActive = capActive,
                        RewardsRemaining = rewardsRemaining,
                        SecondsToNextReward = secondsToNext
                    });
                }

                foreach (var re in eventManager.RunningEventsSnapshot())
                {
                    if (re.Event.Code == ev.Code && re.EventState != EventManagers.EventState.Finished)
                    {
                        int cur = re.EventState == EventManagers.EventState.SearchingForLocation
                            ? re.ParticipantsBefore.Count
                            : re.ParticipantsStillInEvent.Count;
                        entries.Add(new EventListEntryMsg
                        {
                            GameModeCode = ev.Code,
                            Category = category,
                            Rated = ev.Rated,
                            ArenaName = re.Place?.Name ?? "",
                            CurrentPlayers = cur,
                            MaxPlayers = re.ParticipantsBefore.Count,
                            State = re.EventState,
                            EventId = re.Id
                        });
                    }
                }
            }

            return entries.ToArray();
        }

        private bool broadcastPending = false;

        public void BroadcastEventList()
        {
            if (broadcastPending) return;
            broadcastPending = true;
            sapi.Event.RegisterCallback(_ =>
            {
                broadcastPending = false;
                foreach (var p in sapi.World.AllOnlinePlayers)
                {
                    if (p is IServerPlayer sp)
                    {
                        serverChannel.SendPacket(new EventListMsg { Events = BuildEventListFor(sp) }, sp);
                    }
                }
            }, 300);
        }
    }
}
