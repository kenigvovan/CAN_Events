using System;
using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// Holds the "take it back off" half of every subscription a behavior makes, so OnEnd is one
    /// call instead of a list that has to be kept in step with OnStart.
    ///
    /// A leaked subscription is not a tidiness problem: a stale OnEntityDeath keeps a finished match
    /// reacting to deaths in the next one, and a stale damage hook silences damage for everybody.
    /// OnEnd is wrapped in a try/catch per behavior, so anything that throws while announcing a
    /// winner would skip the unhooking that came after it - which is why this is meant to be the
    /// FIRST thing OnEnd does.
    ///
    /// Deliberately not a generic event wrapper: the caller writes the plain `+=` it would have
    /// written anyway and hands over the matching `-=` as a closure, which reads the same as before
    /// and cannot get the two out of step.
    /// </summary>
    public sealed class Unsubscribers
    {
        private readonly List<Action> undo = new List<Action>();

        /// <summary>Records how to undo a subscription that was just made.</summary>
        public void Add(Action unsubscribe)
        {
            if (unsubscribe != null) undo.Add(unsubscribe);
        }

        /// <summary>True once something has been recorded - for a caller with its own "already
        /// hooked" guard to keep.</summary>
        public bool Any => undo.Count > 0;

        /// <summary>
        /// Undoes everything, most recent first, and forgets it. Safe to call twice, and one
        /// throwing handler does not strand the rest.
        /// </summary>
        public void UnhookAll()
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i]?.Invoke(); }
                catch { /* a subscription that is already gone is exactly what we wanted */ }
            }
            undo.Clear();
        }
    }
}
