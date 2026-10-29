using System;
using System.Collections.Generic;
using canevents.src.EventPlacesManagers.Places;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace canevents.src.EventPlacesManagers
{
    public static class TemplateStamper
    {
        // A failed placement removes the place again. customize runs before the first save.
        public static EventPlace Stamp(ArenaTemplate template, BlockPos origin, bool placeBlocks,
            DefaultEventPlacesManager places, SchematicPlacer placer, out string error,
            Action<EventPlace> customize = null)
        {
            error = null;
            var tags = new List<string>();
            if (template.PlaceTypes != null)
            {
                foreach (var typeName in template.PlaceTypes)
                {
                    var tag = PlaceTags.Normalize(typeName);
                    if (!string.IsNullOrEmpty(tag)) tags.Add(tag);
                }
            }

            origin = origin.Copy();
            var place = new EventPlace
            {
                Name = template.Code,
                placeTypes = tags.ToArray(),
                SchematicOnDemand = template.OnDemand,
                SchematicTemplateCode = template.OnDemand ? template.Code : null,
                SchematicOrigin = template.OnDemand ? origin.Copy() : null
            };

            TemplateLayout.Apply(template, place, origin, placer.GetSchematicSize(template));
            customize?.Invoke(place);

            if (!places.AddPlace(place))
            {
                error = Lang.Get("canevents:place-create-failed");
                return null;
            }
            if (!placeBlocks) return place;

            var result = placer.TryPlace(template, origin, place.Id);
            if (result == SchematicPlaceResult.Placed) return place;

            places.DeletePlace(place.Id);
            error = result == SchematicPlaceResult.ChunksNotLoaded
                ? Lang.Get("canevents:tpl-area-not-loaded")
                : Lang.Get("canevents:tpl-schematic-place-failed", template.SchematicPath);
            return null;
        }
    }
}
