using System;
using canevents.src.Network;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace canevents.src.Gui
{
    // HG storm dome: unit hemisphere scaled per frame, drawn with the vanilla blockhighlights shader.
    public class HgZoneDomeRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ClientNetworkHandler networkHandler;

        private MeshRef domeMesh;
        private MeshRef skirtMesh;
        private readonly Matrixf modelView = new Matrixf();

        // ABGR
        private static readonly int DomeColor = ColorUtil.ToRgba(64, 255, 130, 40);

        public HgZoneDomeRenderer(ICoreClientAPI capi, ClientNetworkHandler networkHandler)
        {
            this.capi = capi;
            this.networkHandler = networkHandler;
        }

        // OIT stage only: blockhighlights.fsh writes to OIT buffers, bound only in that pass.
        public double RenderOrder => 0.95;
        public int RenderRange => 999;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var zone = networkHandler?.HgZone;
            if (zone == null || !zone.Active) return;

            var camPos = capi.World?.Player?.Entity?.CameraPos;
            if (camPos == null) return;

            float radius = HgZoneInterpolator.RadiusAt(zone,
                (capi.ElapsedMilliseconds - networkHandler.HgZoneReceivedMs) / 1000f);
            if (radius <= 0.1f) return;

            if (domeMesh == null)
                ClientTrace.Step("HG dome mesh upload", () => domeMesh = BuildUnitDome());
            if (skirtMesh == null)
                ClientTrace.Step("HG dome skirt upload", () => skirtMesh = BuildUnitSkirt());

            var rpi = capi.Render;
            var prog = rpi.GetEngineShader(EnumShaderProgram.Blockhighlights);
            prog.Use();
            prog.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);

            // OIT pass sets its own blend state. No culling, viewer can be inside.
            rpi.GlDisableCullFace();

            modelView.Set(rpi.CameraMatrixOriginf)
                .Translate(zone.X - camPos.X, zone.Y - camPos.Y, zone.Z - camPos.Z)
                .Scale(radius, radius, radius);
            prog.UniformMatrix("modelViewMatrix", modelView.Values);
            rpi.RenderMesh(domeMesh);

            // The zone is X/Z only, so the wall goes down to the dimension's floor; terrain hides the buried part.
            double depth = zone.Y % BlockPos.DimensionBoundary;
            if (depth > 0)
            {
                modelView.Set(rpi.CameraMatrixOriginf)
                    .Translate(zone.X - camPos.X, zone.Y - camPos.Y, zone.Z - camPos.Z)
                    .Scale(radius, (float)depth, radius);
                prog.UniformMatrix("modelViewMatrix", modelView.Values);
                rpi.RenderMesh(skirtMesh);
            }

            rpi.GlEnableCullFace();

            prog.Stop();
        }

        // Vertex colours, no UVs: the layout BlockHighlight meshes use.
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

        // Unit cylinder wall from y=0 down to y=-1, same ring as the dome's base.
        private MeshRef BuildUnitSkirt()
        {
            const int lonBands = 48;
            var mesh = new MeshData((lonBands + 1) * 2, lonBands * 6, false, false, true, false);

            for (int b = 0; b <= lonBands; b++)
            {
                double lon = b * 2.0 * Math.PI / lonBands;
                float x = (float)Math.Cos(lon), z = (float)Math.Sin(lon);
                mesh.AddVertexSkipTex(x, 0f, z, DomeColor);
                mesh.AddVertexSkipTex(x, -1f, z, DomeColor);
            }

            for (int b = 0; b < lonBands; b++)
            {
                int top0 = b * 2, bot0 = top0 + 1, top1 = top0 + 2, bot1 = top0 + 3;
                mesh.AddIndex(top0); mesh.AddIndex(bot0); mesh.AddIndex(top1);
                mesh.AddIndex(top1); mesh.AddIndex(bot0); mesh.AddIndex(bot1);
            }

            return capi.Render.UploadMesh(mesh);
        }

        public void Dispose()
        {
            domeMesh?.Dispose();
            domeMesh = null;
            skirtMesh?.Dispose();
            skirtMesh = null;
        }
    }
}
