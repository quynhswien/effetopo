using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Singleton DirectContext3D server — registered once at Revit startup.</summary>
    internal sealed class TerrainDirectContext3DPreview : IDirectContext3DServer, IDisposable
    {
        private static readonly Guid ServerGuid = new Guid("EFFE7A21-4B3C-4D5E-9F10-92A8B0C1D2E3");

        private readonly object _sync = new();
        private Document _doc;
        private UIDocument _uidoc;
        private TerrainMesh _mesh;
        private bool _visible;
        private bool _registered;
        private int _renderCount;

        public static TerrainDirectContext3DPreview Instance { get; } = new TerrainDirectContext3DPreview();

        private TerrainDirectContext3DPreview() { }

        public void BindSession(Document doc, UIDocument uidoc)
        {
            _doc = doc;
            _uidoc = uidoc;
        }

        public void EnsureRegistered()
        {
            if (_registered) return;

            var service = ExternalServiceRegistry.GetService(
                ExternalServices.BuiltInExternalServices.DirectContext3DService)
                as MultiServerService;
            if (service == null)
            {
                Log.Warning("DirectContext3D service not available");
                return;
            }

            try
            {
                service.AddServer(this);
            }
            catch
            {
                // Server may already be registered from a prior session.
            }

            var activeIds = service.GetActiveServerIds().ToList();
            if (!activeIds.Contains(ServerGuid))
                service.SetActiveServers(activeIds.Append(ServerGuid).ToList());

            _registered = true;
            Log.Information(
                "Terrain DirectContext3D preview server registered (active servers: {Count})",
                service.GetActiveServerIds().Count);
        }

        public void SetMesh(TerrainMesh mesh)
        {
            lock (_sync)
            {
                _mesh = mesh;
                _renderCount = 0;
            }
            RequestRefresh();
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            RequestRefresh();
        }

        public void Dispose()
        {
            _visible = false;
            lock (_sync) { _mesh = null; }
            RequestRefresh();
        }

        private void RequestRefresh()
        {
            try { _uidoc?.RefreshActiveView(); }
            catch (Exception ex)
            {
                Log.Debug("Preview view refresh failed: {Error}", ex.Message);
            }
        }

        public Guid GetServerId() => ServerGuid;
        public string GetVendorId() => "EFFETOPO";
        public string GetName() => "EFFETOPO Terrain Preview";
        public string GetDescription() => "Terrain sculpt preview (DirectContext3D)";
        public string GetApplicationId() => "effetopo";
        public string GetSourceId() => "EFFETOPO.TerrainPreview";
        public ExternalServiceId GetServiceId() =>
            ExternalServices.BuiltInExternalServices.DirectContext3DService;
        public bool UseInTransparentPass(View view) => true;
        public bool UsesHandles() => false;

        public bool CanExecute(View view)
        {
            if (!_visible || view == null || view.IsTemplate)
                return false;

            Document viewDoc = view.Document;
            if (viewDoc == null || _doc == null)
                return false;

            return ReferenceEquals(viewDoc, _doc) || viewDoc.Equals(_doc);
        }

        public Outline GetBoundingBox(View view)
        {
            lock (_sync)
            {
                if (_mesh?.Bounds != null)
                    return _mesh.Bounds;
            }

            return new Outline(new XYZ(-1e6, -1e6, -1e6), new XYZ(1e6, 1e6, 1e6));
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            TerrainMesh mesh;
            lock (_sync) { mesh = _mesh; }

            if (mesh == null || !_visible)
                return;

            int triCount = mesh.TriangleIndices.Count / 3;
            if (triCount == 0 && mesh.LineSegments.Count == 0)
                return;

            if (_renderCount < 3)
            {
                _renderCount++;
                Log.Debug("DirectContext3D RenderScene #{N} view={View} tris={Tris}",
                    _renderCount, view?.Name, triCount);
            }

            try
            {
                if (triCount > 0)
                    DrawTriangles(mesh, triCount);
                if (mesh.LineSegments.Count >= 2)
                    DrawLines(mesh);
            }
            catch (Exception ex)
            {
                Log.Warning("DirectContext3D render failed: {Error}", ex.Message);
            }
        }

        private static void DrawTriangles(TerrainMesh mesh, int triCount)
        {
            int floatsPerVertex = VertexPositionColored.GetSizeInFloats();
            int vertexCount = mesh.Positions.Count;
            using var vbuf = new VertexBuffer(vertexCount * floatsPerVertex);
            vbuf.Map(0);
            var vstream = vbuf.GetVertexStreamPositionColored();
            var color = new ColorWithTransparency(255, 190, 0, 30);
            foreach (XYZ p in mesh.Positions)
                vstream.AddVertex(new VertexPositionColored(p, color));
            vbuf.Unmap();

            using var ibuf = new IndexBuffer(triCount * 3);
            ibuf.Map(0);
            var istream = ibuf.GetIndexStreamTriangle();
            for (int t = 0; t < triCount; t++)
            {
                int i = t * 3;
                istream.AddTriangle(new IndexTriangle(
                    mesh.TriangleIndices[i],
                    mesh.TriangleIndices[i + 1],
                    mesh.TriangleIndices[i + 2]));
            }
            ibuf.Unmap();

            using var vf = new VertexFormat(VertexFormatBits.PositionColored);
            using var fx = new EffectInstance(VertexFormatBits.PositionColored);
            DrawContext.FlushBuffer(vbuf, vertexCount, ibuf, triCount * 3, vf, fx,
                PrimitiveType.TriangleList, 0, triCount);
        }

        private static void DrawLines(TerrainMesh mesh)
        {
            int segmentCount = mesh.LineSegments.Count / 2;
            if (segmentCount == 0) return;

            int floatsPerVertex = VertexPositionColored.GetSizeInFloats();
            int vertexCount = mesh.LineSegments.Count;
            using var vbuf = new VertexBuffer(vertexCount * floatsPerVertex);
            vbuf.Map(0);
            var vstream = vbuf.GetVertexStreamPositionColored();
            var color = new ColorWithTransparency(255, 255, 0, 0);
            foreach (XYZ p in mesh.LineSegments)
                vstream.AddVertex(new VertexPositionColored(p, color));
            vbuf.Unmap();

            using var ibuf = new IndexBuffer(segmentCount * 2);
            ibuf.Map(0);
            var istream = ibuf.GetIndexStreamLine();
            for (int s = 0; s < segmentCount; s++)
            {
                int i = s * 2;
                istream.AddLine(new IndexLine(i, i + 1));
            }
            ibuf.Unmap();

            using var vf = new VertexFormat(VertexFormatBits.PositionColored);
            using var fx = new EffectInstance(VertexFormatBits.PositionColored);
            DrawContext.FlushBuffer(vbuf, vertexCount, ibuf, segmentCount * 2, vf, fx,
                PrimitiveType.LineList, 0, segmentCount);
        }
    }
#endif
}
