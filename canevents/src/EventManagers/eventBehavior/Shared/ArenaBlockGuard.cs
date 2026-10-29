using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public enum BlockBreakVerdict
    {
        NotGuarded,
        BreakWithoutDrop,
        Keep
    }

    // Call Release first thing in OnEnd.
    public sealed class ArenaBlockGuard
    {
        private readonly Unsubscribers subscriptions = new Unsubscribers();

        public bool Active => subscriptions.Any;

        // Creative is exempt for admin testing.
        public void DenyEditsByActive(RunningEvent match, string claimant, string errorCode, string errorLangKey)
        {
            if (match?.sapi == null) return;

            CanPlaceOrBreakDelegate handler = (IServerPlayer byPlayer, BlockSelection sel, out string claimedBy) =>
            {
                claimedBy = null;
                if (byPlayer == null || !match.IsActive(byPlayer.PlayerUID)) return true;
                if (byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;
                claimedBy = claimant;
                byPlayer.SendIngameError(errorCode, Lang.Get(errorLangKey));
                return false;
            };

            var events = match.sapi.Event;
            events.CanPlaceOrBreakBlock += handler;
            subscriptions.Add(() => events.CanPlaceOrBreakBlock -= handler);
        }

        public void GuardBreaks(RunningEvent match, System.Func<IServerPlayer, BlockPos, BlockBreakVerdict> rule,
            string errorCode = null, string errorLangKey = null)
        {
            if (match?.sapi == null || rule == null) return;

            BlockBreakDelegate handler = (IServerPlayer byPlayer, BlockSelection sel,
                ref float dropMult, ref EnumHandling handling) =>
            {
                if (sel?.Position == null) return;

                var verdict = rule(byPlayer, sel.Position);
                if (verdict == BlockBreakVerdict.NotGuarded) return;

                dropMult = 0f;
                if (verdict == BlockBreakVerdict.BreakWithoutDrop) return;

                handling = EnumHandling.PreventDefault;
                if (errorCode != null) byPlayer?.SendIngameError(errorCode, Lang.Get(errorLangKey));
            };

            var events = match.sapi.Event;
            events.BreakBlock += handler;
            subscriptions.Add(() => events.BreakBlock -= handler);
        }

        public void Release() => subscriptions.UnhookAll();
    }
}
