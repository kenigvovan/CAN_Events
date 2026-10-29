using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;

namespace canevents.src.Gui
{
    // Client-side bracket rebuilt from the message's parallel arrays.
    public class BracketView
    {
        public long TournamentId;
        public string Name;
        public int State;
        public string PotPacked;
        public string EntryFeePacked;
        public List<string> FinalPlacement = new List<string>();

        // Empty means all of the mode's loadouts.
        public List<string> AllowedLoadouts = new List<string>();

        public class Entrant
        {
            public string Id;
            public string Name;      // the bracket label: team name, or "Leader +N"
            public string Members;   // the full roster, for the detail line
            public int Seed;
        }

        public class Feed
        {
            public int MatchId = -1;
            public int Slot;
        }

        public class Match
        {
            public int Id;
            public int Round;
            public int Slot;

            // null = unfilled slot
            public List<string> Slots = new List<string>();
            public string Winner;

            public int Arity => Math.Max(2, Slots.Count);
            public IEnumerable<string> Filled => Slots.Where(s => !string.IsNullOrEmpty(s));
            public int State;               // Tournaments.BracketMatchState

            // One entry per advancing place.
            public List<Feed> Feeds = new List<Feed>();
            public int AdvanceCount = 1;
            public bool IsThirdPlace;

            // Forfeited sides, drawn struck through.
            public List<string> Absent = new List<string>();
            public bool IsAbsent(string id) => !string.IsNullOrEmpty(id) && Absent.Contains(id);
            public string EventCode;
            public long ReadyDeadlineUnix;
            public string ResultKind;
        }

        public readonly Dictionary<string, Entrant> Entrants = new Dictionary<string, Entrant>();
        public readonly List<Match> Matches = new List<Match>();

        // Built client-side before seeding, pairings are provisional.
        public bool IsPreview;

        public string NameOf(string entrantId)
            => string.IsNullOrEmpty(entrantId) ? null
             : Entrants.TryGetValue(entrantId, out var e) ? e.Name : null;

        public string MembersOf(string entrantId)
            => !string.IsNullOrEmpty(entrantId) && Entrants.TryGetValue(entrantId, out var e) ? e.Members : null;

        public int SeedOf(string entrantId)
            => !string.IsNullOrEmpty(entrantId) && Entrants.TryGetValue(entrantId, out var e) ? e.Seed : 0;

        public int TotalRounds => Matches.Count == 0 ? 0 : Matches.Max(m => m.Round) + 1;

        public static BracketView FromMsg(TournamentBracketMsg msg)
        {
            if (msg == null) return null;
            var view = new BracketView
            {
                TournamentId = msg.TournamentId,
                Name = msg.Name,
                State = msg.State,
                PotPacked = msg.PotPacked,
                EntryFeePacked = msg.EntryFeePacked,
                FinalPlacement = (msg.FinalPlacementEntrantIds ?? new string[0]).ToList(),
                AllowedLoadouts = (msg.AllowedLoadouts ?? new string[0]).ToList()
            };

            var ids = msg.EntrantIds ?? new string[0];
            for (int i = 0; i < ids.Length; i++)
            {
                view.Entrants[ids[i]] = new Entrant
                {
                    Id = ids[i],
                    Name = At(msg.EntrantNames, i) ?? ids[i],
                    Members = At(msg.EntrantMembers, i),
                    Seed = At(msg.EntrantSeeds, i)
                };
            }

            var matchIds = msg.MatchIds ?? new int[0];
            for (int i = 0; i < matchIds.Length; i++)
            {
                view.Matches.Add(new Match
                {
                    Id = matchIds[i],
                    Round = At(msg.MatchRounds, i),
                    Slot = At(msg.MatchSlots, i),
                    Slots = UnpackSlots(At(msg.MatchSlotsPacked, i)),
                    Winner = Empty(At(msg.MatchWinner, i)),
                    State = At(msg.MatchStates, i),
                    Feeds = UnpackFeeds(At(msg.MatchFeedsPacked, i)),
                    AdvanceCount = Math.Max(1, At(msg.MatchAdvanceCount, i)),
                    IsThirdPlace = At(msg.MatchIsThirdPlace, i),
                    EventCode = Empty(At(msg.MatchEventCode, i)),
                    ReadyDeadlineUnix = At(msg.MatchReadyDeadline, i),
                    ResultKind = Empty(At(msg.MatchResultKind, i)),
                    Absent = PackedList.UnpackNonEmpty(At(msg.MatchAbsentPacked, i))
                });
            }

            // Same BracketBuilder as the server, so the preview has the real shape.
            if (view.Matches.Count == 0)
                BuildPreview(view, msg.ThirdPlaceMatch, msg.MaxEntrants, msg.PartiesPerMatch,
                    msg.AdvancePerMatch);

            return view;
        }

        // No names in the preview: seeding happens at start.
        private static void BuildPreview(BracketView view, bool thirdPlace, int maxEntrants, int arity,
            int advance)
        {
            if (arity < 2) arity = 2;

            // Sized by signups, capacity only while nobody has joined yet.
            int signedUp = view.Entrants.Count;
            int planned = signedUp >= arity
                ? signedUp
                : Math.Max(arity, maxEntrants > 0 ? maxEntrants : arity);

            // Builder rounds up to a power of arity, surplus slots stay empty (byes).
            var field = new List<Tournaments.Entrant>();
            for (int i = 0; i < planned; i++)
                field.Add(new Tournaments.Entrant { Id = "slot" + i, DisplayName = "" });

            var matches = Tournaments.BracketBuilder.Build(field, thirdPlace, arity, Math.Max(1, advance));
            if (matches.Count == 0) return;

            foreach (var m in matches)
            {
                view.Matches.Add(new Match
                {
                    Id = m.Id,
                    Round = m.Round,
                    Slot = m.SlotInRound,
                    Slots = Enumerable.Repeat((string)null, m.Slots.Count).ToList(),
                    Winner = null,
                    AdvanceCount = m.AdvanceCount,
                    Feeds = m.Feeds.Select(f => new Feed { MatchId = f.MatchId, Slot = f.Slot }).ToList(),
                    State = (int)Tournaments.BracketMatchState.Pending,
                    IsThirdPlace = m.IsThirdPlace
                });
            }
            view.IsPreview = true;
        }

        private static T At<T>(T[] array, int i) => array != null && i < array.Length ? array[i] : default;
        private static string Empty(string s) => string.IsNullOrEmpty(s) ? null : s;

        private static List<Feed> UnpackFeeds(string packed)
        {
            var feeds = new List<Feed>();
            foreach (var piece in PackedList.UnpackNonEmpty(packed))
            {
                var parts = piece.Split(':');
                if (parts.Length != 2) continue;
                if (!int.TryParse(parts[0], out int id) || !int.TryParse(parts[1], out int slot)) continue;
                feeds.Add(new Feed { MatchId = id, Slot = slot });
            }
            return feeds;
        }

        // Empty piece = unfilled seat. No pieces = old-format duel.
        private static List<string> UnpackSlots(string packed)
        {
            var pieces = PackedList.Unpack(packed);
            if (pieces.Count == 0) return new List<string> { null, null };
            return pieces.Select(Empty).ToList();
        }
    }

    public static class BracketPalette
    {
        public static double[] ColorOf(int state, bool absentSide = false)
        {
            if (absentSide) return Native.Palette.Idle;
            switch ((Tournaments.BracketMatchState)state)
            {
                case Tournaments.BracketMatchState.Live: return Native.Palette.Live;
                case Tournaments.BracketMatchState.AwaitingReady:
                case Tournaments.BracketMatchState.Starting: return Native.Palette.Go;
                case Tournaments.BracketMatchState.Void: return Native.Palette.Stop;
                case Tournaments.BracketMatchState.Done:
                case Tournaments.BracketMatchState.Bye: return Native.Palette.Idle;
                default: return Native.Palette.Title;
            }
        }

        public static string StatusKey(int state)
        {
            switch ((Tournaments.BracketMatchState)state)
            {
                case Tournaments.BracketMatchState.Live: return "canevents:gui-tournament-status-live";
                case Tournaments.BracketMatchState.AwaitingReady: return "canevents:gui-tournament-status-ready";
                case Tournaments.BracketMatchState.Starting: return "canevents:gui-tournament-status-starting";
                case Tournaments.BracketMatchState.Void: return "canevents:gui-tournament-status-void";
                case Tournaments.BracketMatchState.Bye: return "canevents:gui-tournament-status-bye";
                case Tournaments.BracketMatchState.Done: return "canevents:gui-tournament-status-won";
                default: return "canevents:gui-tournament-status-pending";
            }
        }
    }

    // Local space from (0,0), each renderer adds its own origin.
    public static class BracketLayout
    {
        public struct Node
        {
            public int MatchId;
            public int Round;
            public float X, Y, W, H;
        }

        // Elbow: (X0,Y0)-(XMid,Y0)-(XMid,Y1)-(X1,Y1)
        public struct Edge
        {
            public float X0, Y0, X1, Y1, XMid;
        }

        public class Layout
        {
            public List<Node> Nodes = new List<Node>();
            public List<Edge> Edges = new List<Edge>();
            public float Width, Height;

            public bool TryGetNode(int matchId, out Node node)
            {
                foreach (var n in Nodes)
                    if (n.MatchId == matchId) { node = n; return true; }
                node = default;
                return false;
            }
        }

        public static Layout Compute(BracketView view, float boxW = 190f, float boxH = 46f,
            float hGap = 46f, float vGap = 14f)
        {
            var layout = new Layout();
            if (view == null || view.Matches.Count == 0) return layout;

            // Height follows the max side count.
            int maxArity = view.Matches.Max(m => m.Arity);
            float rowH = boxH / 2.4f;
            boxH = rowH * maxArity + rowH * 0.25f;

            var ordered = view.Matches
                .Where(m => !m.IsThirdPlace)
                .OrderBy(m => m.Round).ThenBy(m => m.Slot)
                .ToList();

            var y = new Dictionary<int, float>();

            // Later rounds are centred on the matches feeding them.
            foreach (var m in ordered)
            {
                if (m.Round == 0)
                {
                    y[m.Id] = m.Slot * (boxH + vGap);
                    continue;
                }

                var children = ordered.Where(c => c.Feeds.Any(f => f.MatchId == m.Id) && y.ContainsKey(c.Id))
                    .ToList();
                if (children.Count > 0)
                    y[m.Id] = children.Average(c => y[c.Id]);
                else
                    y[m.Id] = m.Slot * (boxH + vGap);
            }

            foreach (var m in ordered)
            {
                layout.Nodes.Add(new Node
                {
                    MatchId = m.Id,
                    Round = m.Round,
                    X = m.Round * (boxW + hGap),
                    Y = y[m.Id],
                    W = boxW,
                    H = boxH
                });
            }

            // Third-place match goes under the final.
            var third = view.Matches.FirstOrDefault(m => m.IsThirdPlace);
            if (third != null)
            {
                var final = layout.Nodes.OrderByDescending(n => n.Round).FirstOrDefault();
                layout.Nodes.Add(new Node
                {
                    MatchId = third.Id,
                    Round = third.Round,
                    X = final.X,
                    Y = final.Y + boxH + vGap * 3,
                    W = boxW,
                    H = boxH
                });
            }

            // One edge per advancing place.
            foreach (var m in ordered)
            {
                if (!layout.TryGetNode(m.Id, out var from)) continue;
                foreach (var feed in m.Feeds)
                {
                    if (feed.MatchId < 0) continue;
                    if (!layout.TryGetNode(feed.MatchId, out var to)) continue;

                    layout.Edges.Add(new Edge
                    {
                        X0 = from.X + from.W,
                        Y0 = from.Y + from.H / 2f,
                        X1 = to.X,
                        Y1 = to.Y + to.H / 2f,
                        XMid = from.X + from.W + hGap / 2f
                    });
                }
            }

            foreach (var n in layout.Nodes)
            {
                if (n.X + n.W > layout.Width) layout.Width = n.X + n.W;
                if (n.Y + n.H > layout.Height) layout.Height = n.Y + n.H;
            }
            return layout;
        }

        // Shrinks to fit, but not below minScale; past that the caller scrolls.
        public static Layout ComputeFitted(BracketView view, double availWidth, double availHeight,
            double minScale, out double scale)
        {
            var full = Compute(view);
            scale = 1;
            if (full.Width <= 0 || full.Height <= 0) return full;

            double fit = Math.Min(availWidth / full.Width, availHeight / full.Height);
            if (fit >= 1) return full;

            scale = Math.Max(minScale, fit);
            return Compute(view, (float)(190 * scale), (float)(46 * scale),
                (float)(46 * scale), (float)(14 * scale));
        }

        // Keep in sync with the server's wording.
        public static string RoundLabel(BracketView view, BracketView.Match m)
        {
            if (m.IsThirdPlace) return "3rd place";
            int fromEnd = view.TotalRounds - 1 - m.Round;
            switch (fromEnd)
            {
                case 0: return "Final";
                case 1: return "Semifinal";
                case 2: return "Quarterfinal";
                default: return "Round " + (m.Round + 1);
            }
        }
    }
}
