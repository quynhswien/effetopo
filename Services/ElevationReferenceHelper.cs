using System;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Converts between model Z and elevation values for the selected elevation base.
    /// </summary>
    public sealed class ElevationReferenceHelper
    {
        private readonly ElevationBaseType _baseType;
        private readonly Transform _modelToShared;
        private readonly Transform _sharedToModel;
        private readonly double _levelElevation;
        private readonly double _topPlaneZ;
        private readonly double _projectBaseZ;

        public ElevationReferenceHelper(Document doc, View view, ElevationBaseType baseType)
        {
            _baseType = baseType;

            ProjectLocation location = doc?.ActiveProjectLocation;
            if (location != null)
            {
                _sharedToModel = location.GetTotalTransform();
                _modelToShared = _sharedToModel?.Inverse;
            }

            Level level = view?.GenLevel;
            _levelElevation = level?.Elevation ?? 0;

            _topPlaneZ = ResolveTopPlaneZ(view);
            _projectBaseZ = ResolveProjectBasePointZ(doc);
        }

        /// <summary>Display elevation (feet) at model XY,Z to the chosen base.</summary>
        public double ModelZToDisplayElevation(double x, double y, double modelZ)
        {
            switch (_baseType)
            {
                case ElevationBaseType.InternalOrigin:
                    return modelZ;
                case ElevationBaseType.CurrentLevel:
                    return modelZ - _levelElevation;
                case ElevationBaseType.TopPlane:
                    return modelZ - _topPlaneZ;
                case ElevationBaseType.ProjectBasePoint:
                    return modelZ - _projectBaseZ;
                case ElevationBaseType.SurveyPoint:
                    return ModelZToSurveyElevation(x, y, modelZ);
                default:
                    return modelZ;
            }
        }

        /// <summary>Model Z (feet) from display elevation at model XY.</summary>
        public double DisplayElevationToModelZ(double x, double y, double displayElevation)
        {
            switch (_baseType)
            {
                case ElevationBaseType.InternalOrigin:
                    return displayElevation;
                case ElevationBaseType.CurrentLevel:
                    return _levelElevation + displayElevation;
                case ElevationBaseType.TopPlane:
                    return _topPlaneZ + displayElevation;
                case ElevationBaseType.ProjectBasePoint:
                    return _projectBaseZ + displayElevation;
                case ElevationBaseType.SurveyPoint:
                    return SurveyElevationToModelZ(x, y, displayElevation);
                default:
                    return displayElevation;
            }
        }

        private double ModelZToSurveyElevation(double x, double y, double modelZ)
        {
            if (_modelToShared == null) return modelZ;
            return _modelToShared.OfPoint(new XYZ(x, y, modelZ)).Z;
        }

        private double SurveyElevationToModelZ(double x, double y, double surveyElevation)
        {
            if (_modelToShared == null || _sharedToModel == null) return surveyElevation;
            XYZ sharedAtXY = _modelToShared.OfPoint(new XYZ(x, y, 0));
            XYZ sharedTarget = new XYZ(sharedAtXY.X, sharedAtXY.Y, surveyElevation);
            return _sharedToModel.OfPoint(sharedTarget).Z;
        }

        private static double ResolveTopPlaneZ(View view)
        {
            if (view == null) return 0;

            try
            {
                if (view is ViewPlan viewPlan)
                {
                    PlanViewRange range = viewPlan.GetViewRange();
                    if (range != null)
                    {
                        Level level = view.Document.GetElement(range.GetLevelId(PlanViewPlane.TopClipPlane)) as Level;
                        double offset = range.GetOffset(PlanViewPlane.TopClipPlane);
                        if (level != null)
                            return level.Elevation + offset;
                    }
                }

                SketchPlane sketchPlane = view.SketchPlane;
                if (sketchPlane != null)
                {
                    Plane plane = sketchPlane.GetPlane();
                    if (plane != null)
                        return plane.Origin.Z;
                }
            }
            catch
            {
                // Fall through to zero.
            }

            return 0;
        }

        private static double ResolveProjectBasePointZ(Document doc)
        {
            try
            {
                BasePoint basePoint = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ProjectBasePoint)
                    .WhereElementIsNotElementType()
                    .FirstElement() as BasePoint;

                return basePoint?.Position.Z ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
