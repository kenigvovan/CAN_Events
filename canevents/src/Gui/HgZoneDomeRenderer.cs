using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    /// <summary>
    /// Draws the Hunger Games storm border as a translucent blue dome, client-side: the server
    /// sends only the shrink segment and the radius is interpolated per frame. One unit hemisphere
    /// mesh is uploaded once and scaled per frame, drawn with the engine's block-highlight shader
    /// so fog and depth behave like vanilla highlights with no texture plumbing.
    /// </summary>
    public class HgZoneDomeRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ClientNetworkHandler networkHandler;

        private MeshRef domeMesh;
        private readonly Matrixf modelView = new Matrixf();

        // Same translucent blue as the old block-highlight wall (these ints are ABGR).
        private static readonly int DomeColor = ColorUtil.ToRgba(64, 255, 130, 40);

        public HgZoneDomeRenderer(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
        {
            this.capi = capi;
            this.networkHandler = networkHandler;
        }

        // MUST run in the OIT stage: the blockhighlights shader writes to the order-independent
        // transparency accumulation buffers (see OIT() in blockhighlights.fsh), which are only
        // bound during that pass. In any other stage the dome is written into nowhere.
        public double RenderOrder => 0.95;
        public int RenderRange => 999;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var zone = networkHandler?.HgZone;
            if (zone == null || !zone.Active) return;

            var camPos = capi.World?.Player?.Entity?.CameraPos;
            if (camPos == null) return;

            // Same interpolator as the HUD's distance readout, so wall and readout agree.
            float radius = HgZoneInterpolator.RadiusAt(zone,
                (capi.ElapsedMilliseconds - networkHandler.HgZoneReceivedMs) / 1000f);
            if (radius <= 0.1f) return;

            if (domeMesh == null) domeMesh = BuildUnitDome();

            var rpi = capi.Render;
            var prog = rpi.GetEngineShader(EnumShaderProgram.Blockhighlights);
            prog.Use();

            modelView.Set(rpi.CameraMatrixOriginf)
                .Translate(zone.X - camPos.X, zone.Y - camPos.Y, zone.Z - camPos.Z)
                .Scale(radius, radius, radius);
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
            prog.UniformMatrix("modelViewMatrix", modelView.Values);

            // No blend toggling: the OIT pass has its own accumulation blend state, as vanilla's
            // highlight renderer assumes. Both faces: viewers can be inside or outside.
            rpi.GlDisableCullFace();
            rpi.RenderMesh(domeMesh);
            rpi.GlEnableCullFace();

            prog.Stop();
        }

        /// <summary>
        /// Unit upper hemisphere (radius 1, base at y=0), vertex-coloured, no UVs: the same
        /// vertex layout BlockHighlight meshes use, which is what the shader expects.
        /// </summary>
        private MeshRef BuildUnitDome()
        {
            const int latBands = 16, lonBands = 48;
            var mesh = new MeshData((latBands + 1) * (lonBands + 1), latBands * lonBands * 6,
                false, false, true, false);

            for (int a = 0; a <= latBands; a++)
            {
                double lat = a * (Math.PI / 2.0) / latBands;
                float y = (float)Math.Sin(lat);
                float ringR = (float)Math.Cos(lat);
                for (int b = 0; b <= lonBands; b++)
                {
                    double lon = b * 2.0 * Math.PI / lonBands;
                    mesh.AddVertexSkipTex(ringR * (float)Math.Cos(lon), y, ringR * (float)Math.Sin(lon), DomeColor);
                }
            }

            for (int a = 0; a < latBands; a++)
            {
                for (int b = 0; b < lonBands; b++)
                {
                    int i0 = a * (lonBands + 1) + b;
                    int i1 = i0 + 1;
                    int i2 = i0 + lonBands + 1;
                    int i3 = i2 + 1;
                    mesh.AddIndex(i0); mesh.AddIndex(i2); mesh.AddIndex(i1);
                    mesh.AddIndex(i1); mesh.AddIndex(i2); mesh.AddIndex(i3);
                }
            }

            return capi.Render.UploadMesh(mesh);
        }

        public void Dispose()
        {
            domeMesh?.Dispose();
            domeMesh = null;
        }
    }
}
