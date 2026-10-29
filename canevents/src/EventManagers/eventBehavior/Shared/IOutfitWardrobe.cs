using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    // ArenaBotsEB uses this so bots get outfits from the same deal as players (no duplicates).
    public interface IOutfitWardrobe
    {
        List<string[]> Wardrobe { get; }

        // -1 = none, fall back to the uid hash.
        int OutfitIndexOf(string uid);
    }
}
