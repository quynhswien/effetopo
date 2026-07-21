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
        private const double VertexSnapTolerance = 1.0 / 256.0;
        private const double PlaneTolerance = 1e-6;
        private const double MinFaceNormalZ = 0.35;

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

                segments = DeduplicateSegments(segments, VertexSnapTolerance);
                allPolylines.AddRange(ChainSegments(segments, intervalFeet));
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
                        if (!IsUpwardFacingFace(face, transform))
                            continue;

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

        private static bool IsUpwardFacingFace(Face face, Transform transform)
        {
            try
            {
                BoundingBoxUV bbox = face.GetBoundingBox();
                if (bbox == null)
                    return false;

                UV mid = new UV(
                    (bbox.Min.U + bbox.Max.U) * 0.5,
                    (bbox.Min.V + bbox.Max.V) * 0.5);
                XYZ normal = transform.OfVector(face.ComputeNormal(mid));
                return normal.Z >= MinFaceNormalZ;
            }
            catch
            {
                return false;
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
                if (edge1.CrossProduct(edge2).Z <= MinFaceNormalZ)
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
            CollectEdgeIntersections(triangle.V0, triangle.V1, z, hits);
            CollectEdgeIntersections(triangle.V1, triangle.V2, z, hits);
            CollectEdgeIntersections(triangle.V2, triangle.V0, z, hits);
            hits = DeduplicatePoints(hits, VertexSnapTolerance);

            if (hits.Count == 2)
                TryAddSegment(hits[0], hits[1], segments);
        }

        private static void CollectEdgeIntersections(XYZ a, XYZ b, double z, List<XYZ> hits)
        {
            bool aOn = Math.Abs(a.Z - z) < PlaneTolerance;
            bool bOn = Math.Abs(b.Z - z) < PlaneTolerance;

            if (aOn)
                hits.Add(new XYZ(a.X, a.Y, z));
            if (bOn)
                hits.Add(new XYZ(b.X, b.Y, z));

            if (aOn && bOn)
                return;

            if (Math.Abs(a.Z - b.Z) < PlaneTolerance)
                return;

            if ((a.Z - z) * (b.Z - z) > PlaneTolerance)
                return;

            double t = (z - a.Z) / (b.Z - a.Z);
            if (t < -PlaneTolerance || t > 1 + PlaneTolerance)
                return;

            hits.Add(new XYZ(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), z));
        }

        private static void TryAddSegment(XYZ a, XYZ b, List<(XYZ A, XYZ B)> segments)
        {
            if (a == null || b == null)
                return;

            if (a.DistanceTo(b) < VertexSnapTolerance)
                return;

            segments.Add((a, b));
        }

        private static List<XYZ> DeduplicatePoints(IList<XYZ> points, double tolerance)
        {
            var unique = new List<XYZ>();
            foreach (XYZ point in points)
            {
                if (!unique.Any(existing => existing.DistanceTo(point) < tolerance))
                    unique.Add(point);
            }

            return unique;
        }

        private static List<(XYZ A, XYZ B)> DeduplicateSegments(
            IList<(XYZ A, XYZ B)> segments,
            double tolerance)
        {
            var unique = new List<(XYZ A, XYZ B)>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach ((XYZ a, XYZ b) in segments)
            {
                string key = BuildUndirectedSegmentKey(a, b, tolerance);
                if (!keys.Add(key))
                    continue;

                unique.Add((a, b));
            }

            return unique;
        }

        private static string BuildUndirectedSegmentKey(XYZ a, XYZ b, double tolerance)
        {
            string keyA = BuildSnapKey(a, tolerance);
            string keyB = BuildSnapKey(b, tolerance);
            return string.CompareOrdinal(keyA, keyB) <= 0
                ? $"{keyA}|{keyB}"
                : $"{keyB}|{keyA}";
        }

        private static string BuildSnapKey(XYZ point, double tolerance)
        {
            long x = (long)Math.Round(point.X / tolerance);
            long y = (long)Math.Round(point.Y / tolerance);
            long z = (long)Math.Round(point.Z / tolerance);
            return $"{x}:{y}:{z}";
        }

        private static List<IList<XYZ>> ChainSegments(
            IList<(XYZ A, XYZ B)> segments,
            double intervalFeet)
        {
            if (segments == null || segments.Count == 0)
                return new List<IList<XYZ>>();

            var builder = new ContourSegmentGraph(VertexSnapTolerance);
            foreach ((XYZ a, XYZ b) in segments)
                builder.AddSegment(a, b);

            double maxSegmentLength = ComputeMaxSegmentLength(segments, intervalFeet);
            return builder.BuildPolylines(maxSegmentLength);
        }

        private static double ComputeMaxSegmentLength(
            IList<(XYZ A, XYZ B)> segments,
            double intervalFeet)
        {
            var lengths = segments
                .Select(s => s.A.DistanceTo(s.B))
                .Where(length => length > VertexSnapTolerance)
                .OrderBy(length => length)
                .ToList();

            if (lengths.Count == 0)
                return Math.Max(intervalFeet * 8.0, 10.0);

            double median = lengths[lengths.Count / 2];
            double p90 = lengths[(int)Math.Min(lengths.Count - 1, Math.Floor(lengths.Count * 0.9))];
            return Math.Max(intervalFeet * 8.0, Math.Max(median * 6.0, p90 * 2.5));
        }

        private sealed class ContourSegmentGraph
        {
            private readonly double _snapTolerance;
            private readonly List<XYZ> _vertices = new();
            private readonly Dictionary<string, int> _vertexIndex = new(StringComparer.Ordinal);
            private readonly List<(int A, int B)> _edges = new();

            public ContourSegmentGraph(double snapTolerance)
            {
                _snapTolerance = snapTolerance;
            }

            public void AddSegment(XYZ a, XYZ b)
            {
                int indexA = GetOrAddVertex(a);
                int indexB = GetOrAddVertex(b);
                if (indexA == indexB)
                    return;

                _edges.Add((indexA, indexB));
            }

            public List<IList<XYZ>> BuildPolylines(double maxSegmentLength)
            {
                var polylines = new List<IList<XYZ>>();
                var usedEdges = new bool[_edges.Count];
                var adjacency = BuildAdjacency();

                for (int edgeIndex = 0; edgeIndex < _edges.Count; edgeIndex++)
                {
                    if (usedEdges[edgeIndex])
                        continue;

                    (int a, int b) = _edges[edgeIndex];
                    double length = _vertices[a].DistanceTo(_vertices[b]);
                    if (length > maxSegmentLength)
                    {
                        usedEdges[edgeIndex] = true;
                        continue;
                    }

                    var chain = new List<XYZ> { _vertices[a], _vertices[b] };
                    usedEdges[edgeIndex] = true;

                    ExtendChain(chain, a, forward: false, adjacency, usedEdges, maxSegmentLength);
                    ExtendChain(chain, b, forward: true, adjacency, usedEdges, maxSegmentLength);

                    IList<XYZ> cleaned = RemoveNearDuplicateVertices(chain, _snapTolerance);
                    if (cleaned.Count >= 2)
                        polylines.Add(cleaned);
                }

                return polylines;
            }

            private Dictionary<int, List<(int Neighbor, int EdgeIndex)>> BuildAdjacency()
            {
                var adjacency = new Dictionary<int, List<(int, int)>>();
                for (int i = 0; i < _edges.Count; i++)
                {
                    (int a, int b) = _edges[i];
                    AddAdjacency(adjacency, a, b, i);
                    AddAdjacency(adjacency, b, a, i);
                }

                return adjacency;
            }

            private static void AddAdjacency(
                Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency,
                int from,
                int to,
                int edgeIndex)
            {
                if (!adjacency.TryGetValue(from, out List<(int, int)> list))
                {
                    list = new List<(int, int)>();
                    adjacency[from] = list;
                }

                list.Add((to, edgeIndex));
            }

            private void ExtendChain(
                List<XYZ> chain,
                int tipVertex,
                bool forward,
                Dictionary<int, List<(int Neighbor, int EdgeIndex)>> adjacency,
                bool[] usedEdges,
                double maxSegmentLength)
            {
                int current = tipVertex;
                XYZ incoming = forward
                    ? chain[chain.Count - 2]
                    : chain[1];
                XYZ tip = _vertices[current];

                while (true)
                {
                    if (!adjacency.TryGetValue(current, out List<(int Neighbor, int EdgeIndex)> links))
                        break;

                    XYZ direction = (tip - incoming).Normalize();
                    (int bestNeighbor, int bestEdge) = (-1, -1);
                    double bestScore = double.MinValue;

                    foreach ((int neighbor, int edgeIndex) in links)
                    {
                        if (usedEdges[edgeIndex])
                            continue;

                        double length = _vertices[current].DistanceTo(_vertices[neighbor]);
                        if (length > maxSegmentLength)
                            continue;

                        XYZ outgoing = (_vertices[neighbor] - _vertices[current]).Normalize();
                        double score = direction.DotProduct(outgoing);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestNeighbor = neighbor;
                            bestEdge = edgeIndex;
                        }
                    }

                    if (bestNeighbor < 0)
                        break;

                    usedEdges[bestEdge] = true;
                    incoming = _vertices[current];
                    current = bestNeighbor;
                    tip = _vertices[current];

                    if (forward)
                        chain.Add(tip);
                    else
                        chain.Insert(0, tip);
                }
            }

            private int GetOrAddVertex(XYZ point)
            {
                string key = BuildSnapKey(point, _snapTolerance);
                if (_vertexIndex.TryGetValue(key, out int index))
                    return index;

                index = _vertices.Count;
                _vertices.Add(new XYZ(point.X, point.Y, point.Z));
                _vertexIndex[key] = index;
                return index;
            }
        }

        private static List<XYZ> RemoveNearDuplicateVertices(IList<XYZ> chain, double tolerance)
        {
            var cleaned = new List<XYZ>();
            foreach (XYZ point in chain)
            {
                if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].DistanceTo(point) >= tolerance)
                    cleaned.Add(point);
            }

            if (cleaned.Count >= 3)
            {
                XYZ first = cleaned[0];
                XYZ last = cleaned[cleaned.Count - 1];
                if (first.DistanceTo(last) < tolerance)
                    cleaned.RemoveAt(cleaned.Count - 1);
            }

            return cleaned;
        }
    }
#endif
}
