using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>Render mesh built from TerrainModifier vertex output (not a separate algorithm).</summary>
    internal sealed class TerrainMesh
    {
        public List<XYZ> Positions = new();
        public List<int> TriangleIndices = new();
        public List<XYZ> LineSegments = new();
        public Outline Bounds;
        public int TopSurfaceTriangles;
        public int DeltaWallTriangles;
    }

    internal static class TerrainMeshBuilder
    {
        private const double SlabSearchRadius = 80.0;
        private const double ZDeltaThreshold = 0.005;
        private const double BoundsMargin = 2.0;

        public static TerrainMesh BuildBrushOverlay(
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IReadOnlyList<TerrainModifier.StampDefinition> stamps)
        {
            var mesh = new TerrainMesh();
            AddBrushRings(mesh, workingVertices, stamps);
            mesh.Bounds = ComputeBounds(mesh);
            return mesh;
        }

        public static TerrainMesh Build(
            ModifyTopoGeometrySurfaceCache displayTopology,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IReadOnlyList<TerrainModifier.StampDefinition> stamps)
        {
            var mesh = new TerrainMesh();
            if (baseVertices == null || workingVertices == null)
                return mesh;

            var indexMap = new Dictionary<string, int>(StringComparer.Ordinal);

            if (stamps != null && stamps.Count > 0)
            {
                foreach (TerrainModifier.StampDefinition stamp in stamps)
                    BuildStampVolume(mesh, indexMap, baseVertices, workingVertices, stamp);
            }

            if (mesh.TriangleIndices.Count < 9 && displayTopology != null)
                AppendDisplayMeshDeformation(mesh, indexMap, displayTopology, baseVertices, workingVertices, stamps);

            AddBrushRings(mesh, workingVertices, stamps);
            mesh.Bounds = ComputeBounds(mesh);

            Log.Information(
                "Terrain preview mesh: {TopTris} top tris, {WallTris} wall tris, {Verts} verts, {Lines} line pts",
                mesh.TopSurfaceTriangles, mesh.DeltaWallTriangles,
                mesh.Positions.Count, mesh.LineSegments.Count);

            return mesh;
        }

        /// <summary>Fan TIN + vertical delta walls from calculated slab vertices.</summary>
        private static void BuildStampVolume(
            TerrainMesh mesh,
            Dictionary<string, int> indexMap,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            TerrainModifier.StampDefinition stamp)
        {
            if (stamp?.Center == null || stamp.Options == null)
                return;

            double radius = Math.Max(stamp.Options.ShapeRadiusFeet, 0.5);
            XYZ center = stamp.Center;

            var regionVerts = workingVertices
                .Where(v => ModifyTopoService.HorizontalDistance(v.X, v.Y, center.X, center.Y) <= radius + 0.5)
                .OrderBy(v => Math.Atan2(v.Y - center.Y, v.X - center.X))
                .ToList();

            if (regionVerts.Count < 3)
                return;

            double hubZ = ModifyTopoService.InterpolateSurfaceZ(
                    workingVertices, center.X, center.Y, radius)
                ?? center.Z;
            int hubIdx = AddVertex(mesh, indexMap, new XYZ(center.X, center.Y, hubZ));

            var ringIndices = new List<int>(regionVerts.Count);
            foreach (ModifyTopoService.SculptVertexSnapshot v in regionVerts)
            {
                ringIndices.Add(AddVertex(mesh, indexMap, new XYZ(v.X, v.Y, v.Z)));
            }

            for (int i = 0; i < ringIndices.Count; i++)
            {
                int i0 = ringIndices[i];
                int i1 = ringIndices[(i + 1) % ringIndices.Count];
                AddTriangle(mesh, hubIdx, i0, i1, isTop: true);

                ModifyTopoService.SculptVertexSnapshot v0 = regionVerts[i];
                ModifyTopoService.SculptVertexSnapshot v1 = regionVerts[(i + 1) % regionVerts.Count];
                double baseZ0 = GetBaseZ(baseVertices, v0.X, v0.Y, v0.Z);
                double baseZ1 = GetBaseZ(baseVertices, v1.X, v1.Y, v1.Z);

                if (Math.Abs(v0.Z - baseZ0) < ZDeltaThreshold && Math.Abs(v1.Z - baseZ1) < ZDeltaThreshold)
                    continue;

                int b0 = AddVertex(mesh, indexMap, new XYZ(v0.X, v0.Y, baseZ0));
                int b1 = AddVertex(mesh, indexMap, new XYZ(v1.X, v1.Y, baseZ1));
                AddTriangle(mesh, b0, b1, i1, isTop: false);
                AddTriangle(mesh, b0, i1, i0, isTop: false);
            }
        }

        private static double GetBaseZ(
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            double x, double y, double fallback)
        {
            return ModifyTopoService.InterpolateSurfaceZ(baseVertices, x, y, SlabSearchRadius)
                ?? fallback;
        }

        private static void AppendDisplayMeshDeformation(
            TerrainMesh mesh,
            Dictionary<string, int> indexMap,
            ModifyTopoGeometrySurfaceCache displayTopology,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IReadOnlyList<TerrainModifier.StampDefinition> stamps)
        {
            GetStampBounds(stamps, out double minX, out double minY, out double maxX, out double maxY);
            minX -= BoundsMargin;
            minY -= BoundsMargin;
            maxX += BoundsMargin;
            maxY += BoundsMargin;

            foreach (ModifyTopoGeometrySurfaceCache.SurfaceTriangle tri in
                     displayTopology.GetTrianglesInBounds(minX, minY, maxX, maxY))
            {
                if (!TryDeformCorner(tri.V0, baseVertices, workingVertices, out XYZ d0, out double delta0) ||
                    !TryDeformCorner(tri.V1, baseVertices, workingVertices, out XYZ d1, out double delta1) ||
                    !TryDeformCorner(tri.V2, baseVertices, workingVertices, out XYZ d2, out double delta2))
                    continue;

                double maxDelta = Math.Max(Math.Abs(delta0), Math.Max(Math.Abs(delta1), Math.Abs(delta2)));
                if (maxDelta < ZDeltaThreshold)
                    continue;

                int i0 = AddVertex(mesh, indexMap, d0);
                int i1 = AddVertex(mesh, indexMap, d1);
                int i2 = AddVertex(mesh, indexMap, d2);
                AddTriangle(mesh, i0, i1, i2, isTop: true);
            }
        }

        private static bool TryDeformCorner(
            XYZ meshVertex,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            out XYZ deformed,
            out double deltaZ)
        {
            deformed = meshVertex;
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
            deformed = new XYZ(meshVertex.X, meshVertex.Y, meshVertex.Z + deltaZ);
            return true;
        }

        private static void AddTriangle(TerrainMesh mesh, int i0, int i1, int i2, bool isTop)
        {
            mesh.TriangleIndices.Add(i0);
            mesh.TriangleIndices.Add(i1);
            mesh.TriangleIndices.Add(i2);
            if (isTop)
                mesh.TopSurfaceTriangles++;
            else
                mesh.DeltaWallTriangles++;
        }

        private static int AddVertex(TerrainMesh mesh, Dictionary<string, int> indexMap, XYZ pt)
        {
            string key = $"{Math.Round(pt.X, 4)}:{Math.Round(pt.Y, 4)}:{Math.Round(pt.Z, 4)}";
            if (indexMap.TryGetValue(key, out int existing))
                return existing;

            int index = mesh.Positions.Count;
            mesh.Positions.Add(pt);
            indexMap[key] = index;
            return index;
        }

        private static void AddBrushRings(
            TerrainMesh mesh,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> workingVertices,
            IReadOnlyList<TerrainModifier.StampDefinition> stamps)
        {
            if (stamps == null) return;

            foreach (TerrainModifier.StampDefinition stamp in stamps)
            {
                if (stamp?.Center == null || stamp.Options == null)
                    continue;

                double radius = Math.Max(stamp.Options.ShapeRadiusFeet, 0.5);
                double ringZ = ModifyTopoService.InterpolateSurfaceZ(
                        workingVertices, stamp.Center.X, stamp.Center.Y, radius)
                    ?? stamp.Center.Z;
                ringZ += stamp.Options.ShapeDeltaFeet * 0.15;

                const int segments = 48;
                XYZ prev = null;
                for (int i = 0; i <= segments; i++)
                {
                    double a = 2.0 * Math.PI * i / segments;
                    var pt = new XYZ(
                        stamp.Center.X + radius * Math.Cos(a),
                        stamp.Center.Y + radius * Math.Sin(a),
                        ringZ + 0.25);
                    if (prev != null)
                    {
                        mesh.LineSegments.Add(prev);
                        mesh.LineSegments.Add(pt);
                    }
                    prev = pt;
                }
            }
        }

        private static void GetStampBounds(
            IReadOnlyList<TerrainModifier.StampDefinition> stamps,
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
                foreach (TerrainModifier.StampDefinition stamp in stamps)
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

        private static Outline ComputeBounds(TerrainMesh mesh)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            void Include(XYZ p)
            {
                if (p == null) return;
                any = true;
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
                maxZ = Math.Max(maxZ, p.Z);
            }

            foreach (XYZ p in mesh.Positions)
                Include(p);
            foreach (XYZ p in mesh.LineSegments)
                Include(p);

            if (!any)
                return new Outline(XYZ.Zero, XYZ.Zero);

            const double pad = 1.0;
            return new Outline(
                new XYZ(minX - pad, minY - pad, minZ - pad),
                new XYZ(maxX + pad, maxY + pad, maxZ + pad));
        }
    }
#endif
}
