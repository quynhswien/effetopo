using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Service for merging Toposolid elements (Revit 2024+ only)
    /// </summary>
    public class ToposolidMergeService
    {
        private static ToposolidMergeService _instance;
        private static readonly object _lock = new object();

        private ToposolidMergeService() { }

        /// <summary>
        /// Statistics from last merge operation
        /// </summary>
        public int LastMergePointsAdded { get; private set; }
        public int LastMergePointsUpdated { get; private set; }
        public int LastMergePointsSkipped { get; private set; }
        public int LastMergePointsSkippedDueToZLimit { get; private set; }
        public string LastMergeZLimitInfo { get; private set; }

        /// <summary>
        /// Statistics from last Floor follow Topo operation
        /// </summary>
        public int LastFloorFollowBoundaryPointsUpdated { get; private set; }
        public int LastFloorFollowTopoPointsAdded { get; private set; }
        public int LastFloorFollowPointsSkipped { get; private set; }
        /// <summary>
        /// Number of points that Revit could not accept directly and were adjusted by averaging elevation of 2 nearest neighbors.
        /// </summary>
        public int LastFloorFollowPointsAdjustedByAverage { get; private set; }

        /// <summary>
        /// Helper class to store point update information
        /// </summary>
        private class PointUpdate
        {
            public XYZ OriginalPoint { get; set; }
            public double NewZ { get; set; }
            /// <summary>True when XY is not an existing SlabShape vertex (AddPoint path).</summary>
            public bool IsNewPoint { get; set; }
            /// <summary>True when point came from SlabShapeVertices (ModifySubElement path).</summary>
            public bool IsSlabShapeVertex { get; set; }
        }

        /// <summary>
        /// Helper class to store triangle data with pre-calculated normal
        /// </summary>
        private class TriangleData
        {
            public XYZ V0 { get; set; }
            public XYZ V1 { get; set; }
            public XYZ V2 { get; set; }
            public XYZ Normal { get; set; }
            public double MinX { get; set; }
            public double MaxX { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
        }

        /// <summary>
        /// Spatial grid for fast triangle lookup
        /// </summary>
        private class SpatialGrid
        {
            private Dictionary<string, List<TriangleData>> _grid;
            private List<TriangleData> _allTriangles;
            private double _cellSize;
            private double _minX, _minY;

            public SpatialGrid(List<TriangleData> triangles, double cellSize = 10.0)
            {
                _cellSize = cellSize;
                _grid = new Dictionary<string, List<TriangleData>>();
                _allTriangles = triangles ?? new List<TriangleData>();

                if (triangles == null || triangles.Count == 0) return;

                // Find bounds
                _minX = triangles.Min(t => Math.Min(Math.Min(t.V0.X, t.V1.X), t.V2.X));
                _minY = triangles.Min(t => Math.Min(Math.Min(t.V0.Y, t.V1.Y), t.V2.Y));

                // Add triangles to grid cells
                foreach (var tri in triangles)
                {
                    int minCellX = (int)Math.Floor((tri.MinX - _minX) / _cellSize);
                    int maxCellX = (int)Math.Floor((tri.MaxX - _minX) / _cellSize);
                    int minCellY = (int)Math.Floor((tri.MinY - _minY) / _cellSize);
                    int maxCellY = (int)Math.Floor((tri.MaxY - _minY) / _cellSize);

                    for (int cx = minCellX; cx <= maxCellX; cx++)
                    {
                        for (int cy = minCellY; cy <= maxCellY; cy++)
                        {
                            string key = $"{cx},{cy}";
                            if (!_grid.ContainsKey(key))
                            {
                                _grid[key] = new List<TriangleData>();
                            }
                            _grid[key].Add(tri);
                        }
                    }
                }
            }

            /// <summary>
            /// Gets triangles in the cell containing the point (single cell).
            /// </summary>
            public List<TriangleData> GetTrianglesNear(XYZ point)
            {
                return GetTrianglesNear(point, expandCells: 0);
            }

            /// <summary>
            /// Gets triangles near point. expandCells: 0 = single cell, 1 = 3x3 neighborhood (recommended for edge points).
            /// </summary>
            public List<TriangleData> GetTrianglesNear(XYZ point, int expandCells)
            {
                int cellX = (int)Math.Floor((point.X - _minX) / _cellSize);
                int cellY = (int)Math.Floor((point.Y - _minY) / _cellSize);
                var result = new List<TriangleData>();
                var seen = new HashSet<TriangleData>();
                for (int dx = -expandCells; dx <= expandCells; dx++)
                {
                    for (int dy = -expandCells; dy <= expandCells; dy++)
                    {
                        string key = $"{cellX + dx},{cellY + dy}";
                        if (_grid.TryGetValue(key, out var triangles))
                        {
                            foreach (var t in triangles)
                            {
                                if (seen.Add(t))
                                    result.Add(t);
                            }
                        }
                    }
                }
                return result;
            }

            /// <summary>
            /// Returns all triangles (for boundary points outside mesh – find nearest in whole topo).
            /// </summary>
            public List<TriangleData> GetAllTriangles()
            {
                return _allTriangles ?? new List<TriangleData>();
            }
        }

        public static ToposolidMergeService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ToposolidMergeService();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Extracts points from a Toposolid using geometry and SlabShapeEditor
        /// </summary>
        public List<XYZ> ExtractPointsFromToposolid(
#if REVIT2024_OR_GREATER
            Toposolid toposolid
#else
            Element toposolid
#endif
            )
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            var points = new HashSet<XYZ>(new XYZComparer());
            
            try
            {
                // First, try to get points from SlabShapeEditor
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
                if (editor != null)
                {
                    SlabShapeVertexArray vertices = editor.SlabShapeVertices;
                    if (vertices != null)
                    {
                        foreach (SlabShapeVertex vertex in vertices)
                        {
                            points.Add(vertex.Position);
                        }
                    }
                }

                // Also extract points from geometry (mesh vertices)
                Options options = new Options();
                options.ComputeReferences = false;
                options.DetailLevel = ViewDetailLevel.Fine;
                
                GeometryElement geom = toposolid.get_Geometry(options);
                if (geom != null)
                {
                    ExtractPointsFromGeometry(geom, points);
                }

                Log.Information("Extracted {Count} unique points from Toposolid {Id}", points.Count, GetElementIdValue(toposolid.Id));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error extracting points from Toposolid {Id}", GetElementIdValue(toposolid.Id));
            }

            return points.ToList();
#endif
        }

        /// <summary>
        /// Extracts all top-facing triangles from a Toposolid (optimized for batch processing)
        /// </summary>
        private List<TriangleData> ExtractTopTriangles(
#if REVIT2024_OR_GREATER
            Toposolid toposolid
#else
            Element toposolid
#endif
            )
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            var triangles = new List<TriangleData>();

            try
            {
                Options options = new Options();
                options.ComputeReferences = false;
                options.DetailLevel = ViewDetailLevel.Fine;
                
                GeometryElement geom = toposolid.get_Geometry(options);
                if (geom != null)
                {
                    ExtractTrianglesFromGeometryElement(geom, Transform.Identity, triangles);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error extracting triangles from Toposolid");
            }

            return triangles;
#endif
        }

        private static void AddTopTriangleIfUpwardFacing(XYZ v0, XYZ v1, XYZ v2, List<TriangleData> triangles)
        {
            XYZ edge1 = v1 - v0;
            XYZ edge2 = v2 - v0;
            XYZ normal = edge1.CrossProduct(edge2);

            if (normal.Z > 0.1)
            {
                triangles.Add(new TriangleData
                {
                    V0 = v0,
                    V1 = v1,
                    V2 = v2,
                    Normal = normal,
                    MinX = Math.Min(Math.Min(v0.X, v1.X), v2.X),
                    MaxX = Math.Max(Math.Max(v0.X, v1.X), v2.X),
                    MinY = Math.Min(Math.Min(v0.Y, v1.Y), v2.Y),
                    MaxY = Math.Max(Math.Max(v0.Y, v1.Y), v2.Y)
                });
            }
        }

        private static void ExtractTrianglesFromGeometryElement(
            GeometryElement geom, Transform transform, List<TriangleData> triangles)
        {
            if (geom == null || transform == null) return;

            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh = face.Triangulate();
                        if (mesh == null) continue;

                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            MeshTriangle triangle = mesh.get_Triangle(i);
                            AddTopTriangleIfUpwardFacing(
                                transform.OfPoint(triangle.get_Vertex(0)),
                                transform.OfPoint(triangle.get_Vertex(1)),
                                transform.OfPoint(triangle.get_Vertex(2)),
                                triangles);
                        }
                    }
                }
                else if (obj is Mesh mesh)
                {
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle triangle = mesh.get_Triangle(i);
                        AddTopTriangleIfUpwardFacing(
                            transform.OfPoint(triangle.get_Vertex(0)),
                            transform.OfPoint(triangle.get_Vertex(1)),
                            transform.OfPoint(triangle.get_Vertex(2)),
                            triangles);
                    }
                }
                else if (obj is GeometryInstance instance)
                {
                    Transform combined = transform.Multiply(instance.Transform);
                    ExtractTrianglesFromGeometryElement(instance.SymbolGeometry, combined, triangles);
                }
            }
        }

        /// <summary>
        /// Aligns mesh triangle Z to Toposolid SlabShapeVertex.Position.Z (model coordinates).
        /// Mesh from get_Geometry can be offset from the real surface when survey/project datum differs.
        /// </summary>
        private static double AlignMeshZToSlabVertices(
            Document doc,
#if REVIT2024_OR_GREATER
            Toposolid toposolid,
#else
            Element toposolid,
#endif
            List<TriangleData> triangles)
        {
            if (toposolid == null || triangles == null || triangles.Count == 0)
                return 0;

            var meshZs = new List<double>(triangles.Count);
            foreach (var tri in triangles)
                meshZs.Add((tri.V0.Z + tri.V1.Z + tri.V2.Z) / 3.0);
            double meshMedian = GetMedian(meshZs);

            var slabZs = new List<double>();
            try
            {
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
                if (editor?.SlabShapeVertices != null)
                {
                    foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
                    {
                        if (vertex?.Position != null)
                            slabZs.Add(vertex.Position.Z);
                    }
                }
            }
            catch { }

            if (slabZs.Count > 0)
            {
                double slabMedian = GetMedian(slabZs);
                double diff = slabMedian - meshMedian;
                if (Math.Abs(diff) > 0.5)
                    return diff;
            }

            // Mesh Z is often offset from topo host level – shift to model coordinates (same as floor/topo in Revit).
            Level topoLevel = doc?.GetElement(toposolid.LevelId) as Level;
            double topoLevelElev = topoLevel?.Elevation ?? 0;
            if (Math.Abs(topoLevelElev) > 0.5 && meshMedian < topoLevelElev - 0.5)
                return topoLevelElev;

            return 0;
        }

        private static double GetMedian(List<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 0
                ? (values[mid - 1] + values[mid]) * 0.5
                : values[mid];
        }

        private static void ApplyZCorrectionToTriangles(List<TriangleData> triangles, double correction)
        {
            if (triangles == null || Math.Abs(correction) < 1e-9) return;

            foreach (var tri in triangles)
            {
                tri.V0 = OffsetZ(tri.V0, correction);
                tri.V1 = OffsetZ(tri.V1, correction);
                tri.V2 = OffsetZ(tri.V2, correction);
            }
        }

        private static XYZ OffsetZ(XYZ point, double deltaZ)
        {
            return new XYZ(point.X, point.Y, point.Z + deltaZ);
        }

        private static View3D GetRaycastView3D(Document doc)
        {
            if (doc == null) return null;
            try
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .FirstOrDefault(v => !v.IsTemplate);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts between Revit model coordinates and shared/survey elevation (Elevation Base = Survey Point in UI).
        /// Floor and topo must use the same survey Z when matching elevations.
        /// </summary>
        private sealed class SurveyCoordinateHelper
        {
            private readonly Transform _modelToShared;
            private readonly Transform _sharedToModel;

            public SurveyCoordinateHelper(Document doc)
            {
                ProjectLocation location = doc?.ActiveProjectLocation;
                if (location != null)
                {
                    _sharedToModel = location.GetTotalTransform();
                    _modelToShared = _sharedToModel.Inverse;
                }
            }

            public bool IsAvailable => _modelToShared != null && _sharedToModel != null;

            /// <summary>Survey/shared elevation at model XY,Z (same value Revit shows with Elevation Base = Survey Point).</summary>
            public double ModelZToSurveyElevation(double x, double y, double modelZ)
            {
                if (!IsAvailable) return modelZ;
                return _modelToShared.OfPoint(new XYZ(x, y, modelZ)).Z;
            }

            /// <summary>Model Z that corresponds to a survey elevation at model XY.</summary>
            public double SurveyElevationToModelZ(double x, double y, double surveyElevation)
            {
                if (!IsAvailable) return surveyElevation;
                XYZ sharedAtXY = _modelToShared.OfPoint(new XYZ(x, y, 0));
                XYZ sharedTarget = new XYZ(sharedAtXY.X, sharedAtXY.Y, surveyElevation);
                return _sharedToModel.OfPoint(sharedTarget).Z;
            }

            /// <summary>
            /// Slab shape offset so floor displays the same survey elevation as topo at (x,y).
            /// Keeps level and height offset unchanged.
            /// </summary>
            public double SurveyElevationToFloorSlabOffset(
                double x, double y, double topoSurveyElevation,
                double levelElevation, double heightOffsetFromLevel)
            {
                double targetModelZ = SurveyElevationToModelZ(x, y, topoSurveyElevation);
                return targetModelZ - levelElevation - heightOffsetFromLevel;
            }
        }

        /// <summary>
        /// Reads topo top-surface elevation in survey coordinates (matches Revit Modify Sub Elements, Elevation Base = Survey Point).
        /// Uses SlabShape-aligned triangle mesh – not raw ReferenceIntersector (returns local Z ~2' instead of survey ~441').
        /// </summary>
        private sealed class TopoSurfaceElevationProvider
        {
            private readonly SpatialGrid _spatialGrid;
            private readonly double _rayOriginZ;
            private readonly SurveyCoordinateHelper _surveyCoords;

            public TopoSurfaceElevationProvider(
                Document doc,
#if REVIT2024_OR_GREATER
                Toposolid toposolid,
#else
                Element toposolid,
#endif
                List<TriangleData> triangles,
                double rayOriginZ,
                SurveyCoordinateHelper surveyCoords)
            {
                _rayOriginZ = rayOriginZ;
                _surveyCoords = surveyCoords;

                double meshZOffset = AlignMeshZToSlabVertices(doc, toposolid, triangles);
                if (Math.Abs(meshZOffset) > 0.01)
                {
                    ApplyZCorrectionToTriangles(triangles, meshZOffset);
                    Log.Information(
                        $"Aligned topo mesh to SlabShape vertices by {meshZOffset:F4} ft for survey-coordinate sampling");
                }

                _spatialGrid = new SpatialGrid(triangles, cellSize: 10.0);
                Log.Information("Topo elevation: survey coordinates via aligned mesh + ProjectLocation transform");
            }

            /// <summary>Returns topo surface elevation in survey/shared coordinates (e.g. 441' not 2').</summary>
            public double? GetTopSurfaceSurveyElevation(double x, double y, ToposolidMergeService owner)
            {
                double? modelZ = owner.GetElevationAtPointOptimized(
                    new XYZ(x, y, _rayOriginZ), _spatialGrid);
                if (!modelZ.HasValue) return null;

                if (_surveyCoords != null && _surveyCoords.IsAvailable)
                    return _surveyCoords.ModelZToSurveyElevation(x, y, modelZ.Value);

                return modelZ.Value;
            }
        }

        /// <summary>
        /// Gets elevation at a point using pre-extracted triangle data (optimized)
        /// </summary>
        private double? GetElevationAtPointOptimized(XYZ point, SpatialGrid spatialGrid, bool allowFallback = true)
        {
            try
            {
                XYZ highPoint = new XYZ(point.X, point.Y, point.Z + 1000);
                XYZ direction = new XYZ(0, 0, -1);
                
                double? topZ = null;
                double maxZ = double.MinValue;

                // Get nearby triangles: 5x5 cell neighborhood so boundary points (often outside mesh) still find triangles
                const int expandCells = 2;
                var nearbyTriangles = spatialGrid.GetTrianglesNear(point, expandCells);

                // Bounds margin (feet): allow points slightly outside triangle bbox (edge points)
                const double boundsMargin = 2.0;
                foreach (var triData in nearbyTriangles)
                {
                    if (point.X < triData.MinX - boundsMargin || point.X > triData.MaxX + boundsMargin ||
                        point.Y < triData.MinY - boundsMargin || point.Y > triData.MaxY + boundsMargin)
                    {
                        continue;
                    }

                    XYZ intersection = RayTriangleIntersection(highPoint, direction, triData.V0, triData.V1, triData.V2);
                    if (intersection != null)
                    {
                        double horizontalDist = Math.Sqrt(
                            Math.Pow(intersection.X - point.X, 2) +
                            Math.Pow(intersection.Y - point.Y, 2));
                        const double horizontalTolerance = 2.0;
                        if (horizontalDist < horizontalTolerance && intersection.Z > maxZ)
                        {
                            maxZ = intersection.Z;
                            topZ = intersection.Z;
                        }
                    }
                }

                // Fallback 1: point outside ray-hit triangles but in same cell neighborhood – use Z from nearest triangle plane (20 ft)
                const double fallbackMaxDistSq = 400.0; // 20 ft
                if (allowFallback && !topZ.HasValue && nearbyTriangles.Count > 0)
                {
                    double bestZ = double.NaN;
                    double bestDistSq = double.MaxValue;
                    foreach (var triData in nearbyTriangles)
                    {
                        if (Math.Abs(triData.Normal.Z) < 0.01) continue;
                        double zOnPlane = GetZOnTrianglePlane(point.X, point.Y, triData);
                        double dx = point.X - (triData.V0.X + triData.V1.X + triData.V2.X) / 3.0;
                        double dy = point.Y - (triData.V0.Y + triData.V1.Y + triData.V2.Y) / 3.0;
                        double distSq = dx * dx + dy * dy;
                        if (distSq < bestDistSq && distSq < fallbackMaxDistSq)
                        {
                            bestDistSq = distSq;
                            bestZ = zOnPlane;
                        }
                    }
                    if (!double.IsNaN(bestZ))
                        topZ = bestZ;
                }

                // Fallback 2: point in cell with NO triangles (boundary far outside topo mesh) – search ALL triangles for nearest
                const double fallbackGlobalMaxDistSq = 2500.0; // 50 ft – boundary points can be far from mesh
                if (allowFallback && !topZ.HasValue)
                {
                    var allTriangles = spatialGrid.GetAllTriangles();
                    if (allTriangles != null && allTriangles.Count > 0)
                    {
                        double bestZ = double.NaN;
                        double bestDistSq = double.MaxValue;
                        foreach (var triData in allTriangles)
                        {
                            if (Math.Abs(triData.Normal.Z) < 0.01) continue;
                            double cx = (triData.V0.X + triData.V1.X + triData.V2.X) / 3.0;
                            double cy = (triData.V0.Y + triData.V1.Y + triData.V2.Y) / 3.0;
                            double dx = point.X - cx;
                            double dy = point.Y - cy;
                            double distSq = dx * dx + dy * dy;
                            if (distSq < bestDistSq && distSq < fallbackGlobalMaxDistSq)
                            {
                                bestDistSq = distSq;
                                bestZ = GetZOnTrianglePlane(point.X, point.Y, triData);
                            }
                        }
                        if (!double.IsNaN(bestZ))
                            topZ = bestZ;
                    }
                }

                return topZ;
            }
            catch (Exception ex)
            {
                Log.Debug("Error getting elevation at point ({X}, {Y}, {Z}): {Error}", point.X, point.Y, point.Z, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Gets Z on triangle plane at (x, y). Plane: Normal · (P - V0) = 0.
        /// </summary>
        private static double GetZOnTrianglePlane(double x, double y, TriangleData tri)
        {
            if (Math.Abs(tri.Normal.Z) < 1e-6) return tri.V0.Z;
            return tri.V0.Z - (tri.Normal.X * (x - tri.V0.X) + tri.Normal.Y * (y - tri.V0.Y)) / tri.Normal.Z;
        }

        /// <summary>
        /// Gets elevation at a point by projecting it onto the TOP surface of Toposolid
        /// Projects point vertically downward and finds intersection with top surface only
        /// </summary>
        public double? GetElevationAtPoint(
#if REVIT2024_OR_GREATER
            Toposolid toposolid,
#else
            Element toposolid,
#endif
            XYZ point)
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            if (toposolid == null || point == null)
            {
                return null;
            }

            try
            {
                // Extract triangles and use optimized method
                var triangles = ExtractTopTriangles(toposolid);
                if (triangles.Count == 0)
                {
                    return null;
                }

                var spatialGrid = new SpatialGrid(triangles);
                return GetElevationAtPointOptimized(point, spatialGrid);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error getting elevation at point ({X}, {Y}, {Z})", point.X, point.Y, point.Z);
                return null;
            }
#endif
        }

        /// <summary>
        /// Finds intersection of a ray with a triangle using Möller–Trumbore algorithm
        /// </summary>
        private XYZ RayTriangleIntersection(XYZ rayOrigin, XYZ rayDirection, XYZ v0, XYZ v1, XYZ v2)
        {
            const double EPSILON = 0.0000001;
            
            XYZ edge1 = v1 - v0;
            XYZ edge2 = v2 - v0;
            
            XYZ h = rayDirection.CrossProduct(edge2);
            double a = edge1.DotProduct(h);
            
            // Ray is parallel to triangle
            if (a > -EPSILON && a < EPSILON)
                return null;
            
            double f = 1.0 / a;
            XYZ s = rayOrigin - v0;
            double u = f * s.DotProduct(h);
            
            // Intersection point is outside triangle
            if (u < 0.0 || u > 1.0)
                return null;
            
            XYZ q = s.CrossProduct(edge1);
            double v = f * rayDirection.DotProduct(q);
            
            // Intersection point is outside triangle
            if (v < 0.0 || u + v > 1.0)
                return null;
            
            // Calculate t to find intersection point
            double t = f * edge2.DotProduct(q);
            
            // Ray intersection
            if (t > EPSILON)
            {
                XYZ intersectionPoint = rayOrigin + t * rayDirection;
                return intersectionPoint;
            }
            
            // Line intersection but not ray intersection
            return null;
        }

        /// <summary>
        /// Finds the closest point on a triangle to a given point
        /// </summary>
        private XYZ GetClosestPointOnTriangle(XYZ point, XYZ v0, XYZ v1, XYZ v2)
        {
            // Project point onto triangle plane
            XYZ edge0 = v1 - v0;
            XYZ edge1 = v2 - v0;
            XYZ normal = edge0.CrossProduct(edge1).Normalize();
            
            if (normal == null || normal.IsZeroLength())
            {
                // Degenerate triangle, return closest vertex
                double d0 = point.DistanceTo(v0);
                double d1 = point.DistanceTo(v1);
                double d2 = point.DistanceTo(v2);
                
                if (d0 <= d1 && d0 <= d2) return v0;
                if (d1 <= d2) return v1;
                return v2;
            }
            
            // Project point onto plane
            XYZ toPoint = point - v0;
            double distance = toPoint.DotProduct(normal);
            XYZ projected = point - distance * normal;
            
            // Check if projected point is inside triangle using barycentric coordinates
            double dot00 = edge0.DotProduct(edge0);
            double dot01 = edge0.DotProduct(edge1);
            double dot02 = edge0.DotProduct(projected - v0);
            double dot11 = edge1.DotProduct(edge1);
            double dot12 = edge1.DotProduct(projected - v0);
            
            double invDenom = 1 / (dot00 * dot11 - dot01 * dot01);
            double u = (dot11 * dot02 - dot01 * dot12) * invDenom;
            double v = (dot00 * dot12 - dot01 * dot02) * invDenom;
            
            // Clamp to triangle bounds
            if (u < 0) u = 0;
            if (v < 0) v = 0;
            if (u + v > 1)
            {
                double sum = u + v;
                u /= sum;
                v /= sum;
            }
            
            // Interpolate Z using barycentric coordinates
            double w = 1 - u - v;
            double z = w * v0.Z + u * v1.Z + v * v2.Z;
            
            return new XYZ(projected.X, projected.Y, z);
        }

        /// <summary>
        /// Checks if a point is on a planar face (within tolerance)
        /// </summary>
        private bool IsPointOnFace(PlanarFace face, XYZ point)
        {
            try
            {
                // Check if point is within face boundary
                // For simplicity, check if point is within bounding box of face
                BoundingBoxUV bbox = face.GetBoundingBox();
                if (bbox == null) return false;
                
                // Convert point to UV coordinates
                IntersectionResult result = face.Project(point);
                if (result == null) return false;
                
                UV uv = result.UVPoint;
                if (uv == null) return false;
                
                return uv.U >= bbox.Min.U && uv.U <= bbox.Max.U &&
                       uv.V >= bbox.Min.V && uv.V <= bbox.Max.V;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recursively extracts points from geometry elements
        /// </summary>
        private void ExtractPointsFromGeometry(GeometryElement geom, HashSet<XYZ> points)
        {
            ExtractPointsFromGeometry(geom, Transform.Identity, points);
        }

        private void ExtractPointsFromGeometry(GeometryElement geom, Transform transform, HashSet<XYZ> points)
        {
            if (geom == null || transform == null) return;

            foreach (GeometryObject obj in geom)
            {
                if (obj is Solid solid)
                {
                    ExtractPointsFromSolid(solid, transform, points);
                }
                else if (obj is Mesh mesh)
                {
                    ExtractPointsFromMesh(mesh, transform, points);
                }
                else if (obj is GeometryInstance instance)
                {
                    Transform combined = transform.Multiply(instance.Transform);
                    ExtractPointsFromGeometry(instance.SymbolGeometry, combined, points);
                }
            }
        }

        /// <summary>
        /// Extracts points from a Solid (edges and vertices)
        /// </summary>
        private void ExtractPointsFromSolid(Solid solid, Transform transform, HashSet<XYZ> points)
        {
            if (solid == null || transform == null) return;

            foreach (Edge edge in solid.Edges)
            {
                Curve curve = edge.AsCurve();
                if (curve != null)
                {
                    points.Add(transform.OfPoint(curve.GetEndPoint(0)));
                    points.Add(transform.OfPoint(curve.GetEndPoint(1)));
                }
            }
        }

        /// <summary>
        /// Extracts points from a Mesh
        /// </summary>
        private void ExtractPointsFromMesh(Mesh mesh, Transform transform, HashSet<XYZ> points)
        {
            if (mesh == null || transform == null) return;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                points.Add(transform.OfPoint(triangle.get_Vertex(0)));
                points.Add(transform.OfPoint(triangle.get_Vertex(1)));
                points.Add(transform.OfPoint(triangle.get_Vertex(2)));
            }
        }

        /// <summary>
        /// Comparer for XYZ points with tolerance
        /// </summary>
        private class XYZComparer : IEqualityComparer<XYZ>
        {
            private const double Tolerance = 0.001; // 1mm tolerance

            public bool Equals(XYZ x, XYZ y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                return Math.Abs(x.X - y.X) < Tolerance &&
                       Math.Abs(x.Y - y.Y) < Tolerance &&
                       Math.Abs(x.Z - y.Z) < Tolerance;
            }

            public int GetHashCode(XYZ obj)
            {
                if (obj == null) return 0;
                return Math.Round(obj.X / Tolerance).GetHashCode() ^
                       Math.Round(obj.Y / Tolerance).GetHashCode() ^
                       Math.Round(obj.Z / Tolerance).GetHashCode();
            }
        }

        /// <summary>
        /// Merges multiple Toposolids using max elevation priority
        /// Excavate approach: Use largest Toposolid as base and modify it with points from others
        /// </summary>
        public 
#if REVIT2024_OR_GREATER
            Toposolid
#else
            Element
#endif
            MergeToposolidsMaxElevation(Document doc, 
#if REVIT2024_OR_GREATER
            List<Toposolid> toposolids
#else
            List<Element> toposolids
#endif
            , bool deleteOriginals = true)
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            if (toposolids == null || toposolids.Count < 2)
            {
                throw new ArgumentException("At least 2 Toposolids are required for merging");
            }

            Log.Information("Merging {Count} Toposolids with max elevation priority (Excavate approach)", toposolids.Count);

            // Excavate approach: Find the largest Toposolid to use as base
            Toposolid baseToposolid = null;
            double maxArea = 0;
            foreach (var toposolid in toposolids)
            {
                try
                {
                    BoundingBoxXYZ bbox = toposolid.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        double area = (bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y);
                        if (area > maxArea)
                        {
                            maxArea = area;
                            baseToposolid = toposolid;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not get bounding box for Toposolid {Id}", GetElementIdValue(toposolid.Id));
                }
            }

            if (baseToposolid == null)
            {
                throw new InvalidOperationException("Could not determine base Toposolid for merge");
            }

            Log.Information("Using Toposolid {Id} as base (largest area: {Area} sq ft)", 
                GetElementIdValue(baseToposolid.Id), maxArea);

            // Extract all points from all Toposolids and create elevation map
            var elevationMap = new Dictionary<string, double>();
            const double tolerance = 0.01; // 1cm tolerance for XY comparison

            foreach (var toposolid in toposolids)
            {
                var points = ExtractPointsFromToposolid(toposolid);
                foreach (var point in points)
                {
                    if (point == null) continue;

                    try
                    {
                        string key = GetXYKey(point, tolerance);
                        // Max elevation priority: keep the highest Z value
                        if (!elevationMap.ContainsKey(key) || elevationMap[key] < point.Z)
                        {
                            elevationMap[key] = point.Z;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error processing point ({X}, {Y}, {Z}), skipping", point.X, point.Y, point.Z);
                        continue;
                    }
                }
            }

            if (elevationMap.Count == 0)
            {
                throw new InvalidOperationException("No valid points found after processing. Cannot merge Toposolids.");
            }

            // Excavate approach: Modify base Toposolid using SlabShapeEditor
            // Check if document is in a transaction
            bool isInTransaction = doc.IsModifiable;
            Log.Information("Document transaction state: IsModifiable={IsModifiable}", isInTransaction);
            
            Transaction tx = null;
            if (!isInTransaction)
            {
                tx = new Transaction(doc, "Merge Toposolids (Excavate)");
                tx.Start();
                Log.Information("Started new transaction: {TransactionName}", tx.GetName());
            }
            else
            {
                Log.Information("Document is already in a transaction, using existing transaction");
            }

            try
            {
                    // Duplicate base Toposolid to preserve original
                    Toposolid workingToposolid = null;
                    try
                    {
                        Log.Debug("Attempting to duplicate base Toposolid {Id}", GetElementIdValue(baseToposolid.Id));
                        // Try to duplicate the base Toposolid
                        ICollection<ElementId> duplicatedIds = ElementTransformUtils.CopyElement(doc, baseToposolid.Id, XYZ.Zero);
                        Log.Debug("CopyElement returned {Count} element(s)", duplicatedIds?.Count ?? 0);
                        
                        if (duplicatedIds != null && duplicatedIds.Count > 0)
                        {
                            ElementId duplicatedId = duplicatedIds.First();
                            Log.Debug("Duplicated element ID: {Id}", GetElementIdValue(duplicatedId));
                            
                            if (duplicatedId != null && duplicatedId != ElementId.InvalidElementId)
                            {
                                workingToposolid = doc.GetElement(duplicatedId) as Toposolid;
                                if (workingToposolid != null)
                                {
                                    Log.Information("Successfully duplicated base Toposolid. New ID: {Id}", GetElementIdValue(duplicatedId));
                                }
                                else
                                {
                                    Log.Warning("Duplicated element {Id} is not a Toposolid", GetElementIdValue(duplicatedId));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not duplicate base Toposolid, will modify directly. Error: {Error}", ex.Message);
                        workingToposolid = baseToposolid;
                    }

                    if (workingToposolid == null)
                    {
                        workingToposolid = baseToposolid;
                    }

                    // Modify using SlabShapeEditor (Excavate approach)
                    Log.Debug("Getting SlabShapeEditor from Toposolid {Id}", GetElementIdValue(workingToposolid.Id));
                    SlabShapeEditor editor = workingToposolid.GetSlabShapeEditor();
                    if (editor != null)
                    {
                        Log.Debug("Enabling SlabShapeEditor");
                        editor.Enable();
                        Log.Debug("SlabShapeEditor enabled successfully");

                        // Get existing points from base Toposolid to preserve topology
                        var existingPoints = ExtractPointsFromToposolid(workingToposolid);
                        var existingMap = new Dictionary<string, double>();
                        foreach (var point in existingPoints)
                        {
                            if (point == null) continue;
                            try
                            {
                                string key = GetXYKey(point, tolerance);
                                existingMap[key] = point.Z;
                            }
                            catch { }
                        }

                        // Validate Z range to ensure Toposolid won't be too thin
                        double existingMinZ = existingPoints.Where(p => p != null).Any() ? existingPoints.Where(p => p != null).Min(p => p.Z) : 0;
                        double existingMaxZ = existingPoints.Where(p => p != null).Any() ? existingPoints.Where(p => p != null).Max(p => p.Z) : 0;
                        double existingZRange = existingMaxZ - existingMinZ;
                        
                        double mergedMinZ = elevationMap.Values.Any() ? elevationMap.Values.Min() : existingMinZ;
                        double mergedMaxZ = elevationMap.Values.Any() ? elevationMap.Values.Max() : existingMaxZ;
                        double mergedZRange = mergedMaxZ - mergedMinZ;
                        
                        Log.Information("Base Toposolid Z range: {MinZ} to {MaxZ} (range: {Range} ft)", 
                            existingMinZ, existingMaxZ, existingZRange);
                        Log.Information("Merged Z range will be: {MinZ} to {MaxZ} (range: {Range} ft)", 
                            mergedMinZ, mergedMaxZ, mergedZRange);
                        
                        // Check minimum thickness requirement
                        const double minThickness = 0.5; // Minimum 0.5 feet (6 inches)
                        if (mergedZRange < minThickness)
                        {
                            Log.Warning("Merged Z range ({Range} ft) is below minimum thickness ({MinThickness} ft). Adjusting points.", 
                                mergedZRange, minThickness);
                            
                            // Adjust Z values in elevation map to ensure minimum thickness
                            double adjustment = (minThickness - mergedZRange) / 2.0;
                            var adjustedElevationMap = new Dictionary<string, double>();
                            
                            foreach (var kvp in elevationMap)
                            {
                                double adjustedZ = kvp.Value;
                                
                                // Adjust extreme Z values
                                if (Math.Abs(kvp.Value - mergedMinZ) < 0.001)
                                {
                                    adjustedZ = mergedMinZ - adjustment;
                                }
                                else if (Math.Abs(kvp.Value - mergedMaxZ) < 0.001)
                                {
                                    adjustedZ = mergedMaxZ + adjustment;
                                }
                                
                                adjustedElevationMap[kvp.Key] = adjustedZ;
                            }
                            
                            elevationMap = adjustedElevationMap;
                            mergedMinZ = mergedMinZ - adjustment;
                            mergedMaxZ = mergedMaxZ + adjustment;
                            mergedZRange = mergedMaxZ - mergedMinZ;
                            Log.Information("Adjusted merged Z range to {Range} ft", mergedZRange);
                        }

                        // Add/update points from elevation map
                        int pointsAdded = 0;
                        int pointsUpdated = 0;
                        int pointsSkipped = 0;
                        const int maxPoints = 1000; // Limit to avoid performance issues

                        var pointsToProcess = elevationMap.Take(maxPoints).ToList();
                        foreach (var kvp in pointsToProcess)
                        {
                            try
                            {
                                var xy = ParseXYKey(kvp.Key);
                                XYZ point = new XYZ(xy.X, xy.Y, kvp.Value);

                                // Validate point Z is within reasonable range
                                if (kvp.Value < mergedMinZ - 10.0 || kvp.Value > mergedMaxZ + 10.0)
                                {
                                    Log.Debug("Skipping point at key '{Key}' - Z value {Z} is out of range", kvp.Key, kvp.Value);
                                    pointsSkipped++;
                                    continue;
                                }

                                // Check if point already exists (within tolerance)
                                bool pointExists = existingMap.ContainsKey(kvp.Key);
                                
                                if (!pointExists)
                                {
                                    // Add new point
                                    SlabShapeEditorHelper.TryAddPoint(editor, point);
                                    pointsAdded++;
                                }
                                else if (Math.Abs(existingMap[kvp.Key] - kvp.Value) > tolerance)
                                {
                                    // Update existing point with new Z (max elevation)
                                    // Note: SlabShapeEditor doesn't have direct update, so we add point
                                    // Revit will handle the merge
                                    SlabShapeEditorHelper.TryAddPoint(editor, point);
                                    pointsUpdated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Debug("Could not add/update point at key '{Key}': {Error}", kvp.Key, ex.Message);
                                pointsSkipped++;
                            }
                        }

                        Log.Information("Added {Added} new points, updated {Updated} points, skipped {Skipped} points via SlabShapeEditor", 
                            pointsAdded, pointsUpdated, pointsSkipped);
                    }

                if (tx != null)
                {
                    tx.Commit();
                    Log.Information("Committed transaction: {TransactionName}", tx.GetName());
                }
                else
                {
                    Log.Information("No transaction to commit (using existing transaction)");
                }

                // Delete originals if requested (except the base if we duplicated it)
                if (deleteOriginals && workingToposolid != null)
                {
                    bool deleteInTransaction = doc.IsModifiable;
                    Transaction deleteTx = null;
                    if (!deleteInTransaction)
                    {
                        deleteTx = new Transaction(doc, "Delete Original Toposolids");
                        deleteTx.Start();
                        Log.Information("Started delete transaction");
                    }
                    else
                    {
                        Log.Information("Using existing transaction for delete operations");
                    }

                    try
                    {
                        foreach (var toposolid in toposolids)
                        {
                            // Don't delete if it's the working Toposolid (duplicated or base)
                            if (toposolid.Id == workingToposolid.Id) continue;

                            try
                            {
                                Log.Debug("Deleting Toposolid {Id}", GetElementIdValue(toposolid.Id));
                                doc.Delete(toposolid.Id);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Could not delete Toposolid {Id}", GetElementIdValue(toposolid.Id));
                            }
                        }

                        if (deleteTx != null)
                        {
                            deleteTx.Commit();
                            Log.Information("Committed delete transaction");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (deleteTx != null)
                        {
                            deleteTx.RollBack();
                            Log.Error(ex, "Rolled back delete transaction");
                        }
                        throw;
                    }
                    finally
                    {
                        deleteTx?.Dispose();
                    }
                }

                Log.Information("Successfully merged {Count} Toposolids using Excavate approach", toposolids.Count);
                return workingToposolid;
            }
            catch (Exception ex)
            {
                if (tx != null)
                {
                    tx.RollBack();
                    Log.Error(ex, "Rolled back transaction: {TransactionName}", tx.GetName());
                }
                else
                {
                    Log.Error(ex, "Error occurred but no transaction to rollback (using existing transaction)");
                }
                throw;
            }
            finally
            {
                tx?.Dispose();
            }
#endif
        }

        /// <summary>
        /// Merges a Proposal Toposolid into an Existing Toposolid (Proposal priority)
        /// New approach: Project existing points onto proposal to get elevation, then update existing points
        /// </summary>
        /// <param name="addProposalPoints">If true, add/use proposal points in proposal boundary. Recommended to keep true.</param>
        /// <param name="useExistingReferencePointsOnProposalSurface">If true, also project Existing points in proposal boundary onto proposal surface as extra reference points.</param>
        /// <param name="boundaryCleanupOffset">Outside offset distance from proposal boundary where Existing points are removed/ignored.</param>
        /// <param name="maxZChange">Maximum allowed Z change in feet. Null = no limit. Points exceeding this will be skipped.</param>
        public 
#if REVIT2024_OR_GREATER
            Toposolid
#else
            Element
#endif
            MergeProposalIntoExisting(Document doc, 
#if REVIT2024_OR_GREATER
            Toposolid proposalToposolid, Toposolid existingToposolid, bool deleteProposal = true, bool addProposalPoints = true, bool useExistingReferencePointsOnProposalSurface = false, double boundaryCleanupOffset = 1.0, double? maxZChange = null
#else
            Element proposalToposolid, Element existingToposolid, bool deleteProposal = true, bool addProposalPoints = true, bool useExistingReferencePointsOnProposalSurface = false, double boundaryCleanupOffset = 1.0, double? maxZChange = null
#endif
            )
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            if (proposalToposolid == null || existingToposolid == null)
            {
                throw new ArgumentException("Both Proposal and Existing Toposolids are required");
            }

            string limitInfo = maxZChange.HasValue ? $"with Z limit {maxZChange.Value} ft" : "with NO Z limit";
            Log.Information("Merging Proposal Toposolid {ProposalId} into Existing Toposolid {ExistingId} (Project points approach, {LimitInfo})",
                GetElementIdValue(proposalToposolid.Id), GetElementIdValue(existingToposolid.Id), limitInfo);

            // New approach: Get boundary of proposal, find existing points within boundary,
            // project them onto proposal to get elevation, then update existing points
            // Check if document is in a transaction
            bool isInTransaction = doc.IsModifiable;
            Log.Information("Document transaction state: IsModifiable={IsModifiable}", isInTransaction);
            
            Transaction tx = null;
            if (!isInTransaction)
            {
                tx = new Transaction(doc, "Merge Proposal into Existing (Project points)");
                tx.Start();
                Log.Information("Started new transaction: {TransactionName}", tx.GetName());
            }
            else
            {
                Log.Information("Document is already in a transaction, using existing transaction");
            }

            try
            {
                    // Get boundary of Proposal Toposolid
                    BoundingBoxXYZ proposalBbox = proposalToposolid.get_BoundingBox(null);
                    if (proposalBbox == null)
                    {
                        throw new InvalidOperationException("Could not get bounding box of Proposal Toposolid");
                    }
                    
                    double proposalMinX = proposalBbox.Min.X;
                    double proposalMaxX = proposalBbox.Max.X;
                    double proposalMinY = proposalBbox.Min.Y;
                    double proposalMaxY = proposalBbox.Max.Y;
                    
                    Log.Information("Proposal Toposolid boundary: X[{MinX}, {MaxX}], Y[{MinY}, {MaxY}]",
                        proposalMinX, proposalMaxX, proposalMinY, proposalMaxY);

                    // Extract all points from Proposal and Existing Toposolids
                    var proposalPoints = ExtractPointsFromToposolid(proposalToposolid);
                    var existingPoints = ExtractPointsFromToposolid(existingToposolid);
                    Log.Information("Extracted {ProposalCount} points from Proposal, {ExistingCount} points from Existing",
                        proposalPoints.Count, existingPoints.Count);

                    const double boundaryTolerance = 0.1; // 10cm tolerance for boundary check
                    const double xyTolerance = 0.01; // 1cm tolerance for XY comparison
                    var pointsToAddOrUpdate = new List<PointUpdate>();

                    // OPTIMIZED: Extract proposal triangles ONCE and build spatial index
                    Log.Information("Extracting triangles from Proposal Toposolid (one-time operation)...");
                    var proposalTriangles = ExtractTopTriangles(proposalToposolid);
                    Log.Information("Extracted {TriangleCount} top-facing triangles from Proposal", proposalTriangles.Count);
                    
                    if (proposalTriangles.Count == 0)
                    {
                        Log.Warning("No triangles extracted from Proposal Toposolid - cannot project points");
                        throw new InvalidOperationException("Proposal Toposolid has no valid geometry to project onto");
                    }

                    Log.Information("Building spatial index for fast triangle lookup...");
                    var spatialGrid = new SpatialGrid(proposalTriangles, cellSize: 10.0);
                    Log.Information("Spatial index built successfully");

                    // Get boundary of Existing Toposolid to check if proposal points are within it
                    BoundingBoxXYZ existingBbox = existingToposolid.get_BoundingBox(null);
                    if (existingBbox == null)
                    {
                        throw new InvalidOperationException("Could not get bounding box of Existing Toposolid");
                    }
                    
                    double existingMinX = existingBbox.Min.X;
                    double existingMaxX = existingBbox.Max.X;
                    double existingMinY = existingBbox.Min.Y;
                    double existingMaxY = existingBbox.Max.Y;
                    
                    Log.Information("Existing Toposolid boundary: X[{MinX}, {MaxX}], Y[{MinY}, {MaxY}]",
                        existingMinX, existingMaxX, existingMinY, existingMaxY);

                    // Snapshot of REAL editable vertices (SlabShapeEditor vertices) before any change.
                    // We preserve these by default, except points explicitly targeted by update/delete rules.
                    var existingEditableVerticesSnapshot = GetExistingEditableVertices(existingToposolid);
                    Log.Information("Captured {Count} editable Existing vertices snapshot for preservation logic.",
                        existingEditableVerticesSnapshot.Count);

                    // Create XY map of existing points for quick lookup
                    var existingXYMap = new Dictionary<string, XYZ>();
                    foreach (var existingPoint in existingPoints)
                    {
                        if (existingPoint == null) continue;
                        try
                        {
                            string key = GetXYKey(existingPoint, xyTolerance);
                            if (!existingXYMap.ContainsKey(key))
                            {
                                existingXYMap[key] = existingPoint;
                            }
                        }
                        catch { }
                    }
                    
                    Log.Information("Created XY map with {Count} existing points", existingXYMap.Count);

                    // Deduplicate proposal points by XY: keep only the point with highest Z at each XY (top only)
                    var proposalXYToTopPoint = new Dictionary<string, XYZ>();
                    foreach (var p in proposalPoints)
                    {
                        if (p == null) continue;
                        try
                        {
                            string key = GetXYKey(p, xyTolerance);
                            if (!proposalXYToTopPoint.TryGetValue(key, out XYZ existingAtXY) || p.Z > existingAtXY.Z)
                            {
                                proposalXYToTopPoint[key] = p;
                            }
                        }
                        catch { }
                    }
                    var proposalPointsTopOnly = proposalXYToTopPoint.Values.ToList();
                    Log.Information("Proposal points: {Original} total, {TopOnly} after keeping only highest Z per XY (duplicates with lower Z skipped)",
                        proposalPoints.Count, proposalPointsTopOnly.Count);

                    // STEP 1: PROJECT existing points within proposal boundary onto proposal surface (optional)
                    Log.Information("Step 1: Projecting existing points within proposal boundary onto proposal surface (optional)...");
                    int existingPointsProjected = 0;
                    int pointsOutsideBoundary = 0;
                    int pointsNoIntersection = 0;

                    var existingPointsInProposalBoundary = existingPoints.Where(p =>
                        p != null && IsPointOnProposalSurfaceXY(p, spatialGrid)).ToList();

                    pointsOutsideBoundary = existingPoints.Count - existingPointsInProposalBoundary.Count;
                    Log.Information("Filtered to {InBoundaryCount} existing points within proposal boundary ({OutsideCount} outside)",
                        existingPointsInProposalBoundary.Count, pointsOutsideBoundary);

                    var existingUpdates = new List<PointUpdate>();
                    if (useExistingReferencePointsOnProposalSurface)
                    {
                        var existingUpdatesBag = new System.Collections.Concurrent.ConcurrentBag<PointUpdate>();
                        var noIntersectionCount = new System.Collections.Concurrent.ConcurrentBag<int>();

                        System.Threading.Tasks.Parallel.ForEach(existingPointsInProposalBoundary, existingPoint =>
                        {
                            try
                            {
                                // Strict projection for merge: do not use nearest-triangle fallback,
                                // so points outside proposal surface are not affected.
                                double? proposalZ = GetElevationAtPointOptimized(existingPoint, spatialGrid, allowFallback: false);
                                if (proposalZ.HasValue)
                                {
                                    if (Math.Abs(existingPoint.Z - proposalZ.Value) > 0.01)
                                    {
                                        existingUpdatesBag.Add(new PointUpdate
                                        {
                                            OriginalPoint = existingPoint,
                                            NewZ = proposalZ.Value,
                                            IsNewPoint = false
                                        });
                                    }
                                }
                                else
                                {
                                    noIntersectionCount.Add(1);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Debug("Error projecting existing point ({X}, {Y}, {Z}): {Error}",
                                    existingPoint.X, existingPoint.Y, existingPoint.Z, ex.Message);
                            }
                        });

                        existingUpdates = existingUpdatesBag.ToList();
                        existingPointsProjected = existingUpdates.Count;
                        pointsNoIntersection = noIntersectionCount.Count;
                        Log.Information("Projection complete: {Projected} existing reference points with elevation updates, {NoIntersection} points with no intersection",
                            existingPointsProjected, pointsNoIntersection);
                    }
                    else
                    {
                        Log.Information("Step 1 skipped: user chose not to use Existing reference points on Proposal surface.");
                    }

                    // STEP 1B: If Existing references are NOT used, clear Existing points inside Proposal boundary.
                    // This enforces "Proposal-driven" interior by removing old Existing vertices first.
                    int existingInsideProposalPointsDeleted = 0;
                    if (!useExistingReferencePointsOnProposalSurface)
                    {
                        SlabShapeEditor interiorCleanupEditor = existingToposolid.GetSlabShapeEditor();
                        if (interiorCleanupEditor != null)
                        {
                            interiorCleanupEditor.Enable();
                            existingInsideProposalPointsDeleted = DeleteExistingPointsInProposalBoundary(
                                interiorCleanupEditor,
                                spatialGrid);
                        }
                        Log.Information("Step 1B: Deleted {Count} Existing points inside Proposal boundary (No Existing reference mode).",
                            existingInsideProposalPointsDeleted);
                    }

                    // STEP 2: ADD proposal points (default ON) and prioritize proposal points in proposal boundary.
                    int proposalPointsToAdd = 0;
                    int proposalPointsOutsideExistingBoundary = 0;
                    int proposalPointsSameAsExisting = 0;
                    var proposalPointUpdates = new List<PointUpdate>();

                    if (addProposalPoints)
                    {
                        Log.Information("Step 2: Adding/prioritizing Proposal points in Existing toposolid (using top-only points per XY)...");

                        foreach (var proposalPoint in proposalPointsTopOnly)
                        {
                            if (proposalPoint == null) continue;

                            try
                            {
                                // Only apply Proposal points inside Proposal boundary (XY plan),
                                // and still respect Existing extents.
                                if (proposalPoint.X < proposalMinX - boundaryTolerance ||
                                    proposalPoint.X > proposalMaxX + boundaryTolerance ||
                                    proposalPoint.Y < proposalMinY - boundaryTolerance ||
                                    proposalPoint.Y > proposalMaxY + boundaryTolerance)
                                {
                                    continue;
                                }

                                if (proposalPoint.X < existingMinX - boundaryTolerance ||
                                    proposalPoint.X > existingMaxX + boundaryTolerance ||
                                    proposalPoint.Y < existingMinY - boundaryTolerance ||
                                    proposalPoint.Y > existingMaxY + boundaryTolerance)
                                {
                                    proposalPointsOutsideExistingBoundary++;
                                    continue;
                                }

                                // Strictly require the XY to be on Proposal top surface.
                                // This avoids changing points that are merely inside bbox but outside actual proposal surface.
                                double? strictProposalZ = GetElevationAtPointOptimized(proposalPoint, spatialGrid, allowFallback: false);
                                if (!strictProposalZ.HasValue)
                                {
                                    continue;
                                }

                                // NO mode: use ONLY Proposal points inside Proposal boundary (as NEW points).
                                // Existing points inside Proposal boundary are deleted in Step 1B,
                                // so there must be NO "existing point updates" in this mode.
                                if (!useExistingReferencePointsOnProposalSurface)
                                {
                                    proposalPointUpdates.Add(new PointUpdate
                                    {
                                        OriginalPoint = proposalPoint,
                                        NewZ = strictProposalZ.Value,
                                        IsNewPoint = true
                                    });
                                    proposalPointsToAdd++;
                                }
                                else
                                {
                                    // YES mode: if Existing has a vertex at same XY, update it; otherwise add new.
                                    string xyKey = GetXYKey(proposalPoint, xyTolerance);
                                    if (existingXYMap.ContainsKey(xyKey))
                                    {
                                        XYZ existingPointAtSameXY = existingXYMap[xyKey];
                                        if (Math.Abs(existingPointAtSameXY.Z - strictProposalZ.Value) > 0.01)
                                        {
                                            proposalPointUpdates.Add(new PointUpdate
                                            {
                                                OriginalPoint = existingPointAtSameXY,
                                                NewZ = strictProposalZ.Value,
                                                IsNewPoint = false
                                            });
                                            proposalPointsToAdd++;
                                        }
                                        else
                                        {
                                            proposalPointsSameAsExisting++;
                                        }
                                    }
                                    else
                                    {
                                        proposalPointUpdates.Add(new PointUpdate
                                        {
                                            OriginalPoint = proposalPoint,
                                            NewZ = strictProposalZ.Value,
                                            IsNewPoint = true
                                        });
                                        proposalPointsToAdd++;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Error processing proposal point ({X}, {Y}, {Z}), skipping",
                                    proposalPoint.X, proposalPoint.Y, proposalPoint.Z);
                                continue;
                            }
                        }

                        Log.Information("Proposal points analysis: {ToAdd} points with different xyz to add/use, {OutsideBoundary} outside existing boundary (skipped), {SameXYZ} same xyz (skipped)",
                            proposalPointsToAdd, proposalPointsOutsideExistingBoundary, proposalPointsSameAsExisting);
                    }
                    else
                    {
                        Log.Information("Step 2 skipped: addProposalPoints=false");
                    }

                    // STEP 3: Boundary cleanup band outside proposal boundary.
                    int existingBoundaryBandPointsDeleted = 0;
                    int existingBoundaryBandPointsIgnored = 0;
                    double cleanupOffset = Math.Max(0.0, boundaryCleanupOffset);
                    bool cleanupEnabled = cleanupOffset > 0.0;
                    if (cleanupEnabled)
                    {
                        var proposalXYSeeds = proposalPointsTopOnly ?? new List<XYZ>();
                        var existingBoundaryBandPoints = existingPoints.Where(p =>
                            p != null &&
                            !IsPointOnProposalSurfaceXY(p, spatialGrid) &&
                            IsWithinOffsetFromProposalXY(p, proposalXYSeeds, cleanupOffset)).ToList();

                        existingBoundaryBandPointsIgnored = existingBoundaryBandPoints.Count;
                        Log.Information("Step 3: Boundary cleanup band identified {Count} Existing points to remove/ignore (offset {Offset} ft).",
                            existingBoundaryBandPointsIgnored, cleanupOffset);

                        SlabShapeEditor cleanupEditor = existingToposolid.GetSlabShapeEditor();
                        if (cleanupEditor != null)
                        {
                            cleanupEditor.Enable();
                            existingBoundaryBandPointsDeleted = DeleteExistingPointsInBand(cleanupEditor,
                                spatialGrid, proposalXYSeeds, cleanupOffset);
                        }
                    }
                    else
                    {
                        Log.Information("Step 3 skipped: boundary cleanup offset <= 0.");
                    }

                    var mergedUpdatesByXY = new Dictionary<string, PointUpdate>();
                    foreach (var update in existingUpdates)
                    {
                        string key = GetXYKey(update.OriginalPoint, xyTolerance);
                        if (!mergedUpdatesByXY.ContainsKey(key))
                        {
                            mergedUpdatesByXY[key] = update;
                        }
                    }
                    foreach (var update in proposalPointUpdates)
                    {
                        string key = GetXYKey(update.OriginalPoint, xyTolerance);
                        mergedUpdatesByXY[key] = update;
                    }
                    pointsToAddOrUpdate.AddRange(mergedUpdatesByXY.Values);
                    
                    int pointsToAdd = pointsToAddOrUpdate.Count(p => p.IsNewPoint);
                    int pointsToUpdate = pointsToAddOrUpdate.Count(p => !p.IsNewPoint);
                    
                    Log.Information("Total: {AddCount} new points to add from proposal, {UpdateCount} existing points to update",
                        pointsToAdd, pointsToUpdate);
                    if (cleanupEnabled)
                    {
                        Log.Information("Boundary cleanup result: {Deleted} Existing points deleted in cleanup band, {Ignored} points targeted by cleanup logic.",
                            existingBoundaryBandPointsDeleted, existingBoundaryBandPointsIgnored);
                    }
                    if (!useExistingReferencePointsOnProposalSurface)
                    {
                        Log.Information("Inside-boundary cleanup result: {Deleted} Existing points deleted inside Proposal boundary.",
                            existingInsideProposalPointsDeleted);
                    }
                    
                    if (pointsToAddOrUpdate.Count == 0)
                    {
                        Log.Warning("No points found to update. Proposal may not overlap with Existing boundary, or existing points already have same elevations.");
                    }


                    // Log Z change policy
                    if (maxZChange.HasValue)
                    {
                        Log.Information("Z change limited to {MaxZChange} feet - points exceeding this will be skipped", maxZChange.Value);
                    }
                    else
                    {
                        Log.Information("No Z change restrictions - all existing points in proposal boundary will be updated");
                    }

                    // Modify Existing Toposolid using SlabShapeEditor
                    // Update existing points and add new points from proposal
                    SlabShapeEditor editor = existingToposolid.GetSlabShapeEditor();
                    if (editor != null)
                    {
                        editor.Enable();

                        int pointsAdded = 0;
                        int pointsUpdated = 0;
                        int pointsSkipped = 0;
                        int pointsSkippedDueToZLimit = 0;

                        // Process all point updates (both new points and updates to existing points)
                        foreach (var pointUpdate in pointsToAddOrUpdate)
                        {
                            try
                            {
                                // Check Z change limit if specified (only for existing point updates, not new points)
                                if (!pointUpdate.IsNewPoint && maxZChange.HasValue)
                                {
                                    double zChange = Math.Abs(pointUpdate.NewZ - pointUpdate.OriginalPoint.Z);
                                    
                                    if (zChange > maxZChange.Value)
                                    {
                                        // Skip this point due to Z change exceeding limit
                                        Log.Debug("Skipping point at ({X}, {Y}) - Z change {Change:F2} ft exceeds limit {Limit:F2} ft (from {OldZ:F2} to {NewZ:F2})", 
                                            pointUpdate.OriginalPoint.X, pointUpdate.OriginalPoint.Y, 
                                            zChange, maxZChange.Value,
                                            pointUpdate.OriginalPoint.Z, pointUpdate.NewZ);
                                        pointsSkippedDueToZLimit++;
                                        pointsSkipped++;
                                        continue;
                                    }
                                }
                                
                                XYZ point = new XYZ(pointUpdate.OriginalPoint.X, pointUpdate.OriginalPoint.Y, pointUpdate.NewZ);
                                
                                // Add/update point - SlabShapeEditor will handle merging if point exists
                                SlabShapeEditorHelper.TryAddPoint(editor, point);
                                
                                if (pointUpdate.IsNewPoint)
                                {
                                    pointsAdded++;
                                }
                                else
                                {
                                    pointsUpdated++;
                                }
                            }
                            catch (Exception ex)
                            {
                                string action = pointUpdate.IsNewPoint ? "add new point" : "update point";
                                Log.Debug("Could not {Action} at ({X}, {Y}) Z={Z}: {Error}", 
                                    action, pointUpdate.OriginalPoint.X, pointUpdate.OriginalPoint.Y, pointUpdate.NewZ, ex.Message);
                                pointsSkipped++;
                            }
                        }

                        // Re-apply preserved points (all original editable vertices except explicitly targeted ones).
                        // This protects unaffected Existing area from unintended triangulation/elevation drift.
                        var targetedKeys = new HashSet<string>();
                        foreach (var pointUpdate in pointsToAddOrUpdate)
                        {
                            if (pointUpdate?.OriginalPoint == null) continue;
                            try { targetedKeys.Add(GetXYKey(pointUpdate.OriginalPoint, xyTolerance)); } catch { }
                        }

                        int preservedPointsReapplied = 0;
                        foreach (var preservedPoint in existingEditableVerticesSnapshot)
                        {
                            if (preservedPoint == null) continue;
                            bool inProposalBoundary = IsPointOnProposalSurfaceXY(preservedPoint, spatialGrid);
                            bool inCleanupBand = !inProposalBoundary &&
                                IsWithinOffsetFromProposalXY(preservedPoint, proposalPointsTopOnly, cleanupOffset);

                            // Skip re-apply if point is intentionally modified/deleted by current rules.
                            if (inCleanupBand) continue;
                            if (!useExistingReferencePointsOnProposalSurface && inProposalBoundary) continue;
                            string preserveKey;
                            try { preserveKey = GetXYKey(preservedPoint, xyTolerance); } catch { continue; }
                            if (targetedKeys.Contains(preserveKey)) continue;

                            try
                            {
                                SlabShapeEditorHelper.TryAddPoint(editor, new XYZ(preservedPoint.X, preservedPoint.Y, preservedPoint.Z));
                                preservedPointsReapplied++;
                            }
                            catch (Exception ex)
                            {
                                Log.Debug("Could not re-apply preserved Existing point at ({X}, {Y}) Z={Z}: {Error}",
                                    preservedPoint.X, preservedPoint.Y, preservedPoint.Z, ex.Message);
                            }
                        }
                        Log.Information("Re-applied {Count} preserved Existing points outside impacted area.",
                            preservedPointsReapplied);

                        // Store statistics for user notification
                        LastMergePointsAdded = pointsAdded;
                        LastMergePointsUpdated = pointsUpdated;
                        LastMergePointsSkipped = pointsSkipped;
                        LastMergePointsSkippedDueToZLimit = pointsSkippedDueToZLimit;
                        LastMergeZLimitInfo = maxZChange.HasValue ? $"{maxZChange.Value:F2} feet" : "No limit";
                        
                        if (maxZChange.HasValue && pointsSkippedDueToZLimit > 0)
                        {
                            Log.Warning("Added {Added} new points from proposal, updated {Updated} existing points, skipped {Skipped} points ({SkippedDueToLimit} exceeded Z limit of {Limit} ft) in Existing Toposolid via SlabShapeEditor", 
                                pointsAdded, pointsUpdated, pointsSkipped, pointsSkippedDueToZLimit, maxZChange.Value);
                        }
                        else
                        {
                            Log.Information("Added {Added} new points from proposal, updated {Updated} existing points, skipped {Skipped} points in Existing Toposolid via SlabShapeEditor", 
                                pointsAdded, pointsUpdated, pointsSkipped);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not get SlabShapeEditor from Existing Toposolid");
                    }

                if (tx != null)
                {
                    tx.Commit();
                    Log.Information("Committed transaction: {TransactionName}", tx.GetName());
                }
                else
                {
                    Log.Information("No transaction to commit (using existing transaction)");
                }

                // Delete Proposal Toposolid if requested (Existing is now modified)
                if (deleteProposal)
                {
                    bool deleteInTransaction = doc.IsModifiable;
                    Transaction deleteTx = null;
                    if (!deleteInTransaction)
                    {
                        deleteTx = new Transaction(doc, "Delete Proposal Toposolid");
                        deleteTx.Start();
                        Log.Information("Started delete transaction");
                    }
                    else
                    {
                        Log.Information("Using existing transaction for delete operation");
                    }

                    try
                    {
                        Log.Debug("Deleting Proposal Toposolid {Id}", GetElementIdValue(proposalToposolid.Id));
                        doc.Delete(proposalToposolid.Id);
                        
                        if (deleteTx != null)
                        {
                            deleteTx.Commit();
                            Log.Information("Committed delete transaction");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Could not delete Proposal Toposolid");
                        if (deleteTx != null)
                        {
                            deleteTx.RollBack();
                        }
                        // Don't throw - deletion failure shouldn't fail the whole operation
                    }
                    finally
                    {
                        deleteTx?.Dispose();
                    }
                }
                else
                {
                    Log.Information("Keeping Proposal Toposolid as requested");
                }

                Log.Information("Successfully merged Proposal into Existing Toposolid using Excavate approach");
                return existingToposolid; // Return modified Existing Toposolid
            }
            catch (Exception ex)
            {
                if (tx != null)
                {
                    tx.RollBack();
                    Log.Error(ex, "Rolled back transaction: {TransactionName}", tx.GetName());
                }
                else
                {
                    Log.Error(ex, "Error occurred but no transaction to rollback (using existing transaction)");
                }
                throw;
            }
            finally
            {
                tx?.Dispose();
            }
#endif
        }

        /// <summary>
        /// Makes a Floor follow a Toposolid surface: updates EVERY SlabShape vertex (including all boundary) to topo elevation.
        /// Logic: (1) Use only SlabShapeEditor.SlabShapeVertices so no vertex is missed. (2) Project each vertex onto toposolid and set Z. (3) Add toposolid points within floor boundary. (4) Apply via SlabShapeEditor.
        /// </summary>
        public Floor FloorFollowToposolid(Document doc, Floor floor, 
#if REVIT2024_OR_GREATER
            Toposolid toposolid,
#else
            Element toposolid,
#endif
            FloorBoundarySamplingOptions boundarySampling = null)
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            if (floor == null || toposolid == null)
            {
                throw new ArgumentException("Both Floor and Toposolid are required");
            }

            boundarySampling ??= FloorBoundarySamplingOptions.Default;
            if (boundarySampling.Mode == BoundarySampleMode.ByDistance)
            {
                if (boundarySampling.SpacingFeet <= 0)
                    throw new ArgumentException("Boundary spacing must be greater than zero.");
                Log.Information("Boundary sampling: by distance, spacing = {SpacingFeet:F4} ft", boundarySampling.SpacingFeet);
            }
            else
            {
                if (boundarySampling.SegmentsPerCurve < 1)
                    throw new ArgumentException("Segments per curve must be at least 1.");
                Log.Information("Boundary sampling: by segment count, {Segments} segments per curve",
                    boundarySampling.SegmentsPerCurve);
            }

            Log.Information("Making Floor {FloorId} follow Toposolid {TopoId}",
                GetElementIdValue(floor.Id), GetElementIdValue(toposolid.Id));

            // Get Floor boundary
            BoundingBoxXYZ floorBbox = floor.get_BoundingBox(null);
            if (floorBbox == null)
            {
                throw new InvalidOperationException("Could not get bounding box of Floor");
            }
            
            double floorMinX = floorBbox.Min.X;
            double floorMaxX = floorBbox.Max.X;
            double floorMinY = floorBbox.Min.Y;
            double floorMaxY = floorBbox.Max.Y;
            
            Log.Information("Floor boundary: X[{MinX}, {MaxX}], Y[{MinY}, {MaxY}]",
                floorMinX, floorMaxX, floorMinY, floorMaxY);

            const double boundaryTolerance = 0.1; // 10cm tolerance for boundary check
            const double xyTolerance = 0.01; // 1cm tolerance for XY comparison

            SlabShapeEditor editor = floor.GetSlabShapeEditor();
            if (editor == null)
                throw new InvalidOperationException("Could not get SlabShapeEditor from Floor");

            Level floorLevel = (floor.LevelId != null && floor.LevelId != ElementId.InvalidElementId)
                ? doc.GetElement(floor.LevelId) as Level
                : null;
            double levelElevation = floorLevel?.Elevation ?? 0;
            double heightOffsetFromLevel = GetFloorHeightOffsetFromLevel(floor);
            double floorDatumZ = levelElevation + heightOffsetFromLevel;
            var surveyCoords = new SurveyCoordinateHelper(doc);
            Log.Information(
                $"Floor reference: level={levelElevation:F4} ft, height offset={heightOffsetFromLevel:F4} ft (unchanged); elevations matched in survey coordinates");

            editor = ResetAndPrepareFloorSlabShape(doc, floor, out bool slabShapeWasReset) ?? editor;

            // SlabShape vertices (after reset + Enable + Regenerate) – only real slab vertices use ModifySubElement
            var slabVertexPoints = ExtractSlabShapeVerticesFromFloor(floor, editor);
            var existingSlabVertexXY = new HashSet<string>();
            foreach (var p in slabVertexPoints)
            {
                try { existingSlabVertexXY.Add(GetXYKey(p, xyTolerance)); } catch { }
            }

            var floorPointsToUpdate = new List<XYZ>(slabVertexPoints);
            var floorPointXYKeys = new HashSet<string>(existingSlabVertexXY);

            if (floorPointsToUpdate.Count == 0)
            {
                if (slabShapeWasReset)
                {
                    Log.Information(
                        "Flat floor after slab shape reset – no existing SlabShape vertices; shape will be built from boundary sampling and corner pass only");
                }
                else
                {
                    Log.Warning("No SlabShape vertices and reset failed – using geometry + boundary points as fallback");
                    var floorPoints = ExtractPointsFromFloor(floor);
                    if (floorPoints == null || floorPoints.Count == 0)
                    {
                        Log.Warning("No points found on Floor – cannot update elevation");
                        throw new InvalidOperationException("Floor has no SlabShape vertices and no geometry points to update.");
                    }
                    // Top surface only: at each XY keep points with Z >= maxZ - 2cm
                    var floorTopZByXY = new Dictionary<string, double>();
                    foreach (var p in floorPoints)
                    {
                        if (p == null) continue;
                        try
                        {
                            string key = GetXYKey(p, xyTolerance);
                            if (!floorTopZByXY.TryGetValue(key, out double maxZ)) maxZ = double.MinValue;
                            if (p.Z > maxZ) floorTopZByXY[key] = p.Z;
                        }
                        catch { }
                    }
                    const double topSurfaceTolerance = 0.02;
                    foreach (var p in floorPoints)
                    {
                        if (p == null) continue;
                        try
                        {
                            string key = GetXYKey(p, xyTolerance);
                            if (floorTopZByXY.TryGetValue(key, out double maxZ) && p.Z >= maxZ - topSurfaceTolerance
                                && floorPointXYKeys.Add(key))
                                floorPointsToUpdate.Add(p);
                        }
                        catch { }
                    }
                    Log.Information("Fallback: using {Count} top-surface points from geometry", floorPointsToUpdate.Count);
                }
            }
            else
            {
                Log.Information("Using {SlabCount} SlabShape vertices ({Total} total for Step 1 projection)",
                    slabVertexPoints.Count, floorPointsToUpdate.Count);
            }

            var sketchCorners = ExtractSketchBoundaryCornerPoints(floor);
            var sketchCornerUpdates = new List<(XYZ Corner, double TopoZ)>();

            // Extract points from Toposolid
            var topoPoints = ExtractPointsFromToposolid(toposolid);
            Log.Information("Extracted {Count} points from Toposolid", topoPoints.Count);

            var pointsToAddOrUpdate = new List<PointUpdate>();

            // OPTIMIZED: Extract toposolid triangles ONCE and build spatial index
            Log.Information("Extracting triangles from Toposolid (one-time operation)...");
            var topoTriangles = ExtractTopTriangles(toposolid);
            Log.Information("Extracted {TriangleCount} top-facing triangles from Toposolid", topoTriangles.Count);
            
            if (topoTriangles.Count == 0)
            {
                Log.Warning("No triangles extracted from Toposolid - cannot project points");
                throw new InvalidOperationException("Toposolid has no valid geometry to project onto");
            }

            var topoElevationProvider = new TopoSurfaceElevationProvider(
                doc, toposolid, topoTriangles, floorDatumZ, surveyCoords);

            if (sketchCorners.Count > 0 && sketchCorners[0] != null)
            {
                var sample = sketchCorners[0];
                double? sampleTopoSurvey = topoElevationProvider.GetTopSurfaceSurveyElevation(sample.X, sample.Y, this);
                if (sampleTopoSurvey.HasValue)
                {
                    double sampleOffset = surveyCoords.SurveyElevationToFloorSlabOffset(
                        sample.X, sample.Y, sampleTopoSurvey.Value, levelElevation, heightOffsetFromLevel);
                    Log.Information(
                        $"Sample topo survey elev={sampleTopoSurvey.Value:F4} ft -> floor slab offset={sampleOffset:F4} ft");
                }
            }

            foreach (var corner in sketchCorners)
            {
                if (corner == null) continue;
                try
                {
                    double? topoSurveyZ = topoElevationProvider.GetTopSurfaceSurveyElevation(corner.X, corner.Y, this);
                    if (!topoSurveyZ.HasValue) continue;
                    sketchCornerUpdates.Add((corner, topoSurveyZ.Value));
                    string key = GetXYKey(corner, xyTolerance);
                    floorPointXYKeys.Add(key);
                }
                catch { }
            }
            if (sketchCornerUpdates.Count > 0)
                Log.Information("Prepared {Count} sketch corner elevations for corner pass", sketchCornerUpdates.Count);

            // STEP 1: PROJECT every SlabShape vertex onto toposolid – update all (including boundary) to topo Z
            Log.Information("Step 1: Projecting {FloorPointCount} SlabShape vertices onto toposolid (no filter – all vertices)...", floorPointsToUpdate.Count);
            int floorPointsProjected = 0;
            int pointsNoIntersection = 0;
            
            var floorUpdates = new System.Collections.Concurrent.ConcurrentBag<PointUpdate>();
            var noIntersectionCount = new System.Collections.Concurrent.ConcurrentBag<int>();
            
            System.Threading.Tasks.Parallel.ForEach(floorPointsToUpdate, floorPoint =>
            {
                try
                {
                    double? topoSurveyZ = topoElevationProvider.GetTopSurfaceSurveyElevation(floorPoint.X, floorPoint.Y, this);
                    
                    if (topoSurveyZ.HasValue)
                    {
                        string xyKey = GetXYKey(floorPoint, xyTolerance);
                        bool isSlabVertex = existingSlabVertexXY.Contains(xyKey);
                        floorUpdates.Add(new PointUpdate 
                        { 
                            OriginalPoint = floorPoint, 
                            NewZ = topoSurveyZ.Value,
                            IsNewPoint = !isSlabVertex,
                            IsSlabShapeVertex = isSlabVertex
                        });
                    }
                    else
                    {
                        noIntersectionCount.Add(1);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Error projecting floor point ({X}, {Y}, {Z}): {Error}", 
                        floorPoint.X, floorPoint.Y, floorPoint.Z, ex.Message);
                }
            });
            
            // Convert concurrent bag to list
            pointsToAddOrUpdate.AddRange(floorUpdates);
            floorPointsProjected = floorUpdates.Count;
            pointsNoIntersection = noIntersectionCount.Count;
            
            Log.Information("Projection complete: {Projected} floor points with elevation updates, {NoIntersection} points with no intersection", 
                floorPointsProjected, pointsNoIntersection);

            // STEP 2: ADD toposolid points within floor boundary (optional) + boundary curve samples
            bool addInteriorTopoPoints = boundarySampling.AddToposolidPointsWithinBoundary;
            if (addInteriorTopoPoints)
                Log.Information("Step 2: Finding toposolid points within floor boundary to add (top-only per XY)...");
            else
                Log.Information("Step 2: Skipping interior Toposolid points (user option); boundary curve points only");

            // Create XY map of SlabShape vertices for quick lookup
            var floorXYMap = new Dictionary<string, XYZ>();
            foreach (var floorPoint in floorPointsToUpdate)
            {
                if (floorPoint == null) continue;
                try
                {
                    string key = GetXYKey(floorPoint, xyTolerance);
                    if (!floorXYMap.ContainsKey(key))
                    {
                        floorXYMap[key] = floorPoint;
                    }
                }
                catch { }
            }
            foreach (var (corner, _) in sketchCornerUpdates)
            {
                if (corner == null) continue;
                try
                {
                    string key = GetXYKey(corner, xyTolerance);
                    if (!floorXYMap.ContainsKey(key))
                        floorXYMap[key] = corner;
                }
                catch { }
            }
            
            // Toposolid interior points (optional): at each XY keep only highest Z (top surface)
            var topoPointsToAddByXY = new Dictionary<string, PointUpdate>();
            if (addInteriorTopoPoints)
            {
                foreach (var topoPoint in topoPoints)
                {
                    if (topoPoint == null) continue;

                    try
                    {
                        if (topoPoint.X >= floorMinX - boundaryTolerance &&
                            topoPoint.X <= floorMaxX + boundaryTolerance &&
                            topoPoint.Y >= floorMinY - boundaryTolerance &&
                            topoPoint.Y <= floorMaxY + boundaryTolerance)
                        {
                            string xyKey = GetXYKey(topoPoint, xyTolerance);

                            if (floorXYMap.ContainsKey(xyKey))
                                continue;

                            var update = new PointUpdate
                            {
                                OriginalPoint = topoPoint,
                                NewZ = topoPoint.Z,
                                IsNewPoint = true
                            };
                            if (!topoPointsToAddByXY.TryGetValue(xyKey, out PointUpdate existing) || topoPoint.Z > existing.NewZ)
                                topoPointsToAddByXY[xyKey] = update;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error processing topo point ({X}, {Y}, {Z}), skipping",
                            topoPoint.X, topoPoint.Y, topoPoint.Z);
                    }
                }
            }

            // STEP 2b: Sample entire boundary line and add points so toàn bộ đường boundary được chiếu theo topo
            var boundarySampled = ExtractBoundarySampledPoints(floor, boundarySampling);
            foreach (var pt in boundarySampled)
            {
                if (pt == null) continue;
                string key = GetXYKey(pt, xyTolerance);
                if (floorXYMap.ContainsKey(key)) continue;
                double? topoSurveyZ = topoElevationProvider.GetTopSurfaceSurveyElevation(pt.X, pt.Y, this);
                if (!topoSurveyZ.HasValue) continue;
                if (!topoPointsToAddByXY.TryGetValue(key, out PointUpdate existing) || topoSurveyZ.Value > existing.NewZ)
                    topoPointsToAddByXY[key] = new PointUpdate { OriginalPoint = pt, NewZ = topoSurveyZ.Value, IsNewPoint = true };
            }
            
            // Project each topo point onto toposolid surface for Z (avoid "flying" points - vertex Z can be wrong)
            int topoPointsSkippedNoProjection = 0;
            foreach (var kv in topoPointsToAddByXY)
            {
                var pu = kv.Value;
                double? projectedSurveyZ = topoElevationProvider.GetTopSurfaceSurveyElevation(
                    pu.OriginalPoint.X, pu.OriginalPoint.Y, this);
                if (projectedSurveyZ.HasValue)
                {
                    pointsToAddOrUpdate.Add(new PointUpdate 
                    { 
                        OriginalPoint = pu.OriginalPoint, 
                        NewZ = projectedSurveyZ.Value,
                        IsNewPoint = true
                    });
                }
                else
                {
                    topoPointsSkippedNoProjection++;
                    Log.Debug("Skipping topo point at ({X}, {Y}) - no intersection with topo surface", pu.OriginalPoint.X, pu.OriginalPoint.Y);
                }
            }
            
            int topoPointsToAdd = pointsToAddOrUpdate.Count(p => p.IsNewPoint);
            if (addInteriorTopoPoints)
                Log.Information("Found {NewCount} points to add (interior topo + boundary, projected onto topo surface). Skipped {Skipped} (no surface intersection)",
                    topoPointsToAdd, topoPointsSkippedNoProjection);
            else
                Log.Information("Found {NewCount} boundary-only points to add (no interior topo). Skipped {Skipped} (no surface intersection)",
                    topoPointsToAdd, topoPointsSkippedNoProjection);
            
            int pointsToAdd = pointsToAddOrUpdate.Count(p => p.IsNewPoint);
            int pointsToUpdate = pointsToAddOrUpdate.Count(p => !p.IsNewPoint);
            
            Log.Information("Total: {AddCount} new points to add, {UpdateCount} floor points to update",
                pointsToAdd, pointsToUpdate);
            
            if (pointsToAddOrUpdate.Count == 0)
            {
                Log.Warning("No points found to add or update. Floor may not overlap with Toposolid.");
            }

            // Trùng XY: bỏ bớt điểm – mỗi XY chỉ giữ MỘT điểm với Z = cao độ topo (max Z). Add toàn bộ cao độ theo topo.
            const double applyXYTolerance = 0.05; // ~1.5 cm: coi là trùng XY
            var pointsByXY = new Dictionary<string, PointUpdate>();
            foreach (var pu in pointsToAddOrUpdate)
            {
                if (pu?.OriginalPoint == null) continue;
                try
                {
                    string key = GetXYKey(pu.OriginalPoint, applyXYTolerance);
                    if (!pointsByXY.TryGetValue(key, out PointUpdate existing))
                        pointsByXY[key] = pu;
                    else if (pu.IsSlabShapeVertex && !existing.IsSlabShapeVertex)
                        pointsByXY[key] = pu;
                    else if (!pu.IsSlabShapeVertex && existing.IsSlabShapeVertex)
                        { /* keep existing slab vertex entry */ }
                    else if (pu.NewZ > existing.NewZ)
                        pointsByXY[key] = pu;
                }
                catch { }
            }
            var pointsToApply = pointsByXY.Values.ToList();
            var sketchCornerKeys = new HashSet<string>();
            foreach (var (corner, _) in sketchCornerUpdates)
            {
                try { sketchCornerKeys.Add(GetXYKey(corner, applyXYTolerance)); } catch { }
            }
            if (sketchCornerKeys.Count > 0)
            {
                int before = pointsToApply.Count;
                pointsToApply = pointsToApply
                    .Where(pu =>
                    {
                        if (pu?.OriginalPoint == null) return true;
                        try { return !sketchCornerKeys.Contains(GetXYKey(pu.OriginalPoint, applyXYTolerance)); }
                        catch { return true; }
                    })
                    .ToList();
                Log.Information("Excluded {Count} sketch corner XY from bulk AddPoint (handled in corner pass)", before - pointsToApply.Count);
            }
            Log.Information("Deduplicated by XY (~1.5cm): {Before} -> {After} points to apply (one per XY, Z = topo; bỏ bớt trùng thay vì giữ mà không chỉnh cao độ)",
                pointsToAddOrUpdate.Count, pointsToApply.Count);

            // Modify Floor using SlabShapeEditor – add new points first, then ModifySubElement for existing vertices, then corner pass
            editor = floor.GetSlabShapeEditor() ?? editor;
            if (editor != null)
            {
                EnsureSlabShapeEditingEnabled(editor);

                int pointsApplied = 0;
                int pointsModified = 0;
                int pointsSkipped = 0;
                var appliedPointsWithZ = new List<(double X, double Y, double Z)>();
                var pointsSkippedByRevit = new List<PointUpdate>();

                const double matchXYTolerance = 0.05; // ~1.5 cm to match (x,y) to SlabShapeVertex

                var newPoints = pointsToApply.Where(p => p.IsNewPoint).ToList();
                var existingVertices = pointsToApply.Where(p => p.IsSlabShapeVertex).ToList();

                foreach (var pointUpdate in newPoints.Concat(existingVertices))
                {
                    double x = pointUpdate.OriginalPoint.X;
                    double y = pointUpdate.OriginalPoint.Y;
                    double topoSurveyZ = pointUpdate.NewZ;

                    if (ApplyTopoElevationAtXY(
                        editor, x, y, topoSurveyZ, surveyCoords,
                        levelElevation, heightOffsetFromLevel, matchXYTolerance))
                    {
                        pointsApplied++;
                        pointsModified++;
                        appliedPointsWithZ.Add((x, y, topoSurveyZ));
                    }
                    else
                    {
                        Log.Debug("Could not apply point at ({X}, {Y}) surveyZ={Z} (IsNew={IsNew}, IsSlab={IsSlab})",
                            x, y, topoSurveyZ, pointUpdate.IsNewPoint, pointUpdate.IsSlabShapeVertex);
                        pointsSkipped++;
                        pointsSkippedByRevit.Add(pointUpdate);
                    }
                }

                // Corner pass: sketch junction points are implicit boundary vertices – not in SlabShapeVertices until modified via DrawPoint/ModifySubElement
                int cornersModified = ApplySketchCornerElevations(
                    doc, floor, editor, sketchCornerUpdates, surveyCoords,
                    levelElevation, heightOffsetFromLevel);
                pointsModified += cornersModified;
                pointsApplied += cornersModified;

                // For new points Revit could not accept: set Z to average of 2 nearest neighbors (by XY distance).
                int pointsAdjustedByAverage = 0;
                var skippedPointAvgZ = new List<(double x, double y, double avgZ)>();

                foreach (var skipped in pointsSkippedByRevit)
                {
                    if (skipped.IsSlabShapeVertex) continue;
                    if (appliedPointsWithZ.Count < 2) continue;
                    double x = skipped.OriginalPoint.X, y = skipped.OriginalPoint.Y;
                    var nearest = appliedPointsWithZ
                        .Select(p => (p, distSq: (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)))
                        .OrderBy(t => t.distSq)
                        .Take(2)
                        .ToList();
                    if (nearest.Count < 2) continue;
                    double avgSurveyZ = (nearest[0].p.Z + nearest[1].p.Z) * 0.5;
                    if (ApplyTopoElevationAtXY(
                        editor, x, y, avgSurveyZ, surveyCoords,
                        levelElevation, heightOffsetFromLevel, matchXYTolerance))
                    {
                        pointsAdjustedByAverage++;
                    }
                    else
                    {
                        skippedPointAvgZ.Add((x, y, avgSurveyZ));
                    }
                }

                // For skipped points where AddPoint failed: find existing vertex at (x,y) and set elevation via ModifySubElement (offset from level).
                if (skippedPointAvgZ.Count > 0 && editor.SlabShapeVertices != null)
                {
                    var vertexToOffsetSumCount = new Dictionary<SlabShapeVertex, (double sumZ, int count, double x, double y)>();
                    foreach (var (x, y, avgSurveyZ) in skippedPointAvgZ)
                    {
                        SlabShapeVertex best = FindSlabShapeVertexNearXY(editor, x, y, matchXYTolerance);
                        if (best != null)
                        {
                            if (!vertexToOffsetSumCount.TryGetValue(best, out var t))
                                vertexToOffsetSumCount[best] = (avgSurveyZ, 1, x, y);
                            else
                                vertexToOffsetSumCount[best] = (t.sumZ + avgSurveyZ, t.count + 1, x, y);
                        }
                    }
                    foreach (var kv in vertexToOffsetSumCount)
                    {
                        try
                        {
                            double meanSurveyZ = kv.Value.sumZ / kv.Value.count;
                            double slabOffset = TopoSurveyElevationToSlabOffset(
                                surveyCoords, kv.Value.x, kv.Value.y, meanSurveyZ,
                                levelElevation, heightOffsetFromLevel);
                            editor.ModifySubElement(kv.Key, slabOffset);
                            pointsAdjustedByAverage++;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Could not ModifySubElement for vertex: {Error}", ex.Message);
                        }
                    }
                }

                LastFloorFollowBoundaryPointsUpdated = pointsApplied;
                LastFloorFollowTopoPointsAdded = 0;
                LastFloorFollowPointsSkipped = pointsSkipped;
                LastFloorFollowPointsAdjustedByAverage = pointsAdjustedByAverage;

                Log.Information("Applied {Applied} points ({Modified} existing vertices via ModifySubElement, {Added} new via AddPoint), skipped {Skipped} in Floor via SlabShapeEditor",
                    pointsApplied, pointsModified, pointsApplied - pointsModified, pointsSkipped);
                if (pointsAdjustedByAverage > 0)
                    Log.Information("Adjusted {Count} skipped points by averaging elevation of 2 nearest neighbors", pointsAdjustedByAverage);
            }
            else
            {
                throw new InvalidOperationException("Could not get SlabShapeEditor from Floor");
            }

            Log.Information("Successfully made Floor follow Toposolid surface");
            return floor;
#endif
        }

        /// <summary>
        /// Extracts sketch profile corner points (curve start/end) – exact boundary junctions including arc corners.
        /// </summary>
        private List<XYZ> ExtractSketchBoundaryCornerPoints(Floor floor)
        {
            var list = new List<XYZ>();
            var seen = new HashSet<string>();
            try
            {
                Sketch sketch = floor.Document.GetElement(floor.SketchId) as Sketch;
                if (sketch?.Profile == null) return list;
                for (int i = 0; i < sketch.Profile.Size; i++)
                {
                    CurveArray curves = sketch.Profile.get_Item(i);
                    if (curves == null) continue;
                    foreach (Curve curve in curves)
                    {
                        if (curve == null) continue;
                        AddCorner(curve.GetEndPoint(0));
                        AddCorner(curve.GetEndPoint(1));
                    }
                }
                if (list.Count > 0)
                    Log.Information("Extracted {Count} sketch boundary corner points", list.Count);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not extract sketch corner points: {Error}", ex.Message);
            }
            return list;

            void AddCorner(XYZ pt)
            {
                if (pt == null) return;
                try
                {
                    string key = GetXYKey(pt, 0.001);
                    if (seen.Add(key))
                        list.Add(pt);
                }
                catch { }
            }
        }

        /// <summary>
        /// Samples points along the floor boundary (outline) so the entire boundary line gets projected onto topo.
        /// By distance: steps = ceil(length / spacing). By count: fixed segments per curve.
        /// </summary>
        private List<XYZ> ExtractBoundarySampledPoints(Floor floor, FloorBoundarySamplingOptions options)
        {
            var list = new List<XYZ>();
            if (options == null) return list;
            try
            {
                Sketch sketch = floor.Document.GetElement(floor.SketchId) as Sketch;
                if (sketch != null && sketch.Profile != null)
                {
                    for (int i = 0; i < sketch.Profile.Size; i++)
                    {
                        CurveArray curves = sketch.Profile.get_Item(i);
                        if (curves == null) continue;
                        foreach (Curve curve in curves)
                        {
                            if (curve == null) continue;
                            int steps = GetBoundarySampleSteps(curve, options);
                            for (int k = 0; k <= steps; k++)
                            {
                                double t = steps > 0 ? k / (double)steps : 0;
                                try { list.Add(curve.Evaluate(t, true)); } catch { }
                            }
                        }
                    }
                }
                if (list.Count > 0)
                {
                    if (options.Mode == BoundarySampleMode.ByDistance)
                        Log.Information("Sampled {Count} points along floor boundary (by distance, spacing ~{Spacing:F4} ft)",
                            list.Count, options.SpacingFeet);
                    else
                        Log.Information("Sampled {Count} points along floor boundary (by count, {Segments} segments per curve)",
                            list.Count, options.SegmentsPerCurve);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not sample floor boundary: {Error}", ex.Message);
            }
            return list;
        }

        private static int GetBoundarySampleSteps(Curve curve, FloorBoundarySamplingOptions options)
        {
            if (options.Mode == BoundarySampleMode.BySegmentCount)
                return Math.Max(1, options.SegmentsPerCurve);

            double len = curve.ApproximateLength;
            return Math.Max(1, (int)Math.Ceiling(len / options.SpacingFeet));
        }

        /// <summary>
        /// Extracts ONLY SlabShapeEditor vertices from a Floor (every vertex Revit has for the slab, including all boundary).
        /// Use this to update elevation so no vertex is missed.
        /// </summary>
        private List<XYZ> ExtractSlabShapeVerticesFromFloor(Floor floor, SlabShapeEditor editor = null)
        {
            var list = new List<XYZ>();
            try
            {
                editor = editor ?? floor.GetSlabShapeEditor();
                if (editor != null)
                {
                    try { editor.Enable(); } catch { }
                    if (editor.SlabShapeVertices != null)
                    {
                        foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
                        {
                            if (vertex?.Position != null)
                                list.Add(vertex.Position);
                        }
                    }
                }
                Log.Information("Extracted {Count} SlabShape vertices from Floor {Id} (no filter – all vertices including boundary)", list.Count, GetElementIdValue(floor.Id));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error extracting SlabShape vertices from Floor {Id}", GetElementIdValue(floor.Id));
            }
            return list;
        }

        /// <summary>
        /// Finds the SlabShape vertex nearest to (x, y) within tolerance.
        /// </summary>
        private static SlabShapeVertex FindSlabShapeVertexNearXY(SlabShapeEditor editor, double x, double y, double tolerance)
        {
            return FindSlabShapeVertexAtXY(CollectSlabShapeVertices(editor), x, y, tolerance, tolerance);
        }

        private static List<SlabShapeVertex> CollectSlabShapeVertices(SlabShapeEditor editor)
        {
            var list = new List<SlabShapeVertex>();
            if (editor?.SlabShapeVertices == null) return list;
            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
            {
                if (v != null) list.Add(v);
            }
            return list;
        }

        /// <summary>
        /// Finds a SlabShape vertex at (x,y) within exactTolerance, or the nearest within maxNearestDist.
        /// </summary>
        private static SlabShapeVertex FindSlabShapeVertexAtXY(
            IList<SlabShapeVertex> vertices, double x, double y, double exactTolerance, double maxNearestDist = -1)
        {
            if (vertices == null || vertices.Count == 0) return null;

            double exactTolSq = exactTolerance * exactTolerance;
            foreach (SlabShapeVertex v in vertices)
            {
                if (v?.Position == null) continue;
                double dx = v.Position.X - x, dy = v.Position.Y - y;
                if (dx * dx + dy * dy <= exactTolSq)
                    return v;
            }

            if (maxNearestDist <= 0) return null;

            double maxDistSq = maxNearestDist * maxNearestDist;
            SlabShapeVertex best = null;
            double bestDistSq = maxDistSq;
            foreach (SlabShapeVertex v in vertices)
            {
                if (v?.Position == null) continue;
                double dx = v.Position.X - x, dy = v.Position.Y - y;
                double dSq = dx * dx + dy * dy;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = v;
                }
            }
            return best;
        }

        private static double GetNearestSlabShapeVertexDistance(IList<SlabShapeVertex> vertices, double x, double y)
        {
            if (vertices == null || vertices.Count == 0) return double.NaN;
            double bestDistSq = double.MaxValue;
            foreach (SlabShapeVertex v in vertices)
            {
                if (v?.Position == null) continue;
                double dx = v.Position.X - x, dy = v.Position.Y - y;
                bestDistSq = Math.Min(bestDistSq, dx * dx + dy * dy);
            }
            return Math.Sqrt(bestDistSq);
        }

        private static double GetFloorHeightOffsetFromLevel(Floor floor)
        {
            if (floor == null) return 0;
            try
            {
                Parameter p = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                return p?.AsDouble() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Clears prior slab shape edits, regenerates, and re-enables editing so the next run starts from a flat default shape.
        /// ResetSlabShape disables editing; Regenerate + Enable are required before reading vertices or applying new points.
        /// </summary>
        private SlabShapeEditor ResetAndPrepareFloorSlabShape(Document doc, Floor floor, out bool resetSucceeded)
        {
            resetSucceeded = false;
            if (doc == null || floor == null) return null;

            SlabShapeEditor editor = floor.GetSlabShapeEditor();
            if (editor == null) return null;

            try
            {
                editor.ResetSlabShape();
                doc.Regenerate();

                editor = floor.GetSlabShapeEditor();
                if (editor == null)
                {
                    Log.Warning("SlabShapeEditor unavailable after reset on Floor {Id}", GetElementIdValue(floor.Id));
                    return null;
                }

                editor.Enable();
                doc.Regenerate();

                int vertexCount = editor.SlabShapeVertices?.Size ?? 0;
                Log.Information(
                    "Reset Floor {Id} slab shape to default before follow topo ({VertexCount} SlabShape vertices after regenerate)",
                    GetElementIdValue(floor.Id), vertexCount);
                resetSucceeded = true;
                return editor;
            }
            catch (Exception ex)
            {
                Log.Warning("Could not reset Floor {Id} slab shape: {Error}", GetElementIdValue(floor.Id), ex.Message);
                return floor.GetSlabShapeEditor();
            }
        }

        private static void EnsureSlabShapeEditingEnabled(SlabShapeEditor editor)
        {
            if (editor == null) return;
            try
            {
                if (!editor.IsEnabled)
                    editor.Enable();
            }
            catch
            {
                try { editor.Enable(); } catch { }
            }
        }

        /// <summary>
        /// Converts topo survey elevation to SlabShapeEditor offset (level and height offset unchanged).
        /// </summary>
        private static double TopoSurveyElevationToSlabOffset(
            SurveyCoordinateHelper surveyCoords, double x, double y, double topoSurveyElevation,
            double levelElevation, double heightOffsetFromLevel)
        {
            if (surveyCoords != null && surveyCoords.IsAvailable)
                return surveyCoords.SurveyElevationToFloorSlabOffset(
                    x, y, topoSurveyElevation, levelElevation, heightOffsetFromLevel);
            return topoSurveyElevation - levelElevation - heightOffsetFromLevel;
        }

        /// <summary>
        /// Adds or updates a slab point at (x,y) to topo survey elevation (Elevation Base = Survey Point).
        /// </summary>
        private static bool ApplyTopoElevationAtXY(
            SlabShapeEditor editor, double x, double y, double topoSurveyElevation,
            SurveyCoordinateHelper surveyCoords,
            double levelElevation, double heightOffsetFromLevel, double matchTolerance = 0.05)
        {
            if (editor == null) return false;

            SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, x, y, matchTolerance);
            if (vertex != null)
                return TryModifySlabVertexElevation(
                    editor, vertex, topoSurveyElevation, surveyCoords, x, y,
                    levelElevation, heightOffsetFromLevel);

            double slabOffset = TopoSurveyElevationToSlabOffset(
                surveyCoords, x, y, topoSurveyElevation, levelElevation, heightOffsetFromLevel);
            if (!SlabShapeEditorHelper.TryAddPoint(editor, new XYZ(x, y, slabOffset)))
                return false;

            vertex = FindSlabShapeVertexNearXY(editor, x, y, matchTolerance);
            if (vertex == null)
            {
                Log.Debug("AddPoint succeeded but no vertex found near ({X}, {Y})", x, y);
                return false;
            }

            return TryModifySlabVertexElevation(
                editor, vertex, topoSurveyElevation, surveyCoords, x, y,
                levelElevation, heightOffsetFromLevel);
        }

        private static bool TryModifySlabVertexElevation(
            SlabShapeEditor editor, SlabShapeVertex vertex, double topoSurveyElevation,
            SurveyCoordinateHelper surveyCoords, double x, double y,
            double levelElevation, double heightOffsetFromLevel)
        {
            if (editor == null || vertex == null) return false;
            try
            {
                double slabOffset = TopoSurveyElevationToSlabOffset(
                    surveyCoords, x, y, topoSurveyElevation, levelElevation, heightOffsetFromLevel);
                editor.ModifySubElement(vertex, slabOffset);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("ModifySubElement failed: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// DrawPoint updates an existing slab boundary point; AddPoint fails at corners with "Could not add point".
        /// <paramref name="slabOffsetZ"/> is elevation offset from the slab reference plane (not absolute model Z).
        /// </summary>
        private static bool TryDrawPointOnSlab(SlabShapeEditor editor, double x, double y, double slabOffsetZ)
        {
            if (editor == null) return false;
            try
            {
                var drawPoint = editor.GetType().GetMethod("DrawPoint", new[] { typeof(XYZ) });
                if (drawPoint == null) return false;
                drawPoint.Invoke(editor, new object[] { new XYZ(x, y, slabOffsetZ) });
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("DrawPoint failed at ({X}, {Y}) offset={Z}: {Error}", x, y, slabOffsetZ, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Applies topo elevation to sketch corner junctions after bulk boundary points are added.
        /// </summary>
        private int ApplySketchCornerElevations(
            Document doc, Floor floor, SlabShapeEditor editor,
            IList<(XYZ Corner, double TopoSurveyZ)> cornerUpdates,
            SurveyCoordinateHelper surveyCoords,
            double levelElevation, double heightOffsetFromLevel)
        {
            if (cornerUpdates == null || cornerUpdates.Count == 0) return 0;

            doc.Regenerate();
            editor = floor.GetSlabShapeEditor() ?? editor;
            if (editor == null) return 0;
            try { editor.Enable(); } catch { }

            var vertices = CollectSlabShapeVertices(editor);
            Log.Information("Corner pass: {Count} SlabShape vertices available after regenerate", vertices.Count);

            const double exactCornerTol = 0.02; // ~6 mm
            const double nearCornerTol = 0.25;  // ~3 in
            const double edgePinOffset = 0.05;  // ~0.6 in inward along edges
            int modified = 0;
            int failed = 0;

            foreach (var (corner, topoSurveyZ) in cornerUpdates)
            {
                if (corner == null) continue;
                double cx = corner.X, cy = corner.Y;
                bool done = false;

                SlabShapeVertex vertex = FindSlabShapeVertexAtXY(vertices, cx, cy, exactCornerTol);
                if (vertex != null)
                    done = TryModifySlabVertexElevation(
                        editor, vertex, topoSurveyZ, surveyCoords, cx, cy,
                        levelElevation, heightOffsetFromLevel);

                if (!done)
                {
                    double slabOffset = TopoSurveyElevationToSlabOffset(
                        surveyCoords, cx, cy, topoSurveyZ, levelElevation, heightOffsetFromLevel);
                    if (TryDrawPointOnSlab(editor, cx, cy, slabOffset))
                    {
                        vertices = CollectSlabShapeVertices(editor);
                        vertex = FindSlabShapeVertexAtXY(vertices, cx, cy, exactCornerTol, nearCornerTol);
                        if (vertex != null)
                            done = TryModifySlabVertexElevation(
                                editor, vertex, topoSurveyZ, surveyCoords, cx, cy,
                                levelElevation, heightOffsetFromLevel);
                    }
                }

                if (!done)
                {
                    if (ApplyTopoElevationAtXY(
                        editor, cx, cy, topoSurveyZ, surveyCoords,
                        levelElevation, heightOffsetFromLevel, exactCornerTol))
                        done = true;
                }

                if (!done)
                {
                    vertices = CollectSlabShapeVertices(editor);
                    vertex = FindSlabShapeVertexAtXY(vertices, cx, cy, exactCornerTol, nearCornerTol);
                    if (vertex != null)
                        done = TryModifySlabVertexElevation(
                            editor, vertex, topoSurveyZ, surveyCoords, cx, cy,
                            levelElevation, heightOffsetFromLevel);
                }

                if (!done)
                    done = TryPinCornerViaEdgeOffsets(
                        floor, editor, corner, topoSurveyZ, surveyCoords, edgePinOffset,
                        levelElevation, heightOffsetFromLevel);

                if (!done)
                {
                    vertices = CollectSlabShapeVertices(editor);
                    vertex = FindSlabShapeVertexAtXY(vertices, cx, cy, exactCornerTol, nearCornerTol);
                    if (vertex != null)
                        done = TryModifySlabVertexElevation(
                            editor, vertex, topoSurveyZ, surveyCoords, cx, cy,
                            levelElevation, heightOffsetFromLevel);
                }

                if (done)
                {
                    modified++;
                }
                else
                {
                    failed++;
                    double nearestFt = GetNearestSlabShapeVertexDistance(vertices, cx, cy);
                    Log.Warning(
                        "Corner pass failed at ({X}, {Y}) surveyZ={Z:F3}; nearest SlabShape vertex {Nearest:F3} ft away ({Total} vertices)",
                        cx, cy, topoSurveyZ, nearestFt, vertices.Count);
                }
            }

            Log.Information("Corner pass: modified {Modified} sketch corners, failed {Failed}", modified, failed);
            return modified;
        }

        /// <summary>
        /// Adds points slightly inward on edges meeting at a corner, then retries corner ModifySubElement.
        /// </summary>
        private bool TryPinCornerViaEdgeOffsets(
            Floor floor, SlabShapeEditor editor, XYZ corner, double topoSurveyZ,
            SurveyCoordinateHelper surveyCoords, double offsetFeet,
            double levelElevation, double heightOffsetFromLevel)
        {
            if (editor == null || corner == null || offsetFeet <= 0) return false;

            var dirs = GetCornerEdgeOutwardDirections(floor, corner);
            if (dirs.Count == 0) return false;

            bool anyAdded = false;
            foreach (XYZ dir in dirs)
            {
                if (dir == null || dir.GetLength() < 1e-9) continue;
                XYZ pt = corner + dir.Multiply(offsetFeet);
                if (ApplyTopoElevationAtXY(
                    editor, pt.X, pt.Y, topoSurveyZ, surveyCoords,
                    levelElevation, heightOffsetFromLevel))
                    anyAdded = true;
            }
            return anyAdded;
        }

        /// <summary>
        /// Unit XY directions from a sketch corner along each connected boundary curve (away from corner).
        /// </summary>
        private List<XYZ> GetCornerEdgeOutwardDirections(Floor floor, XYZ corner, double xyTolerance = 0.02)
        {
            var dirs = new List<XYZ>();
            if (floor == null || corner == null) return dirs;

            double tolSq = xyTolerance * xyTolerance;
            try
            {
                Sketch sketch = floor.Document.GetElement(floor.SketchId) as Sketch;
                if (sketch?.Profile == null) return dirs;

                for (int i = 0; i < sketch.Profile.Size; i++)
                {
                    CurveArray curves = sketch.Profile.get_Item(i);
                    if (curves == null) continue;
                    foreach (Curve curve in curves)
                    {
                        if (curve == null) continue;
                        XYZ p0 = curve.GetEndPoint(0);
                        XYZ p1 = curve.GetEndPoint(1);

                        if (XYDistanceSq(p0, corner) <= tolSq)
                            AddDirection(curve, 0, p0, p1);
                        if (XYDistanceSq(p1, corner) <= tolSq)
                            AddDirection(curve, 1, p1, p0);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not get corner edge directions: {Error}", ex.Message);
            }
            return dirs;

            void AddDirection(Curve curve, double param, XYZ atCorner, XYZ otherEnd)
            {
                XYZ dir = null;
                try
                {
                    Transform d = curve.ComputeDerivatives(param, true);
                    dir = new XYZ(d.BasisX.X, d.BasisX.Y, 0);
                    if (dir.GetLength() < 1e-9)
                        dir = null;
                    else
                    {
                        dir = dir.Normalize();
                        XYZ chord = new XYZ(otherEnd.X - atCorner.X, otherEnd.Y - atCorner.Y, 0);
                        if (chord.GetLength() > 1e-9 && dir.DotProduct(chord.Normalize()) < 0)
                            dir = dir.Negate();
                    }
                }
                catch
                {
                    dir = new XYZ(otherEnd.X - atCorner.X, otherEnd.Y - atCorner.Y, 0);
                    if (dir.GetLength() > 1e-9) dir = dir.Normalize();
                    else dir = null;
                }

                if (dir == null) return;
                foreach (XYZ existing in dirs)
                {
                    if (existing != null && existing.DotProduct(dir) > 0.999)
                        return;
                }
                dirs.Add(dir);
            }

            static double XYDistanceSq(XYZ a, XYZ b)
            {
                double dx = a.X - b.X, dy = a.Y - b.Y;
                return dx * dx + dy * dy;
            }
        }

        /// <summary>
        /// Extracts points from a Floor using SlabShapeEditor and geometry
        /// </summary>
        private List<XYZ> ExtractPointsFromFloor(Floor floor)
        {
            var points = new HashSet<XYZ>(new XYZComparer());
            
            try
            {
                // First, try to get points from SlabShapeEditor
                SlabShapeEditor editor = floor.GetSlabShapeEditor();
                if (editor != null)
                {
                    SlabShapeVertexArray vertices = editor.SlabShapeVertices;
                    if (vertices != null)
                    {
                        foreach (SlabShapeVertex vertex in vertices)
                        {
                            points.Add(vertex.Position);
                        }
                    }
                }

                // Also extract points from geometry (boundary curves)
                Options options = new Options();
                options.ComputeReferences = false;
                options.DetailLevel = ViewDetailLevel.Fine;
                
                GeometryElement geom = floor.get_Geometry(options);
                if (geom != null)
                {
                    ExtractPointsFromGeometry(geom, points);
                }

                // Extract boundary points
                try
                {
                    IList<CurveLoop> loops = floor.GetGeometryObjectFromReference(new Reference(floor)) as IList<CurveLoop>;
                    if (loops == null)
                    {
                        // Try alternate method to get boundary
                        Sketch sketch = floor.Document.GetElement(floor.SketchId) as Sketch;
                        if (sketch != null)
                        {
                            CurveArray curves = sketch.Profile.get_Item(0);
                            foreach (Curve curve in curves)
                            {
                                points.Add(curve.GetEndPoint(0));
                                points.Add(curve.GetEndPoint(1));
                                
                                // Sample points along curve
                                for (double t = 0; t <= 1; t += 0.1)
                                {
                                    points.Add(curve.Evaluate(t, true));
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (CurveLoop loop in loops)
                        {
                            foreach (Curve curve in loop)
                            {
                                points.Add(curve.GetEndPoint(0));
                                points.Add(curve.GetEndPoint(1));
                                
                                // Sample points along curve
                                for (double t = 0; t <= 1; t += 0.1)
                                {
                                    points.Add(curve.Evaluate(t, true));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Could not extract boundary points from Floor: {Error}", ex.Message);
                }

                Log.Information("Extracted {Count} unique points from Floor {Id}", points.Count, GetElementIdValue(floor.Id));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error extracting points from Floor {Id}", GetElementIdValue(floor.Id));
            }

            return points.ToList();
        }

        /// <summary>
        /// Creates a Toposolid from a list of points
        /// Approach: Similar to Revit's excavate - use existing Toposolid as base or create from points directly
        /// </summary>
        private 
#if REVIT2024_OR_GREATER
            Toposolid
#else
            Element
#endif
            CreateToposolidFromPoints(Document doc, List<XYZ> points)
        {
            if (points == null || points.Count < 3)
            {
                throw new ArgumentException("At least 3 points are required to create a Toposolid");
            }

#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            // Wrap everything in a single transaction to avoid transaction conflicts
            using (Transaction tx = new Transaction(doc, "Create Toposolid from Points"))
            {
                tx.Start();
                
                try
                {
                // Find ToposolidType in the document
                ToposolidType toposolidType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ToposolidType))
                    .Cast<ToposolidType>()
                    .FirstOrDefault();

                if (toposolidType == null)
                {
                    throw new InvalidOperationException("No ToposolidType found in document");
                }

                // Calculate bounding box to determine placement
                double minX = points.Min(p => p.X);
                double maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y);
                double maxY = points.Max(p => p.Y);
                double minZ = points.Min(p => p.Z);
                double maxZ = points.Max(p => p.Z);
                double zRange = maxZ - minZ;
                
                // Check if Toposolid will be too thin
                // Toposolid types have minimum thickness requirements (e.g., 16 inches = 1.33 feet)
                // Ensure we have sufficient Z variation
                const double minThickness = 0.5; // Minimum 0.5 feet (6 inches) to be safe
                if (zRange < minThickness)
                {
                    Log.Warning("Z range ({Range} ft) is too small. Adjusting points to meet minimum thickness requirement.", zRange);
                    // Add slight variation to ensure minimum thickness
                    double adjustment = (minThickness - zRange) / 2.0;
                    var adjustedPoints = new List<XYZ>();
                    foreach (var point in points)
                    {
                        // Add slight variation to Z values to ensure minimum thickness
                        double newZ = point.Z;
                        if (Math.Abs(point.Z - minZ) < 0.001)
                        {
                            newZ = minZ - adjustment;
                        }
                        else if (Math.Abs(point.Z - maxZ) < 0.001)
                        {
                            newZ = maxZ + adjustment;
                        }
                        adjustedPoints.Add(new XYZ(point.X, point.Y, newZ));
                    }
                    points = adjustedPoints;
                    minZ = points.Min(p => p.Z);
                    maxZ = points.Max(p => p.Z);
                    zRange = maxZ - minZ;
                    Log.Information("Adjusted Z range to {Range} ft", zRange);
                }

                // Create a base profile from the bounding box
                // We'll create a rectangular profile that encompasses all points
                double width = maxX - minX;
                double length = maxY - minY;
                
                if (width < 0.1 || length < 0.1)
                {
                    // If the bounding box is too small, use a default size
                    width = Math.Max(width, 10.0);
                    length = Math.Max(length, 10.0);
                }

                // Get active view's level for Toposolid creation
                Level level = doc.ActiveView.GenLevel;
                if (level == null)
                {
                    // Try to get first level
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }
                
                if (level == null)
                {
                    throw new InvalidOperationException("No Level found in document. Toposolid requires a Level.");
                }

                Toposolid toposolid = null;

#if REVIT2025_OR_GREATER
                // Revit 2025: Create Toposolid using CurveLoop
                // Approach: Create a simple rectangular Toposolid first, then modify with points (similar to excavate)
                
                // Create profile curves for the base rectangle
                var profile = new List<Curve>();
                XYZ basePoint = new XYZ(minX, minY, minZ);
                
                profile.Add(Line.CreateBound(basePoint, new XYZ(maxX, minY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(maxX, minY, minZ), new XYZ(maxX, maxY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(maxX, maxY, minZ), new XYZ(minX, maxY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(minX, maxY, minZ), basePoint));

                // Create CurveLoop from profile - ensure it's closed
                var curveLoop = new CurveLoop();
                foreach (var curve in profile)
                {
                    curveLoop.Append(curve);
                }
                
                // Verify CurveLoop is valid
                if (!curveLoop.IsOpen())
                {
                    var curveLoops = new List<CurveLoop> { curveLoop };
                    
                    // Log all available Create methods for debugging
                    var allCreateMethods = typeof(Toposolid).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Create")
                        .ToList();
                    
                    Log.Debug("Found {Count} Toposolid.Create methods in Revit 2025", allCreateMethods.Count);
                    foreach (var m in allCreateMethods)
                    {
                        Log.Debug("  - Create({Params})", string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")));
                    }
                    
                    // Try all possible Create overloads for Revit 2025
                    bool created = false;
                    Exception lastException = null;
                    
                    // Prepare points list for methods that might need it
                    var pointsList = points.ToList();
                    
                    foreach (var method in allCreateMethods)
                    {
                        var parameters = method.GetParameters();
                        try
                        {
                            // Try: Create(Document, IList<CurveLoop>, IList<XYZ>, ElementId, ElementId) - with points!
                            // This is likely the correct method for Revit 2025 - create with boundary and points at once
                            if (parameters.Length == 5 && 
                                parameters[0].ParameterType == typeof(Document) &&
                                parameters[1].ParameterType.IsGenericType &&
                                parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(IList<>) &&
                                parameters[2].ParameterType.IsGenericType &&
                                parameters[2].ParameterType.GetGenericTypeDefinition() == typeof(IList<>) &&
                                parameters[3].ParameterType == typeof(ElementId) &&
                                parameters[4].ParameterType == typeof(ElementId))
                            {
                                var firstListType = parameters[1].ParameterType.GetGenericArguments()[0];
                                var secondListType = parameters[2].ParameterType.GetGenericArguments()[0];
                                
                                // Check if first is CurveLoop and second is XYZ
                                if (firstListType == typeof(CurveLoop) && secondListType == typeof(XYZ))
                                {
                                    // Try different parameter orders for the two ElementIds
                                    // Method signature might be: (Document, IList<CurveLoop>, IList<XYZ>, ElementId typeId, ElementId levelId)
                                    // or: (Document, IList<CurveLoop>, IList<XYZ>, ElementId levelId, ElementId typeId)
                                    
                                    // Try: typeId, levelId
                                    try
                                    {
                                        Log.Debug("Trying Create(Document, IList<CurveLoop>, IList<XYZ>, ElementId typeId, ElementId levelId)");
                                        toposolid = (Toposolid)method.Invoke(null, new object[] { doc, curveLoops, pointsList, toposolidType.Id, level.Id });
                                        if (toposolid != null)
                                        {
                                            Log.Information("Successfully created Toposolid using Create(Document, IList<CurveLoop>, IList<XYZ>, ElementId typeId, ElementId levelId)");
                                            created = true;
                                            break;
                                        }
                                    }
                                    catch (Exception ex1)
                                    {
                                        Log.Debug("Failed with typeId, levelId order: {Error}", ex1.Message);
                                        
                                        // Try: levelId, typeId
                                        try
                                        {
                                            Log.Debug("Trying Create(Document, IList<CurveLoop>, IList<XYZ>, ElementId levelId, ElementId typeId)");
                                            toposolid = (Toposolid)method.Invoke(null, new object[] { doc, curveLoops, pointsList, level.Id, toposolidType.Id });
                                            if (toposolid != null)
                                            {
                                                Log.Information("Successfully created Toposolid using Create(Document, IList<CurveLoop>, IList<XYZ>, ElementId levelId, ElementId typeId)");
                                                created = true;
                                                break;
                                            }
                                        }
                                        catch (Exception ex2)
                                        {
                                            Log.Debug("Failed with levelId, typeId order: {Error}", ex2.Message);
                                        }
                                    }
                                }
                            }
                            // Try: Create(Document, IList<CurveLoop>, ElementId levelId, ElementId typeId)
                            else if (parameters.Length == 4 && 
                                parameters[0].ParameterType == typeof(Document) &&
                                parameters[1].ParameterType == typeof(IList<CurveLoop>) &&
                                parameters[2].ParameterType == typeof(ElementId) &&
                                parameters[3].ParameterType == typeof(ElementId))
                            {
                                Log.Debug("Trying Create(Document, IList<CurveLoop>, ElementId levelId, ElementId typeId)");
                                toposolid = (Toposolid)method.Invoke(null, new object[] { doc, curveLoops, level.Id, toposolidType.Id });
                                if (toposolid != null)
                                {
                                    Log.Information("Successfully created Toposolid using Create(Document, IList<CurveLoop>, ElementId, ElementId)");
                                    created = true;
                                    break;
                                }
                            }
                            // Try: Create(Document, ElementId typeId, IList<CurveLoop>)
                            else if (parameters.Length == 3 &&
                                     parameters[0].ParameterType == typeof(Document) &&
                                     parameters[1].ParameterType == typeof(ElementId) &&
                                     parameters[2].ParameterType == typeof(IList<CurveLoop>))
                            {
                                Log.Debug("Trying Create(Document, ElementId typeId, IList<CurveLoop>)");
                                toposolid = (Toposolid)method.Invoke(null, new object[] { doc, toposolidType.Id, curveLoops });
                                if (toposolid != null)
                                {
                                    Log.Information("Successfully created Toposolid using Create(Document, ElementId, IList<CurveLoop>)");
                                    created = true;
                                    break;
                                }
                            }
                            // Try: Create(Document, IList<CurveLoop>, ElementId typeId)
                            else if (parameters.Length == 3 &&
                                     parameters[0].ParameterType == typeof(Document) &&
                                     parameters[1].ParameterType == typeof(IList<CurveLoop>) &&
                                     parameters[2].ParameterType == typeof(ElementId))
                            {
                                Log.Debug("Trying Create(Document, IList<CurveLoop>, ElementId typeId)");
                                toposolid = (Toposolid)method.Invoke(null, new object[] { doc, curveLoops, toposolidType.Id });
                                if (toposolid != null)
                                {
                                    Log.Information("Successfully created Toposolid using Create(Document, IList<CurveLoop>, ElementId)");
                                    created = true;
                                    break;
                                }
                            }
                            // Try: Create(Document, IList<CurveLoop>)
                            else if (parameters.Length == 2 &&
                                     parameters[0].ParameterType == typeof(Document) &&
                                     parameters[1].ParameterType == typeof(IList<CurveLoop>))
                            {
                                Log.Debug("Trying Create(Document, IList<CurveLoop>)");
                                toposolid = (Toposolid)method.Invoke(null, new object[] { doc, curveLoops });
                                if (toposolid != null)
                                {
                                    toposolid.ChangeTypeId(toposolidType.Id);
                                    Log.Information("Successfully created Toposolid using Create(Document, IList<CurveLoop>)");
                                    created = true;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            Log.Debug("Failed to create Toposolid with method {Method} ({Params}): {Error}", 
                                method.Name, 
                                string.Join(", ", parameters.Select(p => p.ParameterType.Name)), 
                                ex.Message);
                            toposolid = null;
                            continue;
                        }
                    }
                    
                    if (!created || toposolid == null)
                    {
                        string errorDetails = $"Tried {allCreateMethods.Count} Create methods. ";
                        if (lastException != null)
                        {
                            errorDetails += $"Last error: {lastException.Message}";
                        }
                        errorDetails += " Available methods: " + string.Join("; ", allCreateMethods.Select(m => 
                            $"Create({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})"));
                        
                        throw new InvalidOperationException($"Unable to create Toposolid in Revit 2025. {errorDetails}");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Failed to create closed CurveLoop for Toposolid");
                }
#else
                // Revit 2024: Create from IList<CurveLoop>
                var profile = new List<Curve>();
                XYZ basePoint = new XYZ(minX, minY, minZ);
                
                profile.Add(Line.CreateBound(basePoint, new XYZ(maxX, minY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(maxX, minY, minZ), new XYZ(maxX, maxY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(maxX, maxY, minZ), new XYZ(minX, maxY, minZ)));
                profile.Add(Line.CreateBound(new XYZ(minX, maxY, minZ), basePoint));
                
                var curveLoop = CurveLoop.Create(profile);
                var curveLoops = new List<CurveLoop> { curveLoop };
                toposolid = Toposolid.Create(doc, curveLoops, toposolidType.Id, level.Id);
#endif

                if (toposolid == null)
                {
                    throw new InvalidOperationException("Failed to create Toposolid from profile");
                }

                // Check if Toposolid was created with points already (Revit 2025 method with IList<XYZ>)
                // If created with points, we might not need to modify it further
#if REVIT2025_OR_GREATER
                // Check if we used the Create method with IList<XYZ> - if so, points are already included
                // We'll track this in the creation loop above
#endif

                // Modify the shape using SlabShapeEditor to ensure all points are included
                // Note: We're already inside a transaction, so don't create a new one
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
                if (editor != null)
                {
                    // Enable shape editing
                    editor.Enable();
                    
                    // Add points from our merged dataset
                    // Filter points to those within the Toposolid boundary
                    var pointsToAdd = points.Where(p => IsPointInBounds(p, minX, maxX, minY, maxY)).ToList();
                    
                    // Limit the number of points to avoid performance issues
                    // Revit may have limits on the number of vertices
                    const int maxPoints = 1000;
                    if (pointsToAdd.Count > maxPoints)
                    {
                        Log.Warning("Too many points ({Count}), sampling to {MaxPoints}", pointsToAdd.Count, maxPoints);
                        // Sample points evenly
                        int step = pointsToAdd.Count / maxPoints;
                        pointsToAdd = pointsToAdd.Where((p, i) => i % step == 0).Take(maxPoints).ToList();
                    }

                    int pointsAdded = 0;
                    foreach (var point in pointsToAdd)
                    {
                        try
                        {
#if REVIT2025_OR_GREATER
                            // Use AddPoint instead of DrawPoint in Revit 2025+
                            editor.AddPoint(point);
                            pointsAdded++;
#else
                            editor.DrawPoint(point);
                            pointsAdded++;
#endif
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Could not add point at ({X}, {Y}, {Z}): {Error}", point.X, point.Y, point.Z, ex.Message);
                        }
                    }
                    
                    Log.Information("Added {Count} points to Toposolid via SlabShapeEditor", pointsAdded);
                }

                    // Commit the transaction
                    tx.Commit();
                    
                    Log.Information("Created Toposolid with {Count} points", points.Count);
                    return toposolid;
                }
                catch (Exception ex)
                {
                    // Rollback transaction on error
                    if (tx.HasStarted() && !tx.HasEnded())
                    {
                        tx.RollBack();
                    }
                    Log.Error(ex, "Error creating Toposolid from points");
                    throw;
                }
            }
#endif
        }

        /// <summary>
        /// Checks if a point is within the specified bounds
        /// </summary>
        private bool IsPointInBounds(XYZ point, double minX, double maxX, double minY, double maxY)
        {
            return point.X >= minX - 0.1 && point.X <= maxX + 0.1 &&
                   point.Y >= minY - 0.1 && point.Y <= maxY + 0.1;
        }

        /// <summary>
        /// Creates a key for XY coordinates with tolerance
        /// </summary>
        private string GetXYKey(XYZ point, double tolerance)
        {
            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            // Check for invalid values
            if (double.IsNaN(point.X) || double.IsInfinity(point.X) || 
                double.IsNaN(point.Y) || double.IsInfinity(point.Y))
            {
                throw new ArgumentException($"Invalid point coordinates: X={point.X}, Y={point.Y}");
            }

            // Round to tolerance to group nearby points
            double roundedX = Math.Round(point.X / tolerance) * tolerance;
            double roundedY = Math.Round(point.Y / tolerance) * tolerance;

            // Use InvariantCulture to ensure consistent formatting
            return $"{roundedX.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{roundedY.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private int DeleteExistingPointsInBand(
            SlabShapeEditor editor,
            SpatialGrid proposalSpatialGrid,
            List<XYZ> proposalXYSeeds,
            double cleanupOffset)
        {
            if (editor == null || editor.SlabShapeVertices == null || cleanupOffset <= 0.0)
            {
                return 0;
            }

            int deleted = 0;
            try
            {
                var deletePointMethod = editor.GetType().GetMethod("DeletePoint", new[] { typeof(SlabShapeVertex) });
                if (deletePointMethod == null)
                {
                    Log.Warning("SlabShapeEditor.DeletePoint(SlabShapeVertex) is not available in current API.");
                    return 0;
                }

                var vertices = new List<SlabShapeVertex>();
                foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
                {
                    if (vertex != null) vertices.Add(vertex);
                }

                foreach (var vertex in vertices)
                {
                    XYZ p = vertex?.Position;
                    if (p == null) continue;

                    bool isInsideProposal = IsPointOnProposalSurfaceXY(p, proposalSpatialGrid);
                    bool isInsideCleanupOffsetBand = !isInsideProposal &&
                        IsWithinOffsetFromProposalXY(p, proposalXYSeeds, cleanupOffset);

                    if (isInsideCleanupOffsetBand)
                    {
                        try
                        {
                            deletePointMethod.Invoke(editor, new object[] { vertex });
                            deleted++;
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("Could not delete Existing point in cleanup band at ({X}, {Y}, {Z}): {Error}",
                                p.X, p.Y, p.Z, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Boundary cleanup point deletion encountered an error.");
            }

            return deleted;
        }

        private List<XYZ> GetExistingEditableVertices(
#if REVIT2024_OR_GREATER
            Toposolid toposolid
#else
            Element toposolid
#endif
            )
        {
#if !REVIT2024_OR_GREATER
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
#else
            var result = new List<XYZ>();
            try
            {
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
                if (editor?.SlabShapeVertices != null)
                {
                    foreach (SlabShapeVertex v in editor.SlabShapeVertices)
                    {
                        if (v?.Position != null)
                        {
                            result.Add(v.Position);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read Existing editable vertices snapshot.");
            }
            return result;
#endif
        }

        private int DeleteExistingPointsInProposalBoundary(
            SlabShapeEditor editor,
            SpatialGrid proposalSpatialGrid)
        {
            if (editor == null || editor.SlabShapeVertices == null)
            {
                return 0;
            }

            int deleted = 0;
            try
            {
                var deletePointMethod = editor.GetType().GetMethod("DeletePoint", new[] { typeof(SlabShapeVertex) });
                if (deletePointMethod == null)
                {
                    Log.Warning("SlabShapeEditor.DeletePoint(SlabShapeVertex) is not available in current API.");
                    return 0;
                }

                var vertices = new List<SlabShapeVertex>();
                foreach (SlabShapeVertex vertex in editor.SlabShapeVertices)
                {
                    if (vertex != null) vertices.Add(vertex);
                }

                foreach (var vertex in vertices)
                {
                    XYZ p = vertex?.Position;
                    if (p == null) continue;

                    bool isInsideProposal = IsPointOnProposalSurfaceXY(p, proposalSpatialGrid);
                    if (!isInsideProposal)
                    {
                        continue;
                    }

                    try
                    {
                        deletePointMethod.Invoke(editor, new object[] { vertex });
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Could not delete Existing point inside Proposal boundary at ({X}, {Y}, {Z}): {Error}",
                            p.X, p.Y, p.Z, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Inside-boundary Existing point deletion encountered an error.");
            }

            return deleted;
        }

        private bool IsPointOnProposalSurfaceXY(XYZ point, SpatialGrid proposalSpatialGrid)
        {
            if (point == null || proposalSpatialGrid == null) return false;
            // Strict membership: no nearest-triangle fallback.
            return GetElevationAtPointOptimized(point, proposalSpatialGrid, allowFallback: false).HasValue;
        }

        private bool IsWithinOffsetFromProposalXY(XYZ point, List<XYZ> proposalXYSeeds, double offset)
        {
            if (point == null || proposalXYSeeds == null || proposalXYSeeds.Count == 0 || offset <= 0.0)
            {
                return false;
            }

            double maxDistSq = offset * offset;
            foreach (var seed in proposalXYSeeds)
            {
                if (seed == null) continue;
                double dx = point.X - seed.X;
                double dy = point.Y - seed.Y;
                if ((dx * dx + dy * dy) <= maxDistSq)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Parses an XY key back to coordinates
        /// </summary>
        private (double X, double Y) ParseXYKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Invalid XY key format: key is null or empty");
            }

            var parts = key.Split(',');
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid XY key format: expected 2 parts separated by comma, got {parts.Length}. Key: '{key}'");
            }

            try
            {
                string xStr = parts[0].Trim();
                string yStr = parts[1].Trim();

                if (string.IsNullOrEmpty(xStr) || string.IsNullOrEmpty(yStr))
                {
                    throw new ArgumentException($"Invalid XY key format: empty X or Y value. Key: '{key}'");
                }

                if (!double.TryParse(xStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x))
                {
                    throw new ArgumentException($"Invalid XY key format: cannot parse X value '{xStr}'. Key: '{key}'");
                }

                if (!double.TryParse(yStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    throw new ArgumentException($"Invalid XY key format: cannot parse Y value '{yStr}'. Key: '{key}'");
                }

                return (x, y);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid XY key format: '{key}'. Error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets the ElementId value as string (handles version differences)
        /// </summary>
        private string GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value.ToString() ?? "null";
#else
            return id?.IntegerValue.ToString() ?? "null";
#endif
        }
    }
}
