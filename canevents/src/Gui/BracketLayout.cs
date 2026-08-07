using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.Network;

namespace canevents.src.Gui
{
    /// <summary>
    /// A bracket as the client sees it, rebuilt from the wire's parallel arrays. Renderer-agnostic:
    /// both the ImGui admin panel and the Cairo player browser draw from this.
    /// </summary>
    public class BracketView
    {
        public long TournamentId;
        public string Name;
        public int State;
        public string PotPacked;
        public string EntryFeePacked;
        public List<string> FinalPlacement = new List<string>();

        /// <summary>Classes the organiser allows here; empty means all of the mode's.</summary>
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

            /// <summary>The sides, one per slot; null means unfilled. Two for a duel, more for a free-for-all.</summary>
            public List<string> Slots = new List<string>();
            public string Winner;

            public int Arity => Math.Max(2, Slots.Count);
            public IEnumerable<string> Filled => Slots.Where(s => !string.IsNullOrEmpty(s));
            public int State;               // Tournaments.BracketMatchState

            /// <summary>Where each advancing place goes. One entry for a knockout, more for a heat.</summary>
            public List<Feed> Feeds = new List<Feed>();
            public int AdvanceCount = 1;
            public bool IsThirdPlace;

            /// <summary>Sides that forfeited: still shown, struck through rather than erased.</summary>
            public List<string> Absent = new List<string>();
            public bool IsAbsent(string id) => !string.IsNullOrEmpty(id) && Absent.Contains(id);
            public string EventCode;
            public long ReadyDeadlineUnix;
            public string ResultKind;
        }

        public readonly Dictionary<string, Entrant> Entrants = new Dictionary<string, Entrant>();
        public readonly List<Match> Matches = new List<Match>();

        /// <summary>
        /// Tree was built client-side, not sent by the server: pairings are provisional until
        /// seeding happens at start. Renderers say so on screen.
        /// </summary>
        public bool IsPreview;

        /// <summary>The entrant's name, or null when the slot is empty or not yet decided.</summary>
        public string NameOf(string entrantId)
            => string.IsNullOrEmpty(entrantId) ? null
             : Entrants.TryGetValue(entrantId, out var e) ? e.Name : null;

        /// <summary>The side's roster, when it is a team of more than one.</summary>
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

            // Before the bracket exists, show its shape. Built from the same BracketBuilder the
            // server runs, so the preview cannot disagree structurally with the real thing.
            if (view.Matches.Count == 0)
                BuildPreview(view, msg.ThirdPlaceMatch, msg.MaxEntrants, msg.PartiesPerMatch,
                    msg.AdvancePerMatch);

            return view;
        }

        /// <summary>
        /// Empty skeleton of the planned size. Deliberately nameless: seeding happens at start, so
        /// showing pairings now would invite path-picking and signup timing off fiction.
        /// </summary>
        private static void BuildPreview(BracketView view, bool thirdPlace, int maxEntrants, int arity,
            int advance)
        {
            if (arity < 2) arity = 2;
            int planned = Math.Max(arity, maxEntrants > 0 ? maxEntrants : view.Entrants.Count);

            // Placeholders fill every slot, so the skeleton has no byes: byes depend on turnout.
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

        /// <summary>
        /// Slots keep their positions: an empty piece is an unfilled seat, not padding. An empty
        /// match is treated as a duel (what an old bracket looks like).
        /// </summary>
        private static List<string> UnpackSlots(string packed)
        {
            var pieces = PackedList.Unpack(packed);
            if (pieces.Count == 0) return new List<string> { null, null };
            return pieces.Select(Empty).ToList();
        }
    }

    /// <summary>
    /// One table of match-state colour and label for both renderers and the detail panel; they used
    /// to each have their own and had drifted. Colours come from the shared palette by role.
    /// </summary>
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

        /// <summary>Lang key for the one-word status shown next to a match.</summary>
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

    /// <summary>
    /// Positions bracket nodes and the elbows between them, in a local space starting at (0,0) that
    /// each renderer offsets by its own origin. Elbows, not curves: both ImGui's AddLine and Cairo's
    /// LineTo express them directly.
    /// </summary>
    public static class BracketLayout
    {
        public struct Node
        {
            public int MatchId;
            public int Round;
            public float X, Y, W, H;
        }

        /// <summary>Three segments: (X0,Y0)-(XMid,Y0), (XMid,Y0)-(XMid,Y1), (XMid,Y1)-(X1,Y1).</summary>
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

            // Node height follows the widest match's side count, so duel boxes have no dead space.
            int maxArity = view.Matches.Max(m => m.Arity);
            float rowH = boxH / 2.4f;
            boxH = rowH * maxArity + rowH * 0.25f;

            var ordered = view.Matches
                .Where(m => !m.IsThirdPlace)
                .OrderBy(m => m.Round).ThenBy(m => m.Slot)
                .ToList();

            var y = new Dictionary<int, float>();

            // Round 0 is evenly spaced; later rounds sit centred between the matches feeding them.
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

            // The third-place match hangs under the final rather than in the flow of the tree.
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

            // One line per advancing place, so a heat that sends two on visibly forks.
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

        /// <summary>
        /// The largest layout that still fits the available box, down to <paramref name="minScale"/>.
        /// A bracket is meant to be taken in at once; past the floor it stops shrinking (unreadable
        /// is worse than partial) and the caller falls back to scrolling.
        /// </summary>
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

        /// <summary>Same wording as the server's, so chat and both renderers agree.</summary>
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
