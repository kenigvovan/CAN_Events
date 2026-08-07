using canevents.src.EventManagers;
using Vintagestory.API.Config;

namespace canevents.src.QueueManagers
{
    /// <summary>
    /// Checks a registration against the mode's party-size limits, with the player-facing wording.
    /// Shared by the FIFO and ranked managers, which otherwise assemble matches differently.
    /// </summary>
    public static class PartySizeValidator
    {
        public static (bool ok, string message) Check(Registration reg)
        {
            var eb = reg.Event.AssemblingEB;
            int count = reg.PlayersGuids.Count;

            if (count < eb.MinPartySize)
            {
                int needed = eb.MinPartySize - count;
                return eb.MinPartySize == eb.MaxPartySize
                    ? (false, Lang.Get("canevents:queue-party-too-small-exact", eb.MinPartySize, count, needed))
                    : (false, Lang.Get("canevents:queue-party-too-small-range", eb.MinPartySize, eb.MaxPartySize, count));
            }

            if (count > eb.MaxPartySize)
                return (false, Lang.Get("canevents:queue-party-too-large", eb.MaxPartySize, count));

            return (true, null);
        }

        /// <summary>
        /// The by-parts rule: register alone, the manager builds the teams (ranked and unranked).
        /// </summary>
        public static (bool ok, string message) SoloOnly(Registration reg)
            => reg.PlayersGuids.Count > 1
                ? (false, Lang.Get("canevents:queue-solo-only"))
                : (true, (string)null);
    }
}
