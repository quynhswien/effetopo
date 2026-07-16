using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Single source of truth for terrain deformation math.
    /// Preview and Toposolid commit both consume the same Calculate() output.
    /// </summary>
    internal static class TerrainModifier
    {
        public sealed class StampDefinition
        {
            public XYZ Center;
            public ModifyTopoOptions Options;
        }

        public sealed class CalculateResult
        {
            public List<ModifyTopoService.SculptVertexSnapshot> Vertices = new();
            public TerrainMesh Mesh = new();
            public int TotalPointsAdded;
            public int TotalVerticesModified;
        }

        /// <summary>
        /// Apply all stamps sequentially on base vertices and build the preview mesh.
        /// </summary>
        public static CalculateResult Calculate(
            Document doc,
            Toposolid toposolid,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<StampDefinition> stamps,
            ModifyTopoGeometrySurfaceCache displayTopology = null)
        {
            if (baseVertices == null)
                throw new ArgumentNullException(nameof(baseVertices));

            var working = CloneVertices(baseVertices);
            int totalAdded = 0;
            int totalModified = 0;

            if (stamps != null)
            {
                foreach (StampDefinition stamp in stamps)
                {
                    if (stamp?.Center == null || stamp.Options == null)
                        continue;

                    StampResult step = ApplyShapeByPointStamp(
                        doc, toposolid, working, stamp.Center, stamp.Options);
                    working = step.Vertices;
                    totalAdded += step.PointsAdded;
                    totalModified += step.VerticesModified;
                }
            }

            return new CalculateResult
            {
                Vertices = working,
                Mesh = TerrainMeshBuilder.Build(displayTopology, baseVertices, working, stamps),
                TotalPointsAdded = totalAdded,
                TotalVerticesModified = totalModified
            };
        }

        /// <summary>One shape-by-point stamp — used by preview simulation and Apply.</summary>
        public static StampResult ApplyShapeByPointStamp(
            Document doc,
            Toposolid toposolid,
            IList<ModifyTopoService.SculptVertexSnapshot> vertices,
            XYZ center,
            ModifyTopoOptions options)
        {
            if (vertices == null || center == null || options == null)
                throw new ArgumentNullException();

            var working = CloneVertices(vertices);
            int countBefore = working.Count;
            const double xyTol = 0.15;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);

            var addPoints = ModifyTopoService.ComputeShapeByPointAddPreviewPoints(
                doc, toposolid, center, options, working);
            foreach (XYZ pt in addPoints)
            {
                bool exists = working.Any(v => HorizontalDistance(v.X, v.Y, pt.X, pt.Y) < xyTol);
                if (exists) continue;
                working.Add(new ModifyTopoService.SculptVertexSnapshot
                {
                    X = pt.X,
                    Y = pt.Y,
                    Z = pt.Z
                });
            }

            int pointsAdded = working.Count - countBefore;

            // Sample center Z after add-points on the same vertex set used for deformation.
            double centerOriginalZ = ModifyTopoService.GetModelSurfaceZ(
                    doc, toposolid, working, center.X, center.Y, radius)
                ?? center.Z;
            double targetZ = centerOriginalZ + options.ShapeDeltaFeet;
            int verticesModified = 0;
            const double zTolerance = 1e-6;

            foreach (ModifyTopoService.SculptVertexSnapshot v in working)
            {
                double dist = HorizontalDistance(v.X, v.Y, center.X, center.Y);
                if (dist > radius) continue;

                double w = ModifyTopoService.ComputeFalloff(dist / radius, options.ShapeFalloff);
                double newZ = v.Z + (targetZ - centerOriginalZ) * w;
                if (Math.Abs(newZ - v.Z) > zTolerance)
                    verticesModified++;
                v.Z = newZ;
            }

            return new StampResult
            {
                Vertices = working,
                PointsAdded = pointsAdded,
                VerticesModified = verticesModified
            };
        }

        public sealed class StampResult
        {
            public List<ModifyTopoService.SculptVertexSnapshot> Vertices;
            public int PointsAdded;
            public int VerticesModified;
        }

        internal static List<ModifyTopoService.SculptVertexSnapshot> CloneVertices(
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> source)
        {
            return source?
                .Select(v => new ModifyTopoService.SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList() ?? new List<ModifyTopoService.SculptVertexSnapshot>();
        }

        private static double HorizontalDistance(double x1, double y1, double x2, double y2) =>
            ModifyTopoService.HorizontalDistance(x1, y1, x2, y2);
    }
#endif
}
