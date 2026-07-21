using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Samples the Toposolid top surface and generates contour polylines at fixed Z intervals.
    /// </summary>
    internal static class ToposolidContourExtractor
    {
        private const double PointTolerance = 1e-4;

        internal sealed class Triangle
        {
            public XYZ V0 { get; set; }
            public XYZ V1 { get; set; }
            public XYZ V2 { get; set; }
        }

        internal static List<Triangle> ExtractTopTriangles(Toposolid toposolid)
        {
            var triangles = new List<Triangle>();
            Options options = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geometry = toposolid.get_Geometry(options);
            if (geometry != null)
                CollectTriangles(geometry, Transform.Identity, triangles);

            return triangles;
        }

        internal static List<IList<XYZ>> GenerateContourPolylines(
            IList<Triangle> triangles,
            double intervalFeet)
        {
            if (triangles == null || triangles.Count == 0 || intervalFeet <= 0)
                return new List<IList<XYZ>>();

            double minZ = double.MaxValue;
            double maxZ = double.MinValue;
            foreach (Triangle triangle in triangles)
            {
                minZ = Math.Min(minZ, Math.Min(triangle.V0.Z, Math.Min(triangle.V1.Z, triangle.V2.Z)));
                maxZ = Math.Max(maxZ, Math.Max(triangle.V0.Z, Math.Max(triangle.V1.Z, triangle.V2.Z)));
            }

            if (minZ > maxZ)
                return new List<IList<XYZ>>();

            var allPolylines = new List<IList<XYZ>>();
            double startZ = Math.Floor(minZ / intervalFeet) * intervalFeet;
            if (startZ < minZ)
                startZ += intervalFeet;

            for (double z = startZ; z <= maxZ + intervalFeet * 0.5; z += intervalFeet)
            {
                var segments = new List<(XYZ A, XYZ B)>();
                foreach (Triangle triangle in triangles)
                    AddTriangleSegmentsAtElevation(triangle, z, segments);

                allPolylines.AddRange(ChainSegments(segments));
            }

            return allPolylines;
        }

        internal static double? TryReadContourIntervalFeet(Toposolid toposolid) =>
            TryReadToposolidDoubleParameter(toposolid, "contour", "interval", excludeMajor: true);

        internal static double? TryReadMajorContourIntervalFeet(Toposolid toposolid) =>
            TryReadToposolidDoubleParameter(toposolid, "contour", "interval", requireMajor: true);

        private static double? TryReadToposolidDoubleParameter(
            Toposolid toposolid,
            string requiredToken1,
            string requiredToken2,
            bool requireMajor = false,
            bool excludeMajor = false)
        {
            if (toposolid == null) return null;

            foreach (Parameter parameter in toposolid.Parameters)
            {
                if (parameter == null || parameter.StorageType != StorageType.Double)
                    continue;

                string name = parameter.Definition?.Name ?? string.Empty;
                if (name.IndexOf(requiredToken1, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (name.IndexOf(requiredToken2, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool hasMajor = name.IndexOf("major", StringComparison.OrdinalIgnoreCase) >= 0;
                if (requireMajor && !hasMajor)
                    continue;
                if (excludeMajor && hasMajor)
                    continue;

                double value = parameter.AsDouble();
                if (value > 0)
                    return value;
            }

            return null;
        }

        private static void CollectTriangles(GeometryElement geometry, Transform transform, List<Triangle> triangles)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        Mesh mesh = face.Triangulate();
                        if (mesh == null) continue;
                        AddMeshTriangles(mesh, transform, triangles);
                    }
                }
                else if (obj is Mesh mesh)
                {
                    AddMeshTriangles(mesh, transform, triangles);
                }
                else if (obj is GeometryInstance instance)
                {
                    Transform combined = transform.Multiply(instance.Transform);
                    CollectTriangles(instance.SymbolGeometry, combined, triangles);
                }
            }
        }

        private static void AddMeshTriangles(Mesh mesh, Transform transform, List<Triangle> triangles)
        {
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                XYZ v0 = transform.OfPoint(triangle.get_Vertex(0));
                XYZ v1 = transform.OfPoint(triangle.get_Vertex(1));
                XYZ v2 = transform.OfPoint(triangle.get_Vertex(2));

                XYZ edge1 = v1 - v0;
                XYZ edge2 = v2 - v0;
                if (edge1.CrossProduct(edge2).Z <= 0.1)
                    continue;

                triangles.Add(new Triangle { V0 = v0, V1 = v1, V2 = v2 });
            }
        }

        private static void AddTriangleSegmentsAtElevation(
            Triangle triangle,
            double z,
            List<(XYZ A, XYZ B)> segments)
        {
            var hits = new List<XYZ>();
            AddEdgeHit(triangle.V0, triangle.V1, z, hits);
            AddEdgeHit(triangle.V1, triangle.V2, z, hits);
            AddEdgeHit(triangle.V2, triangle.V0, z, hits);
            hits = DeduplicatePoints(hits);

            if (hits.Count == 2)
            {
                if (hits[0].DistanceTo(hits[1]) >= PointTolerance)
                    segments.Add((hits[0], hits[1]));
            }
        }

        private static void AddEdgeHit(XYZ a, XYZ b, double z, List<XYZ> hits)
        {
            const double tolerance = 1e-6;
            if (Math.Abs(a.Z - z) < tolerance)
                hits.Add(new XYZ(a.X, a.Y, z));
            if (Math.Abs(b.Z - z) < tolerance)
                hits.Add(new XYZ(b.X, b.Y, z));

            if (Math.Abs(a.Z - b.Z) < tolerance)
                return;

            if ((a.Z - z) * (b.Z - z) > tolerance)
                return;

            double t = (z - a.Z) / (b.Z - a.Z);
            if (t < -tolerance || t > 1 + tolerance)
                return;

            hits.Add(new XYZ(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), z));
        }

        private static List<XYZ> DeduplicatePoints(List<XYZ> points)
        {
            var unique = new List<XYZ>();
            foreach (XYZ point in points)
            {
                if (!unique.Any(existing => existing.DistanceTo(point) < PointTolerance))
                    unique.Add(point);
            }
            return unique;
        }

        private static List<IList<XYZ>> ChainSegments(List<(XYZ A, XYZ B)> segments)
        {
            var polylines = new List<IList<XYZ>>();
            if (segments.Count == 0)
                return polylines;

            var unused = new List<(XYZ A, XYZ B)>(segments);

            while (unused.Count > 0)
            {
                var chain = new List<XYZ> { unused[0].A, unused[0].B };
                unused.RemoveAt(0);

                bool extended;
                do
                {
                    extended = false;
                    for (int i = unused.Count - 1; i >= 0; i--)
                    {
                        if (TryAppendSegment(chain, unused[i]))
                        {
                            unused.RemoveAt(i);
                            extended = true;
                        }
                    }
                } while (extended);

                if (chain.Count >= 2)
                {
                    IList<XYZ> cleaned = RemoveNearDuplicateVertices(chain, PointTolerance);
                    if (cleaned.Count >= 2)
                        polylines.Add(cleaned);
                }
            }

            return polylines;
        }

        private static List<XYZ> RemoveNearDuplicateVertices(IList<XYZ> chain, double tolerance)
        {
            var cleaned = new List<XYZ>();
            foreach (XYZ point in chain)
            {
                if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].DistanceTo(point) >= tolerance)
                    cleaned.Add(point);
            }

            return cleaned;
        }

        private static bool TryAppendSegment(List<XYZ> chain, (XYZ A, XYZ B) segment)
        {
            XYZ start = chain[0];
            XYZ end = chain[chain.Count - 1];

            if (end.DistanceTo(segment.A) < PointTolerance)
            {
                chain.Add(segment.B);
                return true;
            }
            if (end.DistanceTo(segment.B) < PointTolerance)
            {
                chain.Add(segment.A);
                return true;
            }
            if (start.DistanceTo(segment.B) < PointTolerance)
            {
                chain.Insert(0, segment.A);
                return true;
            }
            if (start.DistanceTo(segment.A) < PointTolerance)
            {
                chain.Insert(0, segment.B);
                return true;
            }

            return false;
        }
    }
#endif
}
