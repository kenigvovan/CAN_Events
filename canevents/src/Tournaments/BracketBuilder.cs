using System;
using System.Collections.Generic;

namespace canevents.src.Tournaments
{
    // Single-elimination bracket, pure logic with no VS types. Nodes are N-way (arity 2 = duels).
    public static class BracketBuilder
    {
        // Standard seed order for 2^k slots, e.g. 1,8,4,5,2,7,3,6. Duels only.
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

        // seeded[0] = seed 1. Assigns Seed and resolves first-round byes.
        public static List<BracketMatch> Build(IReadOnlyList<Entrant> seeded, bool thirdPlace,
            int arity = 2, int advance = 1)
        {
            var matches = new List<BracketMatch>();
            if (seeded == null || seeded.Count < 2) return matches;
            if (arity < 2) arity = 2;
            advance = Math.Max(1, Math.Min(advance, arity - 1));

            for (int i = 0; i < seeded.Count; i++) seeded[i].Seed = i + 1;

            int nextId = 1;
            // Duels need a full power-of-two draw for SeedOrder, otherwise tail seeds get dropped.
            int firstRoundNodes = arity == 2
                ? Math.Max(1, NextPow2(seeded.Count) / 2)
                : Math.Max(1, (int)Math.Ceiling(seeded.Count / (double)arity));
            var firstRound = new List<BracketMatch>();

            for (int i = 0; i < firstRoundNodes; i++)
                firstRound.Add(NewNode(ref nextId, 0, i, arity, advance, matches));

            Distribute(seeded, firstRound, arity, firstRoundNodes * arity);

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
                    // Knockout: neighbours meet (child i -> node i/arity).
                    for (int i = 0; i < previous.Count; i++)
                    {
                        var target = current[i / arity];
                        previous[i].Feeds.Clear();
                        previous[i].Feeds.Add(new FeedTarget { MatchId = target.Id, Slot = i % arity });
                    }
                }
                else
                {
                    // Heats: winners spread first, then runners-up, so heat-mates don't meet right away.
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

                TrimUnusedSlots(current, advancing);

                previous = current;
                round++;
                if (round > 16) break;                 // guard against a bad config
            }

            if (previous.Count == 1)
            {
                previous[0].IsFinal = true;
                previous[0].AdvanceCount = 1;
                previous[0].Feeds.Clear();
            }

            int rounds = round;

            // Third-place match only for duel brackets.
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

        // Last round may not fill a whole node; drop the seats nobody will take.
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

        // Duels use seed order (byes go to top seeds), N-way nodes get round-robin.
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

        // Loops until stable, bye chains can cascade several rounds.
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

                    if (filled.Count > Math.Max(1, m.AdvanceCount)) continue;

                    // Wait for feeders, or a walkover semi leaves the 3rd place match stuck.
                    if (!FeedersResolved(matches, m)) continue;

                    if (filled.Count == 0)
                    {
                        // Never-seated first-round nodes are skipped; emptied ones close as void.
                        if (m.Round == 0 && !m.IsThirdPlace && m.FilledCount == 0) continue;
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

        // Not "all slots filled": a slot fed by a void match stays empty forever.
        public static bool ReadyToPlay(List<BracketMatch> matches, BracketMatch m)
            => m.ActiveSides.Count >= 2 && !AwaitingFeeders(matches, m);

        public static bool AwaitingFeeders(List<BracketMatch> matches, BracketMatch m)
            => m.FilledCount != m.Slots.Count && !FeedersResolved(matches, m);

        // Checks winner feeds and the loser feed into the 3rd place match.
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

        // Idempotent.
        public static void Advance(List<BracketMatch> matches, BracketMatch m)
        {
            for (int place = 0; place < m.WinnerEntrantIds.Count && place < m.Feeds.Count; place++)
            {
                var feed = m.Feeds[place];
                if (feed == null || feed.MatchId < 0) continue;
                var parent = matches.Find(x => x.Id == feed.MatchId);
                // Never rewrite a closed node.
                if (parent != null && !parent.IsClosed) parent.SetSlot(feed.Slot, m.WinnerEntrantIds[place]);
            }

            if (m.LoserFeedsMatchId >= 0 && !string.IsNullOrEmpty(m.LoserEntrantId))
            {
                var third = matches.Find(x => x.Id == m.LoserFeedsMatchId);
                if (third != null && !third.IsClosed) third.SetSlot(m.LoserFeedsSlot, m.LoserEntrantId);
            }
        }

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

        // Fisher-Yates.
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
