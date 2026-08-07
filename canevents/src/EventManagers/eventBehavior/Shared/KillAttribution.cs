using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    public struct KillInfo
    {
        public string KillerName;
        public string KillerUid;
        public int KillerTeam;
        public int VictimTeam;
        public bool TeamKill;
        public bool HasKiller;
    }

    public static class KillAttribution
    {
        public static KillInfo Resolve(RunningEvent runningEvent, IServerPlayer victim, DamageSource source)
        {
            var info = new KillInfo
            {
                KillerName = "the arena",
                KillerUid = null,
                KillerTeam = -1,
                VictimTeam = -1,
                TeamKill = false,
                HasKiller = false,
            };

            string victimUid = victim.PlayerUID;

            // GetCauseEntity(), not SourceEntity: for a projectile SourceEntity is the ARROW and
            // CauseEntity the archer, so SourceEntity credited every ranged kill to "the arena".
            var cause = source?.GetCauseEntity();
            if (cause == null) return info;

            // A bot kills like anyone else. Only the persistent leaderboard cares about the
            // difference (see BotIdentity), and that check lives at the recording sites.
            string killerUid = Bots.BotMatchLink.UidOf(cause);
            if (killerUid == null) return info;

            if (cause is EntityPlayer killerEntity)
            {
                var killerPlayer = killerEntity.Player as IServerPlayer;
                if (killerPlayer == null) return info;
                info.KillerName = killerPlayer.PlayerName;
            }
            else
            {
                if (!Bots.BotIdentity.IsBot(killerUid)) return info;
                info.KillerName = cause.GetName();
            }

            info.KillerUid = killerUid;
            info.HasKiller = true;

            info.VictimTeam = runningEvent.TeamIndexOf(victimUid);
            info.KillerTeam = runningEvent.TeamIndexOf(info.KillerUid);
            info.TeamKill = info.VictimTeam >= 0
                && info.VictimTeam == info.KillerTeam
                && info.KillerUid != victimUid;

            return info;
        }
    }
}
