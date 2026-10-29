using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers
{
    // A claim over every arena an ArenaProtection event can use: nobody places or breaks blocks inside
    // its bounds. Admins in creative are exempt, so arenas can still be built and fixed.
    // Hooked into CanPlaceOrBreakBlock, the same check land claims answer, so other mods' tools respect it too.
    public sealed class ArenaProtection
    {
        private const long RefreshMs = 2000;

        // A place under protection and the events behind it.
        private sealed class Guarded
        {
            public EventPlace Place;
            // Some protecting event guards it between matches too.
            public bool Always;
            // Events that guard it only while their own match runs there.
            public HashSet<string> DuringMatchOf;
        }

        private readonly ICoreServerAPI sapi;
        private readonly CANEventManager manager;
        private CanPlaceOrBreakDelegate handler;

        private List<Guarded> guarded = new List<Guarded>();
        private long refreshedAtMs;
        private bool stale = true;

        public ArenaProtection(ICoreServerAPI sapi, CANEventManager manager)
        {
            this.sapi = sapi;
            this.manager = manager;
        }

        public void Start()
        {
            if (handler != null) return;
            handler = Check;
            sapi.Event.CanPlaceOrBreakBlock += handler;
        }

        public void Stop()
        {
            if (handler == null) return;
            sapi.Event.CanPlaceOrBreakBlock -= handler;
            handler = null;
        }

        private bool Check(IServerPlayer byPlayer, BlockSelection sel, out string claimedBy)
        {
            claimedBy = null;
            if (byPlayer == null || sel?.Position == null) return true;
            if (byPlayer.WorldData?.CurrentGameMode == EnumGameMode.Creative
                && byPlayer.HasPrivilege(Privilege.controlserver)) return true;

            if (ProtectedPlaceAt(sel.Position) == null) return true;

            // TestAccess runs this for mere checks too, so no message here: TryAccess shows
            // game:ingameerror-noprivilege-buildbreak-canevents-arena when an edit is refused.
            claimedBy = "custommessage-canevents-arena";
            return false;
        }

        // Null when nothing guards pos right now.
        public EventPlace ProtectedPlaceAt(BlockPos pos)
        {
            foreach (var g in Guards())
            {
                // X/Z like every other "inside the arena" check: the whole column, since parkour goes up.
                if (!PlaceBounds.Contains(g.Place, pos)) continue;

                // Only now, for a hit, look at what runs there: most edits are nowhere near an arena.
                var live = manager.RunningEventsSnapshot()
                    .Where(re => re.Place?.Id == g.Place.Id && re.EventState != EventState.Finished)
                    .ToList();
                // A place shared with an unprotected mode (Spleef) follows that mode while its match is on.
                if (live.Any(re => !Protects(re.Event))) continue;
                if (g.Always || live.Any(re => g.DuringMatchOf.Contains(re.Event.Code))) return g.Place;
            }
            return null;
        }

        private static bool Protects(events.CANEvent ev) => ev?.GetBehavior<ArenaProtectionEB>()?.Enabled == true;

        // Re-read every couple of seconds: places and events change at runtime, and a block edit is no
        // time to walk every event's tags. Disabled arenas stay protected; they come back as they were.
        private List<Guarded> Guards()
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (!stale && now - refreshedAtMs < RefreshMs) return guarded;
            refreshedAtMs = now;
            stale = false;

            var protecting = manager.Events
                .Select(e => (ev: e, beh: e.GetBehavior<ArenaProtectionEB>()))
                .Where(x => x.beh != null && x.beh.Enabled && x.ev.EventTypes != null && x.ev.EventTypes.Count > 0)
                .ToList();

            var result = new List<Guarded>();
            if (protecting.Count > 0)
            {
                foreach (var place in manager.eventPlacesManager.EventPlaces)
                {
                    if (!PlaceBounds.Has(place)) continue;
                    var mine = protecting.Where(x => place.HasAllTags(x.ev.EventTypes)).ToList();
                    if (mine.Count == 0) continue;
                    result.Add(new Guarded
                    {
                        Place = place,
                        Always = mine.Any(x => x.beh.BetweenMatches),
                        DuringMatchOf = new HashSet<string>(mine.Select(x => x.ev.Code), StringComparer.OrdinalIgnoreCase)
                    });
                }
            }
            guarded = result;
            return guarded;
        }

        // Next check re-reads, e.g. right after a place or event edit.
        public void Invalidate() => stale = true;
    }
}
