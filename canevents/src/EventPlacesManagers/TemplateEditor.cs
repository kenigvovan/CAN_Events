using System;
using System.Collections.Generic;
using System.Linq;
using canevents.src.EventManagers.eventBehavior.Shared;
using canevents.src.EventPlacesManagers.Places;
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
    }

    public class TemplateEditor
    {
        public Dictionary<string, TemplateEditSession> Sessions = new Dictionary<string, TemplateEditSession>();

        private readonly List<ArenaTemplate> templates;
        private readonly DefaultEventPlacesManager placeManager;
        private readonly SchematicPlacer placer;
        private readonly PlaceConfigService placeConfig;

        public TemplateEditor(List<ArenaTemplate> templates, DefaultEventPlacesManager placeManager, SchematicPlacer placer,
            PlaceConfigService placeConfig)
        {
            this.templates = templates;
            this.placeManager = placeManager;
            this.placer = placer;
            this.placeConfig = placeConfig;
        }

        public TemplateEditSession GetSession(IServerPlayer player)
        {
            Sessions.TryGetValue(player.PlayerUID, out var s);
            return s;
        }

        // The test place is the source of truth, whatever edited it.
        public List<SpawnOffset> SpawnOffsetsOf(TemplateEditSession session)
        {
            var spawns = session == null ? null : placeManager.FindById(session.PlaceId)?.spawnPositions;
            if (spawns == null) return new List<SpawnOffset>();
            return spawns.Select(p => new SpawnOffset
            {
                X = p.X - session.Origin.X,
                Y = p.Y - session.Origin.Y,
                Z = p.Z - session.Origin.Z
            }).ToList();
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
            bool hasOwnSpawns = template.SpawnOffsets != null && template.SpawnOffsets.Length > 0;

            // Blocks are placed even for on-demand templates, as a preview.
            var place = TemplateStamper.Stamp(template, origin, placeBlocks: true, placeManager, placer, out var error, p =>
            {
                // Disabled until Finish so no match picks the half-built arena.
                p.Enabled = false;
                // Drop Apply's fallback spawn at the origin.
                if (!hasOwnSpawns) p.spawnPositions.Clear();
            });
            if (place == null) return (false, error);

            Sessions[player.PlayerUID] = new TemplateEditSession
            {
                TemplateCode = template.Code,
                Origin = origin,
                PlaceId = place.Id
            };
            return (true, Lang.Get("canevents:tpl-editor-editing", template.Code, place.Id, place.spawnPositions.Count));
        }

        public (bool ok, string message) AddSpawn(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            var place = placeManager.FindById(session.PlaceId);
            if (place == null) return (false, Lang.Get("canevents:place-not-found-id", session.PlaceId));

            var pos = player.Entity.Pos.AsBlockPos;
            place.spawnPositions ??= new List<BlockPos>();
            place.spawnPositions.Add(pos.Copy());
            placeManager.SavePlace(place);
            return (true, Lang.Get("canevents:tpl-editor-spawn-added",
                pos.X - session.Origin.X, pos.Y - session.Origin.Y, pos.Z - session.Origin.Z, place.spawnPositions.Count));
        }

        public (bool ok, string message) ClearSpawns(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            var place = placeManager.FindById(session.PlaceId);
            if (place != null)
            {
                place.spawnPositions?.Clear();
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
            var place = placeManager.FindById(session.PlaceId);
            if (place == null) return (false, Lang.Get("canevents:place-not-found-id", session.PlaceId));

            TemplateLayout.Capture(template, place, session.Origin);
            template.PlaceTypes = place.placeTypes?.ToArray() ?? new string[0];

            if (!TemplateStore.Write(template, out var writeError))
                return (false, Lang.Get("canevents:tpl-editor-write-failed", writeError));
            return (true, Lang.Get("canevents:tpl-editor-saved", template.Code, TemplateLayout.Describe(template)));
        }

        public (bool ok, string message) Cancel(IServerPlayer player)
        {
            if (!Sessions.TryGetValue(player.PlayerUID, out var session))
            {
                return (false, Lang.Get("canevents:tpl-editor-no-session"));
            }
            long id = session.PlaceId;
            var place = placeManager.FindById(id);
            if (place != null)
            {
                var result = placeConfig.DeletePlace(place);
                if (!result.Ok) return (false, result.Message);
            }
            else if (placer.HasSavedData(id))
            {
                ArenaBlockJournal.RunAfterRestore(id, () => placer.Restore(id));
            }
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
            long id = session.PlaceId;

            // An on-demand match picking it before the restore below is refused by the placer.
            var place = placeManager.FindById(id);
            if (place != null && !place.Enabled)
            {
                place.Enabled = true;
                placeManager.SavePlace(place);
            }

            if (template != null && template.OnDemand && placer.HasSavedData(id))
            {
                ArenaBlockJournal.RunAfterRestore(id, () => placer.Restore(id));
                return (true, Lang.Get("canevents:tpl-editor-closed-ondemand"));
            }
            return (true, Lang.Get("canevents:tpl-editor-closed"));
        }
    }
}
