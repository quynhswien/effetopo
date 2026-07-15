using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Sculpting operations for Toposolid via SlabShapeEditor (inflate, smooth, remesh, shape by point).
    /// </summary>
    public class ModifyTopoService
    {
        private static ModifyTopoService _instance;
        private static readonly object _lock = new object();

        public static ModifyTopoService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ModifyTopoService();
                    }
                }
                return _instance;
            }
        }

        private ModifyTopoService() { }

        public ModifyTopoResult LastResult { get; private set; }

        public int CountSlabShapeVertices(
#if REVIT2024_OR_GREATER
            Toposolid toposolid
#else
            Element toposolid
#endif
        )
        {
            try
            {
                SlabShapeEditor editor = toposolid?.GetSlabShapeEditor();
                if (editor?.SlabShapeVertices == null) return 0;
                int count = 0;
                foreach (SlabShapeVertex _ in editor.SlabShapeVertices)
                    count++;
                return count;
            }
            catch
            {
                return 0;
            }
        }

#if REVIT2024_OR_GREATER
        public ModifyTopoResult Apply(
            Document doc,
            Toposolid toposolid,
            ModifyTopoOptions options,
            XYZ centerPoint = null)
        {
            if (doc == null || toposolid == null || options == null)
                throw new ArgumentNullException();

            SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
            if (editor == null)
                throw new InvalidOperationException("Could not access SlabShapeEditor on Toposolid.");

            editor.Enable();

            var vertices = CollectVertices(doc, toposolid, editor);
            int originalCount = vertices.Count;
            if (originalCount == 0)
                throw new InvalidOperationException("Toposolid has no SlabShape vertices to modify.");

            double levelElev = GetLevelElevation(doc, toposolid);
            double heightOffset = GetHeightOffsetFromLevel(toposolid);
            var state = new SculptState(doc, toposolid, vertices, levelElev, heightOffset);

            switch (options.Tool)
            {
                case ModifyTopoTool.InflateSurface:
                    if (centerPoint == null)
                        throw new InvalidOperationException("Pick a center point for Inflate Surface.");
                    InflateSurface(state, centerPoint, options);
                    break;
                case ModifyTopoTool.ShapeByPoint:
                    if (centerPoint == null)
                        throw new InvalidOperationException("Pick a control point for Shape by Point.");
                    ShapeByPoint(doc, toposolid, editor, state, centerPoint, options);
                    break;
                case ModifyTopoTool.SmoothGeometry:
                    SmoothGeometry(state, options);
                    break;
                case ModifyTopoTool.MeshControl:
                    MeshControl(doc, toposolid, editor, state, options);
                    break;
            }

            int modified = WriteVertexZChanges(editor, state);
            int added = state.PointsAdded;
            int removed = state.PointsRemoved;

            doc.Regenerate();
            int afterCount = CountSlabShapeVertices(toposolid);

            var result = new ModifyTopoResult
            {
                OriginalPointCount = originalCount,
                PointsAfterModification = afterCount,
                VerticesModified = modified,
                PointsAdded = added,
                PointsRemoved = removed,
                Summary = BuildSummary(options, modified, added, removed, afterCount)
            };
            LastResult = result;
            Log.Information("ModifyTopo: {Summary}", result.Summary);
            return result;
        }

        public sealed class SimulateShapeByPointResult
        {
            public List<SculptVertexSnapshot> Vertices;
            public int PointsAdded;
            public int VerticesModified;
        }

        /// <summary>In-memory shape stamp for draft preview (no Revit geometry changes).</summary>
        public static SimulateShapeByPointResult SimulateShapeByPoint(
            Document doc,
            Toposolid toposolid,
            IList<SculptVertexSnapshot> vertices,
            XYZ center,
            ModifyTopoOptions options)
        {
            if (vertices == null || center == null || options == null)
                throw new ArgumentNullException();

            var working = vertices
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();
            int countBefore = working.Count;
            const double xyTol = 0.15;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);

            var addPoints = ComputeShapeByPointAddPreviewPoints(doc, toposolid, center, options, working);
            foreach (XYZ pt in addPoints)
            {
                bool exists = working.Any(v => HorizontalDistance(v.X, v.Y, pt.X, pt.Y) < xyTol);
                if (exists) continue;
                working.Add(new SculptVertexSnapshot { X = pt.X, Y = pt.Y, Z = pt.Z });
            }

            int pointsAdded = working.Count - countBefore;

            double centerOriginalZ = GetModelSurfaceZ(doc, toposolid, working, center.X, center.Y, radius)
                ?? center.Z;
            double targetZ = centerOriginalZ + options.ShapeDeltaFeet;
            int verticesModified = 0;
            const double zTolerance = 1e-6;

            foreach (SculptVertexSnapshot v in working)
            {
                double dist = HorizontalDistance(v.X, v.Y, center.X, center.Y);
                if (dist > radius) continue;

                double w = ComputeFalloff(dist / radius, options.ShapeFalloff);
                double newZ = v.Z + (targetZ - centerOriginalZ) * w;
                if (Math.Abs(newZ - v.Z) > zTolerance)
                    verticesModified++;
                v.Z = newZ;
            }

            return new SimulateShapeByPointResult
            {
                Vertices = working,
                PointsAdded = pointsAdded,
                VerticesModified = verticesModified
            };
        }
#endif

        private static string BuildSummary(ModifyTopoOptions options, int modified, int added, int removed, int afterCount)
        {
            string tool = options.Tool switch
            {
                ModifyTopoTool.InflateSurface => "Inflate Surface",
                ModifyTopoTool.MeshControl => "Mesh Control",
                ModifyTopoTool.ShapeByPoint => "Shape by Point",
                ModifyTopoTool.SmoothGeometry => "Smooth Geometry",
                _ => "Modify Topo"
            };
            return $"{tool}: {modified} vertices updated, {added} added, {removed} removed ({afterCount} total points)";
        }

        private sealed class SculptVertex
        {
            public SlabShapeVertex RevitVertex;
            public double X, Y, Z;
            public double OriginalZ;
        }

        private sealed class SculptState
        {
            public Document Doc;
            public Toposolid Toposolid;
            public List<SculptVertex> Vertices;
            public double LevelElevation;
            public double HeightOffset;
            public int PointsAdded;
            public int PointsRemoved;

            public SculptState(
                Document doc,
                Toposolid toposolid,
                List<SculptVertex> vertices,
                double levelElev,
                double heightOffset)
            {
                Doc = doc;
                Toposolid = toposolid;
                Vertices = vertices;
                LevelElevation = levelElev;
                HeightOffset = heightOffset;
            }

            public double ModelZToSlabOffset(double modelZ) =>
                modelZ - LevelElevation - HeightOffset;
        }

        private static List<SculptVertex> CollectVertices(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor)
        {
            var list = new List<SculptVertex>();
            if (editor?.SlabShapeVertices == null) return list;

            var rawSnapshots = new List<SculptVertexSnapshot>();
            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
            {
                if (v?.Position == null) continue;
                XYZ p = v.Position;
                rawSnapshots.Add(new SculptVertexSnapshot { X = p.X, Y = p.Y, Z = p.Z });
            }

            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
            {
                if (v?.Position == null) continue;
                XYZ p = v.Position;
                double modelZ = VertexZToModelZ(doc, toposolid, rawSnapshots, p.Z);
                list.Add(new SculptVertex
                {
                    RevitVertex = v,
                    X = p.X,
                    Y = p.Y,
                    Z = modelZ,
                    OriginalZ = modelZ
                });
            }
            return list;
        }

        private static SlabShapeVertex FindSlabShapeVertexNearXY(
            SlabShapeEditor editor, double x, double y, double tolerance)
        {
            if (editor?.SlabShapeVertices == null) return null;

            SlabShapeVertex best = null;
            double bestDist = tolerance;
            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
            {
                if (v?.Position == null) continue;
                double dist = HorizontalDistance(v.Position.X, v.Position.Y, x, y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = v;
                }
            }
            return best;
        }

        private static double GetLevelElevation(Document doc, Toposolid toposolid)
        {
            Level level = doc.GetElement(toposolid.LevelId) as Level;
            return level?.Elevation ?? 0;
        }

        private static double GetHeightOffsetFromLevel(Toposolid toposolid)
        {
            try
            {
                Parameter p = toposolid.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (p != null && p.HasValue)
                    return p.AsDouble();
            }
            catch { }
            return 0;
        }

        private static int WriteVertexZChanges(SlabShapeEditor editor, SculptState state)
        {
            int modified = 0;
            const double zTolerance = 1e-6;

            foreach (SculptVertex v in state.Vertices)
            {
                if (v.RevitVertex == null) continue;
                if (Math.Abs(v.Z - v.OriginalZ) < zTolerance) continue;

                try
                {
                    double slabOffset = state.ModelZToSlabOffset(v.Z);
                    editor.ModifySubElement(v.RevitVertex, slabOffset);
                    modified++;
                }
                catch (Exception ex)
                {
                    Log.Debug("ModifySubElement failed at ({X},{Y}): {Error}", v.X, v.Y, ex.Message);
                }
            }
            return modified;
        }

        private static void InflateSurface(SculptState state, XYZ center, ModifyTopoOptions options)
        {
            double radius = Math.Max(options.InflateRadiusFeet, 0.1);
            double height = options.InflateHeightFeet;

            foreach (SculptVertex v in state.Vertices)
            {
                double dist = HorizontalDistance(v.X, v.Y, center.X, center.Y);
                if (dist > radius) continue;

                double w = ComputeFalloff(dist / radius, options.InflateFalloff);
                v.Z += height * w;
            }
        }

        private static void ShapeByPoint(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor,
            SculptState state,
            XYZ center,
            ModifyTopoOptions options)
        {
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);
            var snapshots = state.Vertices
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();

            AddShapeByPointGridPoints(doc, toposolid, editor, state, center, options);

            double centerOriginalZ = GetModelSurfaceZ(doc, toposolid, snapshots, center.X, center.Y, radius)
                ?? center.Z;

            double targetZ = centerOriginalZ + options.ShapeDeltaFeet;

            foreach (SculptVertex v in state.Vertices)
            {
                double dist = HorizontalDistance(v.X, v.Y, center.X, center.Y);
                if (dist > radius) continue;

                double w = ComputeFalloff(dist / radius, options.ShapeFalloff);
                v.Z = v.Z + (targetZ - centerOriginalZ) * w;
            }

            RefreshRevitVertices(editor, state);
        }

        private static void AddShapeByPointGridPoints(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor,
            SculptState state,
            XYZ center,
            ModifyTopoOptions options)
        {
            var snapshots = state.Vertices
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();
            var addPoints = ComputeShapeByPointAddPreviewPoints(doc, toposolid, center, options, snapshots);
            const double xyTol = 0.15;
            int addedBefore = state.PointsAdded;

            foreach (XYZ pt in addPoints)
            {
                bool exists = state.Vertices.Any(v =>
                    HorizontalDistance(v.X, v.Y, pt.X, pt.Y) < xyTol);
                if (exists) continue;

                double slabZ = state.ModelZToSlabOffset(pt.Z);
                if (!SlabShapeEditorHelper.TryAddPoint(editor, new XYZ(pt.X, pt.Y, slabZ)))
                    continue;

                doc.Regenerate();
                SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, pt.X, pt.Y, xyTol);
                if (vertex == null)
                {
                    Log.Debug("DrawPoint did not create a vertex near ({X:F2}, {Y:F2})", pt.X, pt.Y);
                    continue;
                }

                double modelZ = VertexZToModelZ(doc, toposolid, snapshots, vertex.Position.Z);
                state.PointsAdded++;
                state.Vertices.Add(new SculptVertex
                {
                    RevitVertex = vertex,
                    X = pt.X,
                    Y = pt.Y,
                    Z = modelZ,
                    OriginalZ = modelZ
                });
            }

            if (state.PointsAdded > addedBefore)
            {
                doc.Regenerate();
                RefreshRevitVertices(editor, state);
                Log.Debug("Shape by Point: added {Count} grid points via DrawPoint/AddPoint",
                    state.PointsAdded - addedBefore);
            }
        }

        private static void SmoothGeometry(SculptState state, ModifyTopoOptions options)
        {
            int iterations = Math.Max(1, Math.Min(options.SmoothIterations, 50));
            double strength = Clamp(options.SmoothStrength, 0.05, 1.0);
            double lambda = strength * 0.5;
            double mu = -lambda * 0.53; // Taubin default ratio

            var neighbors = BuildNeighborMap(state.Vertices, options.MeshDensityFeet * 0.5);

            for (int iter = 0; iter < iterations; iter++)
            {
                switch (options.SmoothAlgorithm)
                {
                    case SmoothAlgorithm.Laplacian:
                        ApplyLaplacianPass(state.Vertices, neighbors, lambda);
                        break;
                    case SmoothAlgorithm.HcSmoothing:
                        ApplyLaplacianPass(state.Vertices, neighbors, lambda);
                        ApplyLaplacianPass(state.Vertices, neighbors, -lambda * 0.5);
                        break;
                    case SmoothAlgorithm.Taubin:
                    default:
                        ApplyLaplacianPass(state.Vertices, neighbors, lambda);
                        ApplyLaplacianPass(state.Vertices, neighbors, mu);
                        break;
                }
            }
        }

        private static void ApplyLaplacianPass(
            List<SculptVertex> vertices,
            Dictionary<int, List<int>> neighbors,
            double factor)
        {
            var newZ = new double[vertices.Count];
            for (int i = 0; i < vertices.Count; i++)
            {
                if (!neighbors.TryGetValue(i, out List<int> nbrs) || nbrs.Count == 0)
                {
                    newZ[i] = vertices[i].Z;
                    continue;
                }

                double avg = 0;
                foreach (int j in nbrs)
                    avg += vertices[j].Z;
                avg /= nbrs.Count;
                newZ[i] = vertices[i].Z + factor * (avg - vertices[i].Z);
            }

            for (int i = 0; i < vertices.Count; i++)
                vertices[i].Z = newZ[i];
        }

        private static Dictionary<int, List<int>> BuildNeighborMap(List<SculptVertex> vertices, double maxDist)
        {
            maxDist = Math.Max(maxDist, 1.0);
            var map = new Dictionary<int, List<int>>();
            double maxDistSq = maxDist * maxDist;

            for (int i = 0; i < vertices.Count; i++)
            {
                var nbrs = new List<int>();
                for (int j = 0; j < vertices.Count; j++)
                {
                    if (i == j) continue;
                    double dx = vertices[i].X - vertices[j].X;
                    double dy = vertices[i].Y - vertices[j].Y;
                    if (dx * dx + dy * dy <= maxDistSq)
                        nbrs.Add(j);
                }
                map[i] = nbrs;
            }
            return map;
        }

        private static void MeshControl(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor,
            SculptState state,
            ModifyTopoOptions options)
        {
            double flatSpacing = Math.Max(options.CellSizeFeet, 0.5);
            double denseSpacing = Math.Max(options.MeshDensityFeet, 0.25);
            double curvatureThreshold = Math.Max(options.CurvatureThreshold, 0.001);

            var curvature = ComputeCurvatureMap(state.Vertices, denseSpacing);

            // Remove redundant points in flat areas
            var toRemove = new List<SculptVertex>();
            foreach (SculptVertex v in state.Vertices)
            {
                if (!options.ModifyBoundary && IsNearBoundary(state.Vertices, v, flatSpacing))
                    continue;

                int idx = state.Vertices.IndexOf(v);
                double curv = idx >= 0 && idx < curvature.Count ? curvature[idx] : 0;
                if (curv > curvatureThreshold) continue;

                bool hasCloser = state.Vertices.Any(other =>
                    other != v &&
                    HorizontalDistance(other.X, other.Y, v.X, v.Y) < flatSpacing * 0.85 &&
                    Math.Abs(other.Z - v.Z) < curvatureThreshold * flatSpacing);
                if (hasCloser)
                    toRemove.Add(v);
            }

            foreach (SculptVertex v in toRemove)
            {
                if (TryDeleteVertex(editor, v.RevitVertex))
                {
                    state.Vertices.Remove(v);
                    state.PointsRemoved++;
                }
            }

            // Add points in high-curvature zones on a rotated grid
            BoundingBoxXYZ bbox = toposolid.get_BoundingBox(null);
            if (bbox == null) return;

            double angleRad = options.RotationDegrees * Math.PI / 180.0;
            double cosA = Math.Cos(angleRad);
            double sinA = Math.Sin(angleRad);
            double cx = (bbox.Min.X + bbox.Max.X) * 0.5;
            double cy = (bbox.Min.Y + bbox.Max.Y) * 0.5;

            double minX = bbox.Min.X;
            double maxX = bbox.Max.X;
            double minY = bbox.Min.Y;
            double maxY = bbox.Max.Y;

            for (double gx = minX; gx <= maxX; gx += flatSpacing)
            {
                for (double gy = minY; gy <= maxY; gy += flatSpacing)
                {
                    double rx = cx + (gx - cx) * cosA - (gy - cy) * sinA;
                    double ry = cy + (gx - cx) * sinA + (gy - cy) * cosA;

                    double localCurv = EstimateCurvatureAt(state.Vertices, rx, ry, denseSpacing);
                    double spacing = localCurv > curvatureThreshold ? denseSpacing : flatSpacing;

                    if (localCurv <= curvatureThreshold) continue;

                    bool exists = state.Vertices.Any(v =>
                        HorizontalDistance(v.X, v.Y, rx, ry) < spacing * 0.45);
                    if (exists) continue;

                    double? z = InterpolateZ(state.Vertices, rx, ry, denseSpacing * 2);
                    if (!z.HasValue) continue;

                    if (SlabShapeEditorHelper.TryAddPoint(editor, new XYZ(rx, ry, z.Value)))
                    {
                        state.PointsAdded++;

                        state.Vertices.Add(new SculptVertex
                        {
                            RevitVertex = null,
                            X = rx,
                            Y = ry,
                            Z = z.Value,
                            OriginalZ = z.Value
                        });
                    }
                }
            }

            doc.Regenerate();
            RefreshRevitVertices(editor, state);
        }

        private static void RefreshRevitVertices(SlabShapeEditor editor, SculptState state)
        {
            if (state?.Doc == null || state.Toposolid == null) return;

            var fresh = CollectVertices(state.Doc, state.Toposolid, editor);
            var byXY = fresh.ToDictionary(v => $"{Math.Round(v.X, 4)}:{Math.Round(v.Y, 4)}");

            foreach (SculptVertex v in state.Vertices)
            {
                string key = $"{Math.Round(v.X, 4)}:{Math.Round(v.Y, 4)}";
                if (byXY.TryGetValue(key, out SculptVertex match))
                {
                    v.RevitVertex = match.RevitVertex;
                    if (v.RevitVertex != null)
                        v.OriginalZ = match.OriginalZ;
                }
            }
        }

        private static bool TryDeleteVertex(SlabShapeEditor editor, SlabShapeVertex vertex)
        {
            if (editor == null || vertex == null) return false;
            try
            {
                var deletePoint = editor.GetType().GetMethod("DeletePoint", new[] { typeof(SlabShapeVertex) });
                if (deletePoint == null) return false;
                deletePoint.Invoke(editor, new object[] { vertex });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<double> ComputeCurvatureMap(List<SculptVertex> vertices, double radius)
        {
            var result = new List<double>(vertices.Count);
            foreach (SculptVertex v in vertices)
                result.Add(EstimateCurvatureAt(vertices, v.X, v.Y, radius));
            return result;
        }

        private static double EstimateCurvatureAt(List<SculptVertex> vertices, double x, double y, double radius)
        {
            double maxGrad = 0;
            double centerZ = InterpolateZ(vertices, x, y, radius) ?? 0;

            foreach (SculptVertex v in vertices)
            {
                double dist = HorizontalDistance(v.X, v.Y, x, y);
                if (dist < 0.01 || dist > radius) continue;
                double grad = Math.Abs(v.Z - centerZ) / dist;
                if (grad > maxGrad) maxGrad = grad;
            }
            return maxGrad;
        }

        private static double? InterpolateZ(List<SculptVertex> vertices, double x, double y, double maxRadius)
        {
            SculptVertex nearest = FindNearestVertex(vertices, x, y);
            if (nearest == null) return null;

            double weightSum = 0;
            double zSum = 0;
            maxRadius = Math.Max(maxRadius, 0.5);

            foreach (SculptVertex v in vertices)
            {
                double dist = HorizontalDistance(v.X, v.Y, x, y);
                if (dist > maxRadius) continue;
                double w = dist < 0.01 ? 1e6 : 1.0 / (dist * dist);
                weightSum += w;
                zSum += v.Z * w;
            }

            if (weightSum < 1e-9) return nearest.Z;
            return zSum / weightSum;
        }

        private static SculptVertex FindNearestVertex(List<SculptVertex> vertices, double x, double y)
        {
            SculptVertex best = null;
            double bestDist = double.MaxValue;
            foreach (SculptVertex v in vertices)
            {
                double d = HorizontalDistance(v.X, v.Y, x, y);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = v;
                }
            }
            return best;
        }

        private static bool IsNearBoundary(List<SculptVertex> vertices, SculptVertex v, double margin)
        {
            if (vertices.Count < 3) return true;
            double minX = vertices.Min(p => p.X);
            double maxX = vertices.Max(p => p.X);
            double minY = vertices.Min(p => p.Y);
            double maxY = vertices.Max(p => p.Y);
            return v.X <= minX + margin || v.X >= maxX - margin ||
                   v.Y <= minY + margin || v.Y >= maxY - margin;
        }

        internal static double HorizontalDistance(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2;
            double dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double ComputeFalloff(double normalizedDist, SculptFalloffType type)
        {
            double t = Clamp(normalizedDist, 0, 1);
            return type switch
            {
                SculptFalloffType.Linear => 1.0 - t,
                SculptFalloffType.Smooth => 1.0 - (3 * t * t - 2 * t * t * t), // smoothstep
                SculptFalloffType.Constant => t < 1.0 ? 1.0 : 0.0,
                SculptFalloffType.Gaussian => Math.Exp(-4.5 * t * t),
                _ => 1.0 - t
            };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public sealed class SculptVertexSnapshot
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

#if REVIT2024_OR_GREATER
        public List<SculptVertexSnapshot> GetVertexSnapshots(Toposolid toposolid)
        {
            var doc = toposolid?.Document;
            var editor = toposolid?.GetSlabShapeEditor();
            if (doc == null || editor == null) return new List<SculptVertexSnapshot>();
            return CollectVertices(doc, toposolid, editor)
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();
        }

        public static double? InterpolateSurfaceZ(
            IEnumerable<SculptVertexSnapshot> vertices, double x, double y, double maxRadius)
        {
            if (vertices == null) return null;

            double weightSum = 0;
            double zSum = 0;
            maxRadius = Math.Max(maxRadius, 0.5);
            double bestDist = double.MaxValue;
            double bestZ = 0;
            bool any = false;

            foreach (SculptVertexSnapshot v in vertices)
            {
                any = true;
                double dist = HorizontalDistance(v.X, v.Y, x, y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestZ = v.Z;
                }
                if (dist > maxRadius) continue;
                double w = dist < 0.01 ? 1e6 : 1.0 / (dist * dist);
                weightSum += w;
                zSum += v.Z * w;
            }

            if (!any) return null;
            if (weightSum < 1e-9) return bestZ;
            return zSum / weightSum;
        }

        /// <summary>Resolve a 3D view for face raycasts (active view or any open 3D view).</summary>
        public static View3D ResolveView3D(Document doc, View view)
        {
            if (view is View3D view3d)
                return view3d;

            if (doc == null)
                return null;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);
        }

        /// <summary>Raycast downward onto Toposolid solid geometry — matches what you see in the view.</summary>
        public static double? TryRaycastToposolidSurfaceZ(Toposolid toposolid, View3D view, double x, double y)
        {
            if (toposolid == null || view == null)
                return null;

            BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
            if (bb == null)
                return null;

            const double margin = 30.0;
            try
            {
                var intersector = new ReferenceIntersector(
                    toposolid.Id, FindReferenceTarget.Face, view);

                ReferenceWithContext hitDown = intersector.FindNearest(
                    new XYZ(x, y, bb.Max.Z + margin),
                    XYZ.BasisZ.Negate());
                XYZ gp = hitDown?.GetReference()?.GlobalPoint;
                if (gp != null)
                    return gp.Z;

                XYZ viewDir = view.ViewDirection;
                ReferenceWithContext hitView = intersector.FindNearest(
                    new XYZ(x, y, bb.Max.Z + margin) - viewDir * margin,
                    viewDir.Negate());
                gp = hitView?.GetReference()?.GlobalPoint;
                if (gp != null)
                    return gp.Z;
            }
            catch (Exception ex)
            {
                Log.Debug("Raycast topo surface Z failed: {Error}", ex.Message);
            }

            return null;
        }

        /// <summary>Surface Z for preview: raycast on solid first, then slab vertices + level correction.</summary>
        public static double? GetDisplaySurfaceZ(
            Document doc,
            Toposolid toposolid,
            View view,
            IList<SculptVertexSnapshot> vertices,
            double x,
            double y,
            double searchRadius)
        {
            View3D view3d = ResolveView3D(doc, view);
            if (view3d != null)
            {
                double? rayZ = TryRaycastToposolidSurfaceZ(toposolid, view3d, x, y);
                if (rayZ.HasValue)
                    return rayZ.Value;
            }

            return GetModelSurfaceZ(doc, toposolid, vertices, x, y, searchRadius);
        }

        /// <summary>Slab-shape vertex Z → model elevation (handles offset vs model coords).</summary>
        public static double VertexZToModelZ(
            Document doc, Toposolid toposolid, IList<SculptVertexSnapshot> vertices, double vertexZ)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb != null && vertexZ >= bb.Min.Z - 2.0 && vertexZ <= bb.Max.Z + 2.0)
                    return vertexZ;
            }
            catch { }

            return GetLevelElevation(doc, toposolid) + GetHeightOffsetFromLevel(toposolid) + vertexZ;
        }

        /// <summary>Interpolated topo surface elevation in model coordinates at XY.</summary>
        public static double? GetModelSurfaceZ(
            Document doc,
            Toposolid toposolid,
            IList<SculptVertexSnapshot> vertices,
            double x,
            double y,
            double searchRadius)
        {
            double? slabZ = InterpolateSurfaceZ(vertices, x, y, searchRadius);
            if (!slabZ.HasValue)
            {
                try
                {
                    BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                    if (bb != null)
                        return (bb.Min.Z + bb.Max.Z) * 0.5;
                }
                catch { }
                return null;
            }

            return VertexZToModelZ(doc, toposolid, vertices, slabZ.Value);
        }

        public static double ComputeShapeFalloff(double t, SculptFalloffType type)
        {
            t = Math.Max(0, Math.Min(1, t));
            return type switch
            {
                SculptFalloffType.Linear => 1.0 - t,
                SculptFalloffType.Smooth => 1.0 - (3 * t * t - 2 * t * t * t),
                SculptFalloffType.Constant => t < 1.0 ? 1.0 : 0.0,
                SculptFalloffType.Gaussian => Math.Exp(-4.5 * t * t),
                _ => 1.0 - t
            };
        }

        /// <summary>Radial stamp points for hover preview (on topo surface + gain falloff).</summary>
        public static List<XYZ> ComputeShapeByPointStampPoints(
            XYZ center,
            ModifyTopoOptions options,
            IList<SculptVertexSnapshot> vertices,
            Document doc,
            Toposolid toposolid,
            View view,
            bool previewWithGain)
        {
            var result = new List<XYZ>();
            if (center == null || options == null)
                return result;

            const int maxPoints = 120;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);
            int density = Math.Max(1, Math.Min(options.ShapePointDensity, 10));
            int ringCount = Math.Max(1, density);
            int baseSegments = 6 + density * 2;

            double centerSurfaceZ = doc != null && toposolid != null
                ? GetDisplaySurfaceZ(doc, toposolid, view, vertices, center.X, center.Y, radius) ?? center.Z
                : InterpolateSurfaceZ(vertices, center.X, center.Y, radius) ?? center.Z;

            double centerGain = previewWithGain ? options.ShapeDeltaFeet : 0;
            result.Add(new XYZ(center.X, center.Y, centerSurfaceZ + centerGain));

            for (int ring = 1; ring <= ringCount && result.Count < maxPoints; ring++)
            {
                double ringRadius = radius * ring / ringCount;
                int segments = Math.Max(6, Math.Min(24, baseSegments + ring));
                double angleStep = 2.0 * Math.PI / segments;

                for (int i = 0; i < segments && result.Count < maxPoints; i++)
                {
                    double angle = i * angleStep;
                    double gx = center.X + ringRadius * Math.Cos(angle);
                    double gy = center.Y + ringRadius * Math.Sin(angle);

                    if (HorizontalDistance(gx, gy, center.X, center.Y) > radius + 0.01)
                        continue;

                    double surfaceZ = doc != null && toposolid != null
                        ? GetDisplaySurfaceZ(doc, toposolid, view, vertices, gx, gy, ringRadius + 2) ?? centerSurfaceZ
                        : InterpolateSurfaceZ(vertices, gx, gy, ringRadius + 2) ?? centerSurfaceZ;

                    double dist = HorizontalDistance(gx, gy, center.X, center.Y);
                    double w = previewWithGain
                        ? ComputeShapeFalloff(dist / radius, options.ShapeFalloff)
                        : 0;
                    result.Add(new XYZ(gx, gy, surfaceZ + options.ShapeDeltaFeet * w));
                }
            }

            return result;
        }

        /// <summary>Stamp points that would be newly added (no existing vertex nearby).</summary>
        public static List<XYZ> ComputeShapeByPointAddPreviewPoints(
            Document doc,
            Toposolid toposolid,
            XYZ center,
            ModifyTopoOptions options,
            IList<SculptVertexSnapshot> vertices)
        {
            var stamp = ComputeShapeByPointStampPoints(
                center, options, vertices, doc, toposolid, null, previewWithGain: false);
            if (vertices == null || vertices.Count == 0)
                return stamp;

            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);
            double tol = Math.Max(radius / (6 + options.ShapePointDensity * 4), 0.2);

            return stamp.Where(pt =>
                !vertices.Any(v => HorizontalDistance(v.X, v.Y, pt.X, pt.Y) < tol))
                .ToList();
        }
#endif
    }
}
