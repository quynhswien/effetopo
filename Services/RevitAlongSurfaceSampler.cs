using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace effetopo.Services
{
#if REVIT2024_OR_GREATER
    /// <summary>
    /// Matches Revit Modify Sub Elements → Add Point (Along Surface, Elevation Base = Survey Point):
    /// XY from face pick / intersector; Z from visible top-face geometry; label uses survey elevation.
    /// </summary>
    internal static class RevitAlongSurfaceSampler
    {
        internal sealed class AlongSurfaceSample
        {
            public XYZ ModelPoint;
            public double TopFaceModelZ;
            public double SurveyElevationFt;
        }

        internal sealed class SurveyCoordinateHelper
        {
            private readonly Transform _modelToShared;
            private readonly Transform _sharedToModel;

            public SurveyCoordinateHelper(Document doc)
            {
                ProjectLocation location = doc?.ActiveProjectLocation;
                if (location != null)
                {
                    _sharedToModel = location.GetTotalTransform();
                    _modelToShared = _sharedToModel.Inverse;
                }
            }

            public bool IsAvailable => _modelToShared != null && _sharedToModel != null;

            /// <summary>Survey Point elevation at model XYZ (Revit Elevation Base = Survey Point).</summary>
            public double ModelZToSurveyElevation(double x, double y, double modelZ)
            {
                if (!IsAvailable)
                    return modelZ;
                return _modelToShared.OfPoint(new XYZ(x, y, modelZ)).Z;
            }

            /// <summary>Model Z that Revit shows as <paramref name="surveyElevationFt"/> (Survey Point).</summary>
            public double SurveyElevationToModelZ(double x, double y, double surveyElevationFt)
            {
                if (!IsAvailable)
                    return surveyElevationFt;
                XYZ sharedAtXY = _modelToShared.OfPoint(new XYZ(x, y, 0));
                XYZ sharedTarget = new XYZ(sharedAtXY.X, sharedAtXY.Y, surveyElevationFt);
                return _sharedToModel.OfPoint(sharedTarget).Z;
            }

            /// <summary>Slab-shape offset from reference plane so vertex displays <paramref name="surveyElevationFt"/>.</summary>
            public double SurveyElevationToSlabOffset(
                double x, double y, double surveyElevationFt, double referencePlaneModelZ)
            {
                double targetModelZ = SurveyElevationToModelZ(x, y, surveyElevationFt);
                return targetModelZ - referencePlaneModelZ;
            }

            /// <summary>Survey elevation of an existing slab vertex (offset → model → survey).</summary>
            public double SlabOffsetToSurveyElevation(
                double x, double y, double slabOffset, double referencePlaneModelZ)
            {
                double modelZ = referencePlaneModelZ + slabOffset;
                return ModelZToSurveyElevation(x, y, modelZ);
            }
        }

        public static string FormatSurveyElevation(Document doc, double surveyElevationFeet)
        {
            try
            {
                Units units = doc.GetUnits();
#if REVIT2024_OR_GREATER
                return UnitFormatUtils.Format(units, SpecTypeId.Length, surveyElevationFeet, false);
#else
                return UnitFormatUtils.Format(units, UnitType.UT_Length, surveyElevationFeet, false, false);
#endif
            }
            catch
            {
                return surveyElevationFeet.ToString("F3");
            }
        }

        /// <summary>
        /// Survey elevation of topo surface at XY from slab control points (Elevation Base = Survey Point).
        /// Does not use Project Base Point or Internal Origin for the measurement.
        /// </summary>
        public static bool TryGetSurfaceSurveyElevation(
            Document doc,
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> slabVertices,
            double x,
            double y,
            double referencePlaneModelZ,
            SurveyCoordinateHelper survey,
            out double surveyElevationFt,
            out double surfaceModelZ,
            double nearestToleranceFt = 2.0)
        {
            surveyElevationFt = 0;
            surfaceModelZ = 0;

            double? modelZ = ModifyTopoService.TryGetNearestSlabVertexZ(
                slabVertices, x, y, nearestToleranceFt);
            if (!modelZ.HasValue)
                return false;

            surfaceModelZ = modelZ.Value;
            survey ??= new SurveyCoordinateHelper(doc);
            if (!survey.IsAvailable)
            {
                surveyElevationFt = surfaceModelZ;
                return true;
            }

            surveyElevationFt = survey.ModelZToSurveyElevation(x, y, surfaceModelZ);
            return true;
        }

        /// <summary>
        /// Along-surface model Z at exact XY — visible top face first (matches Revit Add Point),
        /// then exact slab control point (on-vertex), then interpolate.
        /// </summary>
        public static double? GetAlongSurfaceModelZ(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> slabVertices,
            View view,
            double x,
            double y,
            double searchRadius = 20)
        {
            const double onVertexTolerance = 0.15;

            // 1. Visible top face at exact XY (triangulated surface — "Along Surface").
            double? topFace = GetTopFaceModelZ(doc, toposolid, geometry, slabVertices, view, x, y);
            if (topFace.HasValue)
                return topFace.Value;

            // 2. Exactly on an existing slab control point.
            double? onVertex = ModifyTopoService.TryGetNearestSlabVertexZ(
                slabVertices, x, y, onVertexTolerance);
            if (onVertex.HasValue)
                return onVertex.Value;

            // 3. Interpolate from nearby slab vertices.
            if (doc != null && toposolid != null && slabVertices != null)
            {
                return ModifyTopoService.GetModelSurfaceZ(
                    doc, toposolid, slabVertices, x, y, Math.Max(searchRadius, 0.5));
            }

            return null;
        }

        /// <summary>Apply Along Surface gain in survey elevation (matches Revit Elevation Base = Survey Point).</summary>
        public static double ApplyAlongSurfaceGain(
            Document doc,
            double x,
            double y,
            double surfaceModelZ,
            double gainFeet,
            double falloffWeight,
            SurveyCoordinateHelper survey = null)
        {
            survey ??= new SurveyCoordinateHelper(doc);
            if (!survey.IsAvailable)
                return surfaceModelZ + gainFeet * falloffWeight;

            double surfaceSurvey = survey.ModelZToSurveyElevation(x, y, surfaceModelZ);
            double targetSurvey = surfaceSurvey + gainFeet * falloffWeight;
            return survey.SurveyElevationToModelZ(x, y, targetSurvey);
        }

        /// <summary>Top-face model Z at XY — display mesh / raycast (for marker placement on visible surface).</summary>
        public static double? GetTopFaceModelZ(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> slabVertices,
            View view,
            double x,
            double y)
        {
            double? z = geometry?.TryGetSurfaceZ(x, y);
            if (z.HasValue)
                return z.Value;

            View3D view3d = ModifyTopoService.ResolveView3D(doc, view);
            if (view3d != null)
            {
                double? rayZ = ModifyTopoService.TryRaycastToposolidSurfaceZ(toposolid, view3d, x, y);
                if (rayZ.HasValue)
                    return rayZ.Value;
            }

            double searchRadius = Math.Max(GetTopoHorizontalSize(toposolid), 20);
            return ModifyTopoService.GetModelSurfaceZ(doc, toposolid, slabVertices, x, y, searchRadius);
        }

        public static bool TrySampleAtXY(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> slabVertices,
            View view,
            double x,
            double y,
            SurveyCoordinateHelper survey,
            out AlongSurfaceSample sample)
        {
            sample = null;
            if (doc == null || toposolid == null)
                return false;

            survey ??= new SurveyCoordinateHelper(doc);

            double? topZ = GetAlongSurfaceModelZ(
                doc, toposolid, geometry, slabVertices, view, x, y);
            if (!topZ.HasValue)
                return false;

            sample = new AlongSurfaceSample
            {
                TopFaceModelZ = topZ.Value,
                ModelPoint = new XYZ(x, y, topZ.Value),
                SurveyElevationFt = survey.IsAvailable
                    ? survey.ModelZToSurveyElevation(x, y, topZ.Value)
                    : topZ.Value
            };
            return true;
        }

        /// <summary>Normalize a PickPoint / face snap to along-surface model XYZ.</summary>
        public static bool TryNormalizePick(
            Document doc,
            Toposolid toposolid,
            ModifyTopoGeometrySurfaceCache geometry,
            IEnumerable<ModifyTopoService.SculptVertexSnapshot> slabVertices,
            Autodesk.Revit.UI.UIDocument uidoc,
            XYZ rawPick,
            out AlongSurfaceSample sample)
        {
            sample = null;
            if (rawPick == null)
                return false;

            double x = rawPick.X;
            double y = rawPick.Y;
            double? faceModelZ = null;
            View view = uidoc?.ActiveView;

            if (view is View3D view3d)
            {
                try
                {
                    XYZ viewDir = view3d.ViewDirection;
                    var intersector = new ReferenceIntersector(
                        toposolid.Id, FindReferenceTarget.Face, view3d);
                    ReferenceWithContext hit = intersector.FindNearest(
                        rawPick + 1000 * viewDir, -viewDir);
                    XYZ global = hit?.GetReference()?.GlobalPoint;
                    if (global != null)
                    {
                        x = global.X;
                        y = global.Y;
                        faceModelZ = global.Z;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Pick face intersector failed: {Error}", ex.Message);
                }
            }

            var survey = new SurveyCoordinateHelper(doc);

            if (faceModelZ.HasValue)
            {
                sample = new AlongSurfaceSample
                {
                    TopFaceModelZ = faceModelZ.Value,
                    ModelPoint = new XYZ(x, y, faceModelZ.Value),
                    SurveyElevationFt = survey.IsAvailable
                        ? survey.ModelZToSurveyElevation(x, y, faceModelZ.Value)
                        : faceModelZ.Value
                };
                return true;
            }

            if (!TrySampleAtXY(doc, toposolid, geometry, slabVertices, view, x, y, survey, out sample))
            {
                double fallbackZ = rawPick.Z;
                sample = new AlongSurfaceSample
                {
                    TopFaceModelZ = fallbackZ,
                    ModelPoint = new XYZ(x, y, fallbackZ),
                    SurveyElevationFt = survey.ModelZToSurveyElevation(x, y, fallbackZ)
                };
            }

            return true;
        }

        private static double GetTopoHorizontalSize(Toposolid toposolid)
        {
            try
            {
                BoundingBoxXYZ bb = toposolid.get_BoundingBox(null);
                if (bb == null) return 20;
                return Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            }
            catch
            {
                return 20;
            }
        }
    }
#endif
}
