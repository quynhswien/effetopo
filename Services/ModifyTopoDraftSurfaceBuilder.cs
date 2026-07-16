using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Builds draft preview geometry as extruded solids (reliable in DirectShape).
    /// </summary>
    internal static class ModifyTopoDraftSurfaceBuilder
    {
        private const double ZDeltaThreshold = 0.02;
        private const double BoundsMargin = 2.0;
        private const double SlabSearchRadius = 80.0;
        private const double DraftSlabThickness = 0.45;
        private const double StampColumnRadius = 0.55;
        private const int MaxPreviewSolids = 400;

        public sealed class DraftPreviewGeometry
        {
            public IList<GeometryObject> DraftSurface = new List<GeometryObject>();
            public IList<GeometryObject> DeltaVolumes = new List<GeometryObject>();
            public int ChangedTriangleCount;
        }

        public static DraftPreviewGeometry Build(
            ModifyTopoGeometrySurfaceCache geometry,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IEnumerable<ModifyTopoDraftSession.StampRecord> stamps,
            double minX,
            double minY,
            double maxX,
            double maxY)
        {
            var result = new DraftPreviewGeometry();
            if (geometry == null || baseVertices == null || workingVertices == null)
                return result;

            minX -= BoundsMargin;
            minY -= BoundsMargin;
            maxX += BoundsMargin;
            maxY += BoundsMargin;

            int solidBudget = MaxPreviewSolids;

            foreach (ModifyTopoGeometrySurfaceCache.SurfaceTriangle tri in geometry.GetTrianglesInBounds(minX, minY, maxX, maxY))
            {
                if (solidBudget <= 0) break;

                if (!TryDeformTriangle(tri, baseVertices, workingVertices,
                        out XYZ d0, out XYZ d1, out XYZ d2,
                        out double maxDelta))
                    continue;

                if (maxDelta < ZDeltaThreshold)
                    continue;

                result.ChangedTriangleCount++;

                Solid draftSlab = TryExtrudeTriangle(d0, d1, d2, DraftSlabThickness);
                if (draftSlab != null)
                {
                    result.DraftSurface.Add(draftSlab);
                    solidBudget--;
                }

                Solid delta = TryCreateDeltaBlock(tri.V0, tri.V1, tri.V2, d0, d1, d2);
                if (delta != null)
                {
                    result.DeltaVolumes.Add(delta);
                    solidBudget--;
                }
            }

            return result;
        }

        public static void GetBoundsFromStamps(
            IEnumerable<ModifyTopoDraftSession.StampRecord> stamps,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            bool any = false;

            if (stamps != null)
            {
                foreach (ModifyTopoDraftSession.StampRecord stamp in stamps)
                {
                    if (stamp?.Center == null || stamp.Options == null) continue;
                    double r = Math.Max(stamp.Options.ShapeRadiusFeet, 0.5);
                    minX = Math.Min(minX, stamp.Center.X - r);
                    minY = Math.Min(minY, stamp.Center.Y - r);
                    maxX = Math.Max(maxX, stamp.Center.X + r);
                    maxY = Math.Max(maxY, stamp.Center.Y + r);
                    any = true;
                }
            }

            if (!any)
                minX = minY = maxX = maxY = 0;
        }

        private static bool TryDeformTriangle(
            ModifyTopoGeometrySurfaceCache.SurfaceTriangle tri,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            out XYZ d0,
            out XYZ d1,
            out XYZ d2,
            out double maxDelta)
        {
            d0 = d1 = d2 = null;
            maxDelta = 0;

            if (!TryDraftZ(tri.V0, baseVertices, workingVertices, out double z0, out double delta0) ||
                !TryDraftZ(tri.V1, baseVertices, workingVertices, out double z1, out double delta1) ||
                !TryDraftZ(tri.V2, baseVertices, workingVertices, out double z2, out double delta2))
                return false;

            d0 = new XYZ(tri.V0.X, tri.V0.Y, z0);
            d1 = new XYZ(tri.V1.X, tri.V1.Y, z1);
            d2 = new XYZ(tri.V2.X, tri.V2.Y, z2);
            maxDelta = Math.Max(Math.Abs(delta0), Math.Max(Math.Abs(delta1), Math.Abs(delta2)));
            return true;
        }

        private static bool TryDraftZ(
            XYZ meshVertex,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            out double draftZ,
            out double deltaZ)
        {
            draftZ = meshVertex.Z;
            deltaZ = 0;

            double? baseSlabZ = ModifyTopoService.InterpolateSurfaceZ(
                baseVertices, meshVertex.X, meshVertex.Y, SlabSearchRadius);
            double? draftSlabZ = ModifyTopoService.InterpolateSurfaceZ(
                workingVertices, meshVertex.X, meshVertex.Y, SlabSearchRadius);

            if (!baseSlabZ.HasValue && !draftSlabZ.HasValue)
                return true;

            double baseZ = baseSlabZ ?? draftSlabZ ?? meshVertex.Z;
            double workingZ = draftSlabZ ?? baseZ;
            deltaZ = workingZ - baseZ;
            draftZ = meshVertex.Z + deltaZ;
            return true;
        }

        /// <summary>Vertical block from base mesh triangle to draft triangle (per-corner Z).</summary>
        private static Solid TryCreateDeltaBlock(
            XYZ b0, XYZ b1, XYZ b2,
            XYZ d0, XYZ d1, XYZ d2)
        {
            var solids = new List<Solid>();
            TryAddEdgeColumn(b0, d0, solids);
            TryAddEdgeColumn(b1, d1, solids);
            TryAddEdgeColumn(b2, d2, solids);

            Solid top = TryExtrudeTriangle(d0, d1, d2, DraftSlabThickness * 0.5);
            if (top != null) solids.Add(top);

            if (solids.Count == 0) return null;
            if (solids.Count == 1) return solids[0];

            try
            {
                Solid merged = solids[0];
                for (int i = 1; i < solids.Count; i++)
                {
                    merged = BooleanOperationsUtils.ExecuteBooleanOperation(
                        merged, solids[i], BooleanOperationsType.Union);
                }
                return merged;
            }
            catch
            {
                return solids[0];
            }
        }

        private static void TryAddEdgeColumn(XYZ bottom, XYZ top, IList<Solid> solids)
        {
            if (bottom == null || top == null) return;
            double dz = top.Z - bottom.Z;
            if (Math.Abs(dz) < ZDeltaThreshold) return;

            Solid col = TryExtrudeColumn(bottom.X, bottom.Y, bottom.Z, top.Z, StampColumnRadius * 0.85);
            if (col != null) solids.Add(col);
        }

        private static Solid TryExtrudeTriangle(XYZ v0, XYZ v1, XYZ v2, double thickness)
        {
            if (v0 == null || v1 == null || v2 == null) return null;
            try
            {
                double minEdge = MinEdgeLength(v0, v1, v2);
                if (minEdge < 0.05) return null;

                var loop = CurveLoop.Create(new[]
                {
                    Line.CreateBound(v0, v1),
                    Line.CreateBound(v1, v2),
                    Line.CreateBound(v2, v0)
                });
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop },
                    XYZ.BasisZ,
                    Math.Max(thickness, 0.15));
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
                if (Math.Abs(height) < ZDeltaThreshold) return null;

                radius = Math.Max(radius, 0.25);
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
            const int segments = 12;
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
    }
#endif
}
