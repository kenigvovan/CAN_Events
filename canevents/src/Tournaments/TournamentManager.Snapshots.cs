using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using canevents.src.EventManagers.events;
using canevents.src.Network;
using Vintagestory.API.Server;

namespace canevents.src.Tournaments
{
    // Wire snapshots. The live Tournament objects never leave the store, so everything the GUI sees
    // is built here into flat arrays - the same shape the rest of the mod's panels use.
    public partial class TournamentManager
    {
        /// <summary>
        /// The tournament list as this viewer sees it (YouAreIn differs per player). Archived ones only
        /// on request.
        /// </summary>
        public TournamentListMsg BuildListMsg(string viewerUid, bool includeHistory = false)
        {
            var list = store.ActiveSnapshot();
            if (includeHistory) list.AddRange(store.Read(d => new List<Tournament>(d.History)));

            // Hidden drafts are filtered server-side, not on the client: they must not sit in the
            // packet at all. Admins keep the full list.
            if (!IsAdmin(viewerUid))
                list.RemoveAll(t => t.State == TournamentState.Draft && !t.ShowBeforeSignup);
            int n = list.Count;
            var msg = new TournamentListMsg
            {
                Ids = new long[n],
                Names = new string[n],
                States = new int[n],
                EventCodesPacked = new string[n],
                EntrantCounts = new int[n],
                MaxEntrants = new int[n],
                EntryFeePacked = new string[n],
                PotPacked = new string[n],
                YouAreIn = new bool[n],
                SignupDeadlineUnix = new long[n],
                TeamSizes = new int[n],
                OpenAtUnix = new long[n]
            };

            for (int i = 0; i < n; i++)
            {
                var t = list[i];
                msg.Ids[i] = t.Id;
                msg.Names[i] = t.Name;
                msg.States[i] = (int)t.State;
                msg.EventCodesPacked[i] = PackedList.Pack(t.EventCodes);
                msg.EntrantCounts[i] = t.Entrants.Count;
                msg.MaxEntrants[i] = t.MaxEntrants;
                msg.EntryFeePacked[i] = ItemSetUtil.Pack(t.EntryFee);
                msg.PotPacked[i] = PackPot(t.Pot);
                msg.YouAreIn[i] = t.EntrantOfPlayer(viewerUid) != null;
                msg.SignupDeadlineUnix[i] = t.SignupDeadlineUnix;
                msg.TeamSizes[i] = t.TeamSize;
                msg.OpenAtUnix[i] = t.OpenAtUnix;
            }
            return msg;
        }

        /// <summary>
        /// Does this viewer get the unfiltered list? Offline/unknown uids count as ordinary players,
        /// the safe direction.
        /// </summary>
        private bool IsAdmin(string viewerUid)
            => sapi.World.PlayerByUid(viewerUid) is IServerPlayer p
               && p.HasPrivilege(Privilege.controlserver);

        /// <summary>One bracket, or null when the id is unknown. Finished ones come from the history.</summary>
        public TournamentBracketMsg BuildBracketMsg(long id)
        {
            var t = store.FindActive(id) ?? store.Read(d => d.History.FirstOrDefault(x => x.Id == id));
            if (t == null) return null;

            int e = t.Entrants.Count;
            int m = t.Matches.Count;
            var msg = new TournamentBracketMsg
            {
                TournamentId = t.Id,
                Name = t.Name,
                State = (int)t.State,

                EntrantIds = new string[e],
                EntrantNames = new string[e],
                EntrantSeeds = new int[e],
                EntrantMembers = new string[e],

                MatchIds = new int[m],
                MatchRounds = new int[m],
                MatchSlots = new int[m],
                MatchSlotsPacked = new string[m],
                MatchWinner = new string[m],
                MatchStates = new int[m],
                MatchFeedsPacked = new string[m],
                MatchAdvanceCount = new int[m],
                MatchIsThirdPlace = new bool[m],
                MatchEventCode = new string[m],
                MatchReadyDeadline = new long[m],
                MatchResultKind = new string[m],
                MatchAbsentPacked = new string[m],

                PotPacked = PackPot(t.Pot),
                PotSplitPercents = t.PotSplitPercents.ToArray(),
                PlacementPrizesPacked = t.PlacementPrizes
                    .Select(tier => ItemSetUtil.Pack(tier?.Items)).ToArray(),
                FinalPlacementEntrantIds = t.FinalPlacement.ToArray(),
                EntryFeePacked = ItemSetUtil.Pack(t.EntryFee),
                RakePercent = t.RakePercent,
                ThirdPlaceMatch = t.ThirdPlaceMatch,
                MaxEntrants = t.MaxEntrants,
                PartiesPerMatch = t.Matches.Count > 0 ? t.Matches[0].Arity : ResolveArity(t),
                AdvancePerMatch = t.Matches.Count > 0 ? t.Matches[0].AdvanceCount : t.AdvancePerMatch,
                AllowedLoadouts = t.AllowedLoadouts.ToArray(),

                OpenAtUnix = t.OpenAtUnix,
                SignupWindowMinutes = t.SignupWindowMinutes,
                RepeatEveryMinutes = t.RepeatEveryMinutes,
                RepeatAtPacked = PackTimesOfDay(t.RepeatAtMinutesOfDay),
                ShowBeforeSignup = t.ShowBeforeSignup
            };

            for (int i = 0; i < e; i++)
            {
                msg.EntrantIds[i] = t.Entrants[i].Id;
                msg.EntrantNames[i] = t.Entrants[i].DisplayName;
                msg.EntrantSeeds[i] = t.Entrants[i].Seed;
                msg.EntrantMembers[i] = t.Entrants[i].MembersText ?? "";
            }

            for (int i = 0; i < m; i++)
            {
                var bm = t.Matches[i];
                msg.MatchIds[i] = bm.Id;
                msg.MatchRounds[i] = bm.Round;
                msg.MatchSlots[i] = bm.SlotInRound;
                // Packed per match, not a slot-by-slot array: nodes are not all the same width.
                msg.MatchSlotsPacked[i] = PackedList.Pack(bm.Slots);
                msg.MatchWinner[i] = bm.WinnerEntrantId ?? "";
                msg.MatchStates[i] = (int)bm.State;
                msg.MatchFeedsPacked[i] = PackedList.Pack(
                    bm.Feeds.Select(f => f.MatchId + ":" + f.Slot));
                msg.MatchAdvanceCount[i] = bm.AdvanceCount;
                msg.MatchIsThirdPlace[i] = bm.IsThirdPlace;
                msg.MatchEventCode[i] = bm.EventCode ?? "";
                msg.MatchReadyDeadline[i] = bm.ReadyDeadlineUnix;
                msg.MatchResultKind[i] = bm.ResultKind ?? "";
                msg.MatchAbsentPacked[i] = PackedList.Pack(bm.AbsentSides);
            }
            return msg;
        }

        /// <summary>The most recently created tournament's id - used right after a panel "create".</summary>
        public long NewestId()
        {
            var active = store.ActiveSnapshot();
            return active.Count == 0 ? 0 : active.Max(t => t.Id);
        }

        // Times of day travel as text, stored as minutes from midnight. TimeOnly + InvariantCulture:
        // hand-rolled hh/mm parsing accepts "25:99", and the format must not follow the server locale.
        private static readonly string[] TimeOfDayFormats = { "H:mm", "HH:mm" };

        /// <summary>"19:00;21:30" from minutes-of-day, for the admin panel's text field.</summary>
        internal static string PackTimesOfDay(List<int> minutes)
        {
            if (minutes == null || minutes.Count == 0) return "";
            return string.Join(";", minutes.Select(m =>
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m)).ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// The inverse, tolerant of ";" or "," and stray spaces. Null when any piece is not a time, so
        /// the caller refuses the whole edit rather than keeping half a schedule.
        /// </summary>
        internal static List<int> ParseTimesOfDay(string packed)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(packed)) return result;
            foreach (var part in packed.Split(';', ','))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                if (!TimeOnly.TryParseExact(part.Trim(), TimeOfDayFormats,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
                result.Add(time.Hour * 60 + time.Minute);
            }
            result.Sort();
            return result;
        }

        private static string PackPot(Dictionary<string, int> pot)
        {
            if (pot == null || pot.Count == 0) return "";
            return ItemSetUtil.Pack(pot.Select(kv => new RewardItem { Code = kv.Key, Quantity = kv.Value }).ToList());
        }
    }
}
