using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using canevents.src.Tournaments;
using Newtonsoft.Json.Linq;

namespace canevents.src.Gui.Admin.Pages
{
    // Sent as strings, see WireName; must match the server's TournamentAdminActionMsg handler.
    public enum TourAdminAction
    {
        Create, Configure, OpenSignup, CloseSignup, Begin, ForceCall,
        Cancel, Delete, Result, Disqualify, AddBots
    }

    // New tournaments are sent whole, existing ones only with changed fields.
    public sealed class TournamentEditorBuffer
    {
        public long SelectedId;
        public long LoadedFor = -1;
        public bool BracketRequested;
        public bool Creating;

        // Fields equal to this are sent as null, so guessed values never overwrite the server's.
        private JObject baseline;

        // Cached before Save went out, so it must not be loaded.
        private TournamentBracketMsg staleBracket;

        public string Name = "Tournament";
        public readonly HashSet<string> EventCodes = new HashSet<string>();
        public readonly HashSet<long> Places = new HashSet<long>();
        public readonly List<string> Classes = new List<string>();   // empty = every class
        public int TeamSize = 1;
        public int MaxEntrants = 16;
        public int ReadySeconds = 60;
        public int Concurrent = 1;
        public int Rake;
        public int Sides;      // 0 = derive from the event
        public int Advance = 1;
        public int AutoStart;              // 0 = off
        public int AutoStartDelay = 15;
        public int DeadlineMinutes;
        public int OpenInMinutes;
        public int SignupWindow;
        public int RepeatMinutes;
        public string RepeatAt = "";
        public bool ShowBeforeSignup = true;
        public int SeedMode;
        public int RatedMode;
        public bool ThirdPlace = true;
        public bool LockClass;
        public bool OwnGear;
        public bool ManualStart;
        public bool PauseScheduler = true;
        public readonly List<RewardItem> Fee = new List<RewardItem>();
        public readonly List<int> Split = new List<int> { 60, 30, 10 };
        public readonly List<List<RewardItem>> Prizes = new List<List<RewardItem>>();

        public void Select(long id)
        {
            SelectedId = id;
            LoadedFor = -1;
            BracketRequested = false;
            staleBracket = null;
            // Don't show the previous tournament's arenas while the snapshot is in flight.
            Places.Clear();
            baseline = JObject.FromObject(FullDraft());
        }

        // Reload from the next server snapshot, not the cached one.
        public void ReloadAfterSave(ClientNetworkHandler net)
        {
            LoadedFor = -1;
            BracketRequested = false;
            staleBracket = net.TournamentBracket.Value;
            baseline = JObject.FromObject(FullDraft());
        }

        // Auto-start, class lock, own gear and manual start carry over from the previous form.
        public void ResetForNewDraft()
        {
            SelectedId = 0;
            LoadedFor = -1;
            Name = "Tournament";
            EventCodes.Clear();
            Places.Clear();
            Classes.Clear();
            TeamSize = 1;
            MaxEntrants = 16;
            ReadySeconds = 60;
            Concurrent = 1;
            Rake = 0;
            Sides = 0;
            Advance = 1;
            DeadlineMinutes = 0;
            OpenInMinutes = 0;
            SignupWindow = 0;
            RepeatMinutes = 0;
            RepeatAt = "";
            ShowBeforeSignup = true;
            SeedMode = 0;
            RatedMode = 0;
            ThirdPlace = true;
            PauseScheduler = true;
            Fee.Clear();
            Split.Clear(); Split.AddRange(new[] { 60, 30, 10 });
            Prizes.Clear();
            Creating = true;
            baseline = null;
            staleBracket = null;
        }

        // Once per selection. Fields neither snapshot carries keep their values and stay out of the save.
        public void EnsureLoaded(ClientNetworkHandler net)
        {
            if (SelectedId <= 0 || LoadedFor == SelectedId) return;

            if (!BracketRequested)
            {
                net.RequestTournamentBracket(SelectedId);
                BracketRequested = true;
            }

            var b = net.TournamentBracket.Value;
            if (b == null || b.TournamentId != SelectedId || ReferenceEquals(b, staleBracket)) return;
            staleBracket = null;

            Name = b.Name ?? "";
            Rake = b.RakePercent;
            Sides = b.PartiesPerMatch <= 2 ? 0 : b.PartiesPerMatch;
            Advance = Math.Max(1, b.AdvancePerMatch);
            ThirdPlace = b.ThirdPlaceMatch;
            if (b.MaxEntrants > 0) MaxEntrants = Math.Max(2, b.MaxEntrants);
            Classes.Clear();
            foreach (var name in b.AllowedLoadouts ?? new string[0])
                if (!string.IsNullOrEmpty(name)) Classes.Add(name);
            Fee.Clear();
            Fee.AddRange(ItemSetUtil.Parse(b.EntryFeePacked));
            Split.Clear();
            Split.AddRange(b.PotSplitPercents ?? new[] { 60, 30, 10 });
            Prizes.Clear();
            foreach (var packed in b.PlacementPrizesPacked ?? new string[0])
                Prizes.Add(ItemSetUtil.Parse(packed));

            // Unix time edited as minutes from now; unchanged values are not sent, so the opening doesn't drift.
            OpenInMinutes = b.OpenAtUnix <= 0
                ? 0 : (int)Math.Max(0, (b.OpenAtUnix - UnixTime.Now + 59) / 60);
            SignupWindow = b.SignupWindowMinutes;
            RepeatMinutes = b.RepeatEveryMinutes;
            RepeatAt = b.RepeatAtPacked ?? "";
            ShowBeforeSignup = b.ShowBeforeSignup;

            // Only the admin snapshot carries these.
            if (b.HasAdminSettings)
            {
                Places.Clear();
                foreach (var id in b.PlaceWhitelist ?? new long[0]) Places.Add(id);
                LockClass = b.LockLoadout;
                OwnGear = b.BringOwnGear;
                ManualStart = b.ManualMatchStart;
                ReadySeconds = b.ReadyCheckSeconds;
                Concurrent = Math.Max(1, b.MaxConcurrentMatches);
                AutoStart = b.AutoStartEntrants;
                AutoStartDelay = b.AutoStartDelaySeconds;
                SeedMode = b.SeedMode;
                RatedMode = b.RatedMode;
                PauseScheduler = b.PauseScheduler;
            }

            var list = net.TournamentList.Value;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list.Ids[i] != SelectedId) continue;
                    if (b.MaxEntrants <= 0) MaxEntrants = Math.Max(2, list.MaxEntrantsAt(i));
                    TeamSize = list.TeamSizeAt(i);
                    EventCodes.Clear();
                    foreach (var code in list.EventsAt(i).Split(';'))
                        if (!string.IsNullOrWhiteSpace(code)) EventCodes.Add(code);
                    long deadline = list.DeadlineAt(i);
                    DeadlineMinutes = deadline <= 0
                        ? 0 : (int)Math.Max(0, (deadline - UnixTime.Now + 59) / 60);
                    break;
                }
            }

            LoadedFor = SelectedId;
            baseline = JObject.FromObject(FullDraft());
        }

        // Unchanged fields go out as null, which TournamentDraft.ApplyTo leaves alone.
        public TournamentDraft BuildDraft()
        {
            var full = FullDraft();
            if (Creating || baseline == null) return full;

            var current = JObject.FromObject(full);
            foreach (var prop in current.Properties().ToList())
                if (JToken.DeepEquals(prop.Value, baseline[prop.Name])) prop.Value = JValue.CreateNull();
            return current.ToObject<TournamentDraft>();
        }

        private TournamentDraft FullDraft() => new TournamentDraft
        {
            Name = Name,
            EventCodes = EventCodes.ToList(),
            PlaceWhitelist = Places.ToList(),
            TeamSize = TeamSize,
            MaxEntrants = MaxEntrants,
            EntryFee = Fee.Select(i => new RewardItem { Code = i.Code, Quantity = i.Quantity }).ToList(),
            RakePercent = Rake,
            PotSplitPercents = Split.ToList(),
            PlacementPrizes = Prizes.Select(items => new RewardTier { Items = items.ToList() }).ToList(),
            ThirdPlaceMatch = ThirdPlace,
            LockLoadout = LockClass,
            AllowedLoadouts = Classes.ToList(),
            BringOwnGear = OwnGear,
            ManualMatchStart = ManualStart,
            PartiesPerMatch = Sides,
            AdvancePerMatch = Math.Max(1, Advance),
            AutoStartEntrants = Math.Max(0, AutoStart),
            AutoStartDelaySeconds = Math.Max(0, AutoStartDelay),
            ReadyCheckSeconds = ReadySeconds,
            MaxConcurrentMatches = Concurrent,
            SignupDeadlineMinutes = DeadlineMinutes,
            SeedMode = SeedMode,
            RatedMode = RatedMode,
            PauseScheduler = PauseScheduler,
            OpenInMinutes = Math.Max(0, OpenInMinutes),
            SignupWindowMinutes = Math.Max(0, SignupWindow),
            RepeatEveryMinutes = Math.Max(0, RepeatMinutes),
            RepeatAtPacked = RepeatAt ?? "",
            ShowBeforeSignup = ShowBeforeSignup
        };

        public static string WireName(TourAdminAction action)
        {
            switch (action)
            {
                case TourAdminAction.Create: return "create";
                case TourAdminAction.Configure: return "configure";
                case TourAdminAction.OpenSignup: return "opensignup";
                case TourAdminAction.CloseSignup: return "closesignup";
                case TourAdminAction.Begin: return "begin";
                case TourAdminAction.ForceCall: return "forcecall";
                case TourAdminAction.Cancel: return "cancel";
                case TourAdminAction.Delete: return "delete";
                case TourAdminAction.Result: return "result";
                case TourAdminAction.Disqualify: return "dq";
                case TourAdminAction.AddBots: return "bots";
                default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        public static bool ListContains(TournamentListMsg list, long id)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) if (list.Ids[i] == id) return true;
            return false;
        }

        // Draft when not in the list yet.
        public static int StateOf(TournamentListMsg list, long id)
        {
            if (list == null) return (int)TournamentState.Draft;
            for (int i = 0; i < list.Count; i++)
                if (list.Ids[i] == id) return list.StateAt(i);
            return (int)TournamentState.Draft;
        }
    }
}
