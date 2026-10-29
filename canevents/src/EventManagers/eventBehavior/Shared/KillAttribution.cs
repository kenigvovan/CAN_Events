using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
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
        public string VictimUid;

        public bool IsClean => HasKiller && !TeamKill && KillerUid != VictimUid;
    }

    public static class KillAttribution
    {
        public static KillInfo Resolve(RunningEvent runningEvent, string victimUid, DamageSource source)
        {
            var info = new KillInfo
            {
                KillerName = Lang.Get("canevents:killer-the-arena"),
                KillerUid = null,
                KillerTeam = -1,
                VictimTeam = -1,
                TeamKill = false,
                HasKiller = false,
                VictimUid = victimUid,
            };

            // For projectiles SourceEntity is the arrow; CauseEntity is the shooter.
            var cause = source?.GetCauseEntity();
            if (cause == null) return info;

            string killerUid = Bots.BotMatchLink.UidOf(cause);
            if (killerUid == null) return info;
            // Non-participants get no credit.
            if (!runningEvent.IsActive(killerUid)) return info;

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
