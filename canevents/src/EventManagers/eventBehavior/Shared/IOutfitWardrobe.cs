using System.Collections.Generic;

namespace canevents.src.EventManagers.eventBehavior.Shared
{
    /// <summary>
    /// The one behavior that owns this match's wardrobe and decides who wears what. Implemented by
    /// KillScoringEB; ArenaBotsEB asks it so bots are dealt from the SAME deal as the players.
    ///
    /// Without this the bots dressed themselves from their own attribute by a hash of their uid,
    /// which can land on an outfit a player already wears - and "everyone looks different" has to
    /// hold across the whole line-up or it is not a clue anyone can rely on.
    /// </summary>
    public interface IOutfitWardrobe
    {
        /// <summary>The outfits in play this match (managed store if any, else the event JSON).</summary>
        List<string[]> Wardrobe { get; }

        /// <summary>This uid's outfit, or -1 when it has none (dress by the stable hash then).</summary>
        int OutfitIndexOf(string uid);
    }
}
