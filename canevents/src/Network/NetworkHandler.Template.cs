using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace canevents.src.Network
{
    // Server-side arena template & schematic handling: the interactive template editor (spawn-offset
    // capture), placing/removing schematic-backed arenas, and templates from raw schematic files.
    public partial class ServerNetworkHandler
    {
        private void OnTplAction(IServerPlayer player, TplActionMsg msg)
        {
            if (!RequireAdmin(player)) return;
            var editor = eventManager.TemplateEditor;
            (bool ok, string message) result;
            switch (msg.Action)
            {
                case TplAction.Start: result = editor.Start(player, msg.TemplateCode); break;
                case TplAction.AddSpawn: result = editor.AddSpawn(player); break;
                case TplAction.Clear: result = editor.ClearSpawns(player); break;
                case TplAction.Save: result = editor.Save(player); break;
                case TplAction.Cancel: result = editor.Cancel(player); break;
                case TplAction.Finish: result = editor.Finish(player); break;
                case TplAction.RequestState: result = (true, ""); break;
                default: result = (false, Lang.Get("canevents:tpl-unknown-action")); break;
            }

            Reply(player, result.ok, result.message);

            SendTplState(player);
            if (msg.Action == TplAction.Start || msg.Action == TplAction.Cancel)
            {
                SendPlaceListTo(player);
            }
        }

        private void SendTplState(IServerPlayer player)
        {
            var session = eventManager.TemplateEditor.GetSession(player);
            if (session == null)
            {
                serverChannel.SendPacket(new TplSessionStateMsg { InSession = false }, player);
                return;
            }
            serverChannel.SendPacket(new TplSessionStateMsg
            {
                InSession = true,
                TemplateCode = session.TemplateCode,
                PlaceId = session.PlaceId,
                OriginX = session.Origin.X,
                OriginY = session.Origin.Y,
                OriginZ = session.Origin.Z,
                OffsetsX = session.SpawnOffsets.Select(o => o.X).ToArray(),
                OffsetsY = session.SpawnOffsets.Select(o => o.Y).ToArray(),
                OffsetsZ = session.SpawnOffsets.Select(o => o.Z).ToArray()
            }, player);
        }

        private void OnRequestTemplateList(IServerPlayer player, RequestTemplateListMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var templates = eventManager.ArenaTemplates;
            var entries = new List<TemplateListEntryMsg>();

            foreach (var t in templates)
            {
                entries.Add(new TemplateListEntryMsg
                {
                    Code = t.Code,
                    Description = t.Description ?? "",
                    PlaceTypes = t.PlaceTypes != null ? string.Join(", ", t.PlaceTypes) : "",
                    OnDemand = t.OnDemand
                });
            }

            serverChannel.SendPacket(new TemplateListMsg { Templates = entries.ToArray() }, player);
        }

        private void OnPlaceTemplate(IServerPlayer player, PlaceTemplateMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var template = eventManager.ArenaTemplates.Find(t =>
                t.Code.Equals(msg.TemplateCode, StringComparison.OrdinalIgnoreCase));

            if (template == null)
            {
                ReplyFail(player, Lang.Get("canevents:tpl-not-found", msg.TemplateCode));
                return;
            }

            var placeManager = eventManager.eventPlacesManager;
            var placer = eventManager.SchematicPlacer;

            // Create the EventPlace first, to get an id.
            var types = new List<string>();
            if (template.PlaceTypes != null)
            {
                foreach (var typeName in template.PlaceTypes)
                {
                    var tag = EventPlacesManagers.Places.PlaceTags.Normalize(typeName);
                    if (!string.IsNullOrEmpty(tag)) types.Add(tag);
                }
            }

            var origin = player.Entity.Pos.AsBlockPos;
            var place = new EventPlacesManagers.Places.EventPlace
            {
                Name = template.Code,
                placeTypes = types.ToArray()
            };

            if (template.SpawnOffsets != null)
            {
                foreach (var offset in template.SpawnOffsets)
                {
                    place.spawnPositions.Add(new Vintagestory.API.MathTools.BlockPos(
                        origin.X + offset.X,
                        origin.Y + offset.Y,
                        origin.Z + offset.Z,
                        origin.dimension
                    ));
                }
            }
            else
            {
                place.spawnPositions.Add(origin.Copy());
            }

            if (template.OnDemand)
            {
                place.SchematicOnDemand = true;
                place.SchematicTemplateCode = template.Code;
                place.SchematicOrigin = origin.Copy();
            }

            if (!placeManager.AddPlace(place))
            {
                ReplyFail(player, Lang.Get("canevents:place-create-failed"));
                return;
            }

            // Persistent arenas get their blocks now; on-demand ones at match start.
            if (!template.OnDemand)
            {
                if (!placer.Place(template, origin, place.Id))
                {
                    placeManager.DeletePlace(place.Id);
                    ReplyFail(player, Lang.Get("canevents:tpl-schematic-place-failed", template.SchematicPath));
                    return;
                }
            }

            ReplyOk(player, template.OnDemand
                ? Lang.Get("canevents:tpl-ondemand-registered", template.Code, place.Id)
                : Lang.Get("canevents:tpl-arena-placed", template.Code, place.Id));
            SendPlaceListTo(player);
        }

        private void OnRemovePlaceWithRestore(IServerPlayer player, RemovePlaceWithRestoreMsg msg)
        {
            if (!RequireAdmin(player)) return;
            if (!TryResolvePlace(player, msg.PlaceId, out var place)) return;

            var placeManager = eventManager.eventPlacesManager;
            var placer = eventManager.SchematicPlacer;

            // Not RequireNotOccupied: this one explains the restore-specific reason.
            if (place.Occupied)
            {
                ReplyFail(player, Lang.Get("canevents:tpl-arena-in-use"));
                return;
            }

            if (placer.HasSavedData(msg.PlaceId))
            {
                placer.Restore(msg.PlaceId);
            }

            placeManager.DeletePlace(msg.PlaceId);

            ReplyOk(player, Lang.Get("canevents:tpl-place-removed-restored", msg.PlaceId));
            SendPlaceListTo(player);
        }

        private void OnRequestSchematicList(IServerPlayer player, RequestSchematicListMsg msg)
        {
            if (!RequireAdmin(player)) return;

            var dir = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "schematics");
            var entries = new List<SchematicListEntryMsg>();
            if (System.IO.Directory.Exists(dir))
            {
                var existingPaths = new HashSet<string>(
                    eventManager.ArenaTemplates
                        .Where(t => !string.IsNullOrEmpty(t.SchematicPath))
                        .Select(t => t.SchematicPath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.json"))
                {
                    int sx = 0, sy = 0, sz = 0;
                    try
                    {
                        var json = System.IO.File.ReadAllText(file);
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
                        sx = jo.Value<int?>("SizeX") ?? 0;
                        sy = jo.Value<int?>("SizeY") ?? 0;
                        sz = jo.Value<int?>("SizeZ") ?? 0;
                    }
                    catch (Exception ex)
                    {
                        sapi.World.Logger.Warning("Schematic {0} header parse failed: {1}", file, ex.Message);
                    }

                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    entries.Add(new SchematicListEntryMsg
                    {
                        Name = name,
                        SizeX = sx,
                        SizeY = sy,
                        SizeZ = sz,
                        HasTemplate = existingPaths.Contains(name)
                    });
                }
            }

            serverChannel.SendPacket(new SchematicListMsg { Schematics = entries.ToArray() }, player);
        }

        /// <summary>A name safe to paste into a path. Deliberately a whitelist - blacklisting "\"
        /// and ".." misses drive letters, alternate data streams and other separators.</summary>
        private static bool IsSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
            if (name[0] == '.') return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return false;
            return true;
        }

        private void OnCreateTemplateFromSchematic(IServerPlayer player, CreateTemplateFromSchematicMsg msg)
        {
            if (!RequireAdmin(player)) return;

            if (string.IsNullOrWhiteSpace(msg.SchematicName) || string.IsNullOrWhiteSpace(msg.TemplateCode))
            {
                ReplyFail(player, Lang.Get("canevents:schematic-name-code-required"));
                return;
            }

            // Both strings become file names below, so client-supplied path syntax would turn a
            // template save into a write anywhere the server can reach. Checked, never rewritten:
            // a silently "cleaned" name would not be the one the admin typed.
            if (!IsSafeFileName(msg.SchematicName) || !IsSafeFileName(msg.TemplateCode))
            {
                ReplyFail(player, Lang.Get("canevents:schematic-name-invalid"));
                return;
            }

            var schematicFile = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig,
                "canevents", "schematics", msg.SchematicName + ".json");
            if (!System.IO.File.Exists(schematicFile))
            {
                ReplyFail(player, Lang.Get("canevents:schematic-file-not-found", msg.SchematicName));
                return;
            }

            if (eventManager.ArenaTemplates.Any(t => t.Code.Equals(msg.TemplateCode, StringComparison.OrdinalIgnoreCase)))
            {
                ReplyFail(player, Lang.Get("canevents:tpl-already-exists", msg.TemplateCode));
                return;
            }

            var template = new EventPlacesManagers.ArenaTemplate
            {
                Code = msg.TemplateCode,
                SchematicPath = msg.SchematicName,
                PlaceTypes = msg.PlaceTypes ?? Array.Empty<string>(),
                SpawnOffsets = Array.Empty<EventPlacesManagers.SpawnOffset>(),
                Description = "Created from schematic '" + msg.SchematicName + "'",
                OnDemand = msg.OnDemand
            };

            var dir = System.IO.Path.Combine(Vintagestory.API.Config.GamePaths.ModConfig, "canevents", "arenas");
            string writeError = null;
            try
            {
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var file = System.IO.Path.Combine(dir, template.Code + ".json");
                Scoring.JsonStore.TryWriteAtomic(file, template, out writeError);
            }
            catch (Exception ex)
            {
                writeError = ex.Message;
            }
            if (writeError != null)
            {
                ReplyFail(player, Lang.Get("canevents:tpl-write-failed", writeError));
                return;
            }

            eventManager.ArenaTemplates.Add(template);

            ReplyOk(player, Lang.Get("canevents:tpl-created", template.Code));

            OnRequestTemplateList(player, new RequestTemplateListMsg());
            OnRequestSchematicList(player, new RequestSchematicListMsg());
        }
    }
}
