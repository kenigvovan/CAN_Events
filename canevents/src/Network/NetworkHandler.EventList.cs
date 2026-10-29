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
    public partial class ServerNetworkHandler
    {
        private void OnRequestAdminEventList(IServerPlayer player, RequestAdminEventListMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendAdminEventListTo(player);
        }

        // After a reload changes the set of events.
        public void BroadcastAdminEventList()
        {
            foreach (var p in sapi.World.AllOnlinePlayers)
                if (p is IServerPlayer sp && RequireAdmin(sp)) SendAdminEventListTo(sp);
        }

        private void SendAdminEventListTo(IServerPlayer player)
        {
            var entries = eventManager.Events
                .OrderBy(ev => ev.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(ev => ev.Code, StringComparer.OrdinalIgnoreCase)
                .Select(ev =>
                {
                    var capacity = ev.Behaviors.OfType<IPartyCapacityProvider>().FirstOrDefault();
                    bool enabled = eventManager.IsEventEnabled(ev.Code);
                    var (readiness, detail) = ComputeReadiness(ev, enabled);
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
                        RequiredSpawns = ResolveRequiredSpawns(ev),
                        NeedsWaitingRoom = ev.GetBehavior<EventManagers.eventBehavior.WaitingRoomEB>() != null,
                        IsOpenMatch = CANEventManager.OpenModeOf(ev) != null,
                        Teams = CANEventManager.TeamCountOf(ev)
                    };
                })
                .ToArray();
            adminChannel.SendPacket(new AdminEventListMsg { Events = entries }, player);
        }

        // Maps PlaceAvailability's verdict onto the panel enum.
        private (EventReadiness, string) ComputeReadiness(CANEvent ev, bool eventEnabled)
        {
            if (!eventEnabled) return (EventReadiness.EventDisabled, "event is turned off");

            var report = eventManager.AvailabilityOf(ev);
            if (report.Tagged == 0) return (EventReadiness.NoPlaceWithTags, report.TagText);

            if (report.AnyFree)
                return (EventReadiness.Ready, report.FirstFree.Name ?? ("#" + report.FirstFree.Id));
            if (report.Occupied + report.PendingRestore > 0) return (EventReadiness.AllBusy, report.Summary);
            if (report.NoSpawns > 0) return (EventReadiness.NoSpawns, report.Summary);
            if (report.IncompleteSetup > 0) return (EventReadiness.IncompleteSetup, report.Summary);
            return (EventReadiness.AllDisabled, report.Summary);
        }

        // One per team.
        private static int ResolveRequiredSpawns(CANEvent ev)
        {
            // A queue-less event without an open mode (rare) needs just one spawn.
            if (CANEventManager.OpenModeOf(ev) == null && ev.AssemblingEB == null) return 1;
            return CANEventManager.TeamCountOf(ev);
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
                // Disabled events get no queue entry, but running matches are still listed.
                bool isDisabled = !eventManager.IsEventEnabled(ev.Code);
                int currentPlayers = 0;
                bool isQueued = false;
                // This player's own wait.
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

                // Empty for hidden-role modes.
                TryGetSelectableSets(ev, out var loadoutSetNames, out var loadoutSetItemCodes,
                    out var loadoutSetDescriptions);

                string category = string.IsNullOrWhiteSpace(ev.Category) ? "Other" : ev.Category;

                if (!isDisabled)
                {
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
                        SecondsToNextReward = secondsToNext,
                        HasShop = ev.Behaviors.OfType<EventManagers.eventBehavior.MatchShopEB>().Any(s => s.Enabled && s.ShowPreview)
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
