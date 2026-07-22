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
            /// <summary>Survey elevation at pick (Elevation Base = Survey Point), when known.</summary>
            public double? PickSurveyElevationFt;
        }

        public sealed class CalculateResult
        {
            public List<ModifyTopoService.SculptVertexSnapshot> Vertices = new();
            public TerrainMesh Mesh = new();
            public IList<GeometryObject> PreviewSolids = Array.Empty<GeometryObject>();
            public int TotalPointsAdded;
            public int TotalVerticesModified;
        }

        public sealed class LineDefinition
        {
            public IList<XYZ> SamplePoints;
        }

        /// <summary>
        /// Apply shape-by-line sample points sequentially on base vertices and build preview geometry.
        /// </summary>
        public static CalculateResult CalculateWithLines(
            Document doc,
            Toposolid toposolid,
            IReadOnlyList<ModifyTopoService.SculptVertexSnapshot> baseVertices,
            IReadOnlyList<LineDefinition> lines)
        {
            if (baseVertices == null)
                throw new ArgumentNullException(nameof(baseVertices));

            var working = CloneVertices(baseVertices);
            int totalAdded = 0;
            int totalModified = 0;

            if (lines != null)
            {
                foreach (LineDefinition line in lines)
                {
                    if (line?.SamplePoints == null || line.SamplePoints.Count == 0)
                        continue;

                    StampResult step = ApplyShapeByLineVertices(working, line.SamplePoints);
                    working = step.Vertices;
                    totalAdded += step.PointsAdded;
                    totalModified += step.VerticesModified;
                }
            }

            IList<GeometryObject> previewSolids = ToposolidPreviewGeometrySampler.SampleExactSolid(
                doc, toposolid, baseVertices.ToList(), working);

            return new CalculateResult
            {
                Vertices = working,
                Mesh = new TerrainMesh(),
                PreviewSolids = previewSolids,
                TotalPointsAdded = totalAdded,
                TotalVerticesModified = totalModified
            };
        }

        /// <summary>In-memory shape-by-line — sets Z from sample points along curves.</summary>
        public static StampResult ApplyShapeByLineVertices(
            IList<ModifyTopoService.SculptVertexSnapshot> vertices,
            IList<XYZ> samplePoints)
        {
            if (vertices == null || samplePoints == null)
                throw new ArgumentNullException();

            var working = CloneVertices(vertices);
            int pointsAdded = 0;
            int verticesModified = 0;
            const double xyTol = 0.15;
            const double zTolerance = 1e-6;

            foreach (XYZ point in samplePoints)
            {
                ModifyTopoService.SculptVertexSnapshot existing = working
                    .FirstOrDefault(v => HorizontalDistance(v.X, v.Y, point.X, point.Y) < xyTol);

                if (existing != null)
                {
                    if (Math.Abs(existing.Z - point.Z) > zTolerance)
                        verticesModified++;
                    existing.Z = point.Z;
                    continue;
                }

                working.Add(new ModifyTopoService.SculptVertexSnapshot
                {
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z
                });
                pointsAdded++;
            }

            return new StampResult
            {
                Vertices = working,
                PointsAdded = pointsAdded,
                VerticesModified = verticesModified
            };
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
                        doc, toposolid, working, stamp.Center, stamp.Options, displayTopology,
                        stamp.PickSurveyElevationFt);
                    working = step.Vertices;
                    totalAdded += step.PointsAdded;
                    totalModified += step.VerticesModified;
                }
            }

            IList<GeometryObject> previewSolids = ToposolidPreviewGeometrySampler.SampleExactSolid(
                doc, toposolid, baseVertices.ToList(), working);

            return new CalculateResult
            {
                Vertices = working,
                Mesh = TerrainMeshBuilder.BuildBrushOverlay(
                    workingVertices: working,
                    stamps,
                    displayTopology,
                    doc,
                    toposolid),
                PreviewSolids = previewSolids,
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
            ModifyTopoOptions options,
            ModifyTopoGeometrySurfaceCache displayTopology = null,
            double? pickSurveyElevationFt = null)
        {
            if (vertices == null || center == null || options == null)
                throw new ArgumentNullException();

            var working = CloneVertices(vertices);
            int countBefore = working.Count;
            const double xyTol = 0.15;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);

            // Nearest existing slab vertex within stamp radius (for on-vertex picks only).
            double? centerAnchorZ = ModifyTopoService.TryGetNearestSlabVertexZ(
                vertices, center.X, center.Y, xyTol);
            var survey = new RevitAlongSurfaceSampler.SurveyCoordinateHelper(doc);
            double pickSurfaceModelZ = center.Z;

            var addPoints = ModifyTopoService.ComputeShapeByPointAddPreviewPoints(
                doc, toposolid, center, options, working, displayTopology);
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

            int verticesModified = 0;
            const double zTolerance = 1e-6;
            double centerDist = double.MaxValue;
            double centerBeforeModel = 0;
            double centerAfterModel = 0;
            double centerBeforeSurvey = 0;
            double centerAfterSurvey = 0;
            double centerBaseModel = 0;

            foreach (ModifyTopoService.SculptVertexSnapshot v in working)
            {
                double dist = HorizontalDistance(v.X, v.Y, center.X, center.Y);
                if (dist > radius) continue;

                double w = ModifyTopoService.ComputeFalloff(dist / radius, options.ShapeFalloff);

                double? alongSurfaceZ = RevitAlongSurfaceSampler.GetAlongSurfaceModelZ(
                    doc, toposolid, displayTopology, vertices, null, v.X, v.Y, radius);
                double baseZ = alongSurfaceZ ?? v.Z;
                if (dist < xyTol && centerAnchorZ.HasValue)
                    baseZ = centerAnchorZ.Value;
                if (dist < xyTol)
                    baseZ = pickSurfaceModelZ;

                double beforeModel = v.Z;
                double newZ;
                if (pickSurveyElevationFt.HasValue && survey.IsAvailable && dist < xyTol)
                {
                    double targetSurvey = pickSurveyElevationFt.Value + options.ShapeDeltaFeet * w;
                    newZ = survey.SurveyElevationToModelZ(v.X, v.Y, targetSurvey);
                }
                else
                {
                    newZ = RevitAlongSurfaceSampler.ApplyAlongSurfaceGain(
                        doc, v.X, v.Y, baseZ, options.ShapeDeltaFeet, w, survey);
                }

                if (dist < centerDist)
                {
                    centerDist = dist;
                    centerBeforeModel = beforeModel;
                    centerAfterModel = newZ;
                    centerBaseModel = baseZ;
                    if (survey.IsAvailable)
                    {
                        centerBeforeSurvey = survey.ModelZToSurveyElevation(v.X, v.Y, beforeModel);
                        centerAfterSurvey = survey.ModelZToSurveyElevation(v.X, v.Y, newZ);
                    }
                }

                if (Math.Abs(newZ - v.Z) > zTolerance)
                    verticesModified++;
                v.Z = newZ;
            }

            if (pickSurveyElevationFt.HasValue && survey.IsAvailable)
            {
                double pickTargetSurvey = pickSurveyElevationFt.Value + options.ShapeDeltaFeet;
                double pickTargetModel = survey.SurveyElevationToModelZ(
                    center.X, center.Y, pickTargetSurvey);
                Log.Information(
                    "Shape stamp at pick: survey {Pick:F3} + gain {Gain:F3} = {Target:F3} ft (model Z {ModelPick:F3} -> {ModelTarget:F3} ft)",
                    pickSurveyElevationFt.Value,
                    options.ShapeDeltaFeet,
                    pickTargetSurvey,
                    pickSurfaceModelZ,
                    pickTargetModel);
            }

            if (centerDist < double.MaxValue && survey.IsAvailable)
            {
                double anchorSurvey = pickSurveyElevationFt
                    ?? survey.ModelZToSurveyElevation(center.X, center.Y, centerBaseModel);
                Log.Information(
                    "Shape stamp center: survey {Before:F3} -> {After:F3} ft (pick/anchor {Anchor:F3}, gain {Gain:F3} ft, dist {Dist:F3} ft); model Z {ModelBefore:F3} -> {ModelAfter:F3} ft",
                    centerBeforeSurvey,
                    centerAfterSurvey,
                    anchorSurvey,
                    options.ShapeDeltaFeet,
                    centerDist,
                    centerBeforeModel,
                    centerAfterModel);
            }
            else if (centerDist < double.MaxValue)
            {
                Log.Information(
                    "Shape stamp center: model Z {Before:F3} -> {After:F3} ft (base {Base:F3}, gain {Gain:F3} ft, dist {Dist:F3} ft)",
                    centerBeforeModel, centerAfterModel, centerBaseModel, options.ShapeDeltaFeet, centerDist);
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
