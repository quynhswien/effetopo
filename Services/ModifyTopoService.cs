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

            var state = CreateSculptState(doc, toposolid, editor);

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
                case ModifyTopoTool.ShapeByLine:
                    throw new InvalidOperationException("Shape by Line requires a selected model curve.");
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
            TerrainModifier.StampResult result = TerrainModifier.ApplyShapeByPointStamp(
                doc, toposolid, vertices, center, options);
            return new SimulateShapeByPointResult
            {
                Vertices = result.Vertices,
                PointsAdded = result.PointsAdded,
                VerticesModified = result.VerticesModified
            };
        }

        /// <summary>
        /// Apply pre-calculated vertex state to Toposolid (same data used for preview).
        /// </summary>
        public ModifyTopoResult ApplyCalculatedVertices(
            Document doc,
            Toposolid toposolid,
            IList<SculptVertexSnapshot> baseVertices,
            IList<SculptVertexSnapshot> targetVertices,
            bool logResult = true,
            XYZ stampPickCenter = null,
            double? stampPickSurveyFt = null,
            double stampGainFeet = 0)
        {
            if (doc == null || toposolid == null || baseVertices == null || targetVertices == null)
                throw new ArgumentNullException();

            SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
            if (editor == null)
                throw new InvalidOperationException("Could not access SlabShapeEditor on Toposolid.");

            editor.Enable();
            var elevation = new TopoSurveyElevationContext(doc, toposolid, editor);

            var vertices = CollectVertices(doc, toposolid, editor, elevation);
            int originalCount = baseVertices.Count;
            var state = new SculptState(doc, toposolid, vertices, elevation);

            const double xyTol = 0.15;
            int pointsAdded = 0;

            var addTargets = DeduplicateTargetsByXY(targetVertices, xyTol);

            foreach (SculptVertexSnapshot target in addTargets)
            {
                bool exists = state.Vertices.Any(v =>
                    HorizontalDistance(v.X, v.Y, target.X, target.Y) < xyTol);
                if (exists) continue;

                if (!TrySetTopoVertexModelZ(editor, target.X, target.Y, target.Z))
                    continue;

                doc.Regenerate();
                SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, target.X, target.Y, xyTol);
                if (vertex == null) continue;

                doc.Regenerate();
                XYZ pos = vertex.Position;
                double modelZ = SlabPositionToModelZ(doc, toposolid, elevation, pos.Z);
                state.PointsAdded++;
                pointsAdded++;
                state.Vertices.Add(new SculptVertex
                {
                    RevitVertex = vertex,
                    X = pos.X,
                    Y = pos.Y,
                    Z = modelZ,
                    OriginalZ = modelZ
                });
            }

            if (pointsAdded > 0)
            {
                doc.Regenerate();
                RefreshRevitVertices(editor, state, elevation);
            }

            const double zTolerance = 1e-6;
            foreach (SculptVertex v in state.Vertices)
            {
                SculptVertexSnapshot bestTarget = null;
                double bestDist = xyTol;
                foreach (SculptVertexSnapshot t in targetVertices)
                {
                    double d = HorizontalDistance(v.X, v.Y, t.X, t.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestTarget = t;
                    }
                }

                if (bestTarget != null && Math.Abs(bestTarget.Z - v.Z) > zTolerance)
                    v.Z = bestTarget.Z;
            }

            int modified = WriteVertexZChanges(
                editor, state, elevation, logResult, stampPickCenter, stampPickSurveyFt, stampGainFeet);

            if (stampPickCenter != null && stampPickSurveyFt.HasValue)
            {
                double pickTargetModelZ = elevation.SurveyElevationToModelZ(
                    stampPickCenter.X, stampPickCenter.Y, stampPickSurveyFt.Value + stampGainFeet);
                if (TrySetTopoVertexModelZ(
                        editor, stampPickCenter.X, stampPickCenter.Y, pickTargetModelZ))
                    modified++;
            }

            doc.Regenerate();

            if (logResult && stampPickCenter != null && stampPickSurveyFt.HasValue)
            {
                SlabShapeVertex pickVertex = FindSlabShapeVertexNearXY(
                    editor, stampPickCenter.X, stampPickCenter.Y, 0.15);
                if (pickVertex != null)
                {
                    double readModelZ = SlabPositionToModelZ(
                        doc, toposolid, elevation, pickVertex.Position.Z);
                    double readSurvey = elevation.ModelZToSurveyElevation(
                        stampPickCenter.X, stampPickCenter.Y, readModelZ);
                    Log.Information(
                        "Commit verify after regenerate: survey {Read:F3} ft (target {Target:F3} ft)",
                        readSurvey,
                        stampPickSurveyFt.Value + stampGainFeet);
                }
            }

            doc.Regenerate();
            int afterCount = CountSlabShapeVertices(toposolid);

            var result = new ModifyTopoResult
            {
                OriginalPointCount = originalCount,
                PointsAfterModification = afterCount,
                VerticesModified = modified,
                PointsAdded = pointsAdded,
                PointsRemoved = 0,
                Summary = BuildSummary(
                    new ModifyTopoOptions { Tool = ModifyTopoTool.ShapeByPoint },
                    modified, pointsAdded, 0, afterCount)
            };
            LastResult = result;
            if (logResult)
                Log.Information("ModifyTopo (from preview mesh): {Summary}", result.Summary);
            return result;
        }

        public ModifyTopoResult ApplyShapeByLine(
            Document doc,
            Toposolid toposolid,
            Curve curve,
            ModifyTopoOptions options)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (toposolid == null) throw new ArgumentNullException(nameof(toposolid));
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var samplingOptions = new FloorBoundarySamplingOptions
            {
                Mode = options.LineSampleMode,
                SpacingFeet = options.LineSpacingFeet,
                SegmentsPerCurve = options.LineSegmentsPerCurve
            };

            List<XYZ> samplePoints = CurvePointSampler.Sample(curve, samplingOptions);
            if (samplePoints.Count == 0)
                throw new InvalidOperationException("No sample points could be generated along the selected line.");

            SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
            if (editor == null)
                throw new InvalidOperationException("Could not access SlabShapeEditor on Toposolid.");

            editor.Enable();

            var vertices = CollectVertices(doc, toposolid, editor);
            int originalCount = vertices.Count;
            if (originalCount == 0)
                throw new InvalidOperationException("Toposolid has no SlabShape vertices to modify.");

            var state = CreateSculptState(doc, toposolid, editor);

            const double xyTol = 0.15;
            var rawSnapshots = state.Vertices
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();

            foreach (XYZ point in samplePoints)
            {
                SculptVertex existing = state.Vertices
                    .FirstOrDefault(v => HorizontalDistance(v.X, v.Y, point.X, point.Y) < xyTol);

                if (existing != null)
                {
                    existing.Z = point.Z;
                    continue;
                }

                if (!TrySetTopoVertexModelZ(editor, point.X, point.Y, point.Z))
                    continue;

                doc.Regenerate();
                SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, point.X, point.Y, xyTol);
                if (vertex == null)
                    continue;

                double modelZ = SlabPositionToModelZ(doc, toposolid, state.Elevation, vertex.Position.Z);
                state.PointsAdded++;
                state.Vertices.Add(new SculptVertex
                {
                    RevitVertex = vertex,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    OriginalZ = modelZ
                });
                rawSnapshots.Add(new SculptVertexSnapshot { X = point.X, Y = point.Y, Z = point.Z });
            }

            if (state.PointsAdded > 0)
            {
                doc.Regenerate();
                RefreshRevitVertices(editor, state);
            }

            int modified = WriteVertexZChanges(editor, state);
            doc.Regenerate();
            int afterCount = CountSlabShapeVertices(toposolid);

            var result = new ModifyTopoResult
            {
                OriginalPointCount = originalCount,
                PointsAfterModification = afterCount,
                VerticesModified = modified,
                PointsAdded = state.PointsAdded,
                PointsRemoved = 0,
                Summary = BuildSummary(
                    new ModifyTopoOptions { Tool = ModifyTopoTool.ShapeByLine },
                    modified,
                    state.PointsAdded,
                    0,
                    afterCount)
            };
            LastResult = result;
            Log.Information(
                "Shape by Line: sampled {SampleCount} points, {Summary}",
                samplePoints.Count,
                result.Summary);
            return result;
        }

        public ModifyTopoResult ApplyShapeByLines(
            Document doc,
            Toposolid toposolid,
            IEnumerable<Curve> curves,
            ModifyTopoOptions options)
        {
            if (curves == null)
                throw new ArgumentNullException(nameof(curves));

            ModifyTopoResult last = null;
            foreach (Curve curve in curves)
            {
                if (curve == null)
                    continue;
                last = ApplyShapeByLine(doc, toposolid, curve, options);
            }

            return last ?? new ModifyTopoResult
            {
                OriginalPointCount = CountSlabShapeVertices(toposolid),
                PointsAfterModification = CountSlabShapeVertices(toposolid),
                Summary = "No curves applied."
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
                ModifyTopoTool.ShapeByLine => "Shape by Line",
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
            public TopoSurveyElevationContext Elevation;
            public int PointsAdded;
            public int PointsRemoved;

            public SculptState(
                Document doc,
                Toposolid toposolid,
                List<SculptVertex> vertices,
                TopoSurveyElevationContext elevation)
            {
                Doc = doc;
                Toposolid = toposolid;
                Vertices = vertices;
                Elevation = elevation;
            }

            public double ModelZToSlabOffset(double modelZ) =>
                Elevation.ModelZToSlabOffset(modelZ);
        }

        /// <summary>
        /// Survey Point slab read/write — same frame as ToposolidMergeService FloorSurveyElevationContext.
        /// </summary>
        private sealed class TopoSurveyElevationContext
        {
            private readonly RevitAlongSurfaceSampler.SurveyCoordinateHelper _survey;
            private readonly double _referencePlaneModelZ;

            public TopoSurveyElevationContext(
                Document doc,
                Toposolid toposolid,
                SlabShapeEditor editor)
            {
                _survey = new RevitAlongSurfaceSampler.SurveyCoordinateHelper(doc);
                _referencePlaneModelZ = CalibrateToposolidReferencePlaneModelZ(doc, toposolid, editor);
            }

            public bool SurveyAvailable => _survey.IsAvailable;
            public double ReferencePlaneModelZ => _referencePlaneModelZ;

            public double SlabOffsetToModelZ(double slabOffsetFt) =>
                _referencePlaneModelZ + slabOffsetFt;

            public double ModelZToSlabOffset(double modelZ) =>
                modelZ - _referencePlaneModelZ;

            public double SlabOffsetToSurveyElevation(double x, double y, double slabOffsetFt)
            {
                double modelZ = SlabOffsetToModelZ(slabOffsetFt);
                return _survey.IsAvailable
                    ? _survey.ModelZToSurveyElevation(x, y, modelZ)
                    : modelZ;
            }

            public double SurveyElevationToSlabOffset(double x, double y, double surveyElevationFt)
            {
                double targetModelZ = _survey.IsAvailable
                    ? _survey.SurveyElevationToModelZ(x, y, surveyElevationFt)
                    : surveyElevationFt;
                return targetModelZ - _referencePlaneModelZ;
            }

            public double ModelZToSurveyElevation(double x, double y, double modelZ) =>
                _survey.IsAvailable
                    ? _survey.ModelZToSurveyElevation(x, y, modelZ)
                    : modelZ;

            public double SurveyElevationToModelZ(double x, double y, double surveyElevationFt) =>
                _survey.IsAvailable
                    ? _survey.SurveyElevationToModelZ(x, y, surveyElevationFt)
                    : surveyElevationFt;
        }

        /// <summary>
        /// Toposolid SlabShapeEditor uses absolute model Z in AddPoint (see ToposolidMergeService).
        /// Do not average vertex Position.Z — those are surface elevations, not the reference plane.
        /// </summary>
        private static double CalibrateToposolidReferencePlaneModelZ(
            Document doc, Toposolid toposolid, SlabShapeEditor editor) =>
            GetReferencePlaneModelZ(doc, toposolid);

        /// <summary>Write elevation via AddPoint at model Z — matches Toposolid merge / excavate path.</summary>
        private static bool TrySetTopoVertexModelZ(
            SlabShapeEditor editor, double x, double y, double targetModelZ)
        {
            if (editor == null)
                return false;

            return SlabShapeEditorHelper.TryAddPoint(editor, new XYZ(x, y, targetModelZ));
        }

        private static double TargetModelZToSlabOffset(
            TopoSurveyElevationContext elevation, double x, double y, double targetModelZ)
        {
            if (elevation.SurveyAvailable)
            {
                double targetSurvey = elevation.ModelZToSurveyElevation(x, y, targetModelZ);
                return elevation.SurveyElevationToSlabOffset(x, y, targetSurvey);
            }

            return elevation.ModelZToSlabOffset(targetModelZ);
        }

        private static SculptState CreateSculptState(
            Document doc, Toposolid toposolid, SlabShapeEditor editor)
        {
            var elevation = new TopoSurveyElevationContext(doc, toposolid, editor);
            var vertices = CollectVertices(doc, toposolid, editor, elevation);
            return new SculptState(doc, toposolid, vertices, elevation);
        }

        private static double TargetModelZToSlabOffset(
            SculptState state, double x, double y, double targetModelZ) =>
            TargetModelZToSlabOffset(state.Elevation, x, y, targetModelZ);

        private static int WriteVertexZChanges(SlabShapeEditor editor, SculptState state) =>
            WriteVertexZChanges(editor, state, state.Elevation, false, null, null, 0);

        private static List<SculptVertex> CollectVertices(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor,
            TopoSurveyElevationContext elevation = null)
        {
            elevation ??= new TopoSurveyElevationContext(doc, toposolid, editor);
            var list = new List<SculptVertex>();
            if (editor?.SlabShapeVertices == null) return list;

            foreach (SlabShapeVertex v in editor.SlabShapeVertices)
            {
                if (v?.Position == null) continue;
                XYZ p = v.Position;
                double modelZ = SlabPositionToModelZ(doc, toposolid, elevation, p.Z);
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

        /// <summary>
        /// SlabShape Position.Z → model Z. Toposolid may store model Z in bbox range, else slab offset.
        /// </summary>
        private static double SlabPositionToModelZ(
            Document doc, Toposolid toposolid, TopoSurveyElevationContext elevation, double slabPositionZ)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb != null &&
                    slabPositionZ >= bb.Min.Z - 2.0 &&
                    slabPositionZ <= bb.Max.Z + 2.0)
                    return slabPositionZ;
            }
            catch { }

            return elevation.SlabOffsetToModelZ(slabPositionZ);
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

        /// <summary>Toposolid reference plane in model coordinates (level + height above level).</summary>
        public static double GetReferencePlaneModelZ(Document doc, Toposolid toposolid) =>
            GetLevelElevation(doc, toposolid) + GetHeightOffsetFromLevel(toposolid);

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

        private static List<SculptVertexSnapshot> DeduplicateTargetsByXY(
            IEnumerable<SculptVertexSnapshot> targets, double tolerance)
        {
            var result = new List<SculptVertexSnapshot>();
            if (targets == null)
                return result;

            foreach (SculptVertexSnapshot t in targets)
            {
                SculptVertexSnapshot existing = null;
                foreach (SculptVertexSnapshot r in result)
                {
                    if (HorizontalDistance(r.X, r.Y, t.X, t.Y) < tolerance)
                    {
                        existing = r;
                        break;
                    }
                }

                if (existing == null)
                {
                    result.Add(new SculptVertexSnapshot { X = t.X, Y = t.Y, Z = t.Z });
                }
                else if (t.Z > existing.Z)
                {
                    existing.Z = t.Z;
                }
            }

            return result;
        }

        private static int WriteVertexZChanges(
            SlabShapeEditor editor,
            SculptState state,
            TopoSurveyElevationContext elevation,
            bool logSurveySummary = false,
            XYZ stampPickCenter = null,
            double? stampPickSurveyFt = null,
            double stampGainFeet = 0)
        {
            int modified = 0;
            const double zTolerance = 1e-4;
            const double pickTol = 0.15;

            foreach (SculptVertex v in state.Vertices)
            {
                if (v.RevitVertex == null) continue;

                double targetModelZ = v.Z;
                if (stampPickCenter != null && stampPickSurveyFt.HasValue &&
                    HorizontalDistance(v.X, v.Y, stampPickCenter.X, stampPickCenter.Y) < pickTol)
                {
                    targetModelZ = elevation.SurveyElevationToModelZ(
                        v.X, v.Y, stampPickSurveyFt.Value + stampGainFeet);
                }

                double currentModelZ = SlabPositionToModelZ(
                    state.Doc, state.Toposolid, elevation, v.RevitVertex.Position.Z);
                if (Math.Abs(targetModelZ - currentModelZ) < zTolerance)
                    continue;

                if (TrySetTopoVertexModelZ(editor, v.X, v.Y, targetModelZ))
                    modified++;
            }

            if (logSurveySummary)
            {
                Log.Information(
                    "Commit ref plane model Z = {RefZ:F4} ft, {Modified} vertices written via AddPoint(model Z)",
                    elevation.ReferencePlaneModelZ, modified);

                if (stampPickCenter != null && stampPickSurveyFt.HasValue)
                {
                    double expectedSurvey = stampPickSurveyFt.Value + stampGainFeet;
                    double expectedModelZ = elevation.SurveyElevationToModelZ(
                        stampPickCenter.X, stampPickCenter.Y, expectedSurvey);
                    Log.Information(
                        "Commit pick target survey {Target:F3} ft (model Z {ModelZ:F3} ft) at ({X:F2},{Y:F2})",
                        expectedSurvey,
                        expectedModelZ,
                        stampPickCenter.X,
                        stampPickCenter.Y);
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
            var snapshots = state.Vertices
                .Select(v => new SculptVertexSnapshot { X = v.X, Y = v.Y, Z = v.Z })
                .ToList();

            TerrainModifier.StampResult result = TerrainModifier.ApplyShapeByPointStamp(
                doc, toposolid, snapshots, center, options);

            ApplyStampResultToState(doc, toposolid, editor, state, snapshots, result);
        }

        private static void ApplyStampResultToState(
            Document doc,
            Toposolid toposolid,
            SlabShapeEditor editor,
            SculptState state,
            IList<SculptVertexSnapshot> beforeSnapshots,
            TerrainModifier.StampResult result)
        {
            const double xyTol = 0.15;
            int addedBefore = state.PointsAdded;

            foreach (SculptVertexSnapshot target in result.Vertices)
            {
                bool exists = beforeSnapshots.Any(v =>
                    HorizontalDistance(v.X, v.Y, target.X, target.Y) < xyTol);
                if (exists) continue;

                if (!TrySetTopoVertexModelZ(editor, target.X, target.Y, target.Z))
                    continue;

                doc.Regenerate();
                SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, target.X, target.Y, xyTol);
                if (vertex == null) continue;

                doc.Regenerate();
                XYZ pos = vertex.Position;
                double modelZ = SlabPositionToModelZ(doc, toposolid, state.Elevation, pos.Z);
                state.PointsAdded++;
                state.Vertices.Add(new SculptVertex
                {
                    RevitVertex = vertex,
                    X = target.X,
                    Y = target.Y,
                    Z = modelZ,
                    OriginalZ = modelZ
                });
            }

            if (state.PointsAdded > addedBefore)
            {
                doc.Regenerate();
                RefreshRevitVertices(editor, state);
            }

            const double matchTol = 0.15;
            foreach (SculptVertex v in state.Vertices)
            {
                SculptVertexSnapshot bestTarget = null;
                double bestDist = matchTol;
                foreach (SculptVertexSnapshot t in result.Vertices)
                {
                    double d = HorizontalDistance(v.X, v.Y, t.X, t.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestTarget = t;
                    }
                }

                if (bestTarget != null)
                    v.Z = bestTarget.Z;
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

                if (!TrySetTopoVertexModelZ(editor, pt.X, pt.Y, pt.Z))
                    continue;

                doc.Regenerate();
                SlabShapeVertex vertex = FindSlabShapeVertexNearXY(editor, pt.X, pt.Y, xyTol);
                if (vertex == null)
                {
                    Log.Debug("DrawPoint did not create a vertex near ({X:F2}, {Y:F2})", pt.X, pt.Y);
                    continue;
                }

                double modelZ = SlabPositionToModelZ(doc, toposolid, state.Elevation, vertex.Position.Z);
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

        private static void RefreshRevitVertices(
            SlabShapeEditor editor, SculptState state, TopoSurveyElevationContext elevation = null)
        {
            if (state?.Doc == null || state.Toposolid == null) return;

            elevation ??= state.Elevation;
            var fresh = CollectVertices(state.Doc, state.Toposolid, editor, elevation);
            const double xyTol = 0.15;

            foreach (SculptVertex v in state.Vertices)
            {
                SculptVertex best = null;
                double bestDist = xyTol;
                foreach (SculptVertex f in fresh)
                {
                    double d = HorizontalDistance(v.X, v.Y, f.X, f.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = f;
                    }
                }

                if (best == null)
                    continue;

                v.RevitVertex = best.RevitVertex;
                v.X = best.X;
                v.Y = best.Y;
                v.OriginalZ = best.OriginalZ;
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

        /// <summary>Slab-shape vertex offset → model elevation (reference plane + offset when needed).</summary>
        public static double VertexZToModelZ(
            Document doc, Toposolid toposolid, IEnumerable<SculptVertexSnapshot> vertices, double slabOffsetOrModelZ)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb != null &&
                    slabOffsetOrModelZ >= bb.Min.Z - 2.0 &&
                    slabOffsetOrModelZ <= bb.Max.Z + 2.0)
                    return slabOffsetOrModelZ;
            }
            catch { }

            return GetReferencePlaneModelZ(doc, toposolid) + slabOffsetOrModelZ;
        }

        /// <summary>Interpolated topo surface elevation in model coordinates at XY.</summary>
        public static double? GetModelSurfaceZ(
            Document doc,
            Toposolid toposolid,
            IEnumerable<SculptVertexSnapshot> vertices,
            double x,
            double y,
            double searchRadius)
        {
            // Snapshots already store model Z — do not run slab-offset conversion again.
            return InterpolateSurfaceZ(vertices, x, y, searchRadius);
        }

        public static double ComputeShapeFalloff(double t, SculptFalloffType type) =>
            ComputeFalloff(t, type);

        /// <summary>Exact slab vertex Z when XY is on/near an existing control point.</summary>
        public static double? TryGetNearestSlabVertexZ(
            IEnumerable<SculptVertexSnapshot> vertices,
            double x,
            double y,
            double tolerance = 0.15)
        {
            if (vertices == null)
                return null;

            double bestDist = tolerance;
            double? bestZ = null;

            foreach (SculptVertexSnapshot v in vertices)
            {
                double dist = HorizontalDistance(v.X, v.Y, x, y);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestZ = v.Z;
                }
            }

            return bestZ;
        }

        /// <summary>Radial stamp points on visible top face (Along Surface, offset 0 for placement).</summary>
        internal static List<XYZ> ComputeShapeByPointStampPoints(
            XYZ center,
            ModifyTopoOptions options,
            IList<SculptVertexSnapshot> vertices,
            Document doc,
            Toposolid toposolid,
            View view,
            bool previewWithGain,
            ModifyTopoGeometrySurfaceCache geometry = null)
        {
            var result = new List<XYZ>();
            if (center == null || options == null)
                return result;

            const int maxPoints = 120;
            double radius = Math.Max(options.ShapeRadiusFeet, 0.1);
            int density = Math.Max(1, Math.Min(options.ShapePointDensity, 10));
            int ringCount = Math.Max(1, density);
            int baseSegments = 6 + density * 2;

            double centerSurfaceZ = SampleAlongSurfaceModelZ(
                    doc, toposolid, geometry, vertices, view, center.X, center.Y, radius)
                ?? center.Z;

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

                    double surfaceZ = SampleAlongSurfaceModelZ(
                            doc, toposolid, geometry, vertices, view, gx, gy, ringRadius + 2)
                        ?? centerSurfaceZ;

                    double dist = HorizontalDistance(gx, gy, center.X, center.Y);
                    double w = previewWithGain
                        ? ComputeShapeFalloff(dist / radius, options.ShapeFalloff)
                        : 0;
                    result.Add(new XYZ(gx, gy, surfaceZ + options.ShapeDeltaFeet * w));
                }
            }

            return result;
        }

        private static double? SampleAlongSurfaceModelZ(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IEnumerable<SculptVertexSnapshot> vertices,
            View view,
            double x,
            double y,
            double searchRadius)
        {
            if (doc != null && toposolid != null)
            {
                return RevitAlongSurfaceSampler.GetAlongSurfaceModelZ(
                    doc, toposolid, geometry, vertices, view, x, y, searchRadius);
            }

            return InterpolateSurfaceZ(vertices, x, y, searchRadius);
        }

        /// <summary>Stamp points that would be newly added (no existing vertex nearby).</summary>
        internal static List<XYZ> ComputeShapeByPointAddPreviewPoints(
            Document doc,
            Toposolid toposolid,
            XYZ center,
            ModifyTopoOptions options,
            IList<SculptVertexSnapshot> vertices,
            ModifyTopoGeometrySurfaceCache geometry = null)
        {
            var stamp = ComputeShapeByPointStampPoints(
                center, options, vertices, doc, toposolid, null, previewWithGain: false, geometry);
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
