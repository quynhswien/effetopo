using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Toposolid display mesh in model coordinates (from get_Geometry).
    /// SlabShapeVertex Z can differ from visible geometry — preview must use this cache.
    /// </summary>
    internal sealed class ModifyTopoGeometrySurfaceCache
    {
        internal sealed class SurfaceTriangle
        {
            public XYZ V0;
            public XYZ V1;
            public XYZ V2;
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;
        }

        private readonly List<SurfaceTriangle> _triangles = new List<SurfaceTriangle>();
        private readonly List<Solid> _solids = new List<Solid>();

        public int TriangleCount => _triangles.Count;
        public double MinZ { get; private set; }
        public double MaxZ { get; private set; }

        public ModifyTopoGeometrySurfaceCache(Toposolid toposolid)
        {
            if (toposolid == null) return;
            ExtractFromToposolid(toposolid);
            if (_triangles.Count > 0)
            {
                MinZ = _triangles.Min(t => Math.Min(t.V0.Z, Math.Min(t.V1.Z, t.V2.Z)));
                MaxZ = _triangles.Max(t => Math.Max(t.V0.Z, Math.Max(t.V1.Z, t.V2.Z)));
            }
            Log.Information(
                "Preview geometry cache: {TriCount} triangles, Z {MinZ:F2}–{MaxZ:F2}, {SolidCount} solids",
                _triangles.Count, MinZ, MaxZ, _solids.Count);
        }

        public IReadOnlyList<Solid> Solids => _solids;

        /// <summary>Top surface elevation at XY from display mesh (highest hit).</summary>
        public double? TryGetSurfaceZ(double x, double y)
        {
            if (_triangles.Count == 0)
                return null;

            double bestZ = double.MinValue;
            bool found = false;

            foreach (SurfaceTriangle tri in _triangles)
            {
                if (x < tri.MinX - 0.01 || x > tri.MaxX + 0.01 ||
                    y < tri.MinY - 0.01 || y > tri.MaxY + 0.01)
                    continue;

                if (TryInterpolateZInTriangle(tri, x, y, out double z) && z > bestZ)
                {
                    bestZ = z;
                    found = true;
                }
            }

            if (found)
                return bestZ;

            return TryNearestTriangleZ(x, y);
        }

        public List<XYZ> BuildStampPoints(
            XYZ center,
            Models.ModifyTopoOptions options,
            bool previewWithGain)
        {
            var result = new List<XYZ>();
            if (center == null || options == null || _triangles.Count == 0)
                return result;

            const int maxPoints = 120;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);
            int density = Math.Max(1, Math.Min(options.ShapePointDensity, 10));
            int ringCount = Math.Max(1, density);
            int baseSegments = 6 + density * 2;

            double centerSurfaceZ = TryGetSurfaceZ(center.X, center.Y) ?? center.Z;
            double centerGain = previewWithGain ? options.ShapeDeltaFeet : 0;
            result.Add(new XYZ(center.X, center.Y, centerSurfaceZ + centerGain));

            for (int ring = 1; ring <= ringCount && result.Count < maxPoints; ring++)
            {
                double ringRadius = radius * ring / (double)ringCount;
                int segments = Math.Max(6, Math.Min(24, baseSegments + ring));
                double angleStep = 2.0 * Math.PI / segments;

                for (int i = 0; i < segments && result.Count < maxPoints; i++)
                {
                    double angle = i * angleStep;
                    double gx = center.X + ringRadius * Math.Cos(angle);
                    double gy = center.Y + ringRadius * Math.Sin(angle);

                    if (ModifyTopoService.HorizontalDistance(gx, gy, center.X, center.Y) > radius + 0.01)
                        continue;

                    double surfaceZ = TryGetSurfaceZ(gx, gy) ?? centerSurfaceZ;
                    double dist = ModifyTopoService.HorizontalDistance(gx, gy, center.X, center.Y);
                    double w = previewWithGain
                        ? ModifyTopoService.ComputeShapeFalloff(dist / radius, options.ShapeFalloff)
                        : 0;
                    result.Add(new XYZ(gx, gy, surfaceZ + options.ShapeDeltaFeet * w));
                }
            }

            return result;
        }

        private void ExtractFromToposolid(Toposolid toposolid)
        {
            try
            {
                var opt = new Options
                {
                    DetailLevel = ViewDetailLevel.Fine,
                    ComputeReferences = false
                };
                GeometryElement ge = toposolid.get_Geometry(opt);
                if (ge == null) return;
                ExtractFromGeometry(ge, Transform.Identity);
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to extract preview geometry: {Error}", ex.Message);
            }
        }

        private void ExtractFromGeometry(GeometryElement ge, Transform transform)
        {
            foreach (GeometryObject obj in ge)
            {
                if (obj is Solid solid && solid.Volume > 1e-9)
                {
                    _solids.Add(solid);
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh = face.Triangulate();
                        if (mesh == null) continue;
                        for (int i = 0; i < mesh.NumTriangles; i++)
                        {
                            MeshTriangle tri = mesh.get_Triangle(i);
                            AddTriangle(
                                transform.OfPoint(tri.get_Vertex(0)),
                                transform.OfPoint(tri.get_Vertex(1)),
                                transform.OfPoint(tri.get_Vertex(2)));
                        }
                    }
                }
                else if (obj is Mesh mesh)
                {
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle tri = mesh.get_Triangle(i);
                        AddTriangle(
                            transform.OfPoint(tri.get_Vertex(0)),
                            transform.OfPoint(tri.get_Vertex(1)),
                            transform.OfPoint(tri.get_Vertex(2)));
                    }
                }
                else if (obj is GeometryInstance gi)
                {
                    Transform combined = transform.Multiply(gi.Transform);
                    ExtractFromGeometry(gi.GetInstanceGeometry(), combined);
                }
            }
        }

        private void AddTriangle(XYZ v0, XYZ v1, XYZ v2)
        {
            XYZ edge1 = v1 - v0;
            XYZ edge2 = v2 - v0;
            XYZ normal = edge1.CrossProduct(edge2);
            if (normal.Z <= 0.05)
                return;

            _triangles.Add(new SurfaceTriangle
            {
                V0 = v0,
                V1 = v1,
                V2 = v2,
                MinX = Math.Min(Math.Min(v0.X, v1.X), v2.X),
                MaxX = Math.Max(Math.Max(v0.X, v1.X), v2.X),
                MinY = Math.Min(Math.Min(v0.Y, v1.Y), v2.Y),
                MaxY = Math.Max(Math.Max(v0.Y, v1.Y), v2.Y)
            });
        }

        private static bool TryInterpolateZInTriangle(SurfaceTriangle tri, double x, double y, out double z)
        {
            z = 0;
            XYZ v0 = tri.V0;
            XYZ v1 = tri.V1;
            XYZ v2 = tri.V2;

            double denom = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
            if (Math.Abs(denom) < 1e-12)
                return false;

            double w0 = ((v1.Y - v2.Y) * (x - v2.X) + (v2.X - v1.X) * (y - v2.Y)) / denom;
            double w1 = ((v2.Y - v0.Y) * (x - v2.X) + (v0.X - v2.X) * (y - v2.Y)) / denom;
            double w2 = 1.0 - w0 - w1;

            if (w0 < -0.02 || w1 < -0.02 || w2 < -0.02)
                return false;

            z = w0 * v0.Z + w1 * v1.Z + w2 * v2.Z;
            return true;
        }

        private double? TryNearestTriangleZ(double x, double y)
        {
            double bestDist = double.MaxValue;
            double bestZ = 0;
            bool found = false;

            foreach (SurfaceTriangle tri in _triangles)
            {
                double cx = (tri.V0.X + tri.V1.X + tri.V2.X) / 3.0;
                double cy = (tri.V0.Y + tri.V1.Y + tri.V2.Y) / 3.0;
                double dist = ModifyTopoService.HorizontalDistance(x, y, cx, cy);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestZ = (tri.V0.Z + tri.V1.Z + tri.V2.Z) / 3.0;
                    found = true;
                }
            }

            return found ? bestZ : (double?)null;
        }
    }
#endif
}
