using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.src.Gui
{
    // Flag on CTF carriers' backs, and a smaller one on every fighter in team colours (TeamMarkersEB),
    // on the back or over the head as the event sets it.
    // Uses the flag block's mesh so textures come from the block atlas.
    public class CtfFlagRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ClientNetworkHandler networkHandler;

        // Team index -> uploaded mesh of canevents:ctfflag-<team+1>.
        private readonly Dictionary<int, MeshRef> meshByTeam = new Dictionary<int, MeshRef>();

        private readonly Matrixf modelMat = new Matrixf();

        public CtfFlagRenderer(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
        {
            this.capi = capi;
            this.networkHandler = networkHandler;
        }

        // After entities, so the body doesn't occlude it.
        public double RenderOrder => 1.0;
        public int RenderRange => 99;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var carriers = networkHandler?.CtfCarriers;
            var markers = networkHandler?.TeamMarkers;
            if ((carriers == null || carriers.Count == 0) && (markers == null || markers.Count == 0)) return;

            // Own entity can be null for a frame around world load.
            var selfPlayer = capi.World?.Player;
            if (selfPlayer?.Entity == null) return;

            var rpi = capi.Render;
            var prog = rpi.StandardShader;
            prog.Use();
            prog.RgbaAmbientIn = rpi.AmbientColor;
            prog.RgbaFogIn = rpi.FogColor;
            prog.FogMinIn = rpi.FogMin;
            prog.FogDensityIn = rpi.FogDensity;
            prog.ExtraGodray = 0;
            prog.AverageColor = new Vec4f(1, 1, 1, 1);
            // Must set it, a stale light uniform from the previous draw renders the flag black.
            prog.RgbaLightIn = new Vec4f(1, 1, 1, 1);
            prog.RgbaGlowIn = new Vec4f(0, 0, 0, 0);
            prog.NormalShaded = 0;
            prog.ExtraGlow = 0;
            prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
            prog.ViewMatrix = rpi.CameraMatrixOriginf;

            // Local +X is behind the player.
            if (carriers != null)
                foreach (var kv in carriers)
                    Draw(prog, selfPlayer, kv.Key, kv.Value, 0.85f, -0.8f, 0.6f);

            // Own pennant too, seen in third person; Draw hides it in first person like the carrier flag.
            // On the back by default, smaller than a carried flag; over the head where the event says so (CTF).
            if (markers != null)
            {
                bool onHead = networkHandler.TeamMarkersOnHead;
                foreach (var kv in markers)
                {
                    if (onHead) Draw(prog, selfPlayer, kv.Key, kv.Value, 0.5f, 0.15f, 0.3f);
                    else Draw(prog, selfPlayer, kv.Key, kv.Value, 0.8f, -0.75f, 0.45f);
                }
            }

            prog.Stop();
        }

        // offsetY is from the top of the selection box.
        private void Draw(IStandardShaderProgram prog, IClientPlayer selfPlayer, string uid, int team,
            float offsetX, float offsetY, float scale)
        {
            var entity = ResolveCarrier(uid);
            if (entity?.SelectionBox == null) return;

            // In first person ModelMat holds the hand-render matrix.
            if (entity.EntityId == selfPlayer.Entity.EntityId
                && selfPlayer.CameraMode == EnumCameraMode.FirstPerson) return;

            var shapeRenderer = entity.Properties.Client.Renderer as EntityShapeRenderer;
            if (shapeRenderer == null) return;

            var mesh = GetMesh(team);
            if (mesh == null) return;

            // Reuse the body's ModelMat (smoothed, no physics-tick jitter).
            Array.Copy(shapeRenderer.ModelMat, modelMat.Values, 16);
            modelMat
                .Translate(offsetX, entity.SelectionBox.Y2 + offsetY, 0.5f)
                .Scale(scale, scale, scale)
                .Translate(-0.5f, 0f, -0.5f); // block meshes are modelled from a corner

            prog.ModelMatrix = modelMat.Values;
            capi.Render.RenderMesh(mesh);
        }

        // Bots are found by the uid in WatchedAttributes.
        private readonly Dictionary<string, Entity> botBodies = new Dictionary<string, Entity>();

        private Entity ResolveCarrier(string uid)
        {
            if (!Bots.BotIdentity.IsBot(uid)) return capi.World.PlayerByUid(uid)?.Entity;

            if (botBodies.TryGetValue(uid, out var cached)
                && cached.Alive && capi.World.LoadedEntities.ContainsKey(cached.EntityId))
                return cached;

            botBodies.Remove(uid);

            // Full scan at most once a second per missing bot.
            long now = capi.World.ElapsedMilliseconds;
            if (lastBotScanMs.TryGetValue(uid, out var last) && now - last < 1000) return null;
            lastBotScanMs[uid] = now;

            foreach (var e in capi.World.LoadedEntities.Values)
            {
                if (e?.WatchedAttributes?.GetString(Bots.BotMatchLink.UidAttribute) != uid) continue;
                botBodies[uid] = e;
                return e;
            }
            return null;
        }

        private readonly Dictionary<string, long> lastBotScanMs = new Dictionary<string, long>();

        // Missing block caches null too.
        private MeshRef GetMesh(int team)
        {
            if (meshByTeam.TryGetValue(team, out var cached)) return cached;

            MeshRef uploaded = null;
            var block = capi.World.GetBlock(new AssetLocation("canevents:ctfflag-" + Math.Min(team + 1, 6)));
            if (block == null)
            {
                capi.Logger.Warning("[CTF] flag block for team {0} not found - carriers will show no flag.", team + 1);
            }
            else
            {
                ClientTrace.Step("CTF flag mesh upload, team " + (team + 1), () =>
                {
                    capi.Tesselator.TesselateBlock(block, out MeshData mesh);
                    if (mesh != null) uploaded = capi.Render.UploadMesh(mesh);
                });
            }

            meshByTeam[team] = uploaded;
            return uploaded;
        }

        public void Dispose()
        {
            foreach (var mesh in meshByTeam.Values) mesh?.Dispose();
            meshByTeam.Clear();
        }
    }
}
