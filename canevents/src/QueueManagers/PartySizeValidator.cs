using canevents.src.EventManagers;
using Vintagestory.API.Config;

namespace canevents.src.QueueManagers
{
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

        // By-parts modes: solo only, the manager builds teams.
        public static (bool ok, string message) SoloOnly(Registration reg)
            => reg.PlayersGuids.Count > 1
                ? (false, Lang.Get("canevents:queue-solo-only"))
                : (true, (string)null);
    }
}
