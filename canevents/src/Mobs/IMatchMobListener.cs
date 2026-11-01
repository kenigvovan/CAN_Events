using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace canevents.src.Mobs
{
    // Implemented by event behaviors that care about their match's mobs dying.
    public interface IMatchMobListener
    {
        void OnMobDeath(Entity mob, DamageSource source);
    }
}
