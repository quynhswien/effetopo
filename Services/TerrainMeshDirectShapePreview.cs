using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Renders the same TerrainMesh used for commit via transient DirectShape (visible in all view styles).
    /// </summary>
    internal sealed class TerrainMeshDirectShapePreview
    {
        private const string ShapeName = "EFFETOPO_TERRAIN_PREVIEW";
        private const double SlabThickness = 0.35;
        private const int MaxSolids = 600;

        private readonly Document _doc;
        private ElementId _shapeId = ElementId.InvalidElementId;
        private ElementId _dimmedTopoId = ElementId.InvalidElementId;
        private ElementId _lastViewId = ElementId.InvalidElementId;
        private string _lastKey = string.Empty;

        public TerrainMeshDirectShapePreview(Document doc)
        {
            _doc = doc;
        }

        public void UpdateFromRevitSolids(
            View view,
            ElementId toposolidId,
            IList<GeometryObject> revitSolids,
            TerrainMesh brushOverlay = null)
        {
            if (_doc == null || view == null || revitSolids == null || revitSolids.Count == 0)
                return;

            string key = string.Format(CultureInfo.InvariantCulture,
                "revit:{0}:{1}", revitSolids.Count, ComputeSolidFingerprint(revitSolids));
            if (key == _lastKey && _shapeId != ElementId.InvalidElementId && _doc.GetElement(_shapeId) != null)
                return;
            _lastKey = key;

            var shapes = new List<GeometryObject>(revitSolids);
            int budget = MaxSolids - shapes.Count;
            if (brushOverlay != null && budget > 0)
                AddBrushRingColumns(brushOverlay, shapes, ref budget);

            using (Transaction tx = new Transaction(_doc, "Update Terrain Preview"))
            {
                tx.Start();
                try
                {
                    ClearShapeOnly();
                    var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(shapes);
                    ds.Name = ShapeName;
                    _shapeId = ds.Id;
                    ApplyStyle(view, _shapeId, new Color(255, 190, 0), transparency: 15);
                    ApplyTopoDimming(view, toposolidId, dim: true);
                    _lastViewId = view.Id;
                    _doc.Regenerate();
                    tx.Commit();
                    Log.Information("DirectShape Revit-exact preview: {SolidCount} geometry object(s)",
                        shapes.Count);
                }
                catch (Exception ex)
                {
                    Log.Warning("DirectShape Revit-exact preview failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        public void Update(View view, ElementId toposolidId, TerrainMesh mesh)
        {
            if (_doc == null || view == null || mesh == null)
                return;

            int triCount = mesh.TriangleIndices.Count / 3;
            string key = string.Format(CultureInfo.InvariantCulture,
                "{0}:{1}:{2}", triCount, mesh.Positions.Count, mesh.LineSegments.Count);
            if (key == _lastKey && _shapeId != ElementId.InvalidElementId && _doc.GetElement(_shapeId) != null)
                return;
            _lastKey = key;

            if (triCount == 0 && mesh.LineSegments.Count < 2)
            {
                Clear(view);
                return;
            }

            var shapes = BuildShapes(mesh, triCount);
            if (shapes.Count == 0)
            {
                Log.Warning("DirectShape preview: no solids built from mesh ({TriCount} tris)", triCount);
                return;
            }

            using (Transaction tx = new Transaction(_doc, "Update Terrain Preview"))
            {
                tx.Start();
                try
                {
                    ClearShapeOnly();
                    var ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(shapes);
                    ds.Name = ShapeName;
                    _shapeId = ds.Id;
                    ApplyStyle(view, _shapeId, new Color(255, 190, 0), transparency: 15);
                    ApplyTopoDimming(view, toposolidId, dim: true);
                    _lastViewId = view.Id;
                    _doc.Regenerate();
                    tx.Commit();
                    Log.Information("DirectShape terrain preview: {SolidCount} solids from {TriCount} tris",
                        shapes.Count, triCount);
                }
                catch (Exception ex)
                {
                    Log.Warning("DirectShape preview update failed: {Error}", ex.Message);
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        public void Clear(View view = null)
        {
            _lastKey = string.Empty;
            View v = view ?? (_lastViewId != ElementId.InvalidElementId
                ? _doc.GetElement(_lastViewId) as View
                : null);

            using (Transaction tx = new Transaction(_doc, "Clear Terrain Preview"))
            {
                tx.Start();
                try
                {
                    ApplyTopoDimming(v, _dimmedTopoId, dim: false);
                    ClearShapeOnly();
                    _doc.Regenerate();
                    tx.Commit();
                }
                catch
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }
        }

        private void ClearShapeOnly()
        {
            if (_shapeId == null || _shapeId == ElementId.InvalidElementId) return;
            try
            {
                if (_doc.GetElement(_shapeId) != null)
                    _doc.Delete(_shapeId);
            }
            catch { }
            _shapeId = ElementId.InvalidElementId;
        }

        private static string ComputeSolidFingerprint(IList<GeometryObject> solids)
        {
            double sum = 0;
            foreach (GeometryObject obj in solids)
            {
                if (obj is Solid solid)
                    sum += solid.Volume + solid.SurfaceArea;
            }
            return sum.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static List<GeometryObject> BuildShapes(TerrainMesh mesh, int triCount)
        {
            var shapes = new List<GeometryObject>();
            int budget = MaxSolids;

            for (int t = 0; t < triCount && budget > 0; t++)
            {
                int i = t * 3;
                if (i + 2 >= mesh.TriangleIndices.Count) break;

                XYZ v0 = mesh.Positions[mesh.TriangleIndices[i]];
                XYZ v1 = mesh.Positions[mesh.TriangleIndices[i + 1]];
                XYZ v2 = mesh.Positions[mesh.TriangleIndices[i + 2]];
                Solid solid = TryExtrudeTriangle(v0, v1, v2, SlabThickness);
                if (solid == null) continue;

                shapes.Add(solid);
                budget--;
            }

            AddBrushRingColumns(mesh, shapes, ref budget);
            return shapes;
        }

        private static void AddBrushRingColumns(TerrainMesh mesh, IList<GeometryObject> shapes, ref int budget)
        {
            if (mesh.LineSegments.Count < 2 || budget <= 0)
                return;

            const double radius = 0.35;
            for (int i = 0; i + 1 < mesh.LineSegments.Count && budget > 0; i += 12)
            {
                XYZ a = mesh.LineSegments[i];
                XYZ b = mesh.LineSegments[Math.Min(i + 1, mesh.LineSegments.Count - 1)];
                double z = Math.Max(a.Z, b.Z);
                Solid col = TryExtrudeColumn(a.X, a.Y, z - 0.5, z + 0.15, radius);
                if (col != null)
                {
                    shapes.Add(col);
                    budget--;
                }
            }
        }

        private static Solid TryExtrudeTriangle(XYZ v0, XYZ v1, XYZ v2, double thickness)
        {
            if (v0 == null || v1 == null || v2 == null) return null;
            try
            {
                double edge = MinEdgeLength(v0, v1, v2);
                if (edge < 0.05) return null;

                var loop = CurveLoop.Create(new[]
                {
                    Line.CreateBound(v0, v1),
                    Line.CreateBound(v1, v2),
                    Line.CreateBound(v2, v0)
                });
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop },
                    XYZ.BasisZ,
                    Math.Max(thickness, 0.12));
            }
            catch
            {
                return null;
            }
        }

        private static Solid TryExtrudeColumn(double x, double y, double bottomZ, double topZ, double radius)
        {
            try
            {
                double height = topZ - bottomZ;
                if (Math.Abs(height) < 0.01) return null;

                radius = Math.Max(radius, 0.2);
                double z = Math.Min(bottomZ, topZ);
                height = Math.Abs(height);

                var loop = CurveLoop.Create(CreateCircle(x, y, radius, z));
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop },
                    XYZ.BasisZ,
                    height);
            }
            catch
            {
                return null;
            }
        }

        private static IList<Curve> CreateCircle(double cx, double cy, double radius, double z)
        {
            const int segments = 10;
            var curves = new List<Curve>();
            XYZ prev = null;
            for (int i = 0; i <= segments; i++)
            {
                double a = 2.0 * Math.PI * i / segments;
                var pt = new XYZ(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a), z);
                if (prev != null)
                    curves.Add(Line.CreateBound(prev, pt));
                prev = pt;
            }
            return curves;
        }

        private static double MinEdgeLength(XYZ v0, XYZ v1, XYZ v2)
        {
            double a = ModifyTopoService.HorizontalDistance(v0.X, v0.Y, v1.X, v1.Y);
            double b = ModifyTopoService.HorizontalDistance(v1.X, v1.Y, v2.X, v2.Y);
            double c = ModifyTopoService.HorizontalDistance(v2.X, v2.Y, v0.X, v0.Y);
            return Math.Min(a, Math.Min(b, c));
        }

        private void ApplyTopoDimming(View view, ElementId toposolidId, bool dim)
        {
            if (view == null || toposolidId == null || toposolidId == ElementId.InvalidElementId)
                return;

            try
            {
                if (dim)
                {
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceTransparency(70);
                    view.SetElementOverrides(toposolidId, ogs);
                    _dimmedTopoId = toposolidId;
                }
                else if (_dimmedTopoId != ElementId.InvalidElementId)
                {
                    view.SetElementOverrides(_dimmedTopoId, new OverrideGraphicSettings());
                    _dimmedTopoId = ElementId.InvalidElementId;
                }
            }
            catch { }
        }

        private static void ApplyStyle(View view, ElementId id, Color color, int transparency)
        {
            if (view == null || id == null || id == ElementId.InvalidElementId) return;
            try
            {
                if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_GenericModel)))
                    view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_GenericModel), false);
            }
            catch { }

            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetSurfaceBackgroundPatternColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetSurfaceTransparency(transparency);
            ogs.SetSurfaceForegroundPatternVisible(true);
            ogs.SetSurfaceBackgroundPatternVisible(true);
            view.SetElementOverrides(id, ogs);
        }
    }
#endif
}
