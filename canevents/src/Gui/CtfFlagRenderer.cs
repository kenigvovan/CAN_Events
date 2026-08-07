using System;
using System.Collections.Generic;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace canevents.src.Gui
{
    /// <summary>
    /// Draws a team-coloured flag on the back of every CTF carrier. Borrows the flag BLOCK's mesh
    /// rather than tesselating a raw shape, so textures resolve as they do in the world with no
    /// texture-source plumbing. Meshes are built and uploaded lazily, once per team.
    /// </summary>
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

        // Opaque pass, after entities so the flag is not occluded by the body it sits on.
        public double RenderOrder => 1.0;
        public int RenderRange => 99;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var carriers = networkHandler?.CtfCarriers;
            if (carriers == null || carriers.Count == 0) return;

            // The local player's entity can be absent for a frame around world load while a
            // carrier packet is already applied.
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
            // Fully lit, flat: StandardShader multiplies by the light uniform, so inheriting the
            // previous draw's value renders the flag black.
            prog.RgbaLightIn = new Vec4f(1, 1, 1, 1);
            prog.RgbaGlowIn = new Vec4f(0, 0, 0, 0);
            prog.NormalShaded = 0;
            prog.ExtraGlow = 0;
            prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
            prog.ViewMatrix = rpi.CameraMatrixOriginf;

            foreach (var kv in carriers)
            {
                var player = capi.World.PlayerByUid(kv.Key);
                var entity = player?.Entity;
                if (entity?.SelectionBox == null) continue;

                // Your own flag is invisible from first person anyway, and in that mode the
                // player renderer's ModelMat holds the hand-render matrix - skip it.
                if (entity.EntityId == selfPlayer.Entity.EntityId
                    && selfPlayer.CameraMode == EnumCameraMode.FirstPerson) continue;

                var shapeRenderer = entity.Properties.Client.Renderer as EntityShapeRenderer;
                if (shapeRenderer == null) continue;

                var mesh = GetMesh(kv.Value);
                if (mesh == null) continue;

                // Piggyback on the player renderer's ModelMat: same smoothed position and body yaw
                // as the body, so no lag or jitter (own Pos - CameraPos math ticks at physics rate).
                // In its local space +X points behind the player, so only an offset is needed.
                Array.Copy(shapeRenderer.ModelMat, modelMat.Values, 16);
                modelMat
                    .Translate(0.85f, entity.SelectionBox.Y2 - 0.8f, 0.5f)
                    .Scale(0.6f, 0.6f, 0.6f)
                    .Translate(-0.5f, 0f, -0.5f); // block meshes are modelled from a corner

                prog.ModelMatrix = modelMat.Values;
                rpi.RenderMesh(mesh);
            }

            prog.Stop();
        }

        /// <summary>
        /// Mesh for a team's flag, tesselated from the block on first request. A missing block
        /// caches null, so a broken asset costs one lookup rather than one per frame.
        /// </summary>
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
                capi.Tesselator.TesselateBlock(block, out MeshData mesh);
                if (mesh != null) uploaded = capi.Render.UploadMesh(mesh);
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
