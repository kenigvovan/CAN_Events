using System;
using System.Collections.Generic;

namespace canevents.src.Tournaments
{
    /// <summary>
    /// Builds a single-elimination bracket. Pure logic (no VS types, no store, no side effects) so the
    /// seeding and bye arithmetic can be tested on its own. Nodes are N-way: arity 2 is the classic
    /// duel tree, higher arities seat a whole heat per node - the arithmetic is just base-N.
    /// </summary>
    public static class BracketBuilder
    {
        /// <summary>
        /// Classic seeding order for 2^k slots (1,8,4,5,2,7,3,6 for eight): consecutive pairs are the
        /// first-round matches, so the two best seeds can only meet in the final. Duels only.
        /// </summary>
        public static int[] SeedOrder(int bracketSize)
        {
            var order = new List<int> { 1 };
            while (order.Count < bracketSize)
            {
                int len = order.Count;
                var next = new List<int>(len * 2);
                foreach (int s in order)
                {
                    next.Add(s);
                    next.Add(2 * len + 1 - s);
                }
                order = next;
            }
            return order.ToArray();
        }

        public static int NextPow2(int n)
        {
            int size = 1;
            while (size < n) size <<= 1;
            return size;
        }

        /// <summary>Smallest power of <paramref name="arity"/> that seats <paramref name="n"/> sides.</summary>
        public static int NextPow(int n, int arity)
        {
            if (arity < 2) arity = 2;
            int size = arity;
            while (size < n) size *= arity;
            return size;
        }

        /// <summary>
        /// Builds every match of the bracket for <paramref name="seeded"/> (index 0 = seed 1) and
        /// resolves first-round byes immediately. Entrant Seed fields are assigned here.
        /// </summary>
        /// <param name="arity">Sides per match. 2 is the ordinary duel bracket.</param>
        /// <param name="advance">Sides out of each match. 1 is a knockout; more makes every node a heat.</param>
        public static List<BracketMatch> Build(IReadOnlyList<Entrant> seeded, bool thirdPlace,
            int arity = 2, int advance = 1)
        {
            var matches = new List<BracketMatch>();
            if (seeded == null || seeded.Count < 2) return matches;
            if (arity < 2) arity = 2;
            // Advancing everyone would never narrow the field; the last seat has to be contested.
            advance = Math.Max(1, Math.Min(advance, arity - 1));

            for (int i = 0; i < seeded.Count; i++) seeded[i].Seed = i + 1;

            int nextId = 1;
            // Duels round UP to a full power-of-two draw: SeedOrder only speaks powers of two, and a
            // first round sized to ceil(n/2) would read only the head of that order, dropping the
            // entrants at its tail out of the bracket entirely. Byes are the correct shape here.
            int firstRoundNodes = arity == 2
                ? Math.Max(1, NextPow2(seeded.Count) / 2)
                : Math.Max(1, (int)Math.Ceiling(seeded.Count / (double)arity));
            var firstRound = new List<BracketMatch>();

            for (int i = 0; i < firstRoundNodes; i++)
                firstRound.Add(NewNode(ref nextId, 0, i, arity, advance, matches));

            Distribute(seeded, firstRound, arity, firstRoundNodes * arity);

            // Each later round seats everyone who advanced out of the previous one.
            var previous = firstRound;
            int round = 1;
            while (previous.Count > 1)
            {
                int advancing = previous.Count * advance;
                if (advancing <= 1) break;

                int nodes = Math.Max(1, (int)Math.Ceiling(advancing / (double)arity));
                var current = new List<BracketMatch>();
                for (int i = 0; i < nodes; i++)
                    current.Add(NewNode(ref nextId, round, i, arity, advance, matches));

                if (advance == 1)
                {
                    // A knockout: neighbours meet. Child i goes to node i/arity, so the tree reads as
                    // a tree; round-robin here drew a first-round match into a far-away parent.
                    for (int i = 0; i < previous.Count; i++)
                    {
                        var target = current[i / arity];
                        previous[i].Feeds.Clear();
                        previous[i].Feeds.Add(new FeedTarget { MatchId = target.Id, Slot = i % arity });
                    }
                }
                else
                {
                    // Heats: deal winners across the nodes first, then runners-up, so two sides out of
                    // the same heat do not meet again immediately unless the field is too small.
                    int seat = 0;
                    for (int place = 0; place < advance; place++)
                    {
                        foreach (var child in previous)
                        {
                            var target = current[seat % nodes];
                            while (child.Feeds.Count <= place) child.Feeds.Add(new FeedTarget());
                            child.Feeds[place] = new FeedTarget { MatchId = target.Id, Slot = seat / nodes };
                            seat++;
                        }
                    }
                }

                // The last round can be short of a full node - trim the seats nobody will fill.
                TrimUnusedSlots(current, advancing);

                previous = current;
                round++;
                if (round > 16) break;                 // paranoia: never spin on a bad configuration
            }

            if (previous.Count == 1)
            {
                previous[0].IsFinal = true;
                previous[0].AdvanceCount = 1;          // one champion, whatever the heats did
                previous[0].Feeds.Clear();
            }

            int rounds = round;

            // A third-place match only means anything in a duel bracket: with N-way nodes the losers
            // of the semifinals are a crowd, already ranked by the final standings.
            if (thirdPlace && arity == 2 && rounds >= 2)
            {
                var third = new BracketMatch
                {
                    Id = nextId++,
                    Round = rounds - 1,
                    SlotInRound = 1,
                    Arity = 2,
                    IsThirdPlace = true
                };
                third.Slots.Add(null);
                third.Slots.Add(null);
                matches.Add(third);

                var semis = matches.FindAll(m => m.Round == rounds - 2 && !m.IsThirdPlace);
                for (int i = 0; i < semis.Count && i < 2; i++)
                {
                    semis[i].LoserFeedsMatchId = third.Id;
                    semis[i].LoserFeedsSlot = i;
                }
            }

            PropagateByes(matches);
            return matches;
        }

        private static BracketMatch NewNode(ref int nextId, int round, int slotInRound, int arity,
            int advance, List<BracketMatch> into)
        {
            var m = new BracketMatch
            {
                Id = nextId++,
                Round = round,
                SlotInRound = slotInRound,
                Arity = arity,
                AdvanceCount = advance
            };
            for (int s = 0; s < arity; s++) m.Slots.Add(null);
            into.Add(m);
            return m;
        }

        /// <summary>
        /// Drops the seats a round will never fill, so a partly-filled node does not look like it is
        /// still waiting for people who do not exist.
        /// </summary>
        private static void TrimUnusedSlots(List<BracketMatch> round, int arriving)
        {
            int remaining = arriving;
            foreach (var m in round)
            {
                int seats = Math.Min(m.Slots.Count, remaining);
                if (seats < 2) seats = Math.Min(2, m.Slots.Count);
                while (m.Slots.Count > seats) m.Slots.RemoveAt(m.Slots.Count - 1);
                m.Arity = m.Slots.Count;
                m.AdvanceCount = Math.Max(1, Math.Min(m.AdvanceCount, m.Arity - 1));
                remaining -= seats;
            }
        }

        /// <summary>
        /// Places the field into the first round: canonical seed order for duels (strong seeds kept
        /// apart, byes land on them), round-robin across groups for N-way nodes.
        /// </summary>
        private static void Distribute(IReadOnlyList<Entrant> seeded, List<BracketMatch> firstRound,
            int arity, int bracketSize)
        {
            if (arity == 2)
            {
                var order = SeedOrder(bracketSize);
                for (int i = 0; i < firstRound.Count; i++)
                {
                    int a = order[i * 2] - 1;
                    int b = order[i * 2 + 1] - 1;
                    firstRound[i].SetSlot(0, a < seeded.Count ? seeded[a].Id : null);
                    firstRound[i].SetSlot(1, b < seeded.Count ? seeded[b].Id : null);
                }
                return;
            }

            for (int i = 0; i < seeded.Count; i++)
            {
                int node = i % firstRound.Count;
                int slot = i / firstRound.Count;
                if (slot >= arity) break;               // more entrants than the bracket seats
                firstRound[node].SetSlot(slot, seeded[i].Id);
            }
        }

        /// <summary>
        /// Resolves every match with one side into a bye and pushes its winner forward, repeating
        /// until nothing changes - an all-bye branch cascades several rounds deep.
        /// </summary>
        public static void PropagateByes(List<BracketMatch> matches)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var m in matches)
                {
                    if (m.IsClosed) continue;

                    var filled = m.ActiveSides;

                    // Too many to walk over: this one has to be played.
                    if (filled.Count > Math.Max(1, m.AdvanceCount)) continue;

                    // Only once nothing more can arrive. Without this the third-place match waited
                    // forever when a semifinal was a walkover and produced no loser.
                    if (!FeedersResolved(matches, m)) continue;

                    if (filled.Count == 0)
                    {
                        // Nobody is coming: close it as void rather than leave an undecidable node.
                        if (m.Round == 0 && !m.IsThirdPlace) continue;   // an empty first-round slot is just a bye elsewhere
                        m.State = BracketMatchState.Void;
                        m.ResultKind = "void";
                        changed = true;
                        continue;
                    }

                    m.WinnerEntrantIds = new List<string>(filled);
                    m.State = BracketMatchState.Bye;
                    m.ResultKind = "bye";
                    Advance(matches, m);
                    changed = true;
                }
            }
        }

        /// <summary>
        /// True when every match that could still put somebody into this one is decided - counting both
        /// routes: winner feeds and the loser feed into the third-place match.
        /// </summary>
        private static bool FeedersResolved(List<BracketMatch> matches, BracketMatch m)
        {
            foreach (var other in matches)
            {
                if (other.Id == m.Id || other.IsClosed) continue;
                foreach (var feed in other.Feeds)
                    if (feed.MatchId == m.Id) return false;
                if (other.LoserFeedsMatchId == m.Id) return false;
            }
            return true;
        }

        /// <summary>
        /// Writes a decided match's winner into its parent slot (and its loser into the third-place
        /// match when routed there). Idempotent.
        /// </summary>
        public static void Advance(List<BracketMatch> matches, BracketMatch m)
        {
            // One seat per advancing place: the winner into Feeds[0], the runner-up into Feeds[1].
            for (int place = 0; place < m.WinnerEntrantIds.Count && place < m.Feeds.Count; place++)
            {
                var feed = m.Feeds[place];
                if (feed == null || feed.MatchId < 0) continue;
                var parent = matches.Find(x => x.Id == feed.MatchId);
                parent?.SetSlot(feed.Slot, m.WinnerEntrantIds[place]);
            }

            if (m.LoserFeedsMatchId >= 0 && !string.IsNullOrEmpty(m.LoserEntrantId))
            {
                var third = matches.Find(x => x.Id == m.LoserFeedsMatchId);
                third?.SetSlot(m.LoserFeedsSlot, m.LoserEntrantId);
            }
        }

        /// <summary>Human label for a round, used in chat and in both bracket renderers.</summary>
        public static string RoundLabel(BracketMatch m, int totalRounds)
        {
            if (m.IsThirdPlace) return "3rd place";
            int fromEnd = totalRounds - 1 - m.Round;
            switch (fromEnd)
            {
                case 0: return "Final";
                case 1: return "Semifinal";
                case 2: return "Quarterfinal";
                default: return "Round " + (m.Round + 1);
            }
        }

        public static int TotalRounds(IReadOnlyList<BracketMatch> matches)
        {
            int max = 0;
            foreach (var m in matches) if (m.Round > max) max = m.Round;
            return max + 1;
        }

        /// <summary>Fisher-Yates, with the caller's Random so seeding stays reproducible when it wants to be.</summary>
        public static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
