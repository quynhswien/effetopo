using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    public class CreateContourLineService
    {
        private static CreateContourLineService _instance;
        private static readonly object _lock = new object();

        public static CreateContourLineService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new CreateContourLineService();
                    }
                }
                return _instance;
            }
        }

        private CreateContourLineService() { }

#if REVIT2024_OR_GREATER
        public CreateContourLineResult CreateFromToposolid(
            Document doc,
            Toposolid toposolid,
            CreateContourLineOptions options)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (toposolid == null) throw new ArgumentNullException(nameof(toposolid));
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (options.UseMajorMinorContours && options.MajorIntervalFeet <= 0)
                throw new InvalidOperationException("Major contour interval must be greater than zero.");

            List<ToposolidContourExtractor.Triangle> triangles =
                ToposolidContourExtractor.ExtractTopTriangles(toposolid);
            if (triangles.Count == 0)
                throw new InvalidOperationException("Could not read Toposolid surface geometry.");

            List<IList<XYZ>> polylines = ToposolidContourExtractor.GenerateContourPolylines(
                triangles, options.IntervalFeet);
            if (polylines.Count == 0)
                throw new InvalidOperationException("No contour lines were generated for the selected interval.");

            GraphicsStyle minorLineStyle = CreateContourLineRevitHelper.ResolveLineStyle(
                doc, options.MinorLineStyleName);
            GraphicsStyle majorLineStyle = options.UseMajorMinorContours
                ? CreateContourLineRevitHelper.ResolveLineStyle(doc, options.MajorLineStyleName)
                : minorLineStyle;

            ElementId levelId = ResolveLevelId(doc, options);
            double minSegmentLength = CreateContourLineRevitHelper.GetShortCurveTolerance(doc);

            int curveCount = 0;
            int skippedShortSegments = 0;
            int majorCurveCount = 0;
            int minorCurveCount = 0;
            var elevations = new HashSet<double>();
            var majorElevations = new HashSet<double>();

            foreach (IList<XYZ> polyline in polylines)
            {
                if (polyline == null || polyline.Count < 2)
                    continue;

                double elevation = polyline[0].Z;
                bool isMajor = options.UseMajorMinorContours &&
                    CreateContourLineRevitHelper.IsMajorContourElevation(elevation, options.MajorIntervalFeet);
                GraphicsStyle lineStyle = isMajor ? majorLineStyle : minorLineStyle;

                elevations.Add(Math.Round(elevation, 6));
                if (isMajor)
                    majorElevations.Add(Math.Round(elevation, 6));

                int created = CreateModelCurvesForPolyline(
                    doc, polyline, lineStyle, levelId, minSegmentLength, ref skippedShortSegments);
                curveCount += created;
                if (isMajor)
                    majorCurveCount += created;
                else
                    minorCurveCount += created;
            }

            if (curveCount == 0)
            {
                throw new InvalidOperationException(
                    "No model curves could be created. Contour segments are shorter than Revit's minimum curve length. " +
                    "Try increasing the contour interval.");
            }

            if (skippedShortSegments > 0)
            {
                Log.Information(
                    "Create contour lines skipped {SkippedCount} segment(s) shorter than {Tolerance:F6} ft",
                    skippedShortSegments,
                    minSegmentLength);
            }

            double minZ = triangles.Min(t => Math.Min(t.V0.Z, Math.Min(t.V1.Z, t.V2.Z)));
            double maxZ = triangles.Max(t => Math.Max(t.V0.Z, Math.Max(t.V1.Z, t.V2.Z)));

            return new CreateContourLineResult
            {
                CurveCount = curveCount,
                MajorCurveCount = options.UseMajorMinorContours ? majorCurveCount : 0,
                MinorCurveCount = options.UseMajorMinorContours ? minorCurveCount : 0,
                ElevationLevelCount = elevations.Count,
                MajorElevationLevelCount = majorElevations.Count,
                MinElevationFeet = minZ,
                MaxElevationFeet = maxZ
            };
        }

        public static double? TryGetSuggestedIntervalFeet(Toposolid toposolid) =>
            ToposolidContourExtractor.TryReadContourIntervalFeet(toposolid);

        public static double? TryGetSuggestedMajorIntervalFeet(Toposolid toposolid) =>
            ToposolidContourExtractor.TryReadMajorContourIntervalFeet(toposolid);

        private static ElementId ResolveLevelId(Document doc, CreateContourLineOptions options)
        {
            if (!options.AssignLevel || options.LevelId <= 0)
                return ElementId.InvalidElementId;

            ElementId levelId = ToElementId(options.LevelId);
            if (doc.GetElement(levelId) is Level)
                return levelId;

            throw new InvalidOperationException("Selected level is no longer valid in this project.");
        }

        private static int CreateModelCurvesForPolyline(
            Document doc,
            IList<XYZ> points,
            GraphicsStyle lineStyle,
            ElementId levelId,
            double minSegmentLength,
            ref int skippedShortSegments)
        {
            List<XYZ> simplified = CreateContourLineRevitHelper.CollapseNearDuplicatePoints(
                points, minSegmentLength * 0.5);
            if (simplified.Count < 2)
                return 0;

            int created = 0;
            double z = simplified[0].Z;
            SketchPlane sketchPlane = CreateSketchPlaneAtElevation(doc, z);

            for (int i = 0; i < simplified.Count - 1; i++)
            {
                XYZ a = simplified[i];
                XYZ b = simplified[i + 1];
                if (!CreateContourLineRevitHelper.IsSegmentLongEnough(a, b, minSegmentLength))
                {
                    skippedShortSegments++;
                    continue;
                }

                if (Math.Abs(sketchPlane.GetPlane().Origin.Z - a.Z) > 1e-6)
                    sketchPlane = CreateSketchPlaneAtElevation(doc, a.Z);

                if (TryCreateModelCurve(doc, Line.CreateBound(a, b), sketchPlane, lineStyle, levelId))
                    created++;
                else
                    skippedShortSegments++;
            }

            return created;
        }

        private static SketchPlane CreateSketchPlaneAtElevation(Document doc, double elevation)
        {
            Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, elevation));
            return SketchPlane.Create(doc, plane);
        }

        private static bool TryCreateModelCurve(
            Document doc,
            Curve curve,
            SketchPlane sketchPlane,
            GraphicsStyle lineStyle,
            ElementId levelId)
        {
            if (curve == null || sketchPlane == null)
                return false;

            try
            {
                ModelCurve modelCurve = doc.Create.NewModelCurve(curve, sketchPlane);
                if (lineStyle != null)
                    modelCurve.LineStyle = lineStyle;

                if (levelId != ElementId.InvalidElementId)
                    CreateContourLineRevitHelper.TryAssignReferenceLevel(modelCurve, levelId);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Skipped contour model curve: {Error}", ex.Message);
                return false;
            }
        }

        private static ElementId ToElementId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
#else
        public CreateContourLineResult CreateFromToposolid(
            Document doc,
            Element toposolid,
            CreateContourLineOptions options)
        {
            throw new NotSupportedException("Toposolid is only available in Revit 2024 and later");
        }

        public static double? TryGetSuggestedIntervalFeet(Element toposolid) => null;

        public static double? TryGetSuggestedMajorIntervalFeet(Element toposolid) => null;
#endif
    }
}
