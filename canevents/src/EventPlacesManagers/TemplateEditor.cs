using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using canevents.src.EventPlacesManagers.Places;
using Newtonsoft.Json;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace canevents.src.EventPlacesManagers
{
    public class TemplateEditSession
    {
        public string TemplateCode;
        public BlockPos Origin;
        public long PlaceId;
        public List<SpawnOffset> SpawnOffsets = new List<SpawnOffset>();
    }

    public class TemplateEditor
    {
        public Dictionary<string, TemplateEditSession> Sessions = new Dictionary<string, TemplateEditSession>();

        private readonly List<ArenaTemplate> templates;
        private readonly DefaultEventPlacesManager placeManager;
        private readonly SchematicPlacer placer;

        public TemplateEditor(List<ArenaTemplate> templates, DefaultEventPlacesManager placeManager, SchematicPlacer placer)
        {
            this.templates = templates;
            this.placeManager = placeManager;
            this.placer = placer;
        }

        public TemplateEditSession GetSession(IServerPlayer player)
        {
            Sessions.TryGetValue(player.PlayerUID, out var s);
            return s;
        }

        public (bool ok, string message) Start(IServerPlayer player, string code)
        {
            var template = templates.Find(t => t.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (template == null)
            {
                return (false, Lang.Get("canevents:tpl-not-found", code));
            }
            if (Sessions.ContainsKey(player.PlayerUID))
            {
                return (false, Lang.Get("canevents:tpl-editor-session-exists"));
            }

            var origin = player.Entity.Pos.AsBlockPos.Copy();
            var placeTypes = new List<string>();
            if (template.PlaceTypes != null)
            {
                foreach (var tn in template.PlaceTypes)
                {
                    var tag = PlaceTags.Normalize(tn);
                    if (!string.IsNullOrEmpty(tag)) placeTypes.Add(tag);
                }
            }

            var place = new EventPlace
            {
                Name = template.Code,
                placeTypes = placeTypes.ToArray(),
                SchematicOnDemand = template.OnDemand,
                SchematicTemplateCode = template.OnDemand ? template.Code : null,
                SchematicOrigin = template.OnDemand ? origin.Copy() : null
            };

            var offsets = template.SpawnOffsets != null
                ? template.SpawnOffsets.Select(o => new SpawnOffset { X = o.X, Y = o.Y, Z = o.Z }).ToList()
                : new List<SpawnOffset>();
            foreach (var o in offsets)
            {
                place.spawnPositions.Add(new BlockPos(origin.X + o.X, origin.Y + o.Y, origin.Z + o.Z, origin.dimension));
            }

            if (!placeManager.AddPlace(place))
            {
                return (false, Lang.Get("canevents:place-create-failed"));
            }
            if (!placer.Place(template, origin, place.Id))
            {
                placeManager.DeletePlace(place.Id);
                return (false, Lang.Get("canevents:tpl-schematic-place-failed", template.SchematicPath));
            }

            // Parkour only: it needs bounds to associate checkpoints with the place. Spleef also uses
            // bounds but expects the platform footprint (set via mark1/mark2), not the whole schematic.
            if (placeTypes.Contains(PlaceTags.Parkour))
            {
                var size = placer.GetSchematicSize(template);
                if (size.X > 0 && size.Y > 0 && size.Z > 0)
                {
                    place.HasBounds = true;
                    place.BoundsMin = origin.Copy();
                    place.BoundsMax = new BlockPos(
                        origin.X + size.X - 1,
                        origin.Y + size.Y - 1,
                        origin.Z + size.Z - 1,
                        origin.dimension);
                    placeManager.SavePlace(place);
                }
            }

            Sessions[player.PlayerUID] = new TemplateEditSession
            {
                TemplateCode = template.Code,
                Origin = origin,
                PlaceId = place.Id,
                SpawnOffsets = offsets
            };
            return (true, Lang.Get("canevents:tpl-editor-editing", template.Code, place.Id, offsets.Count));
        }

        public (bool ok, string message) AddSpawn(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            var pos = player.Entity.Pos.AsBlockPos;
            var offset = new SpawnOffset
            {
                X = pos.X - session.Origin.X,
                Y = pos.Y - session.Origin.Y,
                Z = pos.Z - session.Origin.Z
            };
            session.SpawnOffsets.Add(offset);

            var place = placeManager.FindById(session.PlaceId);
            if (place != null)
            {
                place.spawnPositions.Add(pos.Copy());
                placeManager.SavePlace(place);
            }
            return (true, Lang.Get("canevents:tpl-editor-spawn-added", offset.X, offset.Y, offset.Z, session.SpawnOffsets.Count));
        }

        public (bool ok, string message) ClearSpawns(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            session.SpawnOffsets.Clear();
            var place = placeManager.FindById(session.PlaceId);
            if (place != null)
            {
                place.spawnPositions.Clear();
                placeManager.SavePlace(place);
            }
            return (true, Lang.Get("canevents:tpl-editor-spawns-cleared"));
        }

        public (bool ok, string message) Save(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            var template = templates.Find(t => t.Code == session.TemplateCode);
            if (template == null)
            {
                return (false, Lang.Get("canevents:tpl-editor-vanished", session.TemplateCode));
            }
            template.SpawnOffsets = session.SpawnOffsets.ToArray();

            var dir = Path.Combine(GamePaths.ModConfig, "canevents", "arenas");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, template.Code + ".json");
            if (!Scoring.JsonStore.TryWriteAtomic(file, template, out var writeError))
                return (false, Lang.Get("canevents:tpl-editor-write-failed", writeError));
            return (true, Lang.Get("canevents:tpl-editor-saved", template.Code, template.SpawnOffsets.Length));
        }

        public (bool ok, string message) Cancel(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            if (placer.HasSavedData(session.PlaceId))
            {
                placer.Restore(session.PlaceId);
            }
            placeManager.DeletePlace(session.PlaceId);
            Sessions.Remove(player.PlayerUID);
            return (true, Lang.Get("canevents:tpl-editor-cancelled"));
        }

        public (bool ok, string message) Finish(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            var template = templates.Find(t => t.Code == session.TemplateCode);
            Sessions.Remove(player.PlayerUID);

            // On-demand arenas exist only while an event runs, so strip the preview blocks.
            if (template != null && template.OnDemand && placer.HasSavedData(session.PlaceId))
            {
                placer.Restore(session.PlaceId);
                return (true, Lang.Get("canevents:tpl-editor-closed-ondemand"));
            }
            return (true, Lang.Get("canevents:tpl-editor-closed"));
        }
    }
}
