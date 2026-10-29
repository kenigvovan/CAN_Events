using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers;
using canevents.src.EventManagers.eventBehavior.Shared;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
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

        // EventId -> uids we still wait on.
        private readonly Dictionary<long, HashSet<string>> _pendingPicks = new Dictionary<long, HashSet<string>>();

        private void GetLoadoutSets(ILoadoutProvider fight, string code, out string[] names, out string[] items, out string[] descs)
        {
            var lm = eventManager.LoadoutManager;
            names = fight.GetLoadoutSetNames(code, lm);
            items = fight.GetLoadoutSetItemCodes(code, lm);
            descs = fight.GetLoadoutSetDescriptions(code, lm);
        }

        // False when there's no choice to offer. In hidden-role modes the set names are the roles,
        // so every path that shows sets to a client must go through here.
        public bool TryGetSelectableSets(EventManagers.events.CANEvent ev,
            out string[] names, out string[] items, out string[] descs)
            => TryGetSelectableSets(ev?.Behaviors, ev?.Code, out names, out items, out descs);

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

        // Asks non-leader party members at queue time; answers go to the Registration.
        public void RequestQueueLoadoutPicks(EventManagers.Registration reg)
        {
            if (reg == null || reg.Event == null) return;
            var ev = reg.Event;

            if (MutatorDecidesKit(ev)) return;

            if (!TryGetSelectableSets(ev, out var names, out var items, out var descs)) return;

            foreach (var uid in reg.PlayersGuids)
            {
                if (uid == reg.RegistrantUid) continue; // leader already chose
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;

                string usable = UsableSavedDefault(uid, ev.Code, names, out string saved);
                if (usable != null)
                {
                    reg.PerPlayerLoadoutChoices[uid] = usable;
                    continue;
                }

                var msg = PickRequest(ev.Code, names, items, descs, saved);
                msg.RegistrationGuid = reg.RegistrationGuid;
                serverChannel.SendPacket(msg, pl);
            }
        }

        // The saved default if this event still offers it, else null; saved is what is stored either way.
        private string UsableSavedDefault(string uid, string eventCode, string[] names, out string saved)
        {
            saved = eventManager.LoadoutPreferences?.Get(uid, eventCode);
            return !string.IsNullOrEmpty(saved) && names.Contains(saved) ? saved : null;
        }

        // CurrentDefault is only the stored default: the client turns its "always use" switch on from it.
        private static LoadoutPickRequestMsg PickRequest(string eventCode, string[] names, string[] items, string[] descs,
            string savedDefault)
            => new LoadoutPickRequestMsg
            {
                EventCode = eventCode,
                Sets = LoadoutSetInfoMsg.FromArrays(names, items, descs),
                CurrentDefault = savedDefault
            };

        private static bool MutatorDecidesKit(EventManagers.events.CANEvent ev)
            => ev.HasMutator(EventManagers.events.EventMutators.Loadout)
               || ev.HasMutator(EventManagers.events.EventMutators.RandomLoadout);

        // Returns how many players were asked; the countdown waits only if > 0.
        public int RequestLoadoutPicks(EventManagers.RunningEvent re, float timeoutSeconds)
        {
            if (re == null) return 0;
            var ev = re.Event;
            if (ev == null) return 0;

            if (!TryGetSelectableSets(re.Behaviors, ev.Code, out var names, out var items, out var descs)) return 0;

            // Forced set; Give() applies it over any stored choice.
            if (ev.HasMutator(EventManagers.events.EventMutators.Loadout)) return 0;

            // Random loadout overrides any queue-time or saved choice.
            if (ev.HasMutator(EventManagers.events.EventMutators.RandomLoadout))
            {
                bool shared = "shared" == ev.MutatorValue(EventManagers.events.EventMutators.RandomLoadout);
                var rand = sapi.World.Rand;
                string sharedPick = names[rand.Next(names.Length)];
                foreach (var uid in re.ParticipantsBefore)
                    re.PlayerLoadoutChoices[uid] = shared ? sharedPick : names[rand.Next(names.Length)];
                return 0;
            }

            var pending = new HashSet<string>();
            _pendingPicks[re.Id] = pending;

            foreach (var uid in re.ParticipantsBefore)
            {
                var pl = sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (pl == null) continue;

                // Already picked at queue time.
                if (re.RegistrantUids.Contains(uid)) continue;
                if (re.ExplicitLoadoutChoiceUids.Contains(uid)) continue;

                string usable = UsableSavedDefault(uid, ev.Code, names, out string saved);
                if (usable != null)
                {
                    re.PlayerLoadoutChoices[uid] = usable;
                    continue;
                }

                pending.Add(uid);
                var msg = PickRequest(ev.Code, names, items, descs, saved);
                msg.EventId = re.Id;
                msg.TimeoutSeconds = timeoutSeconds;
                serverChannel.SendPacket(msg, pl);
            }

            if (pending.Count == 0) _pendingPicks.Remove(re.Id);
            return pending.Count;
        }

        // (match id, uid) -> run once the pick lands. Lets someone not on the roster yet answer
        // (open-match entry from the lobby).
        private readonly Dictionary<(long, string), System.Action<IServerPlayer>> _pickCallbacks
            = new Dictionary<(long, string), System.Action<IServerPlayer>>();

        // (match id, uid) -> number of the latest offer. An expiry timer acts only on its own offer,
        // so an old one running out can't cancel a newer one.
        private readonly Dictionary<(long, string), int> _pickOffers = new Dictionary<(long, string), int>();
        private int _pickOfferSeq;

        // One player outside the start countdown: lobby entry, battle intermission. The offer lapses
        // after timeoutSeconds. False when there is nothing to choose; go ahead with the default then.
        public bool RequestLoadoutPickFor(EventManagers.RunningEvent re, IServerPlayer player,
            float timeoutSeconds, string countdownLangKey, System.Action<IServerPlayer> onPicked)
        {
            if (re?.Event == null || player == null) return false;
            var ev = re.Event;
            if (MutatorDecidesKit(ev)) return false;
            if (!TryGetSelectableSets(re.Behaviors, ev.Code, out var names, out var items, out var descs)) return false;

            string uid = player.PlayerUID;

            // "Always use" means no dialog anywhere, as at the match start; the default is taken as the pick.
            string usable = UsableSavedDefault(uid, ev.Code, names, out string saved);
            if (usable != null && eventManager.Tournaments?.AllowsLoadoutChange(re.Id, uid, usable) != false)
            {
                CancelLoadoutPickFor(re.Id, uid);
                re.PlayerLoadoutChoices[uid] = usable;
                return false;
            }

            if (!_pendingPicks.TryGetValue(re.Id, out var pending))
                _pendingPicks[re.Id] = pending = new HashSet<string>();
            pending.Add(uid);

            var key = (re.Id, uid);
            if (onPicked != null) _pickCallbacks[key] = onPicked;
            else _pickCallbacks.Remove(key);
            int offer = ++_pickOfferSeq;
            _pickOffers[key] = offer;

            // This match's pick only highlights a set; the switch follows the saved default.
            re.PlayerLoadoutChoices.TryGetValue(uid, out var current);
            var msg = PickRequest(ev.Code, names, items, descs, saved);
            msg.EventId = re.Id;
            msg.TimeoutSeconds = timeoutSeconds;
            msg.CountdownLangKey = countdownLangKey;
            msg.Preselected = current;
            serverChannel.SendPacket(msg, player);

            if (timeoutSeconds > 0f)
            {
                sapi.Event.RegisterCallback(_ =>
                {
                    if (_pickOffers.TryGetValue(key, out int latest) && latest == offer)
                        CancelLoadoutPickFor(re.Id, uid);
                }, (int)(timeoutSeconds * 1000) + 500);
            }
            return true;
        }

        public void CancelLoadoutPickFor(long eventId, string uid)
        {
            var key = (eventId, uid);
            _pickCallbacks.Remove(key);
            _pickOffers.Remove(key);
            if (_pendingPicks.TryGetValue(eventId, out var p) && p.Remove(uid) && p.Count == 0)
                _pendingPicks.Remove(eventId);
        }

        private bool OffersSet(IEnumerable<EventManagers.events.CANEventBehavior> behaviors,
            string eventCode, string setName)
        {
            var provider = behaviors?.OfType<ILoadoutProvider>().FirstOrDefault();
            var names = provider?.GetLoadoutSetNames(eventCode, eventManager.LoadoutManager);
            return names != null && names.Contains(setName);
        }

        private void SaveDefaultIfRequested(IServerPlayer player, string eventCode, LoadoutPickResponseMsg msg)
        {
            if (!msg.MakeDefault) return;
            eventManager.LoadoutPreferences?.Set(player.PlayerUID, eventCode, msg.SetName);
            SendLoadoutDefaultsTo(player);
        }

        private void OnLoadoutPickResponse(IServerPlayer player, LoadoutPickResponseMsg msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.SetName) || string.IsNullOrEmpty(msg.EventCode)) return;

            // Queue-time pick.
            if (!string.IsNullOrEmpty(msg.RegistrationGuid))
            {
                var reg = FindRegistrationByGuid(msg.RegistrationGuid);
                if (reg == null) return;
                if (!reg.PlayersGuids.Contains(player.PlayerUID)) return;

                // Don't trust msg.EventCode.
                string regEventCode = reg.Event.Code;
                if (!OffersSet(reg.Event.Behaviors, regEventCode, msg.SetName)) return;

                reg.PerPlayerLoadoutChoices[player.PlayerUID] = msg.SetName;
                SaveDefaultIfRequested(player, regEventCode, msg);
                return;
            }

            var re = eventManager.FindRunningEvent(msg.EventId);
            if (re == null) return;
            var key = (re.Id, player.PlayerUID);
            _pickCallbacks.TryGetValue(key, out var onPicked);
            if (!re.IsParticipant(player.PlayerUID) && onPicked == null) return;

            // Only if we asked, or players could swap class mid-fight.
            if (!_pendingPicks.TryGetValue(re.Id, out var pending) || !pending.Contains(player.PlayerUID)) return;

            string eventCode = re.Event.Code;
            if (!OffersSet(re.Behaviors, eventCode, msg.SetName)) return;

            // Tournaments can restrict or lock the class list.
            if (eventManager.Tournaments != null
                && !eventManager.Tournaments.AllowsLoadoutChange(re.Id, player.PlayerUID, msg.SetName)) return;

            re.PlayerLoadoutChoices[player.PlayerUID] = msg.SetName;
            SaveDefaultIfRequested(player, eventCode, msg);
            // The shop lists what fits the class, so a switch changes the window.
            EventManagers.eventBehavior.MatchShopEB.Of(re)?.Push(player.PlayerUID);

            CancelLoadoutPickFor(re.Id, player.PlayerUID);

            if (onPicked != null)
                eventManager.log.Safely("loadout pick callback for " + player.PlayerName, () => onPicked(player));
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

        // Null (already replied) for an unknown kind. No fallback to loadouts on purpose.
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
                    ItemQuantities = s.Items.Select(i => i.Quantity).ToArray(),
                    ItemIds = s.Items.Select(i => i.Id).ToArray(),
                    Upgrades = s.Items.SelectMany((item, index) => (item.Upgrades ?? new List<UpgradeStep>())
                        .Select(u => StepMsg(u, index)))
                        .ToArray()
                }).ToArray()
            }).ToArray();
            adminChannel.SendPacket(new LoadoutListMsg
            {
                Events = events,
                Kind = EventManagers.ItemSetKinds.Normalize(kind)
            }, player);
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

            if (EventManagers.ItemSetKinds.Normalize(msg.Kind) == EventManagers.ItemSetKinds.Loadout)
                AttachUpgrades(items, msg);

            lm.AddSet(msg.EventCode, new LoadoutManager.LoadoutSet { Name = msg.SetName, Items = items });
            ReplyOk(player, Lang.Get("canevents:loadout-saved", msg.SetName, msg.EventCode, items.Count));
            SendLoadoutList(player, msg.Kind);
        }

        // Every item gets an id (kept if it had one), so a shop level bought on it stays on it after edits.
        private static void AttachUpgrades(List<LoadoutManager.LoadoutEntry> items, SaveLoadoutMsg msg)
        {
            var used = new HashSet<string>();
            for (int i = 0; i < items.Count; i++)
            {
                string id = ClipCode(msg.ItemIds != null && i < msg.ItemIds.Length ? msg.ItemIds[i] : null);
                if (id == null || !used.Add(id))
                {
                    do id = System.Guid.NewGuid().ToString("N").Substring(0, 8);
                    while (!used.Add(id));
                }
                items[i].Id = id;
            }

            foreach (var step in msg.Upgrades ?? new UpgradeStepMsg[0])
            {
                string code = ClipCode(step?.Code);
                if (code == null || step.Item < 0 || step.Item >= items.Count) continue;
                var entry = items[step.Item];
                entry.Upgrades ??= new List<UpgradeStep>();
                if (entry.Upgrades.Count >= MaxUpgradeSteps) continue;
                var extras = new List<UpgradeExtra>();
                for (int i = 0; i < step.ExtraCount && extras.Count < MaxUpgradeExtras; i++)
                {
                    string extra = ClipCode(step.ExtraCodes[i]);
                    if (extra != null)
                        extras.Add(new UpgradeExtra { Code = extra, Quantity = System.Math.Clamp(step.ExtraQuantityAt(i), 1, MaxItemQuantity) });
                }
                entry.Upgrades.Add(new UpgradeStep
                {
                    Code = code,
                    Quantity = System.Math.Clamp(step.Quantity, 0, MaxItemQuantity),
                    Cost = System.Math.Clamp(step.Cost, 0, MaxShopCost),
                    Extras = extras.Count > 0 ? extras : null
                });
            }
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
                // From the `outfits` attribute, only if set explicitly (Parse returns defaults otherwise).
                var wardrobe = ev.Behaviors
                    .Where(b => b.Attributes?[BehaviorConfigKeys.Outfits]?.Exists == true)
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
