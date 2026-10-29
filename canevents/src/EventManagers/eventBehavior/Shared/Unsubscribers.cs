using System;
using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // Collects the -= side of each subscription. Call UnhookAll first in OnEnd: a leaked
    // OnEntityDeath or damage hook carries over into the next match.
    public sealed class Unsubscribers
    {
        private readonly List<Action> undo = new List<Action>();

        public void Add(Action unsubscribe)
        {
            if (unsubscribe != null) undo.Add(unsubscribe);
        }

        public bool Any => undo.Count > 0;

        // Reverse order; safe to call twice.
        public void UnhookAll()
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                try { undo[i]?.Invoke(); }
                catch { /* already gone */ }
            }
            undo.Clear();
        }
    }
}
