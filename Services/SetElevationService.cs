using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using effetopo.Models;

namespace effetopo.Services
{
    /// <summary>
    /// Applies elevation values to model lines and splines, optionally adding labels and graphic overrides.
    /// </summary>
    public class SetElevationService
    {
        private static SetElevationService _instance;
        private static readonly object _lock = new object();

        public static SetElevationService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new SetElevationService();
                    }
                }
                return _instance;
            }
        }

        private SetElevationService() { }

        public IReadOnlyList<SetElevationLineResult> Apply(
            Document doc,
            View view,
            IList<ModelCurve> curves,
            SetElevationOptions options)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (curves == null || curves.Count == 0)
                return Array.Empty<SetElevationLineResult>();

            var elevationHelper = new ElevationReferenceHelper(doc, view, options.ElevationBase);
            var results = new List<SetElevationLineResult>(curves.Count);

            ElementId textTypeId = options.TextTypeId > 0
                ? new ElementId(options.TextTypeId)
                : ElementId.InvalidElementId;

            if (options.AddLabel && textTypeId == ElementId.InvalidElementId)
            {
                textTypeId = new FilteredElementCollector(doc)
                    .OfClass(typeof(TextNoteType))
                    .Cast<TextNoteType>()
                    .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
            }

            for (int i = 0; i < curves.Count; i++)
            {
                ModelCurve modelCurve = curves[i];
                double displayElevation = options.StartElevationFeet + i * options.IncrementFeet;
                var lineResult = new SetElevationLineResult
                {
                    ElementId = GetElementIdValue(modelCurve.Id),
                    DisplayElevation = displayElevation
                };

                try
                {
                    if (!TrySetCurveElevation(modelCurve, elevationHelper, displayElevation, out XYZ labelPoint))
                    {
                        lineResult.Success = false;
                        lineResult.Message = "Could not update curve geometry.";
                        results.Add(lineResult);
                        continue;
                    }

                    lineResult.FormattedElevation = FormatElevation(doc, displayElevation);
                    ApplyGraphicOverride(view, modelCurve.Id, options.OverrideColor);

                    if (options.AddLabel && textTypeId != ElementId.InvalidElementId)
                    {
                        CreateElevationLabel(doc, view, labelPoint, lineResult.FormattedElevation, textTypeId);
                    }

                    lineResult.Success = true;
                }
                catch (Exception ex)
                {
                    lineResult.Success = false;
                    lineResult.Message = ex.Message;
                    Log.Warning(ex, "Failed to set elevation on model curve {Id}", lineResult.ElementId);
                }

                results.Add(lineResult);
            }

            return results;
        }

        private static bool TrySetCurveElevation(
            ModelCurve modelCurve,
            ElevationReferenceHelper elevationHelper,
            double displayElevationFeet,
            out XYZ labelPoint)
        {
            labelPoint = XYZ.Zero;
            LocationCurve location = modelCurve.Location as LocationCurve;
            if (location?.Curve == null)
                return false;

            Curve curve = location.Curve;
            XYZ mid = curve.Evaluate(0.5, true);
            double targetModelZ = elevationHelper.DisplayElevationToModelZ(mid.X, mid.Y, displayElevationFeet);
            double deltaZ = targetModelZ - mid.Z;

            if (Math.Abs(deltaZ) < 1e-9)
            {
                labelPoint = mid;
                return true;
            }

            Transform move = Transform.CreateTranslation(new XYZ(0, 0, deltaZ));
            Curve movedCurve = curve.CreateTransformed(move);
            location.Curve = movedCurve;
            labelPoint = movedCurve.Evaluate(0.5, true);
            return true;
        }

        private static void ApplyGraphicOverride(View view, ElementId elementId, Color color)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetSurfaceForegroundPatternColor(color);
            view.SetElementOverrides(elementId, ogs);
        }

        private static void CreateElevationLabel(
            Document doc,
            View view,
            XYZ position,
            string text,
            ElementId textTypeId)
        {
            View labelView = ResolveLabelView(doc, view);
            if (labelView == null)
                throw new InvalidOperationException("No suitable view found for elevation labels.");

            TextNote.Create(doc, labelView.Id, position, text, textTypeId);
        }

        private static View ResolveLabelView(Document doc, View activeView)
        {
            if (activeView != null && activeView.CanBePrinted && !activeView.IsTemplate)
            {
                if (activeView.ViewType == ViewType.FloorPlan ||
                    activeView.ViewType == ViewType.CeilingPlan ||
                    activeView.ViewType == ViewType.EngineeringPlan ||
                    activeView.ViewType == ViewType.Section ||
                    activeView.ViewType == ViewType.Elevation)
                {
                    return activeView;
                }
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
        }

        private static string FormatElevation(Document doc, double elevationFeet)
        {
            try
            {
                Units units = doc.GetUnits();
#if REVIT2024_OR_GREATER
                return UnitFormatUtils.Format(units, SpecTypeId.Length, elevationFeet, false);
#else
                return UnitFormatUtils.Format(units, UnitType.UT_Length, elevationFeet, false, false);
#endif
            }
            catch
            {
                return elevationFeet.ToString("F3");
            }
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id?.Value ?? -1;
#else
            return id?.IntegerValue ?? -1;
#endif
        }
    }
}
