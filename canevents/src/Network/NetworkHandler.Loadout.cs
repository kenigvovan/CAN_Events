using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side loadout handling: per-player defaults, match/queue-time pick popups, and the
    // admin loadout editor (list/save/delete/import).
    public partial class ServerNetworkHandler
    {
        private void OnRequestLoadoutDefaults(IServerPlayer player, RequestLoadoutDefaultsMsg msg)
        {
            SendLoadoutDefaultsTo(player);
        }

        private void SendLoadoutDefaultsTo(IServerPlayer player)
        {
            var prefs = eventManager.LoadoutPreferences;
            var list = prefs?.GetAllForPlayer(player.PlayerUID) ?? new List<(string, string)>();
            var entries = list.Select(t => new LoadoutDefaultEntryMsg { EventCode = t.eventCode, SetName = t.setName }).ToArray();
            serverChannel.SendPacket(new LoadoutDefaultsListMsg { Entries = entries }, player);
        }

        // Loadout pick at match start

        // EventId -> set of player UIDs we are still waiting on.
        private readonly Dictionary<long, HashSet<string>> _pendingPicks = new Dictionary<long, HashSet<string>>();

        // Available loadout sets (names/item-codes/descriptions) from a loadout-aware behavior.
        private void GetLoadoutSets(ILoadoutProvider fight, string code, out string[] names, out string[] items, out string[] descs)
        {
            var lm = eventManager.LoadoutManager;
            names = fight.GetLoadoutSetNames(code, lm);
            items = fight.GetLoadoutSetItemCodes(code, lm);
            descs = fight.GetLoadoutSetDescriptions(code, lm);
        }

        /// <summary>
        /// The sets this event may show a player, or false when there is nothing to offer: no
        /// loadout-aware behavior, only one set (nothing to choose), or a mode that assigns the sets
        /// itself. The last case is the one that matters for secrecy - in a hidden-role mode the set
        /// names ARE the roles, so they must not reach a client anywhere. Every place that would
        /// show or ask goes through here, so that stays true in all of them.
        /// </summary>
        public bool TryGetSelectableSets(EventManagers.events.CANEvent ev,
            out string[] names, out string[] items, out string[] descs)
            => TryGetSelectableSets(ev?.Behaviors, ev?.Code, out names, out items, out descs);

        /// <summary>As above, from a running match's own (cloned) behaviors.</summary>
        public bool TryGetSelectableSets(IEnumerable<EventManagers.events.CANEventBehavior> behaviors,
            string eventCode, out string[] names, out string[] items, out string[] descs)
        {
            names = items = descs = null;
            if (behaviors == null || eventCode == null) return false;

            var fight = behaviors.OfType<ILoadoutProvider>().FirstOrDefault();
            if (fight == null || !fight.OffersPlayerChoice) return false;

            GetLoadoutSets(fight, eventCode, out names, out items, out descs);
            if (names == null || names.Length <= 1)
            {
                names = items = descs = null;
                return false;
            }
            return true;
        }

        /// <summary>At queue-time, pops a loadout pick on every non-leader party member. Responses are
        /// stored on the Registration and later copied into RunningEvent.PlayerLoadoutChoices.</summary>
        public void RequestQueueLoadoutPicks(EventManagers.Registration reg)
        {
            if (reg == null || reg.Event == null) return;
            var ev = reg.Event;

            // No point prompting at queue time either when a mutator decides the kit.
            if (ev.HasMutator(EventManagers.events.EventMutators.Loadout)
                || ev.HasMutator(EventManagers.events.EventMutators.RandomLoadout)) return;

            if (!TryGetSelectableSets(ev, out var names, out var items, out var descs)) return;

            var prefs = eventManager.LoadoutPreferences;
            foreach (var uid in reg.PlayersGuids)
            {
                if (uid == reg.RegistrantUid) continue; // leader already chose
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;

                string saved = prefs?.Get(uid, ev.Code);
                if (!string.IsNullOrEmpty(saved) && names.Contains(saved))
                {
                    reg.PerPlayerLoadoutChoices[uid] = saved;
                    continue;
                }

                serverChannel.SendPacket(new LoadoutPickRequestMsg
                {
                    EventId = 0,
                    EventCode = ev.Code,
                    Sets = LoadoutSetInfoMsg.FromArrays(names, items, descs),
                    TimeoutSeconds = 0f,
                    CurrentDefault = saved,
                    RegistrationGuid = reg.RegistrationGuid
                }, pl);
            }
        }

        public void RequestLoadoutPicks(EventManagers.RunningEvent re, float timeoutSeconds)
        {
            if (re == null) return;
            var ev = re.Event;
            if (ev == null) return;

            if (!TryGetSelectableSets(re.Behaviors, ev.Code, out var names, out var items, out var descs)) return;

            // A forced set leaves nothing to pick; Give() enforces it over any stored choice.
            if (ev.HasMutator(EventManagers.events.EventMutators.Loadout)) return;

            // Random loadout: the server rolls instead of asking - the roll IS the mutator. Written
            // over any queue-time or saved choice, which the mutator overrides by design.
            if (ev.HasMutator(EventManagers.events.EventMutators.RandomLoadout))
            {
                bool shared = "shared" == ev.MutatorValue(EventManagers.events.EventMutators.RandomLoadout);
                var rand = sapi.World.Rand;
                string sharedPick = names[rand.Next(names.Length)];
                foreach (var uid in re.ParticipantsBefore)
                    re.PlayerLoadoutChoices[uid] = shared ? sharedPick : names[rand.Next(names.Length)];
                return;
            }

            var prefs = eventManager.LoadoutPreferences;
            var pending = new HashSet<string>();
            _pendingPicks[re.Id] = pending;

            foreach (var uid in re.ParticipantsBefore)
            {
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;

                // Registrant already picked at queue-time; don't pop them up.
                if (re.RegistrantUids.Contains(uid)) continue;
                // Party member already picked at queue-time popup.
                if (re.ExplicitLoadoutChoiceUids.Contains(uid)) continue;

                string saved = prefs?.Get(uid, ev.Code);
                if (!string.IsNullOrEmpty(saved) && names.Contains(saved))
                {
                    re.PlayerLoadoutChoices[uid] = saved;
                    continue;
                }

                pending.Add(uid);
                serverChannel.SendPacket(new LoadoutPickRequestMsg
                {
                    EventId = re.Id,
                    EventCode = ev.Code,
                    Sets = LoadoutSetInfoMsg.FromArrays(names, items, descs),
                    TimeoutSeconds = timeoutSeconds,
                    CurrentDefault = saved
                }, pl);
            }

            if (pending.Count == 0) _pendingPicks.Remove(re.Id);
        }

        /// <summary>Does this event actually offer that loadout set? Answered from the event's own
        /// behaviors, never from the client-supplied <c>msg.EventCode</c>.</summary>
        private bool OffersSet(IEnumerable<EventManagers.events.CANEventBehavior> behaviors,
            string eventCode, string setName)
        {
            var provider = behaviors?.OfType<ILoadoutProvider>().FirstOrDefault();
            var names = provider?.GetLoadoutSetNames(eventCode, eventManager.LoadoutManager);
            return names != null && names.Contains(setName);
        }

        /// <summary>When the player ticked the box, stores the pick as their default for the event
        /// and refreshes their list. Offered by both the queue-time and match-start paths.</summary>
        private void SaveDefaultIfRequested(IServerPlayer player, string eventCode, LoadoutPickResponseMsg msg)
        {
            if (!msg.MakeDefault) return;
            eventManager.LoadoutPreferences?.Set(player.PlayerUID, eventCode, msg.SetName);
            SendLoadoutDefaultsTo(player);
        }

        private void OnLoadoutPickResponse(IServerPlayer player, LoadoutPickResponseMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.SetName) || string.IsNullOrEmpty(msg.EventCode)) return;

            // Queue-time pick (party member picked at register-time).
            if (!string.IsNullOrEmpty(msg.RegistrationGuid))
            {
                var reg = FindRegistrationByGuid(msg.RegistrationGuid);
                if (reg == null) return;
                if (!reg.PlayersGuids.Contains(player.PlayerUID)) return;

                // Authoritative event code from the registration; never trust msg.EventCode.
                string regEventCode = reg.Event.Code;
                if (!OffersSet(reg.Event.Behaviors, regEventCode, msg.SetName)) return;

                reg.PerPlayerLoadoutChoices[player.PlayerUID] = msg.SetName;
                SaveDefaultIfRequested(player, regEventCode, msg);
                return;
            }

            var re = eventManager.FindRunningEvent(msg.EventId);
            if (re == null) return;
            if (!re.IsParticipant(player.PlayerUID)) return;

            // Only an answer to a question we actually asked: without this, an unsolicited packet
            // lets a player swap class mid-fight, after seeing what the opponent brought.
            if (!_pendingPicks.TryGetValue(re.Id, out var pending) || !pending.Contains(player.PlayerUID)) return;

            // Validate against the authoritative event code, not client-supplied msg.EventCode.
            string eventCode = re.Event.Code;
            if (!OffersSet(re.Behaviors, eventCode, msg.SetName)) return;

            // A tournament sub-match may restrict the class list (or lock it for the whole bracket);
            // without this check a pick packet sent before spawn re-opens the event's FULL set list.
            if (eventManager.Tournaments != null
                && !eventManager.Tournaments.AllowsLoadoutChange(re.Id, player.PlayerUID, msg.SetName)) return;

            re.PlayerLoadoutChoices[player.PlayerUID] = msg.SetName;
            SaveDefaultIfRequested(player, eventCode, msg);

            pending.Remove(player.PlayerUID);
            if (pending.Count == 0) _pendingPicks.Remove(re.Id);
        }

        private EventManagers.Registration FindRegistrationByGuid(string regGuid)
        {
            foreach (var kvp in eventManager.SnapshotRegistrations())
            {
                foreach (var reg in kvp.Value)
                {
                    if (reg.RegistrationGuid == regGuid) return reg;
                }
            }
            return null;
        }

        private void OnClearLoadoutDefault(IServerPlayer player, ClearLoadoutDefaultMsg msg)
        {
            if (string.IsNullOrEmpty(msg?.EventCode)) return;
            eventManager.LoadoutPreferences?.Clear(player.PlayerUID, msg.EventCode);
            SendLoadoutDefaultsTo(player);
        }

        // Admin item-set editor: one code path for every kind of store, picked by msg.Kind.

        /// <summary>The store this packet is about, or null (already answered) for a kind this
        /// server does not have. Never falls back to loadouts on a bad kind: a save meant for the
        /// wardrobe must not land in the gear players fight with.</summary>
        private LoadoutManager StoreFor(IServerPlayer player, string kind)
        {
            var store = eventManager.Store(kind);
            if (store == null) ReplyFail(player, Lang.Get("canevents:itemset-unknown-kind", kind ?? ""));
            return store;
        }

        private void OnRequestLoadoutList(IServerPlayer player, RequestLoadoutListMsg msg)
        {
            if (!RequireAdmin(player)) return;
            SendLoadoutList(player, msg?.Kind);
        }

        private void SendLoadoutList(IServerPlayer player, string kind)
        {
            var lm = eventManager.Store(kind);
            if (lm == null) return;
            var all = lm.GetAll();
            var events = all.Select(kvp => new LoadoutEventMsg
            {
                EventCode = kvp.Key,
                Sets = kvp.Value.Select(s => new LoadoutSetMsg
                {
                    Name = s.Name,
                    ItemCodes = s.Items.Select(i => i.Code).ToArray(),
                    ItemQuantities = s.Items.Select(i => i.Quantity).ToArray()
                }).ToArray()
            }).ToArray();
            serverChannel.SendPacket(new LoadoutListMsg
            {
                Events = events,
                Kind = EventManagers.ItemSetKinds.Normalize(kind)
            }, player);
        }

        private void OnSaveLoadoutFromInv(IServerPlayer player, SaveLoadoutFromInvMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (string.IsNullOrWhiteSpace(msg.SetName) || string.IsNullOrWhiteSpace(msg.EventCode))
            {
                ReplyFail(player, Lang.Get("canevents:loadout-code-name-required"));
                return;
            }
            if (!LoadoutManager.IsValidEventCode(msg.EventCode) || msg.SetName.Length > MaxLoadoutSetName)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-invalid-code-name"));
                return;
            }
            var lm = StoreFor(player, msg.Kind);
            if (lm == null) return;

            var set = lm.BuildFromInventory(player);
            set.Name = msg.SetName;

            if (set.Items.Count == 0)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-inv-empty"));
                return;
            }
            if (set.Items.Count > MaxLoadoutItems)
            {
                set.Items = set.Items.Take(MaxLoadoutItems).ToList();
            }

            lm.AddSet(msg.EventCode, set);
            ReplyOk(player, Lang.Get("canevents:loadout-saved-from-inv", msg.SetName, msg.EventCode, set.Items.Count));
            SendLoadoutList(player, msg.Kind);
        }

        private void OnSaveLoadout(IServerPlayer player, SaveLoadoutMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (string.IsNullOrWhiteSpace(msg.SetName) || string.IsNullOrWhiteSpace(msg.EventCode)
                || msg.ItemCodes == null || msg.ItemCodes.Length == 0)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-empty-or-missing"));
                return;
            }
            if (!LoadoutManager.IsValidEventCode(msg.EventCode) || msg.SetName.Length > MaxLoadoutSetName
                || msg.ItemCodes.Length > MaxLoadoutItems)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-invalid-input", MaxLoadoutItems));
                return;
            }
            var lm = StoreFor(player, msg.Kind);
            if (lm == null) return;

            var items = msg.ItemCodes.Select((c, i) =>
            {
                var code = c ?? "";
                if (code.Length > MaxLoadoutItemCodeLen) code = code.Substring(0, MaxLoadoutItemCodeLen);
                int qty = msg.ItemQuantities != null && i < msg.ItemQuantities.Length ? msg.ItemQuantities[i] : 1;
                if (qty < 1) qty = 1;
                else if (qty > MaxItemQuantity) qty = MaxItemQuantity;
                return new LoadoutManager.LoadoutEntry { Code = code, Quantity = qty };
            }).ToList();

            lm.AddSet(msg.EventCode, new LoadoutManager.LoadoutSet { Name = msg.SetName, Items = items });
            ReplyOk(player, Lang.Get("canevents:loadout-saved", msg.SetName, msg.EventCode, items.Count));
            SendLoadoutList(player, msg.Kind);
        }

        private void OnDeleteLoadoutSet(IServerPlayer player, DeleteLoadoutSetMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!LoadoutManager.IsValidEventCode(msg.EventCode)
                || string.IsNullOrWhiteSpace(msg.SetName)
                || msg.SetName.Length > MaxLoadoutSetName)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-invalid-code-name"));
                return;
            }
            var lm = StoreFor(player, msg.Kind);
            if (lm == null) return;

            bool ok = lm.DeleteSet(msg.EventCode, msg.SetName);
            Reply(player, ok, ok
                ? Lang.Get("canevents:loadout-deleted", msg.SetName, msg.EventCode)
                : Lang.Get("canevents:loadout-not-found", msg.SetName, msg.EventCode));
            if (ok) SendLoadoutList(player, msg.Kind);
        }

        private void OnImportInlineSets(IServerPlayer player, ImportInlineSetsMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!LoadoutManager.IsValidEventCode(msg.EventCode))
            {
                ReplyFail(player, Lang.Get("canevents:loadout-invalid-code"));
                return;
            }
            var lm = StoreFor(player, msg.Kind);
            if (lm == null) return;

            var ev = eventManager.FindEventByCode(msg.EventCode);
            if (ev == null)
            {
                ReplyFail(player, Lang.Get("canevents:loadout-no-provider"));
                return;
            }

            IEnumerable<LoadoutManager.LoadoutSet> sets;
            if (EventManagers.ItemSetKinds.Normalize(msg.Kind) == EventManagers.ItemSetKinds.Outfit)
            {
                // The wardrobe lives in the mode's `outfits` attribute, not in its loadout sets.
                // Named Outfit 1..N: the JSON form is a bare list of code lists, with no names.
                // Only an explicitly configured wardrobe: EventOutfit.Parse answers with the
                // built-in defaults when the attribute is absent, and importing those would fill
                // the store with clothes nobody asked for.
                var wardrobe = ev.Behaviors
                    .Where(b => b.Attributes?["outfits"]?.Exists == true)
                    .Select(b => EventManagers.eventBehavior.Shared.EventOutfit.Parse(b.Attributes))
                    .FirstOrDefault(w => w != null && w.Count > 0);
                if (wardrobe == null)
                {
                    ReplyFail(player, Lang.Get("canevents:loadout-no-provider"));
                    return;
                }
                sets = wardrobe.Select((codes, i) => new LoadoutManager.LoadoutSet
                {
                    Name = "Outfit " + (i + 1),
                    Items = codes.Select(c => new LoadoutManager.LoadoutEntry { Code = c, Quantity = 1 }).ToList()
                });
            }
            else
            {
                var loadoutProvider = ev.GetBehavior<ILoadoutProvider>();
                if (loadoutProvider == null)
                {
                    ReplyFail(player, Lang.Get("canevents:loadout-no-provider"));
                    return;
                }
                sets = loadoutProvider.GetInlineLoadoutSets().Select(t => new LoadoutManager.LoadoutSet
                {
                    Name = t.name,
                    Items = t.items.Select(i => new LoadoutManager.LoadoutEntry { Code = i.Code, Quantity = i.Quantity }).ToList()
                });
            }
            int imported = lm.AddSets(msg.EventCode, sets);

            ReplyOk(player, Lang.Get("canevents:loadout-imported", imported, msg.EventCode));
            SendLoadoutList(player, msg.Kind);
        }
    }
}
